using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// Regression tests for P312 (review finding C2): one silent TCP connection
/// must never wedge the whole accept loop and block every new join, and a
/// stalled greeting must eventually time out and be dropped rather than parking
/// forever. Tests pin:
///  (a) a connection that connects and sends nothing does NOT stop a second,
///      real client from connecting and completing logon (the accept loop now
///      returns immediately and the greeting read runs on its own thread);
///  (b) a stalled greeting connection is eventually dropped (socket closed by
///      the server after <c>GreetTimeout</c>) instead of lingering forever;
///  (c) a greeting claiming a huge body (&gt; <c>MaximumGreeting</c>) is rejected
///      before any large allocation — it never joins and is dropped fast;
///  (d) a legitimately joined peer that then goes idle (sends nothing) is NOT
///      dropped — the greeting ReceiveTimeout is cleared after a successful
///      greet so it never applies to the peer's data session.
/// </summary>
[Collection("LiveNetwork")]
public abstract class TcpTransportGreetTimeoutTestsBase
{
    // P605 — run the greet-timeout suite under both transport send modes; the
    // accept/greet path is mode-independent, but proving it on both guards a
    // future coupling.
    private readonly TransportMode _mode;
    protected TcpTransportGreetTimeoutTestsBase(TransportMode mode) => _mode = mode;

    private TcpTransport NewHost(Site site) => new TcpTransport(site, _mode, poolWorkers: 2);

    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = new();
        public readonly List<ConnectionId> Left = new();
        public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { lock (Left) Left.Add(id); }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
        public int JoinedCount() { lock (Joined) return Joined.Count; }
        public int LeftCount() { lock (Left) return Left.Count; }
    }

    private static IDisposable ShortenGreetTimeout(int ms)
    {
        var orig = TcpTransport.GreetTimeout;
        TcpTransport.GreetTimeout = TimeSpan.FromMilliseconds(ms);
        return new Restorer(() => TcpTransport.GreetTimeout = orig);
    }

    private static IDisposable LengthenGreetTimeout(int sec)
    {
        var orig = TcpTransport.GreetTimeout;
        TcpTransport.GreetTimeout = TimeSpan.FromSeconds(sec);
        return new Restorer(() => TcpTransport.GreetTimeout = orig);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly Action _a;
        public Restorer(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>Open a raw TCP connection to the host and send nothing — the
    /// "silent staller" from finding C2.</summary>
    private static TcpClient ConnectSilent(ushort port)
    {
        var raw = new TcpClient();
        raw.NoDelay = true;
        raw.Connect("127.0.0.1", port);
        return raw;
    }

    /// <summary>Speak the transport's Greet frame with <paramref name="name"/>
    /// on an already-open raw socket (like the pre-P311 ConnectRawPeer helper).</summary>
    private static void SendGreet(TcpClient raw, string name)
    {
        var ns = raw.GetStream();
        var body = Encoding.UTF8.GetBytes(name);
        var greet = new byte[1 + 4 + body.Length];
        greet[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(greet.AsSpan(1), (uint)body.Length);
        body.CopyTo(greet.AsSpan(5));
        ns.Write(greet);
        ns.Flush();
    }

    /// <summary>Poll until <c>OnPeerJoined</c> has fired, returning the new id.
    /// Throws <see cref="TimeoutException"/> after <paramref name="ms"/> — the
    /// "join can't complete" symptom this task fixes.</summary>
    private static ConnectionId AwaitJoin(Site site, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            lock (site.Joined) { if (site.Joined.Count > 0) return site.Joined[^1]; }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined — accept loop appears wedged");
            Thread.Sleep(5);
        }
    }

    /// <summary>Wait for the server to close the connection (its read returns 0
    /// = orderly FIN) within <paramref name="ms"/>. Throws if it never closes —
    /// the connection is "lingering forever" this task must eliminate.</summary>
    private static void AwaitDropped(TcpClient raw, int ms)
    {
        raw.ReceiveTimeout = ms;
        var ns = raw.GetStream();
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        var buf = new byte[1];
        while (DateTime.UtcNow < deadline)
        {
            int n = ns.Read(buf, 0, 1);
            if (n == 0) return; // server closed -> dropped
            throw new Exception("unexpected data from host");
        }
        throw new TimeoutException("stalled connection was not dropped by the host");
    }

    /// <summary>(a) THE live proof: while a silent connection sits idle, a
    /// second real client must still connect and complete logon. Under the
    /// pre-P312 code the silent socket parked the single accept thread in its
    /// greeting read, so the second client's join could never complete.</summary>
    [Fact]
    public void Silent_connection_does_not_block_a_second_client_join()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var staller = ConnectSilent(host.BoundPort);      // sends NOTHING
            Thread.Sleep(400);                                // let it park in its greeting read

            var real = new TcpClient();
            real.NoDelay = true;
            real.Connect("127.0.0.1", host.BoundPort);
            SendGreet(real, "SECOND");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var joined = AwaitJoin(site, 5000);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 5000,
                $"second client took {sw.ElapsedMilliseconds} ms to join while a staller sat idle");

            Assert.Equal(1, site.JoinedCount());   // only the real client joined; the staller never did
            Assert.False(site.LeftCount() > 0);    // no spurious departures

            staller.Dispose();
            real.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(b) A silent connection is eventually dropped by the server (its
    /// socket closed) rather than lingering forever, courtesy of the socket
    /// ReceiveTimeout on the greeting read.</summary>
    [Fact]
    public void Silent_connection_is_eventually_dropped()
    {
        using var _ = ShortenGreetTimeout(300);
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var staller = ConnectSilent(host.BoundPort);       // sends NOTHING
            AwaitDropped(staller, 5000);                       // server must close it within ~GreetTimeout+margin

            Assert.Equal(0, site.JoinedCount());               // it never joined
            staller.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(c) A greeting claiming a body larger than MaximumGreeting is
    /// rejected before any large allocation: it never joins, and the connection
    /// is dropped immediately (not after a long body-read timeout). The
    /// GreetTimeout is lengthened so a broken implementation that tried to read
    /// the body and wait for the timeout would hang the test — only an
    /// immediate length-cap rejection clears AwaitDropped in time.</summary>
    [Fact]
    public void Oversized_greeting_length_is_rejected_and_dropped_quickly()
    {
        using var _ = LengthenGreetTimeout(30);                // if it waited on the body read, it would stall
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var raw = ConnectSilent(host.BoundPort);
            var ns = raw.GetStream();
            // Claim a huge greeting body (> MaximumGreeting) that we never send.
            var hdr = new byte[5];
            hdr[0] = 0x01;                                     // Greet type
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(1), 65536); // 64 KiB body claimed
            ns.Write(hdr);
            ns.Flush();

            AwaitDropped(raw, 3000);                           // rejected on the length cap -> dropped fast

            Assert.Equal(0, site.JoinedCount());               // never joined
            raw.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(d) Guard against a P312 regression: a peer that greets and then
    /// goes idle (sends no further data) must NOT be dropped just because the
    /// greeting ReceiveTimeout elapsed afterward — the timeout is cleared right
    /// after a successful greet and must never apply to the data session.</summary>
    [Fact]
    public void Joined_but_idle_peer_is_not_dropped()
    {
        using var _ = ShortenGreetTimeout(300);
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var real = new TcpClient();
            real.NoDelay = true;
            real.Connect("127.0.0.1", host.BoundPort);
            SendGreet(real, "IDLE");
            AwaitJoin(site, 3000);
            Assert.Equal(1, site.JoinedCount());

            // Stay joined and send nothing for well past GreetTimeout (300 ms).
            Thread.Sleep(1200);

            Assert.Equal(0, site.LeftCount());                 // must not have been dropped
            real.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }
}

/// <summary>P605 — the greet-timeout suite under thread-per-peer (today's
/// default) transport mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportGreetTimeoutThreadPerPeerTests : TcpTransportGreetTimeoutTestsBase
{
    public TcpTransportGreetTimeoutThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }
}

/// <summary>P605 — the greet-timeout suite under pool (fixed send-worker
/// pool) transport mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportGreetTimeoutPoolTests : TcpTransportGreetTimeoutTestsBase
{
    public TcpTransportGreetTimeoutPoolTests() : base(TransportMode.Pool) { }
}

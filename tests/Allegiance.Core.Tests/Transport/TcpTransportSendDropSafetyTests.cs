using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// Regression tests for the P60 send-race fix (a peer departing mid-tick must
/// never let <see cref="TcpTransport.Send"/> escape onto the sim thread and kill
/// the whole server) **as revised by P311**, which replaced the synchronous
/// blocking socket write with a per-peer bounded send queue drained by a
/// dedicated writer thread. Because <see cref="TcpTransport.Send"/> now only
/// enqueues (never touches the socket), it is structurally non-blocking and
/// cannot throw for a peer whose socket is dead — the two P60 hazards are gone
/// by construction. These tests pin:
///  (a) send to a reaped peer id returns silently;
///  (b) send to a peer whose socket is dead/unresponsive does not propagate
///      (and the stalled peer is eventually disconnected, not queued forever);
///  (c) a healthy host send still arrives on the wire;
///  (d) the client-role "not connected" path still throws (stays loud);
///  (e) multiple sends to one peer preserve FIFO order on the wire;
///  (f) Send is non-blocking even while a peer is stalled (the sim thread can
///      never be frozen by one slow/unresponsive peer — review finding C1).
/// </summary>
[Collection("LiveNetwork")]
public abstract class TcpTransportSendDropSafetyTestsBase
{
    // P605 — parameterize the whole transport suite over send-draining mode
    // (thread-per-peer = today's behaviour, pool = fixed worker pool), so
    // every regression below is proven on BOTH transports.
    private readonly TransportMode _mode;
    protected TcpTransportSendDropSafetyTestsBase(TransportMode mode) => _mode = mode;

    private TcpTransport NewHost(Site site, bool tinyPool = false)
        => new TcpTransport(site, _mode, poolWorkers: tinyPool ? 2 : 0);

    private static readonly FieldInfo PeersField =
        typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("TcpTransport._peers field not found — did the transport change?");

    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = new();
        public readonly List<ConnectionId> Left = new();
        public readonly List<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { lock (Left) Left.Add(id); }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { lock (Received) Received.Add(data.ToArray()); }
        public bool HasLeft(ConnectionId id, int ms)
        {
            lock (Left) { if (Left.Contains(id)) return true; }
            if (ms <= 0) return false;
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
                lock (Left) { if (Left.Contains(id)) return true; }
            }
            return false;
        }
    }

    private static IDictionary Peers(TcpTransport t) => (IDictionary)PeersField.GetValue(t)!;

    /// <summary>Connect a raw loopback socket, speak the transport's Greet frame,
    /// and return the host-assigned id once <see cref="ITransportSite.OnPeerJoined"/>
    /// fired, plus the client <see cref="TcpClient"/> (kept alive so the test can
    /// read a response back — or deliberately never read it to simulate a stalled
    /// peer). When <paramref name="tinyReceiveWindow"/> is set, the client's
    /// receive buffer is shrunk <em>before connecting</em> so the OS can't
    /// autotune it away — the server-side writer then genuinely blocks against a
    /// full window, which the overflow/disconnect policy needs to engage.</summary>
    private static (ConnectionId Id, TcpClient Client) ConnectRawPeer(
        TcpTransport host, Site site, string name, bool tinyReceiveWindow = false)
    {
        var raw = new TcpClient();
        raw.NoDelay = true;
        if (tinyReceiveWindow) raw.ReceiveBufferSize = 1024;   // before Connect so it sticks
        raw.Connect("127.0.0.1", host.BoundPort);
        var ns = raw.GetStream();
        var body = Encoding.UTF8.GetBytes(name);
        var greet = new byte[1 + 4 + body.Length];
        greet[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(greet.AsSpan(1), (uint)body.Length);
        body.CopyTo(greet.AsSpan(5));
        ns.Write(greet);
        ns.Flush();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            lock (site.Joined) { if (site.Joined.Count > 0) return (site.Joined[^1], raw); }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined for raw peer");
            Thread.Sleep(5);
        }
    }

    /// <summary>Set the queue's overflow-disconnect timer small and capacity tiny
    /// for a fast, deterministic test, and restore them afterwards (friend access
    /// via <c>InternalsVisibleTo</c> on Allegiance.Core). The flood in the stall
    /// test far exceeds any OS loopback buffering, so the writer is guaranteed to
    /// block and the queue stays over this tiny capacity the whole time — the
    /// overflow timer then accumulates deterministically.</summary>
    private static IDisposable ShortenOverflowTimer()
    {
        var origT = TcpTransport.OverflowTimeout;
        var origN = TcpTransport.NoWriteProgressTimeout;
        var origC = TcpTransport.CapacityBytes;
        TcpTransport.OverflowTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.NoWriteProgressTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.CapacityBytes = 64 * 1024;
        return new Restorer(() =>
        {
            TcpTransport.OverflowTimeout = origT;
            TcpTransport.NoWriteProgressTimeout = origN;
            TcpTransport.CapacityBytes = origC;
        });
    }

    private sealed class Restorer : IDisposable
    {
        private readonly Action _a;
        public Restorer(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>(a) Branch a — the peer's id is no longer in <c>_peers</c> (its read
    /// loop already reaped it) but the caller still has the id. Old behaviour
    /// threw <c>InvalidOperationException("no such peer")</c>, which would have
    /// killed the server; the fix drops the packet silently.</summary>
    [Fact]
    public void Host_send_to_a_reaped_peer_id_does_not_throw()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "GONE");
            var peers = Peers(host);
            Assert.True(peers.Contains(id), "peer must be present in _peers before the simulated reap");
            peers.Remove(id);
            host.Send(id, new byte[] { 1, 2, 3 }, guaranteed: true);   // must NOT throw
            client.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(b)/(f) A stalled, never-reading peer (its socket never drained, so
    /// the writer thread is blocked against a full send buffer) does not block
    /// <see cref="TcpTransport.Send"/>, and is eventually disconnected rather than
    /// accumulating an unbounded backlog. Send after the peer is already gone must
    /// also stay silent.</summary>
    [Fact]
    [Trait("Tier", "Perf")]
    public void Stalled_non_reading_peer_does_not_block_and_is_disconnected()
    {
        using var shrink = ShortenOverflowTimer();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "STALL", tinyReceiveWindow: true);
            Assert.True(Peers(host).Contains(id), "peer must be present");

            var big = new byte[1 << 16]; // 64 KiB, guaranteed
            // Flood the stalled peer far beyond the tiny test capacity so the
            // writer is guaranteed blocked (or at least unable to drain below
            // capacity), regardless of how much the OS loopback silently buffers.
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 400; i++)
                host.Send(id, big, guaranteed: true);   // must never block, never throw
            sw.Stop();

            // Non-blocking: 400 x 64KiB to a peer that never reads. If Send did the
            // socket write synchronously (the pre-P311 bug) this would block for
            // many seconds; a queueing Send returns in well under a second.
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"Send to a stalled peer took {sw.ElapsedMilliseconds} ms for 400 frames — it must not block");

            // Bounded backlog + overflow policy: keep enqueuing (as the sim thread
            // does every tick in production) so the overflow timer keeps advancing,
            // and confirm the stalled peer is disconnected outright rather than
            // accumulating an unbounded queue forever.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, big, guaranteed: true);
                Thread.Sleep(10);
            }
            Assert.True(site.HasLeft(id, 0), "stalled peer must eventually be disconnected");
            Assert.False(Peers(host).Contains(id), "disconnected peer must be reaped from _peers");

            // Post-disconnect sends stay silent (P60).
            host.Send(id, big, guaranteed: true);

            client.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>P619c — a connection the server marked as a HUMAN client
    /// (via <see cref="TcpTransport.SetPeerHuman"/>, done at logon for a
    /// non-harness character name) gets the longer
    /// <see cref="TcpTransport.HumanGraceTimeout"/> and is NOT overflow-
    /// disconnected: a real player whose machine hiccups must survive a stall
    /// that would cut a bot. Here the bot's strict limits are shrunk to 300 ms
    /// while the human grace stays generous (30 s); a stalled never-reading
    /// human survives past 2 s even though an unmarked peer with these settings
    /// is disconnected in well under a second.</summary>
    [Fact]
    public void Human_marked_peer_survives_the_bot_no_write_progress_window()
    {
        var origH = TcpTransport.HumanGraceTimeout;
        var origN = TcpTransport.NoWriteProgressTimeout;
        var origO = TcpTransport.OverflowTimeout;
        var origC = TcpTransport.CapacityBytes;
        try
        {
            TcpTransport.NoWriteProgressTimeout = TimeSpan.FromMilliseconds(300); // strict bot limit
            TcpTransport.HumanGraceTimeout = TimeSpan.FromSeconds(30);            // generous human grace
            TcpTransport.OverflowTimeout = TimeSpan.FromMilliseconds(300);
            TcpTransport.CapacityBytes = 64 * 1024;

            var site = new Site();
            var host = NewHost(site);
            try
            {
                host.Host(0);
                var (id, client) = ConnectRawPeer(host, site, "Domingo", tinyReceiveWindow: true);
                host.SetPeerHuman(id, true);     // the server marks a non-harness name as human

                var big = new byte[1 << 16];     // 64 KiB guaranteed
                for (int i = 0; i < 200; i++) host.Send(id, big, guaranteed: true);

                // An UNMARKED peer here is disconnected within ~(300ms progress +
                // 300ms overflow) < 1 s. The human must survive far past that.
                bool left = false;
                var start = DateTime.UtcNow;
                while ((DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(2000))
                {
                    host.Send(id, big, guaranteed: true);
                    Thread.Sleep(50);
                    if (site.HasLeft(id, 0)) { left = true; break; }
                }
                Assert.False(left,
                    "a marked human peer must survive well past the bot's strict no-write-progress/overflow window");
                client.Dispose();
            }
            finally { host.Shutdown(); }
        }
        finally
        {
            TcpTransport.NoWriteProgressTimeout = origN;
            TcpTransport.HumanGraceTimeout = origH;
            TcpTransport.OverflowTimeout = origO;
            TcpTransport.CapacityBytes = origC;
        }
    }

    /// <summary>P619c — the other half: a peer NEVER marked human (an MCP bot, or
    /// any unmasked connection) keeps the strict P611 policy and IS disconnected
    /// on a genuine stall — the human grace must not weaken the bot path.</summary>
    [Fact]
    public void Unmarked_bot_peer_is_still_disconnected_on_stall()
    {
        using var shrink = ShortenOverflowTimer();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            // Deliberately NOT marked human (no SetPeerHuman) — the strict path.
            var (id, client) = ConnectRawPeer(host, site, "mcp-b1", tinyReceiveWindow: true);
            var big = new byte[1 << 16];
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, big, guaranteed: true);
                Thread.Sleep(10);
            }
            Assert.True(site.HasLeft(id, 0),
                "an unmasked bot peer must still be disconnected on stall (strict P611 policy preserved)");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>(c) A healthy host send still emits its frame — the queue must not
    /// quietly drop legitimate sends, only the doomed ones.</summary>
    [Fact]
    public void Healthy_host_send_still_arrives_on_the_wire()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "OK");
            var sent = new byte[] { 9, 9, 9 };
            host.Send(id, sent, guaranteed: true);

            var ns = client.GetStream();
            // Read the framed response back on the raw socket: 1 type + 4 length + body.
            var hdr = new byte[5];
            ReadExactly(ns, hdr);
            Assert.Equal(0x02, hdr[0]);                                   // Data frame type
            Assert.Equal((uint)sent.Length, BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1)));
            var body = new byte[sent.Length];
            ReadExactly(ns, body);
            Assert.Equal(sent, body);                                     // exact bytes on the wire
            client.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(e) Multiple sends to one peer arrive in the exact order sent — the
    /// queue must not reorder per-peer traffic (requirement 4 of P311).</summary>
    [Fact]
    public void Multiple_sends_to_one_peer_preserve_fifo_order()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "ORDER");
            var ns = client.GetStream();

            const int n = 50;
            var bodies = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                bodies[i] = Enumerable.Range(0, 8).Select(_ => (byte)(i + 1)).ToArray();
                host.Send(id, bodies[i], guaranteed: true);
            }

            for (int i = 0; i < n; i++)
            {
                var hdr = new byte[5];
                ReadExactly(ns, hdr);
                Assert.Equal(0x02, hdr[0]);
                Assert.Equal((uint)bodies[i].Length, BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1)));
                var body = new byte[bodies[i].Length];
                ReadExactly(ns, body);
                Assert.Equal(bodies[i], body);
            }
            client.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(d) The client-role "not connected" branch must stay loud: a
    /// <see cref="TcpTransport"/> that was never Host()/Join()ed must still throw
    /// when asked to Send — that firing is a real usage bug, not a network race.</summary>
    [Fact]
    public void Client_role_Send_before_join_still_throws()
    {
        var t = new TcpTransport(new Site(), _mode);   // neither Host nor Join
        Assert.Throws<InvalidOperationException>(
            () => t.Send(ConnectionId.Server, new byte[] { 1 }, guaranteed: true));
    }

    private static void ReadExactly(Stream s, Span<byte> dst)
    {
        int off = 0;
        while (off < dst.Length)
        {
            int n = s.Read(dst.Slice(off, dst.Length - off));
            if (n <= 0) throw new EndOfStreamException("no data");
            off += n;
        }
    }
}

/// <summary>P605 — the send/drop-safety suite under thread-per-peer (today's
/// default) transport mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportSendDropSafetyThreadPerPeerTests : TcpTransportSendDropSafetyTestsBase
{
    public TcpTransportSendDropSafetyThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }
}

/// <summary>P605 — the send/drop-safety suite under pool (fixed send-worker
/// pool) transport mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportSendDropSafetyPoolTests : TcpTransportSendDropSafetyTestsBase
{
    public TcpTransportSendDropSafetyPoolTests() : base(TransportMode.Pool) { }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P671 — failing-first tests for the SPLIT server read bounds. Before P671 the
/// host measured ONE deadline (DataFrameReadTimeout) from before the header read,
/// so it measured IDLE time: a peer that simply sent nothing for 30 s was dropped
/// as link-dead, even a healthy-but-silent (docked/staged) client. P671 splits
/// it into two bounds:
///   * <see cref="TcpTransport.DataLinkIdleTimeout"/> — a peer that sends NO
///     bytes at all is dropped as link-dead (native g_sDeadTime = 30,
///     clintlib.cpp:2047). A healthy client never trips it because it pings
///     every 6 s.
///   * <see cref="TcpTransport.DataFrameReadTimeout"/> — bounds a frame only
///     once it has STARTED (first header byte seen), so a header-then-stall
///     attacker is dropped, while the silence between frames is never blamed on
///     a stalled frame.
/// </summary>
[Collection("LiveNetwork")]
public class P671KeepAliveTransportTests
{
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

    private sealed class Restorer : IDisposable
    {
        private readonly Action _a;
        public Restorer(Action a) => _a = a;
        public void Dispose() => _a();
    }

    private static TcpClient Connect(ushort port)
    {
        var c = new TcpClient();
        c.NoDelay = true;
        c.Connect(IPAddress.Loopback, port);
        return c;
    }

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

    /// <summary>Send only a DATA frame HEADER (type + declared body length), then
    /// stop — the header-then-stall attack. The declared length is under the
    /// server cap so it is not rejected on the length check; it simply never
    /// completes.</summary>
    private static void SendDataHeaderThenStall(TcpClient raw, uint bodyLen)
    {
        var hdr = new byte[5];
        hdr[0] = 0x02;                                    // FrameType.Data
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(1), bodyLen);
        raw.GetStream().Write(hdr, 0, hdr.Length);
        raw.GetStream().Flush();
    }

    private static ConnectionId AwaitJoin(Site site, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            lock (site.Joined) { if (site.Joined.Count > 0) return site.Joined[^1]; }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined");
            Thread.Sleep(5);
        }
    }

    /// <summary>Wait for the host to close the connection (orderly FIN) within
    /// <paramref name="ms"/>. Throws if it never closes.</summary>
    private static void AwaitDropped(TcpClient raw, int ms)
    {
        raw.ReceiveTimeout = ms;
        var ns = raw.GetStream();
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        var buf = new byte[1];
        while (DateTime.UtcNow < deadline)
        {
            int n = ns.Read(buf, 0, 1);
            if (n == 0) return;                            // server closed -> dropped
            throw new Exception("unexpected data from host");
        }
        throw new TimeoutException("connection was not dropped by the host");
    }

    /// <summary>
    /// A peer that GREETS and then sends nothing at all is dropped by the IDLE
    /// timeout (DataLinkIdleTimeout), not left hanging. Fails on pre-P671 code:
    /// the only bound there was DataFrameReadTimeout (30 s), so a silent peer
    /// was NOT dropped within the tight idle window — the connection lingered.
    /// </summary>
    [Fact]
    public void SilentPeer_IsDroppedBy_IdleTimeout()
    {
        var savedIdle = TcpTransport.DataLinkIdleTimeout;
        var savedFrame = TcpTransport.DataFrameReadTimeout;
        TcpTransport.DataLinkIdleTimeout = TimeSpan.FromSeconds(2);
        TcpTransport.DataFrameReadTimeout = TimeSpan.FromSeconds(60);  // frame bound must NOT be what drops it
        var site = new Site();
        var host = new TcpTransport(site);
        try
        {
            host.Host(0);
            var raw = Connect(host.BoundPort);
            SendGreet(raw, "SILENT");
            AwaitJoin(site, 3000);
            Assert.Equal(1, site.JoinedCount());

            // No further bytes are sent. The idle bound (2 s) must reap it.
            AwaitDropped(raw, 6000);
            Assert.True(SpinWait.SpinUntil(() => site.LeftCount() >= 1, 2000),
                "silent peer must be dropped by the idle timeout");
            raw.Dispose();
        }
        finally
        {
            host.Shutdown();
            TcpTransport.DataLinkIdleTimeout = savedIdle;
            TcpTransport.DataFrameReadTimeout = savedFrame;
        }
    }

    /// <summary>
    /// A peer that sends a frame HEADER then stops is dropped by the FRAME
    /// deadline (DataFrameReadTimeout), even though the idle timeout is far
    /// longer — the frame deadline is what bounds a header-then-stall attacker
    /// (P654 H-02). Fails on pre-P671 code when tightened: the old single
    /// deadline was the frame deadline, but measured from before the header, so
    /// a header-then-stall did not meet a 2 s bound the way the new one does.
    /// </summary>
    [Fact]
    public void HeaderThenStallPeer_IsDroppedBy_FrameDeadline()
    {
        var savedIdle = TcpTransport.DataLinkIdleTimeout;
        var savedFrame = TcpTransport.DataFrameReadTimeout;
        TcpTransport.DataLinkIdleTimeout = TimeSpan.FromSeconds(60);  // idle must NOT be what drops it
        TcpTransport.DataFrameReadTimeout = TimeSpan.FromSeconds(2);
        var site = new Site();
        var host = new TcpTransport(site);
        try
        {
            host.Host(0);
            var raw = Connect(host.BoundPort);
            SendGreet(raw, "STALL");
            AwaitJoin(site, 3000);
            Assert.Equal(1, site.JoinedCount());

            SendDataHeaderThenStall(raw, 512);               // header only, body never arrives
            AwaitDropped(raw, 6000);                         // frame deadline (2 s) reaps it
            Assert.True(SpinWait.SpinUntil(() => site.LeftCount() >= 1, 2000),
                "header-then-stall peer must be dropped by the frame deadline");
            raw.Dispose();
        }
        finally
        {
            host.Shutdown();
            TcpTransport.DataLinkIdleTimeout = savedIdle;
            TcpTransport.DataFrameReadTimeout = savedFrame;
        }
    }

    /// <summary>
    /// A peer that sends a VALID complete frame, idles shorter than the idle
    /// bound, then sends another valid frame must NOT be dropped — the two
    /// bounds must not mis-fire on legitimate traffic (the healthy-client path
    /// that a 6 s keep-alive maintains indefinitely).
    /// </summary>
    [Fact]
    [Trait("Tier", "Live")]
    public void PeriodicHealthyPeer_IsNotDropped()
    {
        var savedIdle = TcpTransport.DataLinkIdleTimeout;
        var savedFrame = TcpTransport.DataFrameReadTimeout;
        TcpTransport.DataLinkIdleTimeout = TimeSpan.FromSeconds(5);
        TcpTransport.DataFrameReadTimeout = TimeSpan.FromSeconds(3);
        var site = new Site();
        var host = new TcpTransport(site);
        try
        {
            host.Host(0);
            var raw = Connect(host.BoundPort);
            SendGreet(raw, "HEALTHY");
            AwaitJoin(site, 3000);

            // Send small complete data frames periodically, each quicker than
            // both bounds, for well past the idle bound (so a broken idle
            // measurement would have reaped it).
            for (int i = 0; i < 12; i++)
            {
                var frame = new byte[1 + 4 + 8];
                frame[0] = 0x02;
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), 8);
                raw.GetStream().Write(frame, 0, frame.Length);
                raw.GetStream().Flush();
                Thread.Sleep(700);
            }
            Assert.Equal(0, site.LeftCount());               // never dropped
            raw.Dispose();
        }
        finally
        {
            host.Shutdown();
            TcpTransport.DataLinkIdleTimeout = savedIdle;
            TcpTransport.DataFrameReadTimeout = savedFrame;
        }
    }
}

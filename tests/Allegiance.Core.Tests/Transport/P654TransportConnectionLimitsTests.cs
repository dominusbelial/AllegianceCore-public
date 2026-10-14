using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;
using SrvHost = global::Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P654 — failing-first regression tests for the transport-side connection and
/// traffic hardening:
///   H-02  pre-logon memory exhaustion: server-inbound frame cap (a peer can no
///         longer claim a huge body up front) + the in-progress read deadline
///         drops a header-then-stall connection;
///   H-15  connection / per-IP / pending-handshake caps;
///   H-14  ClosePeer (the LOGOFF connection close that stops LOGONREQ re-arm);
///   H-21  the frame cap also bounds the per-frame CRC work (covered by H-02);
///   H-34  Drain must not lose sibling events when one handler throws.
/// Each attack is proven to fail while the matching normal action still works.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P654TransportConnectionLimitsTests
{
    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = new();
        public readonly List<ConnectionId> Left = new();
        public readonly List<(ConnectionId From, byte[] Data)> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.Add(id);
        public void OnPeerLeft(ConnectionId id) => Left.Add(id);
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Received.Add((from, data.ToArray()));
    }

    private static TcpClient Connect(ushort port)
    {
        var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        return c;
    }

    private static void WriteFrame(TcpClient c, byte type, byte[] body)
    {
        var frame = new byte[1 + 4 + body.Length];
        frame[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)body.Length);
        body.CopyTo(frame, 5);
        c.GetStream().Write(frame, 0, frame.Length);
    }

    [Fact]
    public void OversizedServerFrame_IsRejected_NormalFrame_Delivered()
    {
        var savedCap = TcpTransport.ServerInboundMaxFrame;
        var savedTo = TcpTransport.DataFrameReadTimeout;
        TcpTransport.ServerInboundMaxFrame = 1024;
        TcpTransport.DataFrameReadTimeout = TimeSpan.FromSeconds(5);
        var site = new Site();
        var t = new TcpTransport(site);
        try
        {
            t.Host();
            using (var c = Connect(t.BoundPort))
            {
                WriteFrame(c, 0x01, Encoding.ASCII.GetBytes("peer"));         // greet ok
                Thread.Sleep(200);
                WriteFrame(c, 0x02, new byte[8]);              // a normal small data frame
                SpinWait.SpinUntil(() => site.Received.Count >= 1, 2000);
                // The oversize claim is the ATTACK: a frame header declaring
                // more than the server cap must be rejected before allocating.
                using (var c2 = Connect(t.BoundPort))
                {
                    WriteFrame(c2, 0x01, Encoding.ASCII.GetBytes("peer2"));
                    Thread.Sleep(200);
                    var hdr = new byte[5];
                    hdr[0] = 0x02;
                    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(1), 65536);
                    c2.GetStream().Write(hdr, 0, hdr.Length);
                    c2.GetStream().Write(new byte[3], 0, 3);
                    bool dropped = SpinWait.SpinUntil(() => site.Left.Count >= 1, 3000);
                    Assert.True(dropped, "oversized-frame peer should be dropped (H-02 cap)");
                }
            }
            Assert.True(site.Received.Count >= 1, "a normal in-cap frame must be delivered");
        }
        finally
        {
            t.Shutdown();
            TcpTransport.ServerInboundMaxFrame = savedCap;
            TcpTransport.DataFrameReadTimeout = savedTo;
        }
    }

    [Fact]
    public void ConnectionCaps_RefuseOverLimit_NormalJoinsWork()
    {
        var savedTotal = TcpTransport.MaxConnections;
        var savedIp = TcpTransport.MaxConnectionsPerIp;
        var savedPend = TcpTransport.MaxPendingHandshake;
        TcpTransport.MaxConnections = 10;
        TcpTransport.MaxConnectionsPerIp = 2;
        TcpTransport.MaxPendingHandshake = 2;
        var site = new Site();
        var t = new TcpTransport(site);
        try
        {
            t.Host();
            using var c1 = Connect(t.BoundPort);
            WriteFrame(c1, 0x01, Encoding.ASCII.GetBytes("a"));
            Thread.Sleep(150);
            using var c2 = Connect(t.BoundPort);
            WriteFrame(c2, 0x01, Encoding.ASCII.GetBytes("b"));
            Thread.Sleep(150);
            using (var c3 = Connect(t.BoundPort))
            {
                try { WriteFrame(c3, 0x01, Encoding.ASCII.GetBytes("c")); } catch { }
                Thread.Sleep(300);
            }
            Assert.Equal(2, site.Joined.Count);   // only the two in-cap peers joined
            WriteFrame(c1, 0x02, Encoding.ASCII.GetBytes("hello"));
            SpinWait.SpinUntil(() => site.Received.Count >= 1, 2000);
            Assert.True(site.Received.Count >= 1, "an in-cap peer's data frame must be delivered");
        }
        finally
        {
            t.Shutdown();
            TcpTransport.MaxConnections = savedTotal;
            TcpTransport.MaxConnectionsPerIp = savedIp;
            TcpTransport.MaxPendingHandshake = savedPend;
        }
    }

    [Fact]
    public void ClosePeer_FiresOnPeerLeft_Once()
    {
        var site = new Site();
        var t = new TcpTransport(site);
        try
        {
            t.Host();
            using (var c = Connect(t.BoundPort))
            {
                WriteFrame(c, 0x01, Encoding.ASCII.GetBytes("peer"));
                SpinWait.SpinUntil(() => site.Joined.Count >= 1, 2000);
                Assert.Single(site.Joined);
                var id = site.Joined[0];
                t.ClosePeer(id);   // H-14: the server proactively closes the peer
                SpinWait.SpinUntil(() => site.Left.Count >= 1, 3000);
                Assert.Single(site.Left);
                Assert.Equal(id, site.Left[0]);
            }
            if (site.Joined.Count > 0) t.ClosePeer(site.Joined[0]);   // no-op
        }
        finally { t.Shutdown(); }
    }

    [Fact]
    public void Drain_HandlerThrow_DoesNotLoseSiblingEvents()
    {
        // P654 (H-34): a handler that throws for one event must not abort the
        // whole drain and drop the sibling events already pulled into the lanes.
        var host = new SrvHost();
        var peer = new Allegiance.Core.Transport.ConnectionId(4);
        byte[] frame = new byte[8];
        ((ITransportSite)host).OnReceive(peer, frame);
        ((ITransportSite)host).OnReceive(peer, frame);
        int handled = 0;
        host.Drain(e => { handled++; throw new InvalidOperationException("boom"); });
        Assert.Equal(2, handled);   // both events handled despite the throw
    }
}

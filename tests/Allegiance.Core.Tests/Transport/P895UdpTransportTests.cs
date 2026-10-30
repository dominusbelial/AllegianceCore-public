using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TransportProof;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P895UdpTransportTests
{
    private sealed class Site : ITransportSite
    {
        public readonly ConcurrentQueue<(ConnectionId Id, string Name)> Joins = new();
        public readonly ConcurrentQueue<byte[]> Data = new();
        public readonly ConcurrentQueue<string> Events = new();
        public int Lost, Left;
        public void OnPeerJoined(ConnectionId id, string name) { Events.Enqueue("join"); Joins.Enqueue((id, name)); }
        public void OnPeerLeft(ConnectionId id) => Interlocked.Increment(ref Left);
        public void OnSessionLost() => Interlocked.Increment(ref Lost);
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { Events.Enqueue("receive"); Data.Enqueue(data.ToArray()); }
    }
    private static void Until(Func<bool> done, int ms = 8000)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < ms) Thread.Sleep(5);
        Assert.True(done(), $"condition timed out after {ms} ms");
    }
    private static byte[] Value(int value)
    { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    private static int Number(byte[] data) => BinaryPrimitives.ReadInt32LittleEndian(data);

    // Same native game-flow sequence as P888. Real UDP loss, reorder and duplicates;
    // guaranteed-coalesced poses have independent sequencing from ordered flow.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Game_flow_stays_ordered_with_five_percent_loss_and_pose_backlog(bool park)
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs) { ReliableOneShot = park };
        host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort);
        using var client = new UdpTransport(cs);
        client.Join("127.0.0.1", proxy.Port, "P895-flow");
        Until(() => hs.Joins.Count == 1);
        var id = hs.Joins.Single().Id;
        var sides = Enumerable.Repeat(default(SideEndgameInfo) with { SideName = "" }, GameOver.MaxSides).ToArray();
        IFedMessage[] flow =
        [
            new MissionStageMessage { Stage = MissionStageMessage.StageStarting },
            new SetStartTime { TimeStart = 15000 },
            new MissionStageMessage { Stage = MissionStageMessage.StageStarted },
            new GameOver { Sides = sides, SideWinner = 0, GameoverMessage = "first result" },
            new GameOverPlayers { Entries = [new("pilot", 0, 0, 1, Rescues: 1)] },
            new MissionStageMessage { Stage = MissionStageMessage.StageOver },
            new MissionDef { Stage = MissionStageMessage.StageNotStarted },
            new JoinedMission { ShipId = 2 },
            new MissionStageMessage { Stage = MissionStageMessage.StageNotStarted },
            new SetStartTime { TimeStart = 17000 },
            new MissionStageMessage { Stage = MissionStageMessage.StageStarting },
            new SetStartTime { TimeStart = 32000 },
        ];
        var expected = flow.Select(m => FedPacket.Pack([m.Encode()])).ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            for (int j = 0; j < 50; j++) host.SendCoalescing(id, new byte[32], 5);
            host.Send(id, expected[i], true);
            host.SendGuaranteedCoalescing(id, new byte[32], 6);
        }
        Until(() => cs.Data.Count(d => d.Length != 32) == expected.Length);
        Assert.Equal(expected, cs.Data.Where(d => d.Length != 32).ToArray());
        Assert.True(proxy.Dropped > 0);
        Assert.Equal("join", hs.Events.First());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Latest_pending_key_wins_and_reliable_latest_survives_blackout(bool guaranteed)
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort, loss: 0, delayMs: 0, jitterMs: 0, duplicate: false);
        using var client = new UdpTransport(cs);
        client.Join("127.0.0.1", proxy.Port, "P895-latest");
        Until(() => hs.Joins.Count == 1);
        proxy.DropServerData = true;
        var id = hs.Joins.Single().Id;
        for (int i = 1; i <= 500; i++)
        {
            if (guaranteed) host.SendGuaranteedCoalescing(id, Value(i), 10);
            else host.SendCoalescing(id, Value(i), 10);
        }
        Thread.Sleep(80); // packets are consumed/dropped by the proxy, never queued behind the release
        Assert.Empty(cs.Data);
        proxy.DropServerData = false;
        if (!guaranteed) host.SendCoalescing(id, Value(500), 10); // best effort has no retransmission
        Until(() => !cs.Data.IsEmpty);
        Assert.All(cs.Data, data => Assert.Equal(500, Number(data)));
        Until(() => host.SamplePeerQueues().TotalQueuedBytes == 0);
    }

    [Fact]
    public void Ordered_and_keyed_large_frames_fragment_reassemble_with_loss()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort, seed: 896);
        using var client = new UdpTransport(cs);
        client.Join("127.0.0.1", proxy.Port, "P895-fragment");
        Until(() => hs.Joins.Count == 1);
        var data = new byte[96 * 1024 + 17]; new Random(895).NextBytes(data);
        host.Send(hs.Joins.Single().Id, data, true);
        Until(() => cs.Data.Count == 1);
        Assert.Equal(data, cs.Data.Single());
        host.SendGuaranteedCoalescing(hs.Joins.Single().Id, data, 17);
        Until(() => cs.Data.Count == 2);
        Assert.Equal(data, cs.Data.Last());
        Assert.True(proxy.Packets > 170);
    }

    [Fact]
    public void Unhandshaken_spoofed_and_replayed_packets_cannot_trigger_world_reply()
    {
        var hs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        raw.ReceiveTimeout = 300;
        raw.Connect(IPAddress.Loopback, host.BoundPort);
        var hello = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(hello, 0x31554441); hello[4] = 1;
        raw.Send(hello);
        var reply = new byte[1500]; int n = raw.Receive(reply);
        Assert.Equal(64, n); Assert.Equal(2, reply[4]);
        Assert.Empty(hs.Joins); Assert.Equal(0, host.SamplePeerQueues().Peers);
        var forged = reply[..64]; forged[4] = 3; forged[32] ^= 0x80;
        raw.Send(forged);
        Assert.Throws<SocketException>(() => raw.Receive(reply));
        var fakeData = new byte[1200]; BinaryPrimitives.WriteUInt32LittleEndian(fakeData, 0x31554441); fakeData[4] = 5;
        raw.Send(fakeData);
        Assert.Throws<SocketException>(() => raw.Receive(reply));
        // A cookie is bound to the return endpoint: another socket cannot redeem it.
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        other.ReceiveTimeout = 300; other.Connect(IPAddress.Loopback, host.BoundPort);
        forged = reply[..64]; forged[4] = 3;
        other.Send(forged);
        Assert.Throws<SocketException>(() => other.Receive(reply));
        Assert.Empty(hs.Joins);
        // Redeem once at the original endpoint, then retire it. A replay must
        // not recreate the peer even though the cookie's time bucket is valid.
        var valid = reply[..64]; valid[4] = 3;
        raw.Send(valid); Assert.Equal(64, raw.Receive(reply));
        Until(() => host.SamplePeerQueues().Peers == 1);
        host.ClosePeer(new ConnectionId(1));
        // The accepted endpoint may already have a small keepalive queued.
        // Drain through the close notification before testing for a fresh reply.
        int closeSize;
        do { closeSize = raw.Receive(reply); } while (reply[4] != 9);
        Assert.Equal(UdpTransport.Header, closeSize);
        raw.Send(valid);
        Assert.Throws<SocketException>(() => raw.Receive(reply));
        Assert.Equal(0, host.SamplePeerQueues().Peers);
    }

    [Fact]
    public void Superseding_unacked_poses_still_obeys_congestion_window()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort, loss: 0, delayMs: 0, jitterMs: 0, duplicate: false);
        using var client = new UdpTransport(cs); client.Join("127.0.0.1", proxy.Port, "P895-congestion");
        Until(() => hs.Joins.Count == 1);
        proxy.DropAcks = true;
        long before = host.TotalWrittenBytes;
        for (int i = 0; i < 100; i++)
        {
            host.SendCoalescing(hs.Joins.Single().Id, new byte[1000], 1);
            Thread.Sleep(10);
        }
        long bytes = host.TotalWrittenBytes - before;
        // Superseding payloads must retain their congestion debt until ACK/loss.
        // Without that rule this sends ~105 KB despite zero delivery feedback.
        Assert.InRange(bytes, 1000, 40_000);
    }

    [Fact]
    public void Reliable_overflow_closes_peer_and_best_effort_expires_after_500ms()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs) { MaxBufferedBytes = 1024 }; host.Host();
        using var client = new UdpTransport(cs); client.Join("127.0.0.1", host.BoundPort, "P895-cap");
        Until(() => hs.Joins.Count == 1);
        host.Send(hs.Joins.Single().Id, new byte[2048], true);
        Until(() => hs.Left == 1);
        Assert.True(host.IsPeerClosed(hs.Joins.Single().Id));
        using var host2 = new UdpTransport(new Site()); host2.Host();
        var receive = new Site();
        using var proxy = new ImpairedUdpProxy(host2.BoundPort, loss: 0, delayMs: 0, jitterMs: 0);
        using var client2 = new UdpTransport(receive); client2.Join("127.0.0.1", proxy.Port, "P895-expiry");
        // Client outbound maximum is 64 KiB; use host-to-client for a large incomplete best-effort frame.
        Until(() => host2.SamplePeerQueues().Peers == 1);
        proxy.DropServerData = true;
        host2.SendCoalescing(new ConnectionId(1), new byte[100_000], 1);
        Until(() => host2.SamplePeerQueues().TotalQueuedBytes == 0, 3000);
        proxy.DropServerData = false;
        Thread.Sleep(120);
        Assert.Empty(receive.Data);
    }

    [Fact]
    public void Key_zero_false_and_coalescing_zero_preserve_fifo()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort);
        using var client = new UdpTransport(cs); client.Join("127.0.0.1", proxy.Port, "P895-zero");
        Until(() => hs.Joins.Count == 1);
        var id = hs.Joins.Single().Id;
        host.Send(id, Value(1), false); host.SendCoalescing(id, Value(2), 0); host.SendGuaranteedCoalescing(id, Value(3), 0);
        Until(() => cs.Data.Count == 3);
        Assert.Equal(new[] { 1, 2, 3 }, cs.Data.Select(Number));
    }

    [Fact]
    public void Silence_times_out_once_and_fresh_socket_reconnects()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs) { IdleTimeout = TimeSpan.FromMilliseconds(400), KeepAliveInterval = TimeSpan.FromMilliseconds(50) }; host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort, loss: 0, delayMs: 0, jitterMs: 0);
        using var client = new UdpTransport(cs) { IdleTimeout = TimeSpan.FromMilliseconds(400), KeepAliveInterval = TimeSpan.FromMilliseconds(50) };
        client.Join("127.0.0.1", proxy.Port, "P895-old"); Until(() => hs.Joins.Count == 1);
        proxy.DropAll = true;
        Until(() => cs.Lost == 1 && hs.Left == 1);
        Assert.False(client.IsConnected);
        Thread.Sleep(100); Assert.Equal(1, cs.Lost);
        using var fresh = new UdpTransport(new Site()); fresh.Join("127.0.0.1", host.BoundPort, "P895-new");
        Until(() => hs.Joins.Count == 2);
        Assert.NotEqual(hs.Joins.First().Id, hs.Joins.Last().Id);
    }

    [Fact]
    public void One_way_silence_notifies_server_over_surviving_uplink()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs) { KeepAliveInterval = TimeSpan.FromMilliseconds(50) }; host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort, loss: 0, delayMs: 0, jitterMs: 0);
        using var client = new UdpTransport(cs) { IdleTimeout = TimeSpan.FromMilliseconds(400), KeepAliveInterval = TimeSpan.FromMilliseconds(50) };
        client.Join("127.0.0.1", proxy.Port, "P895-one-way"); Until(() => hs.Joins.Count == 1);
        proxy.DropDownstream = true;
        // Server still receives client keepalives; waiting for its ordinary
        // 30-second timeout would strand the old connection through a reconnect.
        Until(() => cs.Lost == 1 && hs.Left == 1);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Graceful_close_delivers_final_ordered_frame_before_loss()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort);
        using var client = new UdpTransport(cs); client.Join("127.0.0.1", proxy.Port, "P895-close");
        Until(() => hs.Joins.Count == 1);
        var id = hs.Joins.Single().Id;
        host.Send(id, Value(895), true); host.ClosePeerAfterFlush(id);
        Until(() => cs.Lost == 1);
        Assert.Equal(895, Number(Assert.Single(cs.Data)));
    }

    [Fact]
    public void Udp_preference_falls_back_to_tcp_without_session_loss()
    {
        var hs = new Site(); var cs = new Site();
        var host = new TcpTransport(hs, TransportMode.Pool, 1); host.Host();
        ITransport client = TransportFactory.CreateClient(cs, "udp");
        try
        {
            client.Join("127.0.0.1", host.BoundPort, "P895-fallback");
            Until(() => hs.Joins.Count == 1);
            client.Send(ConnectionId.Server, Value(895), true);
            Until(() => hs.Data.Count == 1);
            Assert.Equal(0, cs.Lost);
        }
        finally { client.Shutdown(); host.Shutdown(); }
    }

    [Fact]
    public void Ipv6_loopback_validates_endpoint_and_delivers_frames()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host(0, IPAddress.IPv6Loopback);
        using var client = new UdpTransport(cs); client.Join("::1", host.BoundPort, "P895-ipv6");
        Until(() => hs.Joins.Count == 1);
        var id = hs.Joins.Single().Id;
        Assert.True(host.IsPeerLoopback(id));
        host.Send(id, Value(6), true);
        Until(() => cs.Data.Count == 1);
        Assert.Equal(6, Number(cs.Data.Single()));
    }

    [Fact]
    public void Mixed_listener_uses_unique_peer_ids_and_routes_both_protocols()
    {
        var hs = new Site(); var ts = new Site(); var us = new Site();
        var tcp = new TcpTransport(hs, TransportMode.Pool, 1);
        var host = new DualTransport(hs, tcp, 0, null, null, new ServerCounters(), false);
        var tc = new TcpTransport(ts); using var uc = new UdpTransport(us);
        // Deterministically model a constructor keepalive racing Join's return.
        // The greeting must already own ordered frame zero when IsConnected is true.
        uc.AfterHandshakeForTesting = () => uc.Send(ConnectionId.Server, Value(42), true);
        try
        {
            host.Host(); tc.Join("127.0.0.1", host.BoundPort, "TCP"); uc.Join("127.0.0.1", host.UdpPort, "UDP");
            Until(() => hs.Joins.Count == 2 && hs.Data.Count == 1);
            Assert.Equal(42, Number(hs.Data.Single()));
            Assert.Equal("join", hs.Events.First());
            foreach (var join in hs.Joins) host.Send(join.Id, Value(join.Name == "TCP" ? 1 : 2), true);
            Until(() => ts.Data.Count == 1 && us.Data.Count == 1);
            Assert.Equal(1, Number(ts.Data.Single())); Assert.Equal(2, Number(us.Data.Single()));
            Assert.Equal(2, hs.Joins.Select(j => j.Id).Distinct().Count());
            Assert.All(hs.Joins, join => Assert.True(host.IsPeerLoopback(join.Id)));
        }
        finally { tc.Shutdown(); host.Shutdown(); }
    }
}

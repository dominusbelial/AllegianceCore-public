using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Transport;
using Allegiance.TransportProof;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P895bUdpOverheadTests
{
    private sealed class Site : ITransportSite
    {
        public Action<ConnectionId>? Joined;
        public Action<ConnectionId>? Received;
        public Action? Lost;
        public readonly ConcurrentQueue<byte[]> Data = new();
        public ConnectionId Peer;
        public void OnPeerJoined(ConnectionId id, string name) { Peer = id; Joined?.Invoke(id); }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { Data.Enqueue(data.ToArray()); Received?.Invoke(from); }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() => Lost?.Invoke();
    }
    private static void Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.ElapsedMilliseconds < 3000) Thread.Sleep(5);
        Assert.True(ready());
    }

    [Fact]
    public void Queued_frames_share_token_and_ack_without_waiting_for_more_frames()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        // The callback runs on the protocol owner, so these frames are all queued
        // before its next send turn without timing assumptions or lock reflection.
        hs.Joined = id => { for (int i = 0; i < 20; i++) host.Send(id, BitConverter.GetBytes(i), true); };
        using var client = new UdpTransport(cs);
        client.Join("127.0.0.1", host.BoundPort, "batch");
        Until(() => cs.Data.Count == 20 && host.SamplePeerQueues().TotalQueuedBytes == 0);
        Assert.Equal(Enumerable.Range(0, 20), cs.Data.Select(d => BitConverter.ToInt32(d)));
        var sent = host.SampleStatistics();
        Assert.Equal(1, sent.PacketCounts[5]);
        Assert.Equal(20, sent.DataRecords);
        Assert.InRange(sent.PacketBytes[5], 192, 200); // One shared token/header plus optional greeting ACK.
        Assert.Equal(0, sent.PacketCounts[6]); // Greeting ACK rode on the immediate data reply.
    }

    [Fact]
    public void Reply_piggybacks_selective_ack_and_idle_worker_has_no_poll_ticks()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs) { KeepAliveInterval = TimeSpan.FromSeconds(30) }; host.Host();
        using var client = new UdpTransport(cs) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        client.Join("127.0.0.1", host.BoundPort, "idle");
        Until(() => client.SamplePeerQueues().TotalQueuedBytes == 0);
        hs.Received = id => host.Send(id, new byte[] { 2 }, true);
        var before = host.SampleStatistics();
        client.Send(ConnectionId.Server, new byte[] { 1 }, true);
        Until(() => cs.Data.Count == 1 && host.SamplePeerQueues().TotalQueuedBytes == 0 && client.SamplePeerQueues().TotalQueuedBytes == 0);
        var reply = host.SampleStatistics().Since(before);
        Assert.Equal(1, reply.PacketCounts[5]);
        Assert.Equal(0, reply.PacketCounts[6]);
        Assert.Equal(1, reply.AckIdBytes);
        Thread.Sleep(30); // Let the final completion notification settle.
        var idle = client.SampleStatistics();
        Thread.Sleep(200);
        var delta = client.SampleStatistics().Since(idle);
        Assert.Equal(0, delta.WorkerTurns);
        Assert.Equal(0, delta.TimerWakes);
    }

    [Fact]
    public void Pacer_uses_deadline_and_enqueue_wakes_blocked_worker()
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs) { MaxBytesPerSecond = 1000, KeepAliveInterval = TimeSpan.FromSeconds(30) }; host.Host();
        using var client = new UdpTransport(cs) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        client.Join("127.0.0.1", host.BoundPort, "pacer");
        Until(() => client.SamplePeerQueues().TotalQueuedBytes == 0);
        var before = host.SampleStatistics();
        for (int i = 0; i < 5; i++) host.Send(hs.Peer, new byte[1000], true);
        Until(() => cs.Data.Count >= 2);
        var blocked = host.SampleStatistics();
        Thread.Sleep(100);
        Assert.InRange(host.SampleStatistics().WorkerTurns - blocked.WorkerTurns, 0, 5);
        Until(() => cs.Data.Count == 5 && host.SamplePeerQueues().TotalQueuedBytes == 0);
        Assert.InRange(host.SampleStatistics().TimerWakes - before.TimerWakes, 1, 20);
        Assert.Equal(5, cs.Data.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    public void Compact_records_preserve_full_key_range_and_fragment_boundaries(int key)
    {
        var hs = new Site(); var cs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var client = new UdpTransport(cs);
        client.Join("127.0.0.1", host.BoundPort, "records");
        Until(() => client.SamplePeerQueues().TotalQueuedBytes == 0);
        foreach (int size in new[] { 0, 1, 127, 128, 1144, 1145, 16384 })
        {
            var data = new byte[size]; new Random(size).NextBytes(data);
            host.SendGuaranteedCoalescing(hs.Peer, data, key);
            Until(() => !cs.Data.IsEmpty && host.SamplePeerQueues().TotalQueuedBytes == 0);
            Assert.True(cs.Data.TryDequeue(out var actual));
            Assert.Equal(data, actual);
        }
    }

    [Fact]
    public void Shutdown_and_dispose_remain_idempotent_after_blocking_receive()
    {
        using var host = new UdpTransport(new Site()); host.Host();
        var client = new UdpTransport(new Site());
        client.Join("127.0.0.1", host.BoundPort, "shutdown");
        client.Shutdown(); client.Shutdown();
        client.Dispose(); client.Dispose(); client.Shutdown();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Timeout_callback_can_dispose_transport_while_receiver_is_blocked()
    {
        using var host = new UdpTransport(new Site()) { KeepAliveInterval = TimeSpan.FromMilliseconds(20) }; host.Host();
        using var proxy = new ImpairedUdpProxy(host.BoundPort, loss: 0, delayMs: 0, jitterMs: 0, duplicate: false);
        var cs = new Site();
        using var client = new UdpTransport(cs)
        { IdleTimeout = TimeSpan.FromMilliseconds(150), KeepAliveInterval = TimeSpan.FromMilliseconds(20) };
        using var disposed = new ManualResetEventSlim();
        cs.Lost = () => { client.Dispose(); disposed.Set(); };
        client.Join("127.0.0.1", proxy.Port, "timer-shutdown");
        proxy.DropDownstream = true;
        Assert.True(disposed.Wait(3000));
        Assert.False(client.IsConnected);
        client.Shutdown();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Authenticated_malformed_compact_records_close_without_publishing_peer(int invalid)
    {
        var hs = new Site();
        using var host = new UdpTransport(hs); host.Host();
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        raw.ReceiveTimeout = 1000; raw.Connect(IPAddress.Loopback, host.BoundPort);
        var hello = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(hello, UdpTransport.Magic); hello[4] = 1;
        raw.Send(hello);
        var reply = new byte[1200]; Assert.Equal(64, raw.Receive(reply));
        reply[4] = 3; raw.Send(reply.AsSpan(0, 64));
        Assert.Equal(64, raw.Receive(reply)); Assert.Equal(4, reply[4]);
        byte[] record = invalid switch
        {
            0 => [1, 128], // Truncated key varint.
            1 => [1, 255, 255, 255, 255, 31, 0, 0], // Key exceeds 32 bits.
            2 => [1, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2, 0], // Sequence exceeds 64 bits.
            _ => [1, 0, 0, 129, 128, 128, 8], // Size exceeds 16 MiB before allocation.
        };
        var packet = new byte[UdpTransport.Header + record.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, UdpTransport.Magic);
        packet[4] = 5; packet[6] = 1;
        reply.AsSpan(32, 16).CopyTo(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), 1);
        record.CopyTo(packet, UdpTransport.Header); raw.Send(packet);
        Assert.Equal(UdpTransport.Header, raw.Receive(reply)); Assert.Equal(9, reply[4]);
        Assert.Equal(0, host.SamplePeerQueues().Peers);
        Assert.Empty(hs.Data);
        Assert.Equal(default, hs.Peer);
    }
}

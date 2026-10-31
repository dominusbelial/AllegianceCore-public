using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P670SendStallTests(ITestOutputHelper output)
{
    private sealed class Site : ITransportSite
    {
        internal readonly ConcurrentDictionary<string, ConnectionId> Joined = new();
        internal readonly ConcurrentDictionary<ConnectionId, byte> Left = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined[name] = id;
        public void OnPeerLeft(ConnectionId id) => Left[id] = 0;
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    private static (ConnectionId Id, TcpClient Client) Connect(TcpTransport host, Site site, string name, bool paused)
    {
        var client = new TcpClient { NoDelay = true };
        if (paused) client.ReceiveBufferSize = 1024;
        try
        {
            client.Connect(IPAddress.Loopback, host.BoundPort);
            byte[] body = Encoding.UTF8.GetBytes(name);
            byte[] greet = new byte[5 + body.Length];
            greet[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(greet.AsSpan(1), (uint)body.Length);
            body.CopyTo(greet, 5);
            client.GetStream().Write(greet);
            Assert.True(SpinWait.SpinUntil(() => site.Joined.ContainsKey(name), 10000), $"join {name}");
            return (site.Joined[name], client);
        }
        catch { client.Dispose(); throw; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Three_paused_readers_do_not_starve_sixteen_healthy_peers_on_one_worker_and_drop_for_no_progress(bool batchWrites)
    {
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.Pool, 1)
        { Egress = new EgressAccounting(), TcpBatchWrites = batchWrites };
        var slow = new List<(ConnectionId Id, TcpClient Client)>();
        var healthy = new List<(ConnectionId Id, TcpClient Client)>();
        TimeSpan previous = TcpTransport.NoWriteProgressTimeout;
        var peers = new PeerSendWindow[32];
        var workers = new SendWorkerWindow[1];
        try
        {
            TcpTransport.NoWriteProgressTimeout = TimeSpan.FromSeconds(2);
            host.Host(0);
            for (int i = 0; i < 3; i++) slow.Add(Connect(host, site, $"slow-{i}", true));
            for (int i = 0; i < 16; i++) healthy.Add(Connect(host, site, $"healthy-{i}", false));
            // All nineteen sockets share ONE worker. Paused readers' tiny advertised
            // receive windows fill while large guaranteed frames remain in flight.
            byte[] flood = new byte[2 * 1024 * 1024];
            foreach (var p in slow) host.Send(p.Id, flood, true);
            Thread.Sleep(200);
            SendWindow stalled = host.DrainSendWindow(peers, workers);
            Assert.True(stalled.DrainCollapsed);
            Assert.True(stalled.QueuedBytes > flood.Length);
            foreach (var p in peers.AsSpan(0, stalled.Peers))
                if (slow.Any(s => s.Id == p.Peer)) Assert.True(p.EnqueuedBytes > p.WrittenBytes);
            Assert.True(workers[0].WriteCalls > 0);
            Assert.True(workers[0].BusyWriteMs > 0);
            Assert.True(workers[0].LongestWriteMs > 0);

            byte[] payload = new byte[64];
            byte[] received = new byte[69];
            double maxLatency = 0;
            int rounds = 0;
            long healthyWritten = 0;
            var deadline = Stopwatch.StartNew();
            do
            {
                BinaryPrimitives.WriteInt32LittleEndian(payload, rounds);
                long start = Stopwatch.GetTimestamp();
                foreach (var p in healthy) host.Send(p.Id, payload, true);
                foreach (var p in healthy)
                {
                    p.Client.ReceiveTimeout = 1500;
                    p.Client.GetStream().ReadExactly(received);
                    Assert.Equal(2, received[0]);
                    Assert.Equal(payload.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(received.AsSpan(1)));
                    Assert.True(payload.AsSpan().SequenceEqual(received.AsSpan(5)), "framing/order/content");
                }
                double latency = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                maxLatency = Math.Max(maxLatency, latency);
                Assert.True(latency < 1500, $"healthy delivery took {latency:0.0} ms");
                foreach (var p in slow) host.SendCoalescing(p.Id, payload, 1);
                rounds++;
                var window = host.DrainSendWindow(peers, workers);
                for (int i = 0; i < window.Peers; i++)
                    if (healthy.Any(p => p.Id == peers[i].Peer)) healthyWritten += peers[i].WrittenBytes;
                Thread.Sleep(25);
            } while ((rounds < 40 || slow.Any(p => !site.Left.ContainsKey(p.Id))) && deadline.Elapsed < TimeSpan.FromSeconds(10));

            Assert.True(rounds >= 40);
            Assert.All(slow, p => Assert.True(site.Left.ContainsKey(p.Id), "paused reader was not dropped"));
            Assert.All(healthy, p => Assert.False(site.Left.ContainsKey(p.Id)));
            var reasons = host.DrainDisconnectCounts();
            Assert.Equal(3, reasons[DisconnectReason.NoWriteProgress]);
            Assert.Equal(3, reasons.Values.Sum());
            Assert.True(SpinWait.SpinUntil(() => host.TotalWrittenBytes >= healthyWritten, 1000));
            var final = host.DrainSendWindow(peers, workers);
            for (int i = 0; i < final.Peers; i++) healthyWritten += peers[i].WrittenBytes;
            Assert.Equal(16L * rounds * received.Length, healthyWritten);
            output.WriteLine($"P670 loopback: slow=3 healthy=16 workers=1 rounds={rounds} maxHealthyDeliveryMs={maxLatency:0.000} healthyWrittenBytes={healthyWritten} NoWriteProgress=3 initialQueuedBytes={stalled.QueuedBytes}");
        }
        finally
        {
            foreach (var p in slow.Concat(healthy)) p.Client.Dispose();
            host.Shutdown();
            TcpTransport.NoWriteProgressTimeout = previous;
        }
    }

    [Theory]
    [InlineData(TransportMode.ThreadPerPeer)]
    [InlineData(TransportMode.Pool)]
    public void Departed_peer_bytes_remain_in_host_window_totals(TransportMode mode)
    {
        var site = new Site();
        var host = new TcpTransport(site, mode, 1);
        try
        {
            host.Host(0);
            var peer = Connect(host, site, "departing", false);
            using var client = peer.Client;
            host.Send(peer.Id, new byte[64], true);
            client.ReceiveTimeout = 5000;
            client.GetStream().ReadExactly(new byte[69]);
            Assert.True(SpinWait.SpinUntil(() => host.TotalWrittenBytes == 69, 5000));
            host.ClosePeer(peer.Id);
            var window = host.DrainSendWindow(new PeerSendWindow[1], new SendWorkerWindow[1]);
            Assert.Equal(0, window.Peers);
            Assert.Equal(69, window.EnqueuedBytes);
            Assert.Equal(69, window.WrittenBytes);
            Assert.Equal(0, window.QueuedBytes);
            var next = host.DrainSendWindow(new PeerSendWindow[1], new SendWorkerWindow[1]);
            Assert.Equal(0, next.EnqueuedBytes);
            Assert.Equal(0, next.WrittenBytes);
        }
        finally { host.Shutdown(); }
    }

    [Fact]
    public void Partial_and_blocked_writes_count_only_socket_acceptance_and_keep_inflight_backlog()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        using var accepted = listener.AcceptTcpClient();
        listener.Stop();
        var type = typeof(TcpTransport).GetNestedType("PeerSender", BindingFlags.NonPublic)!;
        var sender = Activator.CreateInstance(type, accepted.GetStream(), accepted, (Action<DisconnectReason>)(_ => { }))!;
        var enqueue = type.GetMethod("Enqueue")!;
        var drain = type.GetMethod("TryWriteHead")!;
        var sample = type.GetMethod("DrainSendWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sink = type.GetField("SendOverride", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var counters = new SendCounters();
        type.GetField("Counters", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sender, counters);
        var id = new ConnectionId(1);
        PeerSendWindow Sample() => (PeerSendWindow)sample.Invoke(sender, [id])!;
        void Sink(Func<byte[], int, int, TcpTransport.SendOutcome> f) => sink.SetValue(sender, f);
        try
        {
            byte[] frame = P661EgressAccountingTests.Frame((176, 36));
            enqueue.Invoke(sender, [frame, true, 0]);
            Sink((_, _, _) => TcpTransport.SendOutcome.Sent(10));
            drain.Invoke(sender, null);
            var partial = Sample();
            Assert.Equal(frame.Length, partial.EnqueuedBytes);
            Assert.Equal(10, partial.WrittenBytes);
            Assert.Equal(frame.Length - 10, partial.QueuedBytes);
            Sink((_, _, _) => TcpTransport.SendOutcome.Blocked());
            drain.Invoke(sender, null);
            var blocked = Sample();
            Assert.Equal(0, blocked.EnqueuedBytes);
            Assert.Equal(0, blocked.WrittenBytes);
            Assert.Equal(partial.QueuedBytes, blocked.QueuedBytes);
            Sink((_, offset, count) =>
            {
                Assert.Equal(10, offset);
                return TcpTransport.SendOutcome.Sent(count);
            });
            drain.Invoke(sender, null);
            var complete = Sample();
            Assert.Equal(frame.Length - 10, complete.WrittenBytes);
            Assert.Equal(0, complete.QueuedBytes);
            Assert.Equal(frame.Length, counters.Written);
            Assert.Equal(frame.Length, counters.Enqueued);
        }
        finally { type.GetMethod("Close")!.Invoke(sender, null); }
    }

    [Fact]
    public void Worker_timings_include_active_writes_split_across_report_windows()
    {
        var diagnostics = new SendWorkerDiagnostics();
        diagnostics.BeginWrite();
        Thread.Sleep(20);
        var first = diagnostics.Drain(0);
        Assert.Equal(SendWorkerState.Writing, first.State);
        Assert.Equal(1, first.WriteCalls);
        Assert.True(first.CurrentWriteMs >= 10);
        Thread.Sleep(20);
        var second = diagnostics.Drain(0);
        Assert.True(second.CurrentWriteMs > first.CurrentWriteMs);
        Assert.True(second.BusyWriteMs < second.CurrentWriteMs);
        diagnostics.EndWrite();
        var last = diagnostics.Drain(0);
        Assert.Equal(SendWorkerState.Scanning, last.State);
        Assert.Equal(0, last.CurrentWriteMs);
        Assert.True(last.LongestWriteMs >= second.CurrentWriteMs);
        Assert.InRange(Math.Abs(first.BusyWriteMs + second.BusyWriteMs + last.BusyWriteMs - last.LongestWriteMs), 0, 0.001);
        Assert.Equal(0, diagnostics.Drain(0).BusyWriteMs);
    }

    [Fact]
    public void Stall_detector_requires_queue_growth_and_distinguishes_idle_shedding_and_rate_collapse()
    {
        var detector = new SendStallDetector();
        Assert.False(detector.Observe(5, 10000, 10000, 0));
        Assert.False(detector.Observe(5, 0, 0, 0)); // idle is not a stall
        Assert.False(detector.Observe(5, 10000, 0, 0)); // coalescing/shedding is not growth
        Assert.True(detector.Observe(5, 10000, 0, 10000));
        Assert.False(detector.Observe(5, 10000, 10000, 0)); // recovery
        Assert.True(detector.Observe(5, 2000, 2000, 1)); // prior drain rate collapsed
        Assert.False(detector.Observe(5, 10000, 5000, -1)); // draining backlog
    }

    [Fact]
    public void Diagnostic_updates_and_samples_allocate_zero_bytes_after_warmup()
    {
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.Pool, 1);
        var peers = new PeerSendWindow[1];
        var workers = new SendWorkerWindow[1];
        var diagnostics = new SendWorkerDiagnostics();
        try
        {
            host.Host(0);
            var peer = Connect(host, site, "allocation", false);
            using var client = peer.Client;
            void Exercise()
            {
                diagnostics.BeginWrite();
                diagnostics.EndWrite();
                diagnostics.Drain(0);
                host.DrainSendWindow(peers, workers);
            }
            for (int i = 0; i < 1000; i++) Exercise();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Exercise();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally { host.Shutdown(); }
    }
}

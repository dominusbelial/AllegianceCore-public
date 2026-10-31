using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.TransportProof;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P898TcpWriteTests(ITestOutputHelper output)
{
    [Fact]
    public void Proof_keeps_arrivals_that_beat_tick_log_without_fabricating_age_or_gaps()
    {
        var ticks = new ConcurrentDictionary<uint, long>();
        var timing = new PoseTiming(ticks);
        long unit = Stopwatch.Frequency / 1000;
        long now = Stopwatch.GetTimestamp();
        timing.Observe(42, 10, now);
        Assert.False(timing.PendingTicksReady());
        Assert.Empty(timing.Ages);
        ticks[10] = now - 10 * unit;
        timing.ResolvePendingTicks();
        Assert.Equal(10, Assert.Single(timing.Ages), precision: 3);
        timing.Observe(42, 11, now + 50 * unit);
        timing.Observe(42, 10, now + 51 * unit); // duplicate/stale
        ticks[11] = now + 40 * unit;
        Assert.True(timing.PendingTicksReady());
        timing.ResolvePendingTicks();
        Assert.Equal(2, timing.Ages.Count);
        Assert.All(timing.Ages, age => Assert.Equal(10, age, precision: 3));
        Assert.Equal(50, Assert.Single(timing.Gaps), precision: 3);
    }
    private sealed class TrackingPool : ArrayPool<byte>
    {
        public readonly HashSet<byte[]> Returned = new(ReferenceEqualityComparer.Instance);
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(Returned.Add(array), "frame returned twice");
            Array.Fill(array, (byte)0xEE);
        }
    }

    private sealed class SenderFixture : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly TcpClient _accepted;
        private readonly Type _type = typeof(TcpTransport).GetNestedType("PeerSender", BindingFlags.NonPublic)!;
        private readonly object _sender;
        public readonly TrackingPool Pool = new();
        public SenderFixture()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _client.Connect((IPEndPoint)listener.LocalEndpoint);
            _accepted = listener.AcceptTcpClient();
            listener.Stop();
            _sender = Activator.CreateInstance(_type, _accepted.GetStream(), _accepted, (Action<DisconnectReason>)(_ => Close()))!;
            Field("BatchWrites").SetValue(_sender, true);
            Field("_framePool").SetValue(_sender, Pool);
        }
        private FieldInfo Field(string name) => _type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public byte[] Enqueue(int length, byte value, int key)
        {
            var frame = Pool.Rent(length);
            Array.Fill(frame, value);
            _type.GetMethod("EnqueueCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_sender, [frame, length, true, key == 0, key]);
            return frame.ToArray();
        }
        public void Sink(Func<IList<ArraySegment<byte>>, TcpTransport.SendOutcome> sink) => Field("BatchSendOverride").SetValue(_sender, sink);
        public string Drain() => _type.GetMethod("TryWriteHead")!.Invoke(_sender, null)!.ToString()!;
        public PeerSendWindow Sample() => (PeerSendWindow)_type.GetMethod("DrainSendWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_sender, [new ConnectionId(1)])!;
        public void Close() => _type.GetMethod("Close")!.Invoke(_sender, null);
        public void CloseAfterFlush() => _type.GetMethod("CloseAfterFlush")!.Invoke(_sender, null);
        public void Dispose() { Close(); _accepted.Dispose(); _client.Dispose(); }
    }

    [Fact]
    public void Gather_partial_write_crosses_frame_boundaries_and_resumes_without_corruption()
    {
        using var sender = new SenderFixture();
        var first = sender.Enqueue(100, 1, 0);
        var second = sender.Enqueue(200, 2, 1);
        var third = sender.Enqueue(300, 3, 2);
        using var wire = new MemoryStream();
        int calls = 0;
        sender.Sink(segments =>
        {
            calls++;
            if (calls == 2) return TcpTransport.SendOutcome.Blocked();
            int n = calls == 1 ? 150 : segments.Sum(s => s.Count);
            int remaining = n;
            foreach (var segment in segments)
            {
                int take = Math.Min(remaining, segment.Count);
                wire.Write(segment.AsSpan()[..take]);
                remaining -= take;
            }
            return TcpTransport.SendOutcome.Sent(n);
        });
        Assert.Equal("Wrote", sender.Drain());
        Assert.Equal(450, sender.Sample().InflightBytes);
        Assert.Single(sender.Pool.Returned);
        // A new keyed frame cannot supersede a reserved partial frame. New
        // ordered traffic follows the committed batch, then precedes new streams.
        var fourth = sender.Enqueue(400, 4, 1);
        var fifth = sender.Enqueue(50, 5, 0);
        Assert.Equal("Blocked", sender.Drain());
        Assert.Equal(900, sender.Sample().InflightBytes);
        Assert.Equal("Wrote", sender.Drain());
        Assert.Equal(first.Concat(second).Concat(third).Concat(fifth).Concat(fourth).ToArray(), wire.ToArray());
        Assert.Equal(5, sender.Pool.Returned.Count);
        Assert.Equal(0, sender.Sample().QueuedBytes);
    }

    [Fact]
    public void Closing_during_gather_keeps_buffers_leased_until_send_returns()
    {
        using var sender = new SenderFixture();
        sender.Enqueue(100, 1, 0);
        sender.Enqueue(200, 2, 1);
        sender.Sink(segments =>
        {
            Assert.Equal(2, segments.Count);
            sender.Close();
            Assert.Empty(sender.Pool.Returned);
            Assert.All(segments[0].AsSpan().ToArray(), b => Assert.Equal(1, b));
            Assert.All(segments[1].AsSpan().ToArray(), b => Assert.Equal(2, b));
            return TcpTransport.SendOutcome.Blocked();
        });
        sender.Drain();
        Assert.Equal(2, sender.Pool.Returned.Count);
        Assert.Equal(0, sender.Sample().QueuedBytes);
        Assert.Equal("Dead", sender.Drain());
    }

    [Theory]
    [InlineData(40, 100, 32)]
    [InlineData(20, 1000, 16)]
    public void Gather_turn_respects_frame_and_byte_budgets(int frames, int length, int firstTurnFrames)
    {
        using var sender = new SenderFixture();
        for (int i = 0; i < frames; i++) sender.Enqueue(length, (byte)i, 0);
        int calls = 0, bytes = 0;
        sender.Sink(segments =>
        {
            if (calls++ == 0) Assert.Equal(firstTurnFrames, segments.Count);
            Assert.InRange(segments.Count, 1, 32);
            int n = segments.Sum(s => s.Count);
            Assert.InRange(n, 1, 16 * 1024);
            bytes += n;
            return TcpTransport.SendOutcome.Sent(n);
        });
        while (sender.Sample().QueuedBytes > 0) Assert.Equal("Wrote", sender.Drain());
        Assert.Equal(2, calls);
        Assert.Equal(frames * length, bytes);
        Assert.Equal(frames, sender.Pool.Returned.Count);
    }

    [Fact]
    public void Graceful_close_completes_all_reserved_frames_before_closing()
    {
        using var sender = new SenderFixture();
        sender.Enqueue(100, 1, 0);
        sender.Enqueue(200, 2, 1);
        int bytes = 0;
        sender.Sink(segments =>
        {
            int n = Math.Min(50, segments.Sum(s => s.Count));
            bytes += n;
            return TcpTransport.SendOutcome.Sent(n);
        });
        Assert.Equal("Wrote", sender.Drain());
        sender.CloseAfterFlush();
        for (int i = 0; i < 4; i++) Assert.Equal("Wrote", sender.Drain());
        Assert.Equal("Dead", sender.Drain());
        Assert.Equal(300, bytes);
        Assert.Equal(2, sender.Pool.Returned.Count);
        Assert.Equal(0, sender.Sample().QueuedBytes);
    }

    private sealed class Site : ITransportSite
    {
        public readonly ConcurrentQueue<ConnectionId> Joined = new();
        public readonly ConcurrentQueue<byte[]> Received = new();
        public readonly ConcurrentDictionary<ConnectionId, long> Left = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.Enqueue(id);
        public void OnPeerLeft(ConnectionId id) => Left[id] = Stopwatch.GetTimestamp();
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Received.Enqueue(data.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Paused_reader_with_small_gatherable_frames_keeps_no_progress_deadline(bool batching)
    {
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.Pool, 1) { TcpBatchWrites = batching };
        var previous = TcpTransport.NoWriteProgressTimeout;
        try
        {
            TcpTransport.NoWriteProgressTimeout = TimeSpan.FromMilliseconds(400);
            host.Host();
            using var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 1024 };
            client.Connect(IPAddress.Loopback, host.BoundPort);
            byte[] name = "P898-paused"u8.ToArray();
            byte[] greet = new byte[5 + name.Length];
            greet[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(greet.AsSpan(1), (uint)name.Length);
            name.CopyTo(greet, 5);
            client.GetStream().Write(greet);
            Assert.True(SpinWait.SpinUntil(() => !site.Joined.IsEmpty, 5000));
            Assert.True(site.Joined.TryPeek(out var id));
            long started = Stopwatch.GetTimestamp();
            byte[] frame = new byte[512];
            // Every frame fits the gather budget. This complements P670's large
            // partial-head case: the stalled socket actually exercises batches.
            for (int i = 0; i < 256; i++) host.Send(id, frame, true);
            while (!site.Left.ContainsKey(id) && Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
            {
                host.SendCoalescing(id, frame, 1);
                Thread.Sleep(10);
            }
            Assert.True(site.Left.TryGetValue(id, out long ended), "paused small-frame reader was not dropped");
            Assert.Equal(1, host.DrainDisconnectCounts()[DisconnectReason.NoWriteProgress]);
            double elapsed = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
            Assert.InRange(elapsed, 400, 2000);
            output.WriteLine($"P898 small-frame paused reader: batching={batching} noProgressBudgetMs=400 droppedAfterMs={elapsed:0.000}");
        }
        finally { host.Shutdown(); TcpTransport.NoWriteProgressTimeout = previous; }
    }

    [Fact]
    public void Batching_is_default_off_and_roundtrips_through_aot_config()
    {
        Assert.False(new TcpTransport(new Site(), TransportMode.Pool).TcpBatchWrites);
        Assert.False(new TransportConfig().TcpBatchWrites);
        var absent = JsonSerializer.Deserialize("{}", ServerJsonContext.Default.ServerConfig)!;
        Assert.False(absent.Transport?.TcpBatchWrites ?? false);
        var config = JsonSerializer.Deserialize("""{"transport":{"tcpBatchWrites":true}}""",
            ServerJsonContext.Default.ServerConfig)!;
        Assert.True(config.Transport!.TcpBatchWrites);
        var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!;
        Assert.True(roundtrip.Transport!.TcpBatchWrites);
    }

    [Theory]
    [InlineData(TransportMode.Pool, false)]
    [InlineData(TransportMode.Pool, true)]
    // Explicit legacy-mode controls prove that the pool option is ignored.
    [InlineData(TransportMode.ThreadPerPeer, false)]
    [InlineData(TransportMode.ThreadPerPeer, true)]
    public void Sends_large_frames_exactly_and_keeps_small_socket_buffer(TransportMode mode, bool batching)
    {
        var site = new Site();
        var receiver = new Site();
        var host = new TcpTransport(site, mode, 1) { TcpBatchWrites = batching };
        var client = new TcpTransport(receiver);
        try
        {
            host.Host();
            client.Join("127.0.0.1", host.BoundPort, "P898");
            Assert.True(SpinWait.SpinUntil(() => !site.Joined.IsEmpty, 5000));
            Assert.True(site.Joined.TryPeek(out var id));
            // Verify the accepted socket, not the public option alone. This
            // experiment must not hide a dead reader behind a larger SO_SNDBUF.
            var peers = (System.Collections.IDictionary)typeof(TcpTransport)
                .GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var sender = peers[id]!;
            Assert.Equal(batching && mode == TransportMode.Pool,
                (bool)sender.GetType().GetField("BatchWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!);
            var socket = (TcpClient)sender.GetType().GetField("_tcpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
            Assert.Equal(OperatingSystem.IsLinux() ? 16384 : 8192, socket.SendBufferSize);
            var expected = Enumerable.Range(0, 4).Select(n => Enumerable.Range(0, 65537 + n)
                .Select(i => (byte)(i + n)).ToArray()).ToArray();
            foreach (var payload in expected) host.Send(id, payload, true);
            Assert.True(SpinWait.SpinUntil(() => receiver.Received.Count == expected.Length, 5000));
            Assert.Equal(expected.Length, receiver.Received.Count);
            foreach (var payload in expected)
            {
                Assert.True(receiver.Received.TryDequeue(out var received));
                Assert.Equal(payload, received);
            }
            Assert.True(SpinWait.SpinUntil(() => host.TotalWrittenBytes == expected.Sum(p => p.Length + 5L), 5000));
        }
        finally { client.Shutdown(); host.Shutdown(); }
    }

    [Fact]
    public void Diagnostics_distinguish_superseding_from_partially_sent_frame_bytes()
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
        var sample = type.GetMethod("DrainSendWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sink = type.GetField("SendOverride", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PeerSendWindow Sample() => (PeerSendWindow)sample.Invoke(sender, [new ConnectionId(1)])!;
        try
        {
            // Unstarted sender: replacement and partial progress are deterministic.
            enqueue.Invoke(sender, [new byte[100], false, 1]);
            enqueue.Invoke(sender, [new byte[200], false, 1]);
            Assert.Equal(1, Sample().SupersededFrames);
            sink.SetValue(sender, (Func<byte[], int, int, TcpTransport.SendOutcome>)((_, _, _) => TcpTransport.SendOutcome.Sent(10)));
            drain.Invoke(sender, null);
            var partial = Sample();
            Assert.Equal(190, partial.InflightBytes);
            Assert.Equal(190, partial.QueuedBytes);
            enqueue.Invoke(sender, [new byte[300], false, 1]);
            var queued = Sample();
            Assert.Equal(1, queued.SupersededFrames); // in-flight bytes cannot be superseded
            Assert.Equal(190, queued.InflightBytes);
            Assert.Equal(490, queued.QueuedBytes);
        }
        finally { type.GetMethod("Close")!.Invoke(sender, null); }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using System.Text.Json;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests;

public sealed class P968PublisherStreamIndexTests(ITestOutputHelper output)
{
    private sealed record Measurement(int Peers, bool Indexed, bool Coalesced, bool Instrumented, long Comparisons,
        long Lookups, long IndexEntries, long IndexVisits, long GateAcquisitions, double FreshnessGateMs,
        double SubmitGateMs, double PublishLagMs, long Packets);

    [Theory]
    [InlineData(100, false, false)]
    [InlineData(400, false, false)]
    [InlineData(100, true, false)]
    [InlineData(400, true, false)]
    [InlineData(100, false, true)]
    [InlineData(400, false, true)]
    [InlineData(100, true, true)]
    [InlineData(400, true, true)]
    public void Reliable_backlog_measures_pending_scan_work(int peers, bool indexed, bool coalesced)
    {
        output.WriteLine($"ServerGC={System.Runtime.GCSettings.IsServerGC}; workers=4; pre-encoded immutable packets");
        // Isolate the publisher, after immutable/encode-once capture. No sim,
        // projection, transport or interest-tier substitute is timed here.
        foreach (bool instrumented in new[] { true, false })
        {
            for (int run = 0; run < 9; run++)
            {
                var measurement = Measure(peers, indexed, coalesced, instrumented);
                if (instrumented)
                {
                    Assert.Equal(indexed ? 0 : ((coalesced ? 89L : 81L) * peers * peers - (coalesced ? 39L : 47L) * peers) / 2, measurement.Comparisons);
                    Assert.Equal(indexed ? peers : 0, measurement.Lookups);
                    Assert.Equal(indexed ? (coalesced ? 3L : 2L) * peers : 0, measurement.IndexEntries);
                    Assert.Equal(indexed ? (coalesced ? 35L : 18L) * peers : 0, measurement.IndexVisits);
                    Assert.Equal((indexed ? 2L : coalesced ? 26L : 18L) * peers, measurement.GateAcquisitions);
                }
                Assert.Equal(peers * (coalesced ? 25L : 17L), measurement.Packets); // stale own poses are dropped
                if (run >= 2) output.WriteLine(JsonSerializer.Serialize(measurement));
            }
        }
    }

    private static Measurement Measure(int peers, bool indexed, bool coalesced, bool instrumented)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var errors = new ConcurrentQueue<Exception>();
        long packets = 0;
        using var publisher = new BroadcastPublisher((_, _) => packets++, 4, memorySink: true, pendingStreamIndex: indexed);
        var diagnostics = instrumented ? new BroadcastPublisherDiagnostics() : null;
        publisher.Diagnostics = diagnostics;
        publisher.OnError = errors.Enqueue;
        int calls = 0;
        publisher.BeforeEncode = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("P968 stalled publisher");
        };
        BroadcastSnapshot Tick(int tick)
        {
            var snapshot = publisher.Rent(tick);
            for (uint peer = 1; peer <= peers; peer++)
            {
                for (int i = 0; i < 8; i++)
                    snapshot.Deliveries.Add(new(new ConnectionId(peer), new BroadcastPacket([(byte)tick, (byte)i])));
                snapshot.Deliveries.Add(new(new ConnectionId(peer), new BroadcastPacket([(byte)tick, 12]), 12, true));
            }
            return snapshot;
        }
        long started;
        try
        {
            publisher.Submit(Tick(0));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            if (coalesced) publisher.Submit(Tick(1));
            publisher.Submit(Tick(3));
            started = Stopwatch.GetTimestamp();
        }
        finally { release.Set(); }
        publisher.Flush();
        double lag = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Empty(errors);
        Assert.Equal((coalesced ? 2L : 1L) * peers, publisher.DroppedStreamFrames);
        Assert.Equal(2, publisher.PublishedSnapshots);
        return new(peers, indexed, coalesced, instrumented, diagnostics?.StreamComparisons ?? 0,
            diagnostics?.StreamLookups ?? 0, diagnostics?.IndexEntries ?? 0, diagnostics?.IndexVisits ?? 0,
            diagnostics?.FreshnessGateAcquisitions ?? 0,
            (diagnostics?.FreshnessGateTicks ?? 0) * 1000d / Stopwatch.Frequency,
            (diagnostics?.SubmitGateTicks ?? 0) * 1000d / Stopwatch.Frequency, lag, packets);
    }
}

[Collection("LiveNetwork")]
public sealed class P968PublisherStreamEquivalenceTests(ITestOutputHelper output)
{
    private sealed record Frame(ConnectionId Peer, int Key, bool Guaranteed, byte[] Bytes);
    private sealed record Tick(long Number, Frame[] Frames);
    private sealed record ReplayResult(Frame[] Frames, int[] Callbacks, long Dropped, long Coalesced);

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, "", false)]
    [InlineData(false, " 1 ", true)]
    [InlineData(true, "0", false)]
    [InlineData(false, "TRUE", true)]
    [InlineData(true, "false", false)]
    public void Pending_index_environment_overrides_config(bool configured, string? env, bool expected)
        => Assert.Equal(expected, Srv.ResolvePendingStreamIndexFlag(configured, env));

    [Fact]
    public void Pending_index_defaults_off_and_binds_using_production_json_contract()
    {
        Assert.False(new BroadcastConfig().PendingStreamIndex);
        var config = JsonSerializer.Deserialize("""{"broadcast":{"pendingStreamIndex":true}}""",
            ServerJsonContext.Default.ServerConfig)!;
        Assert.True(config.Broadcast!.PendingStreamIndex);
        Assert.False(JsonSerializer.Deserialize("""{"broadcast":{}}""",
            ServerJsonContext.Default.ServerConfig)!.Broadcast!.PendingStreamIndex);
        Assert.Throws<InvalidDataException>(() => Srv.ResolvePendingStreamIndexFlag(false, "yes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Freshness_is_rechecked_after_a_submit_between_individual_sends(bool indexed)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sent = new List<byte>();
        var errors = new ConcurrentQueue<Exception>();
        BroadcastPublisher? publisher = null;
        using var owned = publisher = new BroadcastPublisher((delivery, bytes) =>
        {
            sent.Add(bytes[0]);
            if (bytes[0] != 1) return;
            // This arrives AFTER the first delivery's freshness decision. Key
            // 12 for peer 1 is new, key 13 is still absent, peer 2 is distinct.
            var pending = publisher!.Rent(3);
            pending.Deliveries.Add(new(new(1), new BroadcastPacket([31]), 12, false));
            publisher.Submit(pending);
        }, 2, indexed);
        publisher.OnError = errors.Enqueue;
        int calls = 0;
        publisher.BeforeEncode = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        try
        {
            var active = publisher.Rent(0);
            active.Deliveries.Add(new(new(1), new BroadcastPacket([1])));
            active.Deliveries.Add(new(new(1), new BroadcastPacket([2]), 12, true));
            active.Deliveries.Add(new(new(1), new BroadcastPacket([3]), 13, true));
            active.Deliveries.Add(new(new(2), new BroadcastPacket([4]), 12, true));
            active.Deliveries.Add(new(new(1), new BroadcastPacket([5]), 99, false));
            active.Deliveries.Add(new(new(1), new BroadcastPacket([6]), 0, false));
            publisher.Submit(active);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            // One-tick boundary: this must not make active streams stale.
            var adjacent = publisher.Rent(1);
            adjacent.Deliveries.Add(new(new(1), new BroadcastPacket([11]), 13, true));
            publisher.Submit(adjacent);
        }
        finally { release.Set(); }
        publisher.Flush();
        Assert.Empty(errors);
        Assert.Equal(new byte[] { 1, 4, 6, 11, 31 }, sent);
        Assert.Equal(3, publisher.DroppedStreamFrames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rebuild_removes_shed_streams_keeps_reliable_streams_and_respects_adjacent_tick(bool indexed)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sent = new List<byte>();
        var errors = new ConcurrentQueue<Exception>();
        using var publisher = new BroadcastPublisher((_, bytes) => sent.Add(bytes[0]), 2, indexed);
        publisher.OnError = errors.Enqueue;
        int calls = 0;
        publisher.BeforeEncode = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        void Submit(int tick, params (uint Peer, int Key, bool Guaranteed, byte Value)[] records)
        {
            var snapshot = publisher.Rent(tick);
            foreach (var r in records)
                snapshot.Deliveries.Add(new(new(r.Peer), new BroadcastPacket([r.Value]), r.Key, r.Guaranteed));
            publisher.Submit(snapshot);
        }
        try
        {
            Submit(0, (1, 12, true, 1), (1, 13, true, 2), (1, 14, true, 3));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Submit(1, (1, 12, false, 11), (1, 13, true, 12));
            // A same-tick complementary phase retains 12 and 13.
            Submit(1, (2, 14, true, 13));
            // Advancing the tick sheds unreliable 12, retains reliable 13.
            // An empty phase must rebuild from the FINAL list, not its input.
            Submit(3);
        }
        finally { release.Set(); }
        publisher.Flush();
        Assert.Empty(errors);
        Assert.Equal(new byte[] { 1, 3, 12, 13 }, sent);
        Assert.Equal(2, publisher.DroppedStreamFrames);
        // Reuse after draining: the old pending key 13 must not survive.
        sent.Clear();
        Submit(4, (1, 13, true, 44));
        publisher.Flush();
        Assert.Equal(new byte[] { 44 }, sent);

        entered.Reset(); release.Reset(); calls = 0;
        sent.Clear();
        try
        {
            Submit(10, (1, 12, true, 50), (1, 99, false, 51));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Submit(11, (1, 12, true, 52));
        }
        finally { release.Set(); }
        publisher.Flush();
        Assert.Empty(errors);
        Assert.Equal(new byte[] { 50, 51, 52 }, sent); // exactly one tick is fresh
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Requires", "Artwork")]
    public void Production_boot_wires_the_configured_index(bool configured)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = BaselineConfig();
        config.Broadcast!.PendingStreamIndex = configured;
        server.Boot(config);
        Assert.Equal(Srv.ResolvePendingStreamIndexFlag(configured,
            Environment.GetEnvironmentVariable("ALLEGIANCE_BROADCAST_PENDING_STREAM_INDEX")),
            server.TestPublisher!.PendingStreamIndexEnabled);
    }

    private static ServerConfig BaselineConfig()
    {
        var config = UpdateCadenceTests.Config(2, native: true);
        config.Broadcast!.ImmutableSnapshot = true;
        config.Broadcast.EncodeOnce = true;
        config.Broadcast.ParallelBuild = true;
        config.Broadcast.MaxDegreeOfParallelism = 4;
        config.Broadcast.InterestTiers = false;
        config.Transport = new() { Mode = "pool" };
        config.Sim = new() { NativeGotoPlan = true, NativeBroadcastOrder = false };
        return config;
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Recorded_production_surge_is_byte_identical_with_index_off_and_on()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(BaselineConfig());
        var peers = UpdateCadenceTests.Seed(server, ships: 24, viewers: 24);
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        for (int i = 0; i < peers.Count; i++)
        {
            peers[i].Ship.SideId = (short)(i % 2);
            peers[i].Ship.CommandTarget = peers[(i + 1) % peers.Count].Ship;
            for (short side = 0; side < 2; side++) peers[i].Ship.SideVisibility.SetSideVisibility(side, true);
        }
        var tape = new List<Tick>();
        for (uint tick = 0; tick < 16; tick++)
        {
            var frames = new List<Frame>();
            server.TestPublicationCapture = (peer, bytes, key, guaranteed) => frames.Add(new(peer, key, guaranteed, bytes));
            server.TestUpdateCadenceClock = () => tick / 20d;
            foreach (var peer in peers) peer.Ship.Position += new Allegiance.Core.Collision.Vector3(3, 1, 2);
            server.TestProjectRecordedTick(tick, true, discovery: true, surge: () =>
            {
                if (tick % 4 != 0) return;
                var victim = peers[(int)tick].Ship;
                var killer = peers[(int)tick + 1].Ship;
                mission.Site.KillShipEvent(tick / 20d, victim, killer, 1,
                    victim.Position, victim.Position + new Allegiance.Core.Collision.Vector3(1, 0, 0));
            });
            server.FlushBroadcastSnapshots();
            tape.Add(new(tick, frames.ToArray()));
        }
        server.TestPublicationCapture = null;
        Assert.Contains(tape.SelectMany(t => t.Frames), f => f.Key == 0 && f.Guaranteed);
        Assert.Contains(tape.SelectMany(t => t.Frames), f => f.Key == 12 && f.Guaranteed);
        Assert.Contains(tape.SelectMany(t => t.Frames), f => f.Key != 0 && !f.Guaranteed);
        Assert.Equal(0, server.TestPublisher!.EncodeFailures);
        // Record once, then feed the SAME peer/lane/bytes/tick tape to both arms.
        var off = Replay(tape, false);
        var on = Replay(tape, true);
        Assert.Equal(off.Frames.Length, on.Frames.Length);
        for (int i = 0; i < off.Frames.Length; i++)
        {
            Assert.Equal((off.Frames[i].Peer, off.Frames[i].Key, off.Frames[i].Guaranteed),
                (on.Frames[i].Peer, on.Frames[i].Key, on.Frames[i].Guaranteed));
            Assert.Equal(off.Frames[i].Bytes, on.Frames[i].Bytes);
        }
        Assert.Equal(off.Callbacks, on.Callbacks);
        Assert.Equal(off.Dropped, on.Dropped);
        Assert.Equal(off.Coalesced, on.Coalesced);
        Assert.True(off.Dropped > 0);
        Assert.True(off.Coalesced > 0);
        var reliable = tape.SelectMany(t => t.Frames).Where(f => f.Key == 0).ToArray();
        Assert.Equal(reliable.Select(f => Convert.ToHexString(f.Bytes)),
            on.Frames.Where(f => f.Key == 0).Select(f => Convert.ToHexString(f.Bytes)));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var frame in on.Frames)
        {
            hash.AppendData(BitConverter.GetBytes(frame.Peer.Value));
            hash.AppendData(BitConverter.GetBytes(frame.Key));
            hash.AppendData([frame.Guaranteed ? (byte)1 : (byte)0]);
            hash.AppendData(BitConverter.GetBytes(frame.Bytes.Length));
            hash.AppendData(frame.Bytes);
        }
        output.WriteLine(JsonSerializer.Serialize(new { recordedTicks = tape.Count,
            recordedFrames = tape.Sum(t => t.Frames.Length), publishedFrames = on.Frames.Length,
            on.Dropped, on.Coalesced, bytes = on.Frames.Sum(f => (long)f.Bytes.Length),
            sha256 = Convert.ToHexString(hash.GetHashAndReset()) }));
    }

    private static ReplayResult Replay(List<Tick> tape, bool indexed)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var frames = new List<Frame>();
        var callbacks = new List<int>();
        var errors = new ConcurrentQueue<Exception>();
        using var publisher = new BroadcastPublisher((delivery, bytes) =>
            frames.Add(new(delivery.Peer, delivery.Key, delivery.Guaranteed, bytes.ToArray())), 4, indexed);
        publisher.OnError = errors.Enqueue;
        int stallNext = 0;
        publisher.BeforeEncode = () =>
        {
            if (Interlocked.Exchange(ref stallNext, 0) == 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        void Submit(Tick tick, int start, int count, int callback)
        {
            var snapshot = publisher.Rent(tick.Number);
            foreach (var frame in tick.Frames.Skip(start).Take(count))
                snapshot.Deliveries.Add(new(frame.Peer, new BroadcastPacket(frame.Bytes), frame.Key, frame.Guaranteed));
            snapshot.AfterPublish.Add(() => callbacks.Add(callback));
            publisher.Submit(snapshot);
        }
        for (int group = 0; group < tape.Count; group += 4)
        {
            entered.Reset(); release.Reset();
            Interlocked.Exchange(ref stallNext, 1);
            try
            {
                Submit(tape[group], 0, tape[group].Frames.Length, group * 2);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                for (int i = group + 1; i < group + 4; i++)
                {
                    int half = tape[i].Frames.Length / 2;
                    Submit(tape[i], 0, half, i * 2);
                    Submit(tape[i], half, tape[i].Frames.Length - half, i * 2 + 1);
                }
                // Rebuild from an empty incoming phase: retained reliable
                // streams must still be represented, recycled containers too.
                Submit(new(tape[group + 3].Number, []), 0, 0, 1000 + group);
            }
            finally { release.Set(); }
            publisher.Flush();
        }
        Assert.Empty(errors);
        return new(frames.ToArray(), callbacks.ToArray(), publisher.DroppedStreamFrames, publisher.CoalescedSnapshots);
    }
}

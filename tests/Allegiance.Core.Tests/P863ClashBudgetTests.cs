using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P863ClashBudgetTests(ITestOutputHelper output)
{
    private sealed record Frame(uint Tick, ConnectionId Peer, int Key, bool Guaranteed, byte[] Bytes);

    [SkippableTheory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")] // crowded transcript proof, like BroadcastSnapshotTests
    public void Two_hundred_ships_preserve_every_peer_message_and_deviation(bool parallel, double floor)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config(floor, native: true);
        config.Games[0].Map = "m2xspiderw.igc";
        config.Broadcast!.ParallelBuild = parallel;
        config.Broadcast.MaxDegreeOfParallelism = 4;
        server.Boot(config);
        var peers = UpdateCadenceTests.Seed(server, ships: 200, viewers: 200);
        var sector = peers[0].Sector;
        var core = server.TestMission!.Core;
        server.TestMission.Stage = MissionStage.Started;
        foreach (var s in server.TestMission.Sectors)
        {
            foreach (var rock in s.Mineable)
                for (short side = 0; side < 2; side++) rock.SideVisibility.SetSideVisibility(side, true);
            foreach (var warp in s.Warps)
                for (short side = 0; side < 2; side++) warp.SideVisibility.SetSideVisibility(side, true);
        }
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        (List<Frame>, float[]) Replay(bool legacy)
        {
            server.TestResetSnapshotReplay();
            server.TestLegacyClashProjection = legacy;
            server.TestSeedPeers(peers);
            var probe = new Probe(25000, sector, core.ProbeTypes.Values.First(), new(10, 20, 30), 0, 0, peers[0].Ship);
            var treasure = new Treasure(25001, sector, DataTreasureIGC.Decode(new byte[DataTreasureIGC.Size]), 0);
            sector.Add(probe);
            var cloakEmitter = typeof(Ship).GetField("_cloakEmitterActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var frames = new List<Frame>();
            var deviations = new List<float>();
            uint tick = 0;
            server.TestPublicationCapture = (peer, bytes, key, guaranteed) => frames.Add(new(tick, peer, key, guaranteed, bytes));
            try
            {
                for (; tick < 125; tick++)
                {
                    now = tick / 20d;
                    for (int i = 0; i < peers.Count; i++)
                    {
                        var ship = peers[i].Ship;
                        ship.Position = new(i * 13 + tick, i % 7 * 100, tick * 2);
                        ship.SideId = (short)((i + (tick >= 45 ? 1 : 0)) % 2);
                        ship.CommandTarget = tick % 3 == 0 ? peers[(i + 1) % peers.Count].Ship : null;
                        ship.StationId = i % 31 == 0 && tick < 12 ? (short)1 : null;
                        cloakEmitter.SetValue(ship, i % 5 == 0 && tick % 20 < 10);
                        for (short side = 0; side < 2; side++)
                            ship.SideVisibility.SetSideVisibility(side, side == ship.SideId || (tick / 10 + i) % 3 != 0);
                    }
                    probe.SideVisibility.SetSideVisibility(1, tick >= 8);
                    // New object and removal, late join / departure, side changes,
                    // latch eviction, visibility edges and both refresh phases.
                    if (tick == 20) { sector.Add(treasure); treasure.SideVisibility.SetSideVisibility(1, true); }
                    if (tick == 30) sector.Remove(treasure);
                    if (tick == 40) server.TestSeedPeers(peers.Take(190).ToList());
                    if (tick == 50) server.TestSeedPeers(peers);
                    if (tick == 65) server.TestInvalidateClashLatches(peers[0].Conn, peers[1].Ship.Id);
                    if (tick == 80) { sector.Remove(probe); sector.Add(probe); }
                    server.TestProjectRecordedTick(tick, snapshot: true, discovery: true);
                    server.FlushBroadcastSnapshots();
                    deviations.AddRange(peers.Select(p => p.Ship.Deviation));
                }
            }
            finally { server.TestPublicationCapture = null; sector.Remove(probe); sector.Remove(treasure); }
            return (frames, deviations.ToArray());
        }
        var (reference, oldDeviation) = Replay(true);
        var (optimized, newDeviation) = Replay(false);
        Assert.Equal(oldDeviation, newDeviation);
        foreach (var peer in peers)
        {
            var a = reference.Where(f => f.Peer == peer.Conn).ToArray();
            var b = optimized.Where(f => f.Peer == peer.Conn).ToArray();
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal((a[i].Tick, a[i].Key, a[i].Guaranteed), (b[i].Tick, b[i].Key, b[i].Guaranteed));
                Assert.Equal(a[i].Bytes, b[i].Bytes);
            }
        }
        Assert.Equal(0, server.TestPublisher!.EncodeFailures);
        output.WriteLine($"Compared {reference.Count} messages, {reference.Sum(f => (long)f.Bytes.Length)} bytes, 200 peers × 125 ticks.");
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")]
    public void Recorded_clash_projection_measures_three_runs_of_both_arms()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config(2, native: true);
        config.Games[0].Map = "m2xspiderw.igc";
        config.Broadcast!.ParallelBuild = true;
        config.Broadcast.MaxDegreeOfParallelism = 15;
        server.Boot(config);
        var peers = UpdateCadenceTests.Seed(server, ships: 400, viewers: 400);
        server.TestMission!.Stage = MissionStage.Started;
        for (int i = 0; i < peers.Count; i++)
        {
            peers[i].Ship.SideId = (short)(i % 2);
            peers[i].Ship.CommandTarget = peers[(i + 1) % peers.Count].Ship;
            peers[i].Ship.SideVisibility.SetSideVisibility(0, true);
            peers[i].Ship.SideVisibility.SetSideVisibility(1, true);
        }
        foreach (var sector in server.TestMission.Sectors)
            foreach (var rock in sector.Mineable)
                for (short side = 0; side < 2; side++) rock.SideVisibility.SetSideVisibility(side, true);
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        for (int run = 1; run <= 3; run++)
            foreach (bool legacy in run % 2 == 1 ? new[] { true, false } : new[] { false, true })
            {
                server.TestResetSnapshotReplay();
                server.TestSeedPeers(peers);
                server.TestLegacyClashProjection = legacy;
                for (uint tick = 0; tick < 30; tick++)
                { now = tick / 20d; server.TestProjectRecordedTick(tick, true, discovery: true); server.FlushBroadcastSnapshots(); }
                TickProfiler.Configure(true, 3600, null);
                try
                {
                    for (uint tick = 30; tick < 180; tick++)
                    {
                        now = tick / 20d;
                        server.TestProjectRecordedTick(tick, true, measure: true, discovery: true);
                        server.FlushBroadcastSnapshots();
                    }
                    foreach (var (bucket, value) in TickProfiler.Snapshot())
                        output.WriteLine($"P863 projection run={run} arm={(legacy ? "before" : "after")} {bucket} mean={value.MeanMs:F4} p95={value.P95Ms:F4}");
                }
                finally { TickProfiler.Shutdown(); }
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(250)]
    public void Bounded_rank_keeps_the_full_stable_served_prefix_including_duplicate_references(int limit)
    {
        var ranking = new ShipUpdateRanking();
        for (short i = 0; i < 200; i++)
            ranking.Candidates.Add(new Ship(i, default, Vector3.Zero, Vector3.Zero, Orientation.Identity));
        ranking.Candidates.Add(ranking.Candidates[0]);
        for (int tick = 0; tick < 20; tick++)
        {
            for (int i = 0; i < ranking.Candidates.Count; i++)
                ranking.Candidates[i].Deviation = (i + tick) % 9;
            ranking.Candidates[3].Deviation = float.NaN;
            ranking.Candidates[4].Deviation = float.PositiveInfinity;
            var expected = ranking.Candidates.OrderByDescending(c => c.Deviation).ToArray();
            var actual = ranking.Rank(limit).ToArray();
            Assert.Equal(expected.Take(limit), actual.Take(limit));
            Assert.Equal(expected.OrderBy(c => c.Id), actual.OrderBy(c => c.Id));
        }
    }

    [Theory]
    [InlineData((short)12, 600)]
    [InlineData((short)-5, 100)]
    [InlineData((short)32760, 100)]
    public void Incremental_recipient_phases_preserve_uint_wrap_churn_and_latch_eviction(short id, int period)
    {
        var cache = new CrossSideSweepCache();
        var authoritative = new SweepLatchSet<ConnectionId>();
        var reference = new HashSet<ConnectionId>();
        var subject = new object();
        var all = new List<ConnectionId> { new(1), new(17), new(uint.MaxValue - 10), new(uint.MaxValue) };
        for (long tick = 0; tick < 1250; tick++)
        {
            if (tick == 77) { authoritative.RemoveWhere(c => c.Value == 17); reference.Remove(new(17)); }
            if (tick == 200) { authoritative.Clear(); reference.Clear(); }
            var audience = tick >= 30 && tick < 60 ? all.Take(3).ToList() : all;
            using var sweep = cache.Begin(authoritative.RemovalVersion);
            sweep.SetAudience(0, audience, period);
            bool Due(ConnectionId c) => (tick + unchecked(c.Value + (uint)id) % period) % period == 0;
            var expected = audience.Where(c => reference.Add(c) || Due(c)).ToArray();
            var actual = sweep.Recipients(subject, 0, id, tick)
                .Where(c => authoritative.Add(c) || Due(c)).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    private sealed class CallbackModel(short id, Action action) : Allegiance.Core.Sim.Model(id, Vector3.Zero)
    {
        protected override void OnUpdate(double now, double dt) => action();
    }

    [Fact]
    public void Sector_membership_preserves_capture_order_removal_readdition_and_duplicates()
    {
        var sector = new Sector(DataClusterIGC.Decode(new byte[DataClusterIGC.Size]));
        var trace = new List<int>();
        var child = new CallbackModel(3, () => trace.Add(3));
        var victim = new CallbackModel(2, () => trace.Add(2));
        var first = new CallbackModel(1, () => { trace.Add(1); sector.Remove(victim); sector.Add(child); });
        sector.Add(first); sector.Add(victim);
        sector.Update(1);
        Assert.Equal(new[] { 1 }, trace); // removed snapshot member and new child both skip this pass
        sector.Add(victim); sector.Add(victim);
        Assert.True(sector.Remove(victim)); // one duplicate remains eligible
        sector.Update(2);
        Assert.Equal(new[] { 1, 1, 3 }, trace); // first removes the remaining victim before its turn
        Assert.False(sector.Remove(victim));
        Assert.True(sector.Remove(child));
        Assert.True(sector.Remove(child));
        Assert.False(sector.Remove(child));
    }

    private sealed class AllocatingReportWriter() : StringWriter(CultureInfo.InvariantCulture)
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            // Reporting allocations must belong to the NEXT contiguous window.
            if (value?.StartsWith("[gcprof] window", StringComparison.Ordinal) == true)
                GC.KeepAlive(new byte[1024 * 1024]);
        }
    }

    [Fact]
    public void Allocation_windows_count_workers_and_do_not_lose_report_bytes()
    {
        var writer = new AllocatingReportWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(writer);
            TickProfiler.Configure(true, 3600, null);
            for (int window = 0; window < 4; window++)
            {
                TickProfiler.EnterTick();
                var thread = new Thread(() => { var bytes = new byte[2 * 1024 * 1024]; GC.KeepAlive(bytes); });
                thread.Start(); thread.Join();
                TickProfiler.EndTick();
                TickProfiler.ForceReport();
            }
        }
        finally { TickProfiler.Shutdown(); Console.SetOut(original); }
        var allocations = Regex.Matches(writer.ToString(), @"\[gcprof\] window .*?alloc=([\d.]+)MB")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, allocations.Length);
        Assert.True(allocations[0] >= 2);
        Assert.All(allocations.Skip(1), mb => Assert.True(mb >= 3));
        // No upper bound: this is deliberately a GLOBAL counter; unrelated
        // test threads are legitimate allocations in this same process.
    }
}

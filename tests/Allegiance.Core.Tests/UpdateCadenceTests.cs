using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

public sealed class UpdateAgeMetricsTests
{
    [Fact]
    public void Crowded_histogram_reports_distance_percentiles_thresholds_and_overflow()
    {
        var window = new PoseAgeWindow();
        float[] distances = [999, 1000, 3000, 6000];
        foreach (float distance in distances)
            for (int i = 0; i < 25; i++) window.Record(distance * distance, i / 10d);
        for (int band = 0; band < 4; band++)
        {
            var value = window.Band(band);
            Assert.Equal(25, value.Count);
            Assert.Equal(1.2, value.P50, 8);
            Assert.Equal(2.3, value.P95, 8);
            Assert.Equal(2.4, value.Max, 8);
        }
        Assert.Equal(56, window.Older1);
        Assert.Equal(0, window.Older3);
        var overflow = new PoseAgeWindow();
        overflow.Record(36_000_000, 15.123, true);
        Assert.Equal((1L, 15.123, 15.123, 15.123), overflow.Band(3));
        window.Merge(overflow);
        Assert.Equal(1, window.Older3);
        Assert.Equal(1, window.NeverPose);
        window.Clear();
        Assert.Equal((0L, 0d, 0d, 0d), window.Band(3));
        Assert.Equal(0, window.Older1);
    }

    [Fact]
    public void Histogram_sampling_allocates_zero_bytes_after_warmup()
    {
        var window = new PoseAgeWindow();
        for (int i = 0; i < 10000; i++) window.Record(i, i % 200 / 20d);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) window.Record(i, i % 200 / 20d);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

[Collection("LiveNetwork")]
public sealed class UpdateCadenceTests
{
    internal static ServerConfig Config(double hz = 2, bool native = true) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Broadcast = new() { MinUpdateHz = hz, ShipUpdateModel = native ? "native" : "port" },
        Games = [new() { Name = "P659 cadence", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    internal static List<(ConnectionId Conn, Ship Ship, Sector Sector)> Seed(Srv server,
        int ships = 100, int viewers = 4)
    {
        var sector = server.TestSectors[0];
        var hull = server.TestMission!.Core.HullTypes.Values.First();
        var peers = new List<(ConnectionId, Ship, Sector)>();
        for (int i = 0; i < ships; i++)
        {
            var ship = new Ship((short)(10000 + i), hull, new Vector3(i * 90, 0, 0),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            server.TestMission.PlaceShip(ship, sector);
            if (i < viewers) peers.Add((new ConnectionId((uint)(700 + i)), ship, sector));
        }
        server.TestSeedPeers(peers);
        return peers;
    }

    internal static IEnumerable<HeavyShipsUpdate> Heavy(IEnumerable<byte[]> packets)
        => packets.SelectMany(p => FedPacket.Unpack(p)).Where(m =>
            (ushort)(m[2] | m[3] << 8) == HeavyShipsUpdate.Fmid).Select(m => HeavyShipsUpdate.Decode(m));

    [SkippableTheory]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    [InlineData(false, 5)]
    [Trait("Requires", "Artwork")]
    public void Every_visible_ship_meets_floor_even_when_ranking_repeatedly_favours_others(bool native, double hz)
    {
        Require.Artwork();
        using var server = new Srv();
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(hz, native));
        var peers = Seed(server);
        var ships = peers[0].Sector.GetShipUpdateCandidates();
        var last = new Dictionary<(ConnectionId, short), double>();
        foreach (var peer in peers)
            foreach (var ship in ships.Where(s => s != peer.Ship)) last[(peer.Conn, ship.Id)] = 0;
        for (uint tick = 0; tick < 160; tick++)
        {
            now = tick / 20d;
            // Adversarial ranking: a per-viewer floor must work independently
            // of how shared native deviation state is changed by other viewers.
            for (int i = 0; i < ships.Count; i++) ships[i].Deviation = 10000 - i;
            if (tick > 20) Assert.All(last.Values, t => Assert.True(now - t <= 1 / hz + 0.051,
                $"pose age {now - t:0.###} > {1 / hz:0.###} + one tick"));
            var packets = server.TestCollectBroadcast(tick, parallel: tick % 2 == 0, "shipsUpdate");
            foreach (var peer in peers)
                foreach (var frame in Heavy(packets[peer.Conn]))
                {
                    Assert.DoesNotContain(frame.HeavyShipEntries, e => e.ShipId == peer.Ship.Id);
                    Assert.Equal(frame.HeavyShipEntries.Length, frame.HeavyShipEntries.Select(e => e.ShipId).Distinct().Count());
                    foreach (var e in frame.HeavyShipEntries) last[(peer.Conn, e.ShipId)] = now;
                }
        }
        Assert.All(last.Values, t => Assert.True(t > 7));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Requires", "Artwork")]
    public void Zero_floor_preserves_ranked_bytes_and_deviations_with_metrics_enabled(bool native)
    {
        Require.Artwork();
        using var server = new Srv();
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(0, native));
        var peers = Seed(server, ships: 100);
        var ships = peers[0].Sector.GetShipUpdateCandidates();
        var expected = new List<Dictionary<ConnectionId, List<byte[]>>>();
        for (uint tick = 0; tick < 40; tick++)
        {
            now = tick / 20d;
            expected.Add(server.TestCollectBroadcast(tick, false, "shipsUpdate"));
        }
        var deviations = ships.Select(s => s.Deviation).ToArray();
        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        TickProfiler.Configure(true, 5, null);
        for (uint tick = 0; tick < 40; tick++)
        {
            now = tick / 20d;
            var actual = server.TestCollectBroadcast(tick, true, "shipsUpdate");
            foreach (var peer in peers) Assert.Equal(expected[(int)tick][peer.Conn], actual[peer.Conn]);
        }
        Assert.Equal(deviations, ships.Select(s => s.Deviation).ToArray());
        string report = server.TakeUpdateAgeReport(2);
        Assert.Contains("floorExtraBytes=0", report);
        Assert.Contains("[update-age]", report);
        Assert.Contains("viewers=4", report);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Requires", "Artwork")]
    public void Added_floor_bytes_are_exact_and_native_ranking_is_unchanged(bool native)
    {
        Require.Artwork();
        using var server = new Srv();
        server.TestUpdateCadenceClock = () => 0;
        server.Boot(Config(0, native));
        var peers = Seed(server, ships: 100, viewers: 1);
        var ships = peers[0].Sector.GetShipUpdateCandidates();
        var baseline = server.TestCollectBroadcast(0, false, "shipsUpdate")[peers[0].Conn];
        var deviations = ships.Select(s => s.Deviation).ToArray();
        server.TestResetBroadcastState();
        server.TestSeedPeers(peers);
        server._minUpdateHz = 2;
        var floor = server.TestCollectBroadcast(0, false, "shipsUpdate")[peers[0].Conn];
        Assert.Equal(deviations, ships.Select(s => s.Deviation).ToArray());
        var baseEntries = Heavy(baseline).Single().HeavyShipEntries;
        var floorEntries = Heavy(floor).Single().HeavyShipEntries;
        Assert.Equal(baseEntries.Select(e => e.ShipId), floorEntries.Take(baseEntries.Length).Select(e => e.ShipId));
        long extra = floor.Sum(p => p.Length) - baseline.Sum(p => p.Length);
        Assert.Equal((floorEntries.Length - baseEntries.Length) * HeavyShipUpdateEntry.Width, extra);
        Assert.True(extra > 0);
        Assert.Contains($"floorExtraBytes={extra} ", server.TakeUpdateAgeReport(1));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Tracking_forgets_hidden_departed_and_reused_ids_and_allocates_zero_when_warm()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var peers = Seed(server, ships: 2, viewers: 1);
        var self = peers[0].Ship;
        var sector = peers[0].Sector;
        var subject = sector.GetShipUpdateCandidates().Single(s => s != self);
        var state = new ViewerPoseCadence();
        Ship[] both = [self, subject];
        state.Begin(both, self, sector, false, 0, true);
        state.Begin(both, self, sector, false, 1.1, true);
        Assert.Equal(1, state.Window.Older1); // no HEAVY: presence alone cannot reset age
        state.MarkPose(subject.Id, 1.1);
        state.Begin(both, self, sector, false, 1.2, true);
        Assert.Equal(1, state.Window.Older1);
        for (int i = 0; i < 10000; i++) state.Begin(both, self, sector, false, 1.2, true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) state.Begin(both, self, sector, false, 1.2, true);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        subject.SideId = 1;
        state.Begin(both, self, sector, false, 9, false);
        Assert.Equal(1, state.TrackedCount); // uncloaked: same audience as ranked telemetry/fire
        subject.MountPart(server.TestMission!.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Cloak),
            1, mountId: 0, fractionMounted: 255);
        subject.Energy = 1_000_000;
        subject.StateM = ShipControlState.CloakActive;
        subject.Update(subject.LastUpdate + 0.05);
        Assert.True(subject.IsCloaked);
        subject.SideVisibility.SetSideVisibility(0, false);
        state.Begin(both, self, sector, false, 10, false);
        Assert.Equal(0, state.TrackedCount);
        var entries = new List<HeavyShipUpdateEntry>();
        state.AppendFloor(entries, 0, self.Position, 10, 2, 20);
        Assert.Empty(entries); // unseen enemies receive no additional data
        subject.SideVisibility.SetSideVisibility(0, true);
        state.Begin(both, self, sector, false, 10, false);
        Assert.Equal(1, state.TrackedCount);
        state.MarkPose(subject.Id, 10);
        var replacement = new Ship(subject.Id, subject.HullType, subject.Position, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        state.Begin(new[] { self, replacement }, self, sector, false, 20, false);
        state.AppendFloor(entries, 0, self.Position, 20, 2, 20);
        Assert.Single(entries); // new identity with same id gets an initial pose
        state.Begin(new[] { self }, self, sector, false, 20.1, false);
        Assert.Equal(0, state.TrackedCount);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Profiler_windows_emit_one_age_line_and_reset_sample_and_byte_counters()
    {
        Require.Artwork();
        using var server = new Srv();
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config());
        Seed(server);
        TickProfiler.Configure(true, 3600, null);
        server.TestCollectBroadcast(0, false, "shipsUpdate");
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            TickProfiler.ForceReport();
            now = 0.05;
            server.TickSim();
            server.TickSim();
            Assert.Single(output.ToString().Split('\n'), line => line.StartsWith("[update-age]"));
            TickProfiler.ForceReport();
            now = 0.1;
            server.TickSim();
            Assert.Equal(2, output.ToString().Split('\n').Count(line => line.StartsWith("[update-age]")));
            // A second drain without another broadcast has no window samples or bytes.
            server.TakeUpdateAgeReport(now);
            string empty = server.TakeUpdateAgeReport(now + 1);
            Assert.Contains("0-1km[n=0,", empty);
            Assert.Contains("shipsBytes=0 ", empty);
            Assert.Contains("floorExtraBytes=0 ", empty);
        }
        finally { Console.SetOut(previous); }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Departing_viewers_keep_their_window_samples_and_bytes_without_retaining_ship_state()
    {
        Require.Artwork();
        using var server = new Srv();
        server.TestUpdateCadenceClock = () => 0;
        server.Boot(Config());
        var peers = Seed(server);
        TickProfiler.Configure(true, 3600, null);
        var packets = server.TestCollectBroadcast(0, false, "shipsUpdate");
        long bytes = packets.Values.SelectMany(p => p).Sum(p => p.Length);
        long extra = packets.Values.SelectMany(p => Heavy(p)).Sum(u => u.HeavyShipEntries.Length - 4) * HeavyShipUpdateEntry.Width;
        Assert.True(extra > 0);
        foreach (var peer in peers) server.RetireViewerUpdateCadence(peer.Conn);
        string report = server.TakeUpdateAgeReport(1);
        Assert.Contains("viewers=0 ", report);
        Assert.Contains("visiblePairs=0 ", report);
        Assert.Contains($"shipsBytes={bytes} ", report);
        Assert.Contains($"floorExtraBytes={extra} ", report);
        Assert.DoesNotContain("0-1km[n=0,", report);
        Assert.Contains("shipsBytes=0 ", server.TakeUpdateAgeReport(2));
    }

    [Fact]
    public void Floor_configuration_defaults_to_two_and_rejects_invalid_values()
    {
        Assert.Equal(2, new BroadcastConfig().MinUpdateHz);
        foreach (double hz in new[] { -1, double.NaN, double.PositiveInfinity })
        {
            using var server = new Srv();
            var error = Assert.Throws<InvalidDataException>(() => server.Boot(Config(hz)));
            Assert.Contains("minUpdateHz", error.Message);
        }
        // Failed boots must not leave existing profiler callbacks subscribed
        // with a null host. This reproduces the whole-suite initialization bug
        // in isolation, without depending on which test forces the next report.
        TickProfiler.Configure(true, 3600, null);
        try { TickProfiler.ForceReport(); }
        finally { TickProfiler.Shutdown(); }
    }
}

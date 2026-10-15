using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class InterestTierTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, " TRUE ", true)]
    [InlineData(true, "0", false)]
    [InlineData(false, "1", true)]
    [InlineData(true, " false ", false)]
    public void Flag_defaults_off_and_environment_wins(bool configured, string? env, bool expected)
        => Assert.Equal(expected, Srv.ResolveInterestTiersFlag(configured, env));

    [Fact]
    public void Json_binds_defaults_and_bad_environment_is_rejected()
    {
        Assert.False(new BroadcastConfig().InterestTiers);
        Assert.Equal(5, new BroadcastConfig().Tier1Hz);
        Assert.Equal(2, new BroadcastConfig().Tier2Hz);
        Assert.Throws<InvalidDataException>(() => Srv.ResolveInterestTiersFlag(false, "yes"));
        string path = Path.Combine(Path.GetTempPath(), $"p677-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"broadcast":{"interestTiers":true,"tier1Hz":10,"tier2Hz":3,"engagementRange":2000,"midDistanceRange":7000}}""");
            var config = ServerConfigLoader.Load(path, []).Broadcast!;
            Assert.True(config.InterestTiers);
            Assert.Equal(10, config.Tier1Hz);
            Assert.Equal(3, config.Tier2Hz);
            Assert.Equal(2000, config.EngagementRange);
            Assert.Equal(7000, config.MidDistanceRange);
        }
        finally { File.Delete(path); }
    }

    internal static ServerConfig Config(double floor = 2, bool native = false)
    {
        var config = UpdateCadenceTests.Config(floor, native);
        config.Broadcast!.InterestTiers = true;
        return config;
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Classification_boundaries_allies_and_command_target_promotion()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        Assert.True(server._broadcastSnapshotEnabled);
        Assert.True(server._broadcastEncodeOnceEnabled);
        var peers = UpdateCadenceTests.Seed(server, 2, 2);
        var viewer = peers[0].Ship;
        var subject = peers[1].Ship;
        viewer.Position = Vector3.Zero;
        Assert.Equal(0, server.ClassifyInterest(viewer, viewer));
        subject.SideId = 1;
        foreach (var (distance, tier) in new[] { (1500f, 0), (1501f, 1), (6000f, 1), (6001f, 2) })
        {
            subject.Position = new(distance, 0, 0);
            Assert.Equal(tier, server.ClassifyInterest(viewer, subject));
        }
        subject.SideId = 0;
        Assert.Equal(1, server.ClassifyInterest(viewer, subject));
        subject.Position = new(10, 0, 0);
        Assert.Equal(1, server.ClassifyInterest(viewer, subject));
        subject.SideId = 1;
        subject.Position = new(20000, 0, 0);
        viewer.CommandTarget = subject;
        Assert.Equal(0, server.ClassifyInterest(viewer, subject));
        viewer.CommandTarget = null;
        Assert.Equal(2, server.ClassifyInterest(viewer, subject));
        foreach (var side in server.TestMission!.Sides) side.AllyGroup = 1;
        Assert.Equal(1, server.ClassifyInterest(viewer, subject));
        subject.Position = new(10, 0, 0);
        Assert.Equal(1, server.ClassifyInterest(viewer, subject));
        foreach (var side in server.TestMission.Sides) side.AllyGroup = null;
        Assert.Equal(0, server.ClassifyInterest(viewer, subject));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Mounted_weapon_reach_extends_the_engagement_radius()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config();
        config.Broadcast!.EngagementRange = 10;
        config.Broadcast.MidDistanceRange = 20;
        server.Boot(config);
        var peers = UpdateCadenceTests.Seed(server, 2, 2);
        var viewer = peers[0].Ship;
        var subject = peers[1].Ship;
        viewer.Position = Vector3.Zero;
        subject.SideId = 1;
        var core = server.TestMission!.Core;
        var gun = core.PartTypes.Values.Select(p => p.Type).OfType<Allegiance.Core.DataPartTypeIGC.Weapon>()
            .First(w => core.ProjectileTypes[w.ProjectileTypeId].Speed * core.ProjectileTypes[w.ProjectileTypeId].Lifespan > 100);
        var projectile = core.ProjectileTypes[gun.ProjectileTypeId];
        subject.Position = new(projectile.Speed * projectile.Lifespan * .9f, 0, 0);
        Assert.Equal(2, server.ClassifyInterest(viewer, subject));
        subject.MountWeapon(gun);
        Assert.Equal(0, server.ClassifyInterest(viewer, subject));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Classification_snapshot_is_detached_from_later_sim_changes()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var peers = UpdateCadenceTests.Seed(server, 2, 2);
        var viewer = peers[0].Ship;
        var subject = peers[1].Ship;
        subject.SideId = 1;
        subject.Position = new(9000, 0, 0);
        var v = Srv.CaptureInterestShip(viewer);
        var s = Srv.CaptureInterestShip(subject);
        BroadcastSnapshotGuard.AssertDetached(v);
        BroadcastSnapshotGuard.AssertDetached(s);
        Assert.Equal(2, server.ClassifyInterest(v, s));
        viewer.CommandTarget = subject;
        subject.Position = viewer.Position;
        subject.SideId = viewer.SideId;
        Assert.Equal(0, server.ClassifyInterest(viewer, subject));
        Assert.Equal(2, server.ClassifyInterest(v, s));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    [InlineData(false, 30)]
    public void Target_and_near_enemies_are_full_rate_and_T2_meets_floor(bool native, double floor)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(floor, native));
        var peers = UpdateCadenceTests.Seed(server, 80, 1);
        var self = peers[0].Ship;
        self.Position = Vector3.Zero;
        var subjects = peers[0].Sector.GetShipUpdateCandidates().Where(s => s != self).ToArray();
        for (int i = 0; i < subjects.Length; i++)
        {
            subjects[i].SideId = 1;
            subjects[i].Position = new(8000 + i * 10, 0, 0);
            subjects[i].SideVisibility.SetSideVisibility(0, true);
        }
        subjects[0].Position = new(1000, 0, 0);
        self.CommandTarget = subjects[^1];
        var last = new Dictionary<short, int>();
        double hz = Math.Min(20, Math.Max(2, floor));
        int maxGap = Math.Max(1, (int)Math.Floor(20 / hz));
        for (uint tick = 0; tick <= 80; tick++)
        {
            now = tick / 20d;
            if (tick == 23) self.CommandTarget = subjects[^2]; // immediate promotion, no old-tier wait
            var update = UpdateCadenceTests.Heavy(server.TestCollectBroadcast(tick, false, "shipsUpdate")[peers[0].Conn]).Single();
            Assert.Equal(((Ship)self.CommandTarget!).Id, update.HeavyShipEntries[0].ShipId);
            Assert.Contains(update.HeavyShipEntries, e => e.ShipId == subjects[0].Id);
            foreach (var entry in update.HeavyShipEntries) last[entry.ShipId] = (int)tick;
            foreach (var subject in subjects) Assert.InRange((int)tick - last[subject.Id], 0, maxGap - 1);
        }
        string report = server.TakeUpdateAgeReport(now);
        Assert.Contains("T0[n=", report);
        Assert.Contains("T2[n=", report);
        server.RetireViewerUpdateCadence(peers[0].Conn);
        Assert.Contains("T2[n=0,", server.TakeUpdateAgeReport(now + 1));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Deviation_is_descending_within_tiers_target_first_and_hidden_ships_stay_hidden()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.TestUpdateCadenceClock = () => 0;
        server.Boot(Config());
        var peers = UpdateCadenceTests.Seed(server, 12, 1);
        var self = peers[0].Ship;
        var subjects = peers[0].Sector.GetShipUpdateCandidates().Where(s => s != self).ToArray();
        for (int i = 0; i < subjects.Length; i++)
        {
            subjects[i].SideId = 1;
            subjects[i].Position = new(i < 3 ? 1000 : i < 6 ? 3000 : 9000, 0, 0);
            subjects[i].Deviation = i * 19 % 11;
        }
        self.CommandTarget = subjects[^1];
        typeof(Ship).GetField("_cloakEmitterActive", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(subjects[^2], true);
        subjects[^2].SideVisibility.SetSideVisibility(0, false);
        var expected = new[] { subjects[^1].Id }.Concat(subjects
            .Where(s => s != self.CommandTarget && s != subjects[^2])
            .OrderBy(s => server.ClassifyInterest(self, s)).ThenByDescending(s => s.Deviation).Select(s => s.Id)).ToArray();
        var entries = UpdateCadenceTests.Heavy(server.TestCollectBroadcast(0, false, "shipsUpdate")[peers[0].Conn]).Single().HeavyShipEntries;
        Assert.Equal(expected, entries.Select(e => e.ShipId));
        Assert.All(subjects.Where(s => s != self.CommandTarget && s != subjects[^2]), s => Assert.Equal(0, s.Deviation));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Tier_projection_is_identical_serial_or_parallel_and_shares_records(bool native)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(native: native));
        var peers = UpdateCadenceTests.Seed(server, 64, 16);
        foreach (var peer in peers) peer.Ship.CommandTarget = peers[(int)(peer.Conn.Value % 16)].Ship;
        List<byte[]> Replay(bool parallel, bool sharing)
        {
            server.TestResetSnapshotReplay(); server.TestSeedPeers(peers);
            server._broadcastParallelBuild = parallel;
            server._broadcastEncodeOnceEnabled = sharing;
            var result = new List<byte[]>();
            server.TestPublicationCapture = (_, bytes, _, _) => result.Add(bytes);
            for (uint tick = 0; tick < 25; tick++)
            {
                now = tick / 20d;
                server.TestProjectRecordedTick(tick, true);
                server.FlushBroadcastSnapshots();
            }
            server.TestPublicationCapture = null;
            return result;
        }
        var unshared = Replay(false, false);
        long before = server.ShipEncodingCounters.HeavyRecords;
        var shared = Replay(false, true);
        Assert.InRange(server.ShipEncodingCounters.HeavyRecords - before, 1, 64 * 25);
        Assert.Equal(unshared, shared);
        Assert.Equal(shared, Replay(true, true));
        Assert.Equal(0, server.TestPublisher!.EncodeFailures);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Invalid_rates_and_ranges_fail_boot_and_floor_raises_both_tiers()
    {
        Require.Artwork();
        foreach (var change in new Action<BroadcastConfig>[] {
            c => c.Tier1Hz = double.NaN, c => c.Tier2Hz = 0, c => c.Tier2Hz = -1,
            c => c.EngagementRange = float.PositiveInfinity, c => c.MidDistanceRange = 100 })
        {
            var config = Config(); change(config.Broadcast!);
            using var bad = new Srv();
            Assert.Throws<InvalidDataException>(() => bad.Boot(config));
        }
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(10));
        Assert.Equal(10, server.InterestRate(1));
        Assert.Equal(10, server.InterestRate(2));
    }
}

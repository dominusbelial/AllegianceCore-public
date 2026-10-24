using System.Linq;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>P813a — distance-tiered big-change promotion and bounded tier
/// telemetry on the server. These are the NEW behaviours on top of the P677
/// interest-tier cadence: a far (T1/T2) ship that has drifted past
/// <c>broadcast.promotionEpsilon</c> since its last served pose is promoted to
/// a full-rate send for that tick, and the <c>[update-age]</c> report carries
/// per-tier messages/bytes and a promotions/s rate. All tier cadence, deviation
/// ordering, flag-off byte-identity, dead-reckoner extrapolation and near-full-
/// rate guarantees remain covered by the existing P677/UpdateCadence suites;
/// these tests exercise only the promotion + telemetry deltas.</summary>
[Collection("LiveNetwork")]
public sealed class P813aDistanceTierTests
{
    private static ServerConfig Config(float promotionEpsilon, double tier2Hz = 2)
    {
        var config = UpdateCadenceTests.Config(hz: 2, native: false);
        config.Broadcast!.InterestTiers = true;
        config.Broadcast.PromotionEpsilon = promotionEpsilon;
        config.Broadcast.Tier2Hz = tier2Hz;
        config.Broadcast.MidDistanceRange = 6000;
        config.Broadcast.EngagementRange = 1500;
        return config;
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Far_ship_big_change_is_promoted_within_one_tick_while_static_stays_at_cadence()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(promotionEpsilon: 100, tier2Hz: 2));   // period 10 ticks @ 20 Hz
        var peers = UpdateCadenceTests.Seed(server, ships: 4, viewers: 1);
        var self = peers[0].Ship;
        self.Position = Vector3.Zero;
        var subjects = peers[0].Sector.GetShipUpdateCandidates().Where(s => s != self).ToArray();
        var moving = subjects[0];           // manoeuvring far ship (drifts every tick)
        var still = subjects[1];            // static far ship (control)
        foreach (var s in subjects) { s.SideId = 1; s.Position = new(9000, 0, 0); }
        moving.Position = new(9000, 0, 0); still.Position = new(9010, 0, 0);
        foreach (var s in subjects) s.SideVisibility.SetSideVisibility(0, true);

        var movingServed = new List<long>();
        var stillServed = new List<long>();
        for (uint tick = 0; tick < 40; tick++)
        {
            now = tick / 20d;
            // The manoeuvring ship drifts 500u every tick — well past the 100u
            // promotion epsilon from its last served pose — so it must be
            // promoted (served every tick) within one tick of moving. The static
            // ship never moves, so it stays on its 10-tick tier cadence.
            if (tick >= 1) moving.Position = new(moving.Position.X + 500, 0, 0);
            var heavy = UpdateCadenceTests.Heavy(
                server.TestCollectBroadcast(tick, false, "shipsUpdate")[peers[0].Conn]).Single();
            if (heavy.HeavyShipEntries.Any(e => e.ShipId == moving.Id)) movingServed.Add(tick);
            if (heavy.HeavyShipEntries.Any(e => e.ShipId == still.Id)) stillServed.Add(tick);
        }

        // Promoted within one tick: served every tick once it starts moving.
        Assert.Contains(1L, movingServed);
        Assert.DoesNotContain(0L, movingServed.Skip(1));           // served every tick after the first
        Assert.Equal(39, movingServed.Count(t => t >= 1));
        // The static far ship stays at its tier cadence, never every tick.
        Assert.True(stillServed.Count >= 2, "static far ship must still meet its cadence");
        Assert.True(stillServed.Count <= 8, $"static far ship must NOT be served every tick (got {stillServed.Count}/40)");

        // Bounded telemetry reflects the promotion + per-tier message counts.
        string report = server.TakeUpdateAgeReport(now);
        Assert.Contains("proms=[n=", report);
        Assert.Contains("T2[m=", report);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Far_ship_cruising_on_its_predicted_line_is_not_promoted()
    {
        // The client dead-reckons far ships from the served position + velocity, so a
        // ship that moves exactly along that line has no prediction error: it stays on
        // its tier cadence instead of being promoted every time it drifts past epsilon.
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(promotionEpsilon: 100, tier2Hz: 2));
        var peers = UpdateCadenceTests.Seed(server, ships: 3, viewers: 1);
        var self = peers[0].Ship;
        self.Position = Vector3.Zero;
        var cruiser = peers[0].Sector.GetShipUpdateCandidates().First(s => s != self);
        cruiser.SideId = 1;
        cruiser.Position = new(9000, 0, 0);
        cruiser.Velocity = new(2000, 0, 0);  // 100 u per 20 Hz tick: past epsilon every tick
        cruiser.SideVisibility.SetSideVisibility(0, true);

        int served = 0;
        for (uint tick = 0; tick < 40; tick++)
        {
            now = tick / 20d;
            cruiser.Position = new(9000 + 2000 * (float)now, 0, 0);
            var heavy = UpdateCadenceTests.Heavy(
                server.TestCollectBroadcast(tick, false, "shipsUpdate")[peers[0].Conn]).Single();
            if (heavy.HeavyShipEntries.Any(e => e.ShipId == cruiser.Id)) served++;
        }
        Assert.True(served <= 8, $"a cruising far ship must stay on its tier cadence (served {served}/40)");
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Promotion_epsilon_zero_disables_big_change_promotion()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(Config(promotionEpsilon: 0, tier2Hz: 2));     // promotion disabled
        var peers = UpdateCadenceTests.Seed(server, ships: 4, viewers: 1);
        var self = peers[0].Ship;
        self.Position = Vector3.Zero;
        var subjects = peers[0].Sector.GetShipUpdateCandidates().Where(s => s != self).ToArray();
        foreach (var s in subjects) { s.SideId = 1; s.Position = new(9000, 0, 0); }
        foreach (var s in subjects) s.SideVisibility.SetSideVisibility(0, true);

        var served = new List<long>();
        for (uint tick = 0; tick < 40; tick++)
        {
            now = tick / 20d;
            if (tick >= 1) subjects[0].Position = new(subjects[0].Position.X + 500, 0, 0);
            var heavy = UpdateCadenceTests.Heavy(
                server.TestCollectBroadcast(tick, false, "shipsUpdate")[peers[0].Conn]).Single();
            if (heavy.HeavyShipEntries.Any(e => e.ShipId == subjects[0].Id)) served.Add(tick);
        }
        // No promotion: the manoeuvring far ship is served only at its 10-tick
        // cadence, never every tick.
        Assert.True(served.Count <= 8, $"disabled promotion still promoted (got {served.Count}/40)");
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Flag_off_emits_no_tier_telemetry_and_is_untouched_by_promotion()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        var config = UpdateCadenceTests.Config(hz: 2, native: false);
        config.Broadcast!.InterestTiers = false;      // the flag-off (pre-P813a) default
        server.Boot(config);
        var peers = UpdateCadenceTests.Seed(server, ships: 4, viewers: 1);
        var self = peers[0].Ship;
        self.Position = Vector3.Zero;
        foreach (var s in peers[0].Sector.GetShipUpdateCandidates())
        { if (s != self) { s.SideId = 1; s.Position = new(9000, 0, 0); s.SideVisibility.SetSideVisibility(0, true); } }
        for (uint tick = 0; tick < 10; tick++) { now = tick / 20d; server.TestCollectBroadcast(tick, false, "shipsUpdate"); }
        // The P813a promotion + per-tier telemetry only exist behind the flag.
        Assert.False(server._broadcastInterestTiersEnabled);
        string report = server.TakeUpdateAgeReport(now);
        Assert.DoesNotContain("proms=[n=", report);
        Assert.DoesNotContain("T2[m=", report);
    }
}

using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P778a — the sim half of the bomber push: the REAL <see cref="Sector"/>
/// ship-vs-ship collision pass (the same physics
/// <c>tests/Allegiance.Core.Tests/ShipShipCollisionTests.cs</c> proves, ported
/// line by line from <c>Igc/shipIGC.cpp:1023-1202</c>) with the REAL PCore001
/// hulls the showcase finale actually uses — the bomber hull
/// <see cref="ShowcaseFinale.SelectLoadout"/> buys against a Garrison, and the
/// fighter hull the escorts fly. A pusher closing astern at the chosen
/// <see cref="ShowcaseFinale.PushClosingSpeed"/> must measurably raise the
/// bomber's speed along the target line, and the per-contact damage measured on
/// both hull pools must match the native formula (the derivation behind
/// <see cref="ShowcaseFinale.PushContactDamage"/>) within float tolerance.
/// </summary>
public sealed class P778ABomberPushSimTests(ITestOutputHelper output)
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P778a_Pusher_closing_at_chosen_speed_raises_bomber_speed_and_matches_collision_damage()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();

        // The hulls the finale actually uses: the strike loadout bought against
        // a Garrison target (the showcase finale target class) and the escort fighter.
        var garrison = core.StationTypes.Values
            .First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase));
        var loadout = ShowcaseFinale.SelectLoadout(core, garrison);
        Assert.NotNull(loadout);
        var bomberHull = core.HullTypes[loadout.HullId];
        Assert.Equal(Showcase.HullRole.Bomber, Showcase.ClassifyHull(bomberHull));
        var pusherHull = core.HullTypes[(short)15];   // the "fighter" hull the escorts fly
        Assert.False(pusherHull.HasCapability(DataHullTypeIGC.HabmLifepod));
        output.WriteLine($"P778a hulls: bomber={bomberHull.Name} (id {bomberHull.HullId}, mass {bomberHull.Mass:0.#}, hp {bomberHull.HitPoints:0.#}, length {bomberHull.Length:0.#}); " +
                         $"pusher={pusherHull.Name} (id {pusherHull.HullId}, mass {pusherHull.Mass:0.#}, hp {pusherHull.HitPoints:0.#}, length {pusherHull.Length:0.#}); closing={ShowcaseFinale.PushClosingSpeed:0.#}");

        // One clean synthetic sector: no rocks, warps or stations to interact with.
        var cluster = new DataClusterIGC(0, [0, 1, 0], 0, 0, 0, 1, 0, 0, "test", "", "", 0, 0, 0, true, false);
        var map = MissionMap.FromLists([cluster], [], [], []);
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        List<SideSpec> specs = [new(0, "Team 1", civs[0].CivilizationId), new(1, "Team 2", civs[1].CivilizationId)];
        var mission = Mission.Assemble(core, map, specs, new NullIgcSite());
        mission.Stage = MissionStage.Started;
        // Native shipIGC.cpp:1222-1228: ReceiveDamage suppresses same-side damage
        // unless the mission params allow friendly fire. The collision pass itself
        // exempts only lifepods (shipIGC.cpp:1166-1168), so with FF ON the push
        // costs hull on both parties (the P778a premise the nanite scouts pay
        // back); with FF OFF (the port/server default) pushing is damage-free.
        mission.AllowFriendlyFire = true;
        var sector = mission.Sectors[0];
        var spot = new Vector3(20000, 0, 0);

        // Stern push, 1-D along +x: the bomber coasts at 20 u/s toward the target
        // (+x), the escort flies 28 u/s (closing = PushClosingSpeed), overlapping
        // by 5 so the swept detector sees the pair on the first tick. Same side —
        // friendly rams DO damage (shipIGC.cpp:1166-1168).
        const float BomberSpeed = 20f;
        float closing = ShowcaseFinale.PushClosingSpeed;
        float contact = 0.5f * bomberHull.Length + 0.5f * pusherHull.Length;
        var bomber = Place(mission, sector, 9801, bomberHull, spot, new(BomberSpeed, 0, 0), side: 0);
        var pusher = Place(mission, sector, 9802, pusherHull, spot + new Vector3(-(contact - 5f), 0, 0),
            new(BomberSpeed + closing, 0, 0), side: 0);

        float alongPre = bomber.Velocity.X;
        float pusherVPre = pusher.Velocity.X;
        Pump(mission, 1);

        Assert.False(bomber.Dead && pusher.Dead, "both hulls must survive one gentle contact");
        float alongPost = bomber.Velocity.X;
        float pusherVPost = pusher.Velocity.X;

        // (a) The push works: the bomber's speed along the target line ROSE.
        float mb = bomber.Mass, mp = pusher.Mass;
        float expectedDvBomber = 1.75f * closing * mp / (mb + mp);   // shipIGC.cpp:1093-1124
        Assert.True(alongPost > alongPre,
            $"the bomber must gain speed along the target line ({alongPre:0.###} → {alongPost:0.###})");
        Assert.True(alongPost - alongPre >= expectedDvBomber * 0.8f - 1e-3f,
            $"bomber speed gain {alongPost - alongPre:0.####} should reach the mass-weighted launch {expectedDvBomber:0.####}");

        // (b) The per-contact damage matches the native two-term split measured
        // from the velocity changes themselves (order-independent: each party's
        // damage = 2·base_own + base_other, split through the counterpart pool).
        // The pools are the CURRENT hit points (CollisionHpPool, Sector.cs:3621,
        // shipIGC.cpp:1147-1158) — full hull here, GA-scaled maxarmor included.
        float dvBomber = MathF.Abs(alongPre - alongPost);
        float dvPusher = MathF.Abs(pusherVPre - pusherVPost);
        float baseB = dvBomber * dvBomber * (1f / 128f);
        float baseP = dvPusher * dvPusher * (1f / 128f);
        float hpB = bomber.EffectiveMaxHitPoints();   // full-health pool (GA-scaled maxarmor)
        float hpP = pusher.EffectiveMaxHitPoints();
        float expectedBomberDamage = hpP * (2f * baseB + baseP) / (hpP + 2f * baseB + baseP);
        float expectedPusherDamage = hpB * (baseB + 2f * baseP) / (hpB + baseB + 2f * baseP);
        float measuredBomberDamage = (1f - bomber.Fraction) * hpB;
        float measuredPusherDamage = (1f - pusher.Fraction) * hpP;
        Assert.True(MathF.Abs(measuredBomberDamage - expectedBomberDamage) <= MathF.Max(1e-2f, expectedBomberDamage * 0.02f),
            $"bomber damage {measuredBomberDamage:0.####} must match the native formula {expectedBomberDamage:0.####}");
        Assert.True(MathF.Abs(measuredPusherDamage - expectedPusherDamage) <= MathF.Max(1e-2f, expectedPusherDamage * 0.02f),
            $"pusher damage {measuredPusherDamage:0.####} must match the native formula {expectedPusherDamage:0.####}");
        Assert.True(measuredBomberDamage < 0.01f * hpB,
            $"one contact must stay cheap for the bomber ({measuredBomberDamage:0.####} of {hpB:0.#} hp)");

        // (c) The pure helper's expectation agrees with the sim measurement.
        var (helperBomber, helperPusher) = ShowcaseFinale.PushContactDamage(mb, mp, closing, hpP, hpB);
        Assert.True(MathF.Abs(helperBomber - measuredBomberDamage) <= MathF.Max(1e-2f, measuredBomberDamage * 0.02f),
            $"helper expectation {helperBomber:0.####} must match the measured bomber damage {measuredBomberDamage:0.####}");
        Assert.True(MathF.Abs(helperPusher - measuredPusherDamage) <= MathF.Max(1e-2f, measuredPusherDamage * 0.02f),
            $"helper expectation {helperPusher:0.####} must match the measured pusher damage {measuredPusherDamage:0.####}");
        output.WriteLine($"P778a contact: bomber {alongPre:0.###}→{alongPost:0.###} u/s (expected Δv {expectedDvBomber:0.####}), " +
                         $"damages measured {measuredBomberDamage:0.####}/{measuredPusherDamage:0.####} hp, " +
                         $"formula {expectedBomberDamage:0.####}/{expectedPusherDamage:0.####} hp.");
    }

    /// <summary>With the mission's friendly-fire OFF (the server default —
    /// nothing in the port sets <see cref="Mission.AllowFriendlyFire"/>), the
    /// SAME contact transfers the same momentum but ReceiveDamage suppresses
    /// all of it: pushing a bomber is then damage-free (shipIGC.cpp:1222-1228).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P778a_WithFriendlyFireOff_a_push_contact_is_damage_free()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var cluster = new DataClusterIGC(0, [0, 1, 0], 0, 0, 0, 1, 0, 0, "test", "", "", 0, 0, 0, true, false);
        var map = MissionMap.FromLists([cluster], [], [], []);
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        List<SideSpec> specs = [new(0, "Team 1", civs[0].CivilizationId), new(1, "Team 2", civs[1].CivilizationId)];
        var mission = Mission.Assemble(core, map, specs, new NullIgcSite());
        mission.Stage = MissionStage.Started;
        Assert.False(mission.AllowFriendlyFire);
        var sector = mission.Sectors[0];
        var spot = new Vector3(20000, 0, 0);
        var bomberHull = core.HullTypes[(short)23];
        var pusherHull = core.HullTypes[(short)15];
        float contact = 0.5f * bomberHull.Length + 0.5f * pusherHull.Length;
        var bomber = Place(mission, sector, 9811, bomberHull, spot, new(20, 0, 0), side: 0);
        var pusher = Place(mission, sector, 9812, pusherHull, spot + new Vector3(-(contact - 5f), 0, 0),
            new(20 + ShowcaseFinale.PushClosingSpeed, 0, 0), side: 0);
        Pump(mission, 1);
        Assert.True(bomber.Velocity.X > 20f, "the momentum transfer happens regardless of the FF setting");
        Assert.Equal(1f, bomber.Fraction, 5);
        Assert.Equal(1f, pusher.Fraction, 5);
    }

    private static Ship Place(Mission m, Sector sector, short id, DataHullTypeIGC hull,
        Vector3 pos, Vector3 vel, short side)
    {
        var ship = new Ship(id, hull, pos, vel, Orientation.Identity);
        ship.SideId = side;
        ship.StateM = ShipControlState.CoastButton;   // preserve velocity (no auto-brake)
        m.PlaceShip(ship, sector);
        return ship;
    }

    private static void Pump(Mission m, int n, double step = 0.25)
    {
        for (int i = 0; i < n; i++) m.Update(m.LastUpdate + step);
    }
}

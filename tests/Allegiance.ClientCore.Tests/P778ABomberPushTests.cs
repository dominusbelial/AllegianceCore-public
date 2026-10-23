using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>P778a — finale bomber wing: escorts push their bomber, nanite scouts
/// repair bombers first. Pure geometry/selection helper proofs (the driver-side
/// stop conditions are proven against a wired rig in
/// <see cref="BotFixRegressionTests"/>; the physics against the real
/// <c>Sector</c> collision pass in <see cref="P778ABomberPushSimTests"/>).</summary>
public sealed class P778ABomberPushTests
{
    // ── push geometry ──────────────────────────────────────────────────────────
    [Fact]
    public void P778a_PushPointSitsJustBehindTheBomberOnTheBomberTargetLine()
    {
        var bomber = new Vector3(10000, 0, 0);
        var target = new Vector3(0, 0, 0);          // station behind the bomber (−x)
        var escort = new Vector3(11000, 0, 0);      // astern of it (+x side)
        var plan = ShowcaseFinale.BomberPush(escort, bomber, target, bomberRadius: 20f, pusherRadius: 10f);
        Assert.NotNull(plan);
        var p = plan!.Value;
        // Contact distance = sum of the two hull radii + the small margin, and
        // the contact point sits exactly that far BEHIND the bomber on the
        // bomber→target line (target is at −x, so astern is +x).
        Assert.Equal(20f + 10f + ShowcaseFinale.PusherMargin, p.ContactDistance, 3);
        var behind = p.ContactPoint - bomber;
        Assert.Equal(p.ContactDistance, behind.Length(), 3);
        Assert.Equal(1f, behind.X / behind.Length(), 3);   // on the line, away from the target
        Assert.Equal(-1f, p.Direction.X, 3);        // pointing at the target (−x)
        Assert.Equal(0f, p.Direction.Y, 4);
        Assert.Equal(ShowcaseFinale.PushClosingSpeed, p.ClosingSpeed, 3);
    }

    [Fact]
    public void P778a_EscortAheadOfTheBomberNeverPushes()
    {
        var bomber = new Vector3(0, 0, 0);
        var target = new Vector3(10000, 0, 0);
        // Escort between the bomber and the target, beyond the contact shell:
        // pushing would mean nosing BACKWARDS — not the tactic.
        Assert.Null(ShowcaseFinale.BomberPush(new(500, 0, 0), bomber, target, 20f, 10f));
        // Just inside the shell (slightly ahead) is still a push.
        Assert.NotNull(ShowcaseFinale.BomberPush(new(30f, 0, 0), bomber, target, 20f, 10f));
        // Degenerate lines and unknown radii never plan a push.
        Assert.Null(ShowcaseFinale.BomberPush(new(-100, 0, 0), bomber, bomber, 20f, 10f));
        Assert.Null(ShowcaseFinale.BomberPush(new(-100, 0, 0), bomber, target, 0f, 10f));
        Assert.Null(ShowcaseFinale.BomberPush(new(-100, 0, 0), bomber, target, 20f, 0f));
        // Diagonal line: the contact point stays on the bomber→target line.
        var diag = ShowcaseFinale.BomberPush(new(-100, -100, 0), bomber, new(3000, 3000, 0), 20f, 10f)!.Value;
        var fromBomber = diag.ContactPoint - bomber;
        Assert.True(Vector3.CrossProduct(fromBomber, new(1, 1, 0)).Length() < 1e-2f, "contact point must be on the line");
    }

    // ── stable pusher selection ───────────────────────────────────────────────
    [Fact]
    public void P778a_PusherPickIsTheTwoLowestInSectorEscortIds_AndThreatCancelsIt()
    {
        short[] escorts = [7, 3, 5, 4];
        Assert.True(ShowcaseFinale.IsBomberPusher(escorts, bomberThreatened: false, self: 3));
        Assert.True(ShowcaseFinale.IsBomberPusher(escorts, bomberThreatened: false, self: 4));
        // Two pushers max — the third-lowest id never pushes.
        Assert.False(ShowcaseFinale.IsBomberPusher(escorts, bomberThreatened: false, self: 5));
        Assert.False(ShowcaseFinale.IsBomberPusher(escorts, bomberThreatened: false, self: 7));
        // Threat response keeps priority: any attacker on the bomber inside the
        // threat window cancels pushing for every escort.
        foreach (short self in escorts)
            Assert.False(ShowcaseFinale.IsBomberPusher(escorts, bomberThreatened: true, self));
        // Fewer escorts than the cap: all of them push.
        Assert.True(ShowcaseFinale.IsBomberPusher(new short[] { 9 }, false, 9));
        // Stable under input permutation (the pick is deterministic per id set).
        Assert.Equal(ShowcaseFinale.IsBomberPusher([3, 4], false, 3), ShowcaseFinale.IsBomberPusher([4, 3], false, 3));
    }

    // ── closing speed ↔ damage ────────────────────────────────────────────────
    [Fact]
    public void P778a_ContactDamageFormulaMatchesTheNativeTwoBodySplit()
    {
        // Equal masses: Δv = 1.75·s/2 for both, base = Δv²/128, damage = 3·base,
        // and the hp-weighted native share (shipIGC.cpp:1141-1186).
        float s = ShowcaseFinale.PushClosingSpeed;
        var (bomber, pusher) = ShowcaseFinale.PushContactDamage(100f, 100f, s, pusherHpPool: 300f, bomberHpPool: 300f);
        float dv = 1.75f * s / 2f;
        float d = 3f * dv * dv / 128f;
        Assert.Equal(300f * d / (300f + d), bomber, 4);
        Assert.Equal(300f * d / (300f + d), pusher, 4);
        // Heavy bomber, light pusher: the bomber's own Δv shrinks with the mass
        // ratio while the pusher's approaches 1.75·s — the bomber's worst-case
        // share is bounded by the pusher's base term.
        var (heavy, light) = ShowcaseFinale.PushContactDamage(2500f, 40f, s, pusherHpPool: 400f, bomberHpPool: 3000f);
        float dvBomber = 1.75f * s * 40f / 2540f;
        float dvPusher = 1.75f * s * 2500f / 2540f;
        float baseB = dvBomber * dvBomber / 128f, baseP = dvPusher * dvPusher / 128f;
        Assert.Equal(400f * (2f * baseB + baseP) / (400f + 2f * baseB + baseP), heavy, 3);
        Assert.Equal(3000f * (baseB + 2f * baseP) / (3000f + baseB + 2f * baseP), light, 3);
        // A gentler closing speed strictly reduces both parties' damage.
        var (gentleB, gentleP) = ShowcaseFinale.PushContactDamage(2500f, 40f, s / 2f, 400f, 3000f);
        Assert.True(gentleB < heavy && gentleP < light);
    }
}

/// <summary>P778a — driver-side stop conditions and the bomber-first repair
/// priority, against a wired finale rig (no sockets).</summary>
public sealed partial class BotFixRegressionTests
{
    private static object RigPush(FinaleRig rig, object escort, ConnectionSession escortCs,
        object bomber, object run, double now)
    {
        ShipEntity? leader;
        lock (escortCs.Gate) leader = escortCs.World.Ships.GetValueOrDefault(Get<short>(bomber, "ShipId"));
        return Call("TryPushFinaleBomber", rig.Manager, escort, escortCs, run, leader, now)!;
    }

    private static void PlaceInSector4(ConnectionSession cs, short id, short hullId, float x)
    {
        cs.World.Apply(new ExportShip { ShipId = id, SideId = 0, HullId = hullId });
        var bytes = new byte[ServerShipUpdate.Width];
        var writer = new WireWriter(bytes); Pose(id, x).EncodeBody(ref writer); bytes[40] = 255; // full hull
        var reader = new WireReader(bytes);
        cs.World.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        cs.World.Apply(new ShipStatusMessage { ShipId = id, HullId = hullId, SectorId = 4, StationId = -1 });
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P778a_EscortsPushTheBomberInTheTargetSector_AndStopAtLaunchRangeOrThreat()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        // 6 per side: bots 0..5 side 0 (0 = reserved bomber, 1 = bomber-hull fighter,
        // 2..5 fighters); EscortShare 1 → all 4 fighters become escorts (ship ids 3..6).
        using var rig = new FinaleRig(new(Bombers: 1, EscortShare: 1.0), 6);
        rig.Mature();
        var bomber = rig.Bots[0];
        var run = Get<object>(bomber, "Finale");
        Set(run, "StrikeStartedAt", (double?)(rig.Started + 1801));
        Set(bomber, "PodDocked", true);
        Call("PrepareFinaleRelaunch", null, bomber, rig.Started + 1801);
        Set(bomber, "PodDocked", false);
        Assert.True(Get<bool>(bomber, "FinaleBomberActive"));
        var escorts = rig.Bots.Skip(2).Take(4).ToArray();
        foreach (var escort in escorts)
        {
            Set(escort, "PodDocked", true);
            Call("PrepareFinaleRelaunch", null, escort, rig.Started + 1801);
            Set(escort, "PodDocked", false);
            Assert.True(Get<bool>(escort, "FinaleEscort"));
        }
        // The bomber's own ordnance envelope must be known for the stop condition
        // (PrepareFinaleRelaunch may rewrite the launcher part from the loadout).
        var core = Catalog.RepresentativeCore();
        var launcher = core.Launchers.First(kv => core.MissileTypes.TryGetValue(kv.Value.ExpendabletypeId, out var m) && m.Lifespan > 0).Key;
        Set(bomber, "LauncherPartId", (short?)launcher);
        float range = (float)Call("FinaleLaunchRange", null, bomber)!;
        Assert.True(range > 0, "the rigged bomber needs a launcher with a real flight envelope");
        short bomberHullId = Get<short>(bomber, "HullId");
        float outDistance = MathF.Max(range * 2f, 3000f);   // strictly beyond the bomber's own launch range

        // Out of launch range, astern of the bomber, in the target sector: the two
        // LOWEST escort ship ids push; the others hold their escort station.
        double now = rig.Started + 1802;
        var bomberCs = rig.Sessions[0];
        short[] escortIds = escorts.Select(e => Get<short>(e, "ShipId")).ToArray();
        // The escort's own SET_CLUSTER retires every remote pose (native appmsg.cpp:1009-1023),
        // so presence for the whole wing is re-established AFTER it in each session.
        for (int i = 0; i < 4; i++)
            rig.Sessions[2 + i].World.Apply(new SetCluster { Ship = Pose(escortIds[i], outDistance + 1000), SectorId = 4 });
        PlaceInSector4(bomberCs, 1, bomberHullId, x: outDistance);
        foreach (var session in new[] { rig.Sessions[2], rig.Sessions[3], rig.Sessions[4], rig.Sessions[5] })
        {
            PlaceInSector4(session, 1, bomberHullId, x: outDistance);
            for (int i = 0; i < 4; i++)                                 // every escort sees the whole wing
                PlaceInSector4(session, escortIds[i], Get<short>(escorts[i], "HullId"), x: outDistance + 1000 + i);
        }
        Assert.True((bool)RigPush(rig, escorts[0], rig.Sessions[2], bomber, run, now), "lowest escort id pushes");
        Assert.True((bool)RigPush(rig, escorts[1], rig.Sessions[3], bomber, run, now), "second-lowest escort id pushes");
        Assert.False((bool)RigPush(rig, escorts[2], rig.Sessions[4], bomber, run, now), "third escort holds (two pushers max)");
        Assert.False((bool)RigPush(rig, escorts[3], rig.Sessions[5], bomber, run, now), "fourth escort holds (two pushers max)");
        Assert.Null(Get<short?>(escorts[0], "TargetId"));   // pushing escorts carry no combat target

        // A threat on the bomber inside the threat window cancels every push.
        Get<Dictionary<short, double>>(bomber, "RecentAttackers")[90] = now;
        foreach (var (escort, session) in new[] { (escorts[0], rig.Sessions[2]), (escorts[1], rig.Sessions[3]) })
            Assert.False((bool)RigPush(rig, escort, session, bomber, run, now), "threat response keeps priority");
        Get<Dictionary<short, double>>(bomber, "RecentAttackers").Clear();

        // Within the bomber's own launch range of the target the push stops —
        // and the per-strike summary is logged once by the finale tick.
        PlaceInSector4(bomberCs, 1, bomberHullId, x: range * .5f);
        for (int i = 0; i < 2; i++)
            PlaceInSector4(rig.Sessions[2 + i], 1, bomberHullId, x: range * .5f);
        Assert.False((bool)RigPush(rig, escorts[0], rig.Sessions[2], bomber, run, now), "bomber can engage: no push");
        rig.Tick(rig.Started + 1803);
        var plan = ((System.Collections.IDictionary)Get<object>(run, "Plans"))[(short)0]!;
        Assert.NotNull(Get<double?>(plan, "LaunchRangeAt"));
        Assert.True(Get<bool>(plan, "SummaryLogged"));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P778a_RepairPriority_BomberThenPusherThenStation_unchangedOutsideFinale()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var cs = Session(true);
        var w = cs.World;
        DefenseMap(w);
        void Friend(short id, float hull, short sector = 1)
        {
            w.Apply(new ExportShip { ShipId = id, SideId = 0, HullId = 15 });
            var bytes = new byte[ServerShipUpdate.Width];
            var writer = new WireWriter(bytes); Pose(id, id * 10).EncodeBody(ref writer); bytes[40] = (byte)(hull * 240);
            var reader = new WireReader(bytes);
            w.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.DecodeBody(ref reader) });
            w.Apply(new ShipStatusMessage { ShipId = id, HullId = 15, SectorId = sector, StationId = -1, ParentId = -1 });
        }
        short[] bombers = [2];
        short[] pushers = [3];
        Friend(2, .5f);   // damaged finale bomber
        Friend(3, .5f);   // damaged pusher
        Friend(4, .5f);   // damaged other ship
        w.Apply(new StationsUpdate { Entries = [new StationsUpdateEntry(100, 120, 240)] });   // damaged station
        // Bomber first, then pushers, then the station-first existing order.
        Assert.Equal((short)2, BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, bombers, pushers)!.Value.Id);
        Assert.True(BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, bombers, pushers)!.Value.FinaleBomber);
        w.Apply(new KillShip { ShipId = 2, BDeathCredit = true });
        var pusher = BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, bombers, pushers)!.Value;
        Assert.Equal((short)3, pusher.Id);
        Assert.True(pusher.FinalePusher);
        w.Apply(new KillShip { ShipId = 3, BDeathCredit = true });
        var station = BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, bombers, pushers)!.Value;
        Assert.True(station.Station);
        // A full-hull bomber never outranks a damaged station.
        Friend(5, 1f);
        Assert.True(BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, [5], pushers)!.Value.Station);
        // Outside a finale (no lists) the behaviour is unchanged: the damaged
        // station outranks every damaged ship, exactly as before P778a.
        Assert.True(BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero)!.Value.Station);
        // Repair the station and the nearest damaged ship wins, again as before.
        w.Apply(new StationsUpdate { Entries = [new StationsUpdateEntry(100, 240, 240)] });
        Assert.Equal((short)4, BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero)!.Value.Id);
    }
}

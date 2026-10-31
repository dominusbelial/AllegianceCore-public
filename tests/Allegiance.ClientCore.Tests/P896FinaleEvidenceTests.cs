using System.Collections;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P896 — the finale may not call a station dead because a side has not observed
/// it.
///
/// <para><b>The b59 defect.</b> <c>RearmRotatedFinales</c> handed
/// <c>RegisterFinale</c> the UNION of every side's observed stations, so after the
/// P831 fog grace one side could be given a target only the ENEMY's own world
/// held. <c>UpdateFinales</c> then read "this world's station table has no such
/// id" as "destroyed", which under native map discovery (P797) only means "never
/// observed" — the M5 run logged <c>outcome=station-killed hitsInWindow=0
/// hull=-1-&gt;-1</c> for both sides right after the trigger while neither garrison
/// had been hit.</para>
///
/// <para><b>Rules enforced here.</b> A side only ever receives a target from its
/// OWN worlds; a blind side is registered in a legal "no target yet" state and
/// picks one up as soon as it sees a station. A station is dead only on real
/// evidence: the WHOLE-MISSION <c>STATION_DESTROYED(59)</c> the world received
/// (<c>src/Allegiance.Server/Server.Combat.cs:956</c>, native
/// <c>Messages.h:436-439</c>) or a station this side really observed and then
/// lost.</para>
/// </summary>
public sealed partial class BotFixRegressionTests
{
    private static DataStationIGC P896Garrison(short id, short side, short sector) =>
        Station(id, side, sector) with { StationTypeId = P831GarrisonType() };

    /// <summary>A fogged world: real topology, the bot's own ship, and ONLY the
    /// stations this side has observed.</summary>
    private static void P896World(ClientWorld world, short ship, short side, short sector,
        IEnumerable<DataStationIGC> seen)
    {
        world.Reset();
        world.Apply(new ClusterInfo
        {
            Clusters = [new(1, true), new(2, false), new(3, false), new(4, true)],
            Warps = [new(1, 2), new(2, 3), new(3, 4)],
        });
        world.Apply(new ExportCluster { Cluster = new(0, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Home", "", "", 0, 0, 0, true, false) });
        Seat(world, ship, sector);
        world.Apply(new JoinSide { ShipId = ship, SideId = side });
        foreach (var station in seen) world.Apply(new ExportStation { Station = station });
    }

    private static object P896Plan(object run, short side) =>
        ((IDictionary)run.GetType().GetField("Plans", FinaleFields)!.GetValue(run)!)[side]!;

    /// <summary>Fixture: <paramref name="perSide"/> bots a side, the fleet-wide
    /// station union handed to <see cref="BotManager.RegisterFinale"/> exactly like
    /// the production callers do, and each side's own world holding only what that
    /// side has observed.</summary>
    private static (BotManager Manager, ConnectionSession[] Sessions, object[] Bots, object Run, double Created)
        P896Finale(int perSide, IReadOnlyList<DataStationIGC> fleet,
            Func<short, IEnumerable<DataStationIGC>> observed, ShowcaseFinale.Settings? settings = null)
    {
        var core = Catalog.RepresentativeCore();
        int count = perSide * 2;
        var sessions = Enumerable.Range(0, count).Select(_ => Session(true)).ToArray();
        var bots = new object[count];
        for (int i = 0; i < count; i++)
        {
            short side = (short)(i / perSide);
            var cs = sessions[i];
            P896World(cs.World, (short)(i + 1), side, (short)(1 + (i % 2)), observed(side));
            cs.World.Apply(new SideTechChange { SideId = side, TtbmDevelopments = core.ComputeRequiredTechUnion().ToBytes() });
            bots[i] = Bot(cs, (short)(i + 1));
            Set(bots[i], "Name", $"p896-{i}");
            Set(bots[i], "Side", side);
            Set(bots[i], "CompletedPodCycles", 1);
            Set(bots[i], "TargetSector", (short?)1);
            DefenseNoSocketSender(bots[i]);
        }
        var manager = Manager(bots);
        manager.RegisterFinale(settings ?? new(AfterMinutes: .01, Side: "both", MinRelaunchShare: 0,
            MinKillsLastFiveMinutes: 1, Bombers: 2), bots.Select(b => Get<string>(b, "Name")).ToArray(), fleet);
        var run = Get<object>(bots[0], "Finale");
        return (manager, sessions, bots, run, Get<double>(run, "Created"));
    }

    /// <summary>Fire the finale and advance past it, so the per-bot death-evidence
    /// pass has run at least once with the strike armed.</summary>
    private static string P896Trigger(BotManager manager, object run, double created)
    {
        var kills = Get<Queue<(double At, short Side)>>(run, "Kills");
        double at = created + 60;
        foreach (short side in new short[] { 0, 1 }) for (int i = 0; i < 3; i++) kills.Enqueue((at, side));
        string log = P831Capture(() =>
        {
            Call("UpdateFinales", manager, at);
            Call("UpdateFinales", manager, at + 2);
        });
        return log;
    }

    private static bool P896Triggered(object run) => Get<double?>(run, "TriggeredAt") is not null;

    [SkippableFact]
    public void P896_Station_absence_is_not_death_and_destruction_is_remembered()
    {
        Allegiance.TestInfra.Require.Artwork();
        using var world = new ConnectionSession();
        world.World.Reset();
        world.World.Apply(new ExportStation { Station = P896Garrison(70, 1, 1) });
        Assert.True(world.World.Stations.ContainsKey(70));
        Assert.False(world.World.StationWasDestroyed(70), "an observed station is not destroyed");

        // The WHOLE-MISSION broadcast records the evidence even for an id this
        // world never had exported (an unobserved enemy station under fog).
        world.World.Apply(new StationDestroyed { StationId = 70, Launcher = -1 });
        Assert.False(world.World.Stations.ContainsKey(70));
        Assert.True(world.World.StationWasDestroyed(70));
        world.World.Apply(new StationDestroyed { StationId = 71, Launcher = -1 });
        Assert.True(world.World.StationWasDestroyed(71), "a destroyed station the side never saw is still dead");

        // A fresh mission clears the evidence: a new mission's station ids are new
        // and no death from the old one may leak into the finale re-arm.
        world.World.BeginNextMission();
        Assert.False(world.World.StationWasDestroyed(70));
    }

    /// <summary>RegisterFinale used to hand every side the union of all sides'
    /// stations. Side 1 here has observed only its own garrison: it must be armed
    /// with NO target (not side 0's garrison, which it has never seen), and must
    /// pick one up the moment its own world reports one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P896_A_side_is_never_armed_with_a_station_it_has_not_observed()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var fleet = new[] { P896Garrison(100, 0, 1), P896Garrison(101, 1, 4) };
        var rig = P896Finale(2, fleet, side => side == 0 ? fleet : [fleet[1]]);
        try
        {
            Assert.Equal(101, Get<short>(P896Plan(rig.Run, 0), "TargetId"));
            // P896: side 1 has seen no enemy station, so it holds no target at all.
            Assert.Equal((short)-1, Get<short>(P896Plan(rig.Run, 1), "TargetId"));

            // Observing it is what grants the target — and only then.
            string log = P831Capture(() =>
            {
                rig.Sessions[2].World.Apply(new ExportStation { Station = fleet[0] });
                Call("UpdateFinales", rig.Manager, rig.Created + 2);
            });
            Assert.Contains("side=1 observed enemy station#100", log);
            Assert.Equal(100, Get<short>(P896Plan(rig.Run, 1), "TargetId"));
            Assert.Equal((short)0, Get<short>(P896Plan(rig.Run, 1), "TargetSide"));
            Assert.Equal((short)1, Get<short>(P896Plan(rig.Run, 1), "Sector"));
        }
        finally { foreach (var cs in rig.Sessions) cs.Dispose(); }
    }

    /// <summary>Regression for the b59 M5 line: with the strike armed, a target the
    /// side never observed must never be reported as <c>station-killed</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P896_Fog_absence_never_reports_the_target_as_killed()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var fleet = new[] { P896Garrison(100, 0, 1), P896Garrison(101, 1, 4) };
        var rig = P896Finale(2, fleet, side => side == 0 ? fleet : [fleet[1]]);
        try
        {
            string log = P896Trigger(rig.Manager, rig.Run, rig.Created);
            Assert.True(P896Triggered(rig.Run), log);
            Assert.Contains("no enemy station observed yet", log);
            Assert.DoesNotContain("finale end", log);
            Assert.Null(Get<double?>(P896Plan(rig.Run, 1), "KilledAt"));
            // Side 0 observed its target, so nothing about side 0's plan changed.
            Assert.Null(Get<double?>(P896Plan(rig.Run, 0), "KilledAt"));
        }
        finally { foreach (var cs in rig.Sessions) cs.Dispose(); }
    }

    /// <summary>The export reaches a side's bots at different moments. A side that
    /// first sees its target after the trigger must not read the teammate whose
    /// copy is still in flight as a lost station, and it must get the same strike
    /// loadout as a side armed at the trigger.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P896_A_late_sighting_arms_a_loadout_and_a_lagging_teammate_is_no_kill()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var fleet = new[] { P896Garrison(100, 0, 1), P896Garrison(101, 1, 4) };
        var rig = P896Finale(2, fleet, side => side == 0 ? fleet : [fleet[1]]);
        try
        {
            string log = P896Trigger(rig.Manager, rig.Run, rig.Created);
            Assert.True(P896Triggered(rig.Run), log);

            log = P831Capture(() =>
            {
                rig.Sessions[2].World.Apply(new ExportStation { Station = fleet[0] });
                Call("UpdateFinales", rig.Manager, rig.Created + 64);
            });
            Assert.Contains("side=1 observed enemy station#100", log);
            Assert.DoesNotContain("no shop", log);
            Assert.NotNull(Get<ShowcaseFinale.StrikeLoadout?>(P896Plan(rig.Run, 1), "Loadout"));
            Assert.Null(Get<double?>(P896Plan(rig.Run, 1), "KilledAt"));
            Assert.DoesNotContain("finale end", log);
        }
        finally { foreach (var cs in rig.Sessions) cs.Dispose(); }
    }

    /// <summary>Two real evidences, and only those: a station this side really
    /// observed and then lost (a client-side table rebuild such as the P813c
    /// reconnect burst) and the WHOLE-MISSION STATION_DESTROYED(59).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P896_A_kill_needs_observed_then_lost_or_a_destroyed_broadcast()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var fleet = new[] { P896Garrison(100, 0, 1), P896Garrison(101, 1, 4) };
        var rig = P896Finale(2, fleet, _ => fleet);
        try
        {
            string log = P896Trigger(rig.Manager, rig.Run, rig.Created);
            Assert.True(P896Triggered(rig.Run), log);
            Assert.Null(Get<double?>(P896Plan(rig.Run, 0), "KilledAt"));

            // (a) observed then lost: the world's own station table is rebuilt (the
            // reconnect path) and the target is not re-exported.
            var stations = (IDictionary)typeof(ClientWorld)
                .GetField("_stations", FinaleFields)!.GetValue(rig.Sessions[0].World)!;
            stations.Clear();
            // One world losing it proves nothing while a teammate's still holds it.
            Call("UpdateFinales", rig.Manager, rig.Created + 63);
            Assert.Null(Get<double?>(P896Plan(rig.Run, 0), "KilledAt"));
            ((IDictionary)typeof(ClientWorld).GetField("_stations", FinaleFields)!
                .GetValue(rig.Sessions[1].World)!).Clear();
            string ended = P831Capture(() => Call("UpdateFinales", rig.Manager, rig.Created + 65));
            Assert.Equal("observed-lost", Get<string?>(P896Plan(rig.Run, 0), "KilledEvidence"));
            Assert.NotNull(Get<double?>(P896Plan(rig.Run, 0), "KilledAt"));
            Assert.Contains("outcome=station-killed", ended);
            Assert.Contains("evidence=observed-lost", ended);

            // (b) a real STATION_DESTROYED(59) is evidence even for a side whose own
            // station table never held the target.
            rig.Sessions[3].World.Apply(new StationDestroyed { StationId = 100, Launcher = -1 });
            Assert.True(rig.Sessions[3].World.StationWasDestroyed(100));
            Call("UpdateFinales", rig.Manager, rig.Created + 70);
            Assert.Equal("destroyed", Get<string?>(P896Plan(rig.Run, 1), "KilledEvidence"));
            Assert.NotNull(Get<double?>(P896Plan(rig.Run, 1), "KilledAt"));
        }
        finally { foreach (var cs in rig.Sessions) cs.Dispose(); }
    }

    /// <summary>A wave that provably cannot damage the target is never committed:
    /// the P801 planner already refuses such loadouts (LoadoutCount 0), and P896
    /// makes the finale hold instead of feeding the defenders unarmed bombers that
    /// would leave the station at hull=1.000/shield=1.000 for the next wave.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P896_A_wave_with_no_station_damaging_loadout_is_not_committed()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var fleet = new[] { P896Garrison(100, 0, 1), P896Garrison(101, 1, 4) };
        var rig = P896Finale(2, fleet, _ => fleet);
        try
        {
            // The rig b59 shape: bots reserved as finale bombers whose granted
            // hardware is still the ordinary fighter gun (no anti-base round).
            var fighterParts = Catalog.NativePreferredLoadout(15, "fighter").Parts;
            foreach (var bot in rig.Bots)
            {
                Set(bot, "HullId", (short)15);
                Set(bot, "Parts", fighterParts);
                Set(bot, "FinaleBomberActive", true);
                Set(bot, "FinaleBomberReserved", true);
                Set(bot, "FinaleGrantVerified", true);
            }
            var core = Catalog.RepresentativeCore();
            var garrison = core.StationTypes.Values.First(s => s.StationTypeId == Get<short>(P896Plan(rig.Run, 0), "TargetTypeId"));
            var loadout = StrikeVolley.FromParts(core, 15, fighterParts);
            var requirement = StrikeVolley.Compute(StrikeVolley.StationProfile.From(core, garrison), [loadout], 2);
            Assert.Equal(0, requirement.LoadoutCount);   // the fighter gun is not a station weapon

            string log = P896Trigger(rig.Manager, rig.Run, rig.Created);
            Assert.True(P896Triggered(rig.Run), log);
            Assert.Contains("finale holding", log);
            Assert.DoesNotContain("finale commit", log);
            Assert.Null(Get<double?>(P896Plan(rig.Run, 0), "GoTime"));
        }
        finally { foreach (var cs in rig.Sessions) cs.Dispose(); }
    }

    /// <summary>The actual b59 path: after a rotation the P831 fog grace arms the
    /// next match, and the union of observed stations used to give the blind side
    /// the ENEMY's own garrison. Through the real re-arm the blind side must stay
    /// targetless until its own worlds report one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P896_Rearm_never_arms_a_blind_side_with_an_unobserved_station()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var (manager, sessions, _, rotatedAt) = P831RotatedRoster(2, blindSide: true);
        try
        {
            string grace = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + BotManager.RearmGraceSeconds + 1));
            Assert.Contains("re-armed for next mission", grace);
            // P831RotatedRoster's blind side 1 holds only its OWN station 201; the
            // fleet union also holds side 0's 200, which side 1 never observed.
            var runs = JsonSerializer.SerializeToElement(manager.FinaleStatus());
            var fresh = runs[1].GetProperty("wings");
            Assert.Equal(201, fresh.EnumerateArray().Single(w => w.GetProperty("side").GetInt16() == 0)
                .GetProperty("targetStation").GetInt16());
            Assert.Equal(-1, fresh.EnumerateArray().Single(w => w.GetProperty("side").GetInt16() == 1)
                .GetProperty("targetStation").GetInt16());

            // The side picks the target up as soon as IT observes one.
            string resolved = P831Capture(() =>
            {
                sessions[2].World.Apply(new ExportStation { Station = P831Station(200, 0, 1) });
                Call("UpdateFinales", manager, rotatedAt + BotManager.RearmGraceSeconds + 3);
            });
            Assert.Contains("side=1 observed enemy station#200", resolved);
            var after = JsonSerializer.SerializeToElement(manager.FinaleStatus())[1].GetProperty("wings");
            Assert.Equal(200, after.EnumerateArray().Single(w => w.GetProperty("side").GetInt16() == 1)
                .GetProperty("targetStation").GetInt16());
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }
}

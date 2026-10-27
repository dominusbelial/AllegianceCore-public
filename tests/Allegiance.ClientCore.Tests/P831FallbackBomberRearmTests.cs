using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P831 — after a rotation a requested-Bomber bot flying a P821 fallback hull
/// (requested role survives, the hull is a fighter/interceptor) never finished
/// its new-mission setup: its objective was derived through the bomber branch,
/// which needs either an enemy station or a uniquely deducible enemy home — and
/// under native fog a fresh mission has neither until somebody flies. Because a
/// bot with <c>ObjectivesPending</c> is never driven it could not obtain that
/// knowledge itself, so it stayed pending forever, and the finale re-arm
/// (<c>RearmRotatedFinales</c>) waited on it and left the match without the only
/// deadline a showcase has (rig batches 34 match B, 36 match 3: 48 of 48 such
/// bots, hours, no deadline). The fix has two halves, both bot-side:
/// <list type="number">
/// <item>a fresh life ALWAYS resolves its objectives — an undeducible objective
/// becomes "no fixed objective yet" (a resolvable state the clash assignment
/// fills in) instead of pending forever;</item>
/// <item>the re-arm is bounded in time: a bot still blocked past the grace is
/// excluded, named in the log, and the next match's finale is armed anyway.</item>
/// </list>
/// </summary>
public sealed partial class BotFixRegressionTests
{
    private const BindingFlags FinaleFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static DataStationIGC P831Station(short id, short side, short sector) =>
        Station(id, side, sector) with { StationTypeId = P831GarrisonType() };

    private static short P831GarrisonType() => Catalog.RepresentativeCore().StationTypes.Values
        .OrderBy(s => s.StationTypeId).First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase)).StationTypeId;

    /// <summary>A fresh-mission world under native fog: the full topology with home
    /// flags (CLUSTERINFO), every cluster NAME exported at join, the bot's own ship
    /// reseated, and only the stations the side has really observed.</summary>
    private static void P831FreshWorld(ClientWorld world, short ship, short side, short sector,
        IEnumerable<DataStationIGC> stations, bool stageStarted = false)
    {
        world.BeginNextMission();
        Seat(world, ship, sector);
        world.Apply(new JoinSide { ShipId = ship, SideId = side });
        world.Apply(new ClusterInfo
        {
            Clusters = [new(1, true), new(2, false), new(3, false), new(4, true), new(7, false)],
            Warps = [new(1, 2), new(2, 3), new(3, 4), new(3, 7)],
        });
        foreach (var (id, name) in new[] { (0, "Home"), (1, "Mid"), (2, "Far"), (3, "Enemy Home"), (6, "Target") })
            world.Apply(new ExportCluster
            {
                Cluster = new(0, [0, 1, 0], 0, 0, 0, (short)id, 0, 0, name, "", "", 0, 0, 0, true, false),
            });
        foreach (var station in stations) world.Apply(new ExportStation { Station = station });
        if (stageStarted) world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
    }

    private static object[] P831FinaleRuns(BotManager manager) =>
        typeof(BotManager).GetField("_finales", FinaleFields)!.GetValue(manager) is System.Collections.IEnumerable runs
            ? runs.Cast<object>().ToArray() : [];

    private static int P831FinaleBots(object run) =>
        ((Array)run.GetType().GetField("Bots", FinaleFields)!.GetValue(run)!).Length;

    private static string P831Capture(Action action)
    {
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try { action(); }
        finally { Console.SetOut(saved); }
        return log.ToString();
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P831_Fallback_bomber_resolves_its_new_mission_objectives_under_fog()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var core = Catalog.RepresentativeCore();
        var fighter = Showcase.RepresentativeHullOfRole(core, Showcase.HullRole.Fighter)!.Value;
        using var cs = Session();
        var bot = Bot(cs);
        var manager = Manager(bot);
        Call("RefreshLife", manager, bot, cs, 0.0);
        // P821: the requested Bomber duty survives, the hull is a fighter fallback.
        Set(bot, "Role", "bomber");
        Set(bot, "HullId", fighter.HullId);
        Set(bot, "HullName", fighter.Name);
        Set(bot, "TargetSector", (short?)4);   // objective carried over from the previous match
        // The rotation: fresh world, native fog, no station observed yet — the topology
        // shows BOTH home sectors, so the enemy home is not deducible (the rig's case).
        string log = P831Capture(() =>
        {
            P831FreshWorld(cs.World, 2, 0, 1, []);
            Assert.True((bool)Call("RefreshLife", manager, bot, cs, 1.0)!);
            Assert.False(Get<bool>(bot, "ObjectivesPending"), "a fresh life must always resolve its objectives");
            Assert.Null(Get<short?>(bot, "TargetSector"));   // no stale sector from the old map
            Assert.True(Get<bool>(bot, "ObjectiveFallbackLogged"));
            // Resolved is sticky: later passes neither re-resolve nor re-log.
            Assert.True((bool)Call("RefreshLife", manager, bot, cs, 2.0)!);
            Assert.Null(Get<short?>(bot, "TargetSector"));
        });
        var lines = log.Split('\n').Where(l => l.Contains("[p831]")).ToArray();
        Assert.Single(lines);
        Assert.Contains("P821 fallback hull", lines[0]);
        Assert.Contains("candidate enemy homes=2", lines[0]);
        Assert.Contains("enemyStations=0", lines[0]);
        Assert.Contains("requested role=bomber", lines[0]);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P831_Bomber_objective_rules_are_unchanged_when_the_world_can_answer()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var core = Catalog.RepresentativeCore();
        var bomberHull = Showcase.RepresentativeHullOfRole(core, Showcase.HullRole.Bomber)!.Value;
        // (a) an observed enemy station wins, exactly as before.
        using (var cs = Session())
        {
            var bot = Bot(cs); var manager = Manager(bot);
            Call("RefreshLife", manager, bot, cs, 0.0);
            Set(bot, "Role", "bomber"); Set(bot, "HullId", bomberHull.HullId);
            Set(bot, "TargetSector", (short?)4);
            P831FreshWorld(cs.World, 2, 0, 1, [P831Station(102, 1, 7)]);
            Assert.True((bool)Call("RefreshLife", manager, bot, cs, 1.0)!);
            Assert.False(Get<bool>(bot, "ObjectivesPending"));
            Assert.Equal((short?)7, Get<short?>(bot, "TargetSector"));
            Assert.False(Get<bool>(bot, "ObjectiveFallbackLogged"));
        }
        // (b) no enemy station seen, but the side's own home is known: the single
        // remaining home-flagged cluster is the enemy home.
        using (var cs = Session())
        {
            var bot = Bot(cs); var manager = Manager(bot);
            Call("RefreshLife", manager, bot, cs, 0.0);
            Set(bot, "Role", "bomber"); Set(bot, "HullId", bomberHull.HullId);
            Set(bot, "TargetSector", (short?)4);
            P831FreshWorld(cs.World, 2, 0, 1, [P831Station(100, 0, 1)]);
            Assert.True((bool)Call("RefreshLife", manager, bot, cs, 1.0)!);
            Assert.False(Get<bool>(bot, "ObjectivesPending"));
            Assert.Equal((short?)4, Get<short?>(bot, "TargetSector"));
        }
        // (c) a named objective still resolves for any role.
        using (var cs = Session())
        {
            var bot = Bot(cs); var manager = Manager(bot);
            Call("RefreshLife", manager, bot, cs, 0.0);
            Set(bot, "Role", "Fighter"); Set(bot, "ObjectiveName", "Target"); Set(bot, "TargetSector", (short?)4);
            P831FreshWorld(cs.World, 2, 0, 1, []);
            Assert.True((bool)Call("RefreshLife", manager, bot, cs, 1.0)!);
            Assert.Equal((short?)6, Get<short?>(bot, "TargetSector"));
        }
    }

    /// <summary>Rotate a small finale roster onto a fresh, fogged mission and hand
    /// back the manager with every bot reset (one bot may be forced stuck).</summary>
    private static (BotManager Manager, ConnectionSession[] Sessions, object[] Bots, double RotatedAt)
        P831RotatedRoster(int perSide, int? stuckIndex = null, bool blindSide = false)
    {
        var core = Catalog.RepresentativeCore();
        var home = new[] { P831Station(100, 0, 1), P831Station(101, 1, 4) };
        var fresh = new[] { P831Station(200, 0, 1), P831Station(201, 1, 4) };
        int count = perSide * 2;
        var sessions = Enumerable.Range(0, count).Select(_ => Session(true)).ToArray();
        var bots = new object[count];
        for (int i = 0; i < count; i++)
        {
            short side = (short)(i / perSide);
            var cs = sessions[i];
            cs.World.Reset();
            Seat(cs.World, (short)(i + 1), 1);
            DefenseMap(cs.World);
            foreach (var station in home) cs.World.Apply(new ExportStation { Station = station });
            cs.World.Apply(new SideTechChange { SideId = side, TtbmDevelopments = core.ComputeRequiredTechUnion().ToBytes() });
            bots[i] = Bot(cs, (short)(i + 1));
            Set(bots[i], "Name", $"p831-{i}");
            Set(bots[i], "Side", side);
            Set(bots[i], "TargetSector", (short?)1);
            DefenseNoSocketSender(bots[i]);
        }
        var manager = Manager(bots);
        // Seat every life once, so the rotation below is a real mission change
        // (RefreshLife only restores finale state when it already has a life).
        for (int i = 0; i < count; i++) Call("RefreshLife", manager, bots[i], sessions[i], 0.0);
        manager.RegisterFinale(new(), bots.Select(b => Get<string>(b, "Name")).ToArray(), home);
        var rotatedAt = Get<double>(Get<object>(bots[0], "Finale"), "Created") + 100;
        for (int i = 0; i < count; i++)
        {
            short side = (short)(i / perSide);
            // A blind side has not observed any enemy station in its own worlds.
            IEnumerable<DataStationIGC> seen = blindSide && side == 1 ? [fresh[1]] : fresh;
            P831FreshWorld(sessions[i].World, (short)(40 + i), side, (short)(1 + (i % 2)), seen, stageStarted: true);
            Call("RefreshLife", manager, bots[i], sessions[i], rotatedAt);
        }
        if (stuckIndex is { } stuck) Set(bots[stuck], "ObjectivesPending", true);
        return (manager, sessions, bots, rotatedAt);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P831_Rearm_excludes_a_bot_stuck_past_the_grace_and_names_it()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var (manager, sessions, bots, rotatedAt) = P831RotatedRoster(2, stuckIndex: 3);
        try
        {
            string blocked = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + 1));
            Assert.Single(P831FinaleRuns(manager));      // the stuck bot still holds the re-arm
            Assert.Contains("re-arm blocked", blocked);
            Assert.Contains("objectives-pending=1", blocked);
            Assert.Contains(Get<string>(bots[3], "Name"), blocked);

            string graced = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + BotManager.RearmGraceSeconds + 1));
            var runs = P831FinaleRuns(manager);
            Assert.Equal(2, runs.Length);                // the next match now HAS a deadline
            Assert.Equal(3, P831FinaleBots(runs[1]));     // the stuck bot is excluded
            Assert.Equal(4, P831FinaleBots(runs[0]));     // the finished match keeps its report
            Assert.Contains("[finale] re-arm grace", graced);
            Assert.Contains("excluding 1 still-stuck bot(s)", graced);
            Assert.Contains("re-armed for next mission", graced);
            // Bounded: the exclusion is logged once, not once a minute.
            Assert.DoesNotContain("excluding 1 still-stuck", P831Capture(() => Call("UpdateFinales", manager, rotatedAt + BotManager.RearmGraceSeconds + 2)));
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P831_Rearm_grace_releases_a_side_that_never_saw_an_enemy_station()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var (manager, sessions, _, rotatedAt) = P831RotatedRoster(2, blindSide: true);
        try
        {
            string blocked = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + 1));
            Assert.Single(P831FinaleRuns(manager));
            Assert.Contains("no enemy station observed by side(s) [1]", blocked);

            string graced = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + BotManager.RearmGraceSeconds + 1));
            Assert.Equal(2, P831FinaleRuns(manager).Length);
            Assert.Contains("[finale] re-arm grace", graced);
            Assert.Contains("no enemy station observed by side(s) [1]", graced);
            Assert.Contains("re-armed for next mission with 4 bots", graced);
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P831_Rearm_grace_never_excludes_a_bot_blocked_only_just_now()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var (manager, sessions, bots, rotatedAt) = P831RotatedRoster(2, blindSide: true);
        try
        {
            // Past the run-level grace the fog gate alone would be released...
            string first = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + 1));
            Assert.Contains("no enemy station observed by side(s) [1]", first);
            // ...but a bot that only NOW reports blocked (a relaunch in flight) must not
            // be excluded for it: its own grace clock starts here.
            Set(bots[2], "ObjectivesPending", true);
            string waiting = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + BotManager.RearmGraceSeconds + 1));
            Assert.Single(P831FinaleRuns(manager));
            Assert.Contains("re-arm blocked", waiting);
            Assert.DoesNotContain("re-arm grace", waiting);
            // Still inside ITS grace: nothing is excluded and the report stays one run.
            Assert.Single(P831FinaleRuns(manager));
            P831Capture(() => Call("UpdateFinales", manager, rotatedAt + BotManager.RearmGraceSeconds + 2));
            Assert.Single(P831FinaleRuns(manager));
            // A full grace after it was first seen stuck it IS excluded, and the fog gate
            // relaxation rides along — the match gets its deadline.
            string graced = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + 2 * BotManager.RearmGraceSeconds + 2));
            var runs = P831FinaleRuns(manager);
            Assert.Equal(2, runs.Length);
            Assert.Equal(3, P831FinaleBots(runs[1]));
            Assert.Contains("excluding 1 still-stuck bot(s)", graced);
            Assert.Contains("no enemy station observed by side(s) [1]", graced);
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P831_Rearm_still_waits_inside_the_grace()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var (manager, sessions, _, rotatedAt) = P831RotatedRoster(2);
        try
        {
            // Every bot is reset and the enemy stations are observed, so this re-arms
            // on the first pass — the grace must never delay a healthy rotation.
            string log = P831Capture(() => Call("UpdateFinales", manager, rotatedAt + 1));
            Assert.Equal(2, P831FinaleRuns(manager).Length);
            Assert.Contains("re-armed for next mission with 4 bots", log);
            Assert.DoesNotContain("re-arm grace", log);
            var states = manager.FinaleStatus()
                .Select(r => JsonSerializer.SerializeToElement(r).GetProperty("state").GetString()).ToArray();
            Assert.Equal(new[] { "next mission", "armed" }, states);   // the finished report + a fresh armed run
        }
        finally { foreach (var cs in sessions) cs.Dispose(); }
    }
}

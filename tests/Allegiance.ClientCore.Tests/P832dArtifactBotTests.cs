using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    private static void ObjectivePass(BotManager manager, double now, params object[] bots)
    {
        var roster = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) roster.SetValue(bots[i], i);
        Call("UpdateObjectiveDuties", manager, roster, now);
    }

    [SkippableTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void P832d_No_objective_preserves_master_decisions_for_100_ticks(int goal)
    {
        Require.Artwork();
        using var cs = Session(true);
        cs.World.Apply(new MissionDef { Goals = goal switch
        { 1 => new() { TeamMoney = 1 }, 2 => new() { GameLengthSeconds = 600 }, _ => new() } });
        var bot = Bot(cs); var manager = Manager(bot);
        Set(bot, "TargetId", (short?)77); Set(bot, "TargetSector", (short?)2);
        for (int tick = 0; tick < 100; tick++)
        {
            ObjectivePass(manager, tick, bot);
            Assert.False((bool)Call("TryDriveObjective", manager, bot, cs, (double)tick)!);
            Assert.Equal((short?)77, Get<short?>(bot, "TargetId"));
            Assert.Equal((short?)2, Get<short?>(bot, "TargetSector"));
            Assert.Equal(BotManager.FlagDuty.None, Get<BotManager.FlagDuty>(bot, "FlagTask"));
        }
        Assert.Null(Get<object?>(manager, "_intelStore"));
        Assert.Equal(0, Get<double>(manager, "_nextFlagAssignment"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void P832d_Artifacts_and_flags_share_one_budget_across_workers(bool flags)
    {
        var candidates = Enumerable.Range(0, 200).Select(i => new FlagCandidate(832, 0, $"pilot-{i:D3}",
            (short)i, i == 0, false, 900, i, Artifacts: true, Flags: flags)).ToArray();
        var coordinator = new ScenarioCoordinator(BotManager.DefenseSettings.Read(_ => null), false);
        var states = Enumerable.Range(0, 4).Select(worker => new CoordinationState([], [], [], [], [],
            Flags: candidates.Where((_, i) => i % 4 == worker).ToArray()));
        var duties = coordinator.Decide(states, 1, false).Flags!;
        Assert.Equal(20, duties.Length);
        Assert.Equal(duties.Length, duties.DistinctBy(d => d.Name).Count());
        Assert.Single(duties, d => d.Duty == BotManager.FlagDuty.Collect);
        Assert.Single(duties, d => d.Duty == BotManager.FlagDuty.Escort);
        Assert.Equal(12, duties.Count(d => d.Duty == BotManager.FlagDuty.Intercept));
        Assert.Equal(flags ? 1 : 0, duties.Count(d => d.Duty == BotManager.FlagDuty.Raid));
        Assert.Equal(BotManager.PlanFlagDuties(candidates), duties);
        Assert.Equal(8, BotManager.PlanFlagDuties(candidates.Select(c => c with { Thief = null })).Length);
    }

    [Fact]
    public void P832d_Nearest_capable_ship_collects_drop_and_dead_carrier_is_replaced()
    {
        var a = new FlagCandidate(1, 0, "scout", 1, true, false, null, 0,
            Artifacts: true, Flags: false, Artifact: 90, ArtifactDistance: 2000);
        var b = a with { Name = "near", ShipId = 2, Scout = false, ArtifactDistance = 100 };
        var c = a with { Name = "incapable", ShipId = 3, CanCarry = false, ArtifactDistance = 1 };
        Assert.Equal("near", Assert.Single(BotManager.PlanFlagDuties([a, b, c]),
            d => d.Duty == BotManager.FlagDuty.Collect).Name);
        Assert.Contains(BotManager.PlanFlagDuties([a, b with { Carrying = true }, c]),
            d => d.Name == "near" && d.Duty == BotManager.FlagDuty.Return);
        var replacement = BotManager.PlanFlagDuties([a, c]);
        Assert.Equal("scout", Assert.Single(replacement, d => d.Duty == BotManager.FlagDuty.Collect).Name);
        Assert.DoesNotContain(replacement, d => d.Name == "near" || d.Anchor == 2);
        Assert.DoesNotContain(BotManager.PlanFlagDuties([c]),
            d => d.Duty is BotManager.FlagDuty.Collect or BotManager.FlagDuty.Search);
    }

    [SkippableFact]
    public void P832d_Side_intel_contains_only_wire_observed_artifacts_and_forgets_a_pickup()
    {
        Require.Artwork();
        using var cs = Session(true);
        cs.World.Apply(new MissionDef { DwCookie = 832, Goals = new() { Artifacts = 1 } });
        var bot = Bot(cs); var manager = Manager(bot);
        ObjectivePass(manager, 1, bot);
        var store = Get<SideIntelStore>(manager, "_intelStore");
        Assert.Empty(store.Get(832, 0).Artifacts);
        cs.World.Apply(new TreasureSpawn { Data = new([100, 0, 0], [0, 0, 0], 9999, 0, 90,
            Side.ArtifactSentinelFlag, 1, 0, TreasureCode.Flag, false) });
        ObjectivePass(manager, 3, bot);
        Assert.True(store.Get(832, 0).Artifacts[90].Present);
        Assert.Null(store.Find(832, 1)); // no cross-side knowledge
        var sibling = new SideIntelStore();
        var old = Assert.Single(store.DigestAll());
        sibling.Apply(old);
        Assert.True(sibling.Get(832, 0).Artifacts[90].Present);
        cs.World.Apply(new DestroyTreasure { TreasureId = 90 });
        store.Get(832, 0).Artifacts[90] = store.Get(832, 0).Artifacts[90] with { AtMs = Environment.TickCount64 - 3000 };
        ObjectivePass(manager, 5, bot);
        Assert.False(store.Get(832, 0).Artifacts[90].Present);
        sibling.Apply(Assert.Single(store.DigestAll()));
        sibling.Apply(old); // stale worker cannot resurrect it
        Assert.False(sibling.Get(832, 0).Artifacts[90].Present);
        Assert.Null(sibling.Find(833, 0));
        // A respawn has a new ID and location; no worker learns either until
        // the side receives a fresh native treasure export.
        Assert.False(store.Get(832, 0).Artifacts.ContainsKey(91));
        cs.World.Apply(new TreasureSpawn { Data = new([200, 0, 0], [0, 0, 0], 9999, 0, 91,
            Side.ArtifactSentinelFlag, 1, 0, TreasureCode.Flag, false) });
        ObjectivePass(manager, 7, bot);
        Assert.True(store.Get(832, 0).Artifacts[91].Present);
        Assert.False(store.Get(832, 0).Artifacts[90].Present);
    }

    [SkippableFact]
    public void P832d_Visible_enemy_artifact_carrier_gets_nearest_free_fighter_and_hidden_pose_is_rejected()
    {
        Require.Artwork();
        using var far = Session(true); using var near = Session(true); using var scout = Session(true);
        var sessions = new[] { far, near, scout };
        var bots = sessions.Select((cs, i) =>
        {
            cs.World.Apply(new MissionDef { Goals = new() { Artifacts = 1 } });
            Seat(cs.World, x: i == 1 ? 100 : 5000);
            cs.World.Apply(new ExportShip { ShipId = 77, HullId = 15, SideId = 1 });
            cs.World.Apply(new SingleShipUpdate { OidTarget = 77, Ship = Pose(77) });
            cs.World.Apply(new ShipStatusMessage { ShipId = 77, HullId = 15, SectorId = 1, StationId = -1, ParentId = -1 });
            cs.World.Apply(new GainFlag { ShipidRecipient = 77, SideidFlag = Side.ArtifactSentinelFlag });
            var bot = Bot(cs); Set(bot, "Name", $"pilot-{i}");
            if (i == 2) Set(bot, "Role", "scout");
            return bot;
        }).ToArray();
        var manager = Manager(bots);
        ObjectivePass(manager, 1, bots);
        Assert.Equal(BotManager.FlagDuty.Intercept, Get<BotManager.FlagDuty>(bots[1], "FlagTask"));
        Assert.Equal((short?)77, Get<short?>(bots[1], "FlagAnchor"));
        foreach (var cs in sessions) cs.World.Apply(new ShipDelete { ShipId = 77, Sdr = ShipDelete.SdrLeftSector });
        ObjectivePass(manager, 3, bots);
        Assert.All(bots, b => Assert.NotEqual(BotManager.FlagDuty.Intercept, Get<BotManager.FlagDuty>(b, "FlagTask")));
    }

    [SkippableFact]
    public void P832d_Artifacts_preset_requires_goal_and_skirmish_suppresses_finale()
    {
        Require.Artwork(); using var cs = Session(true);
        var manager = Manager(); Set(manager, "<Probe>k__BackingField", cs);
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            """{"preset":"artifacts","count_a":6}""")!;
        Assert.True(McpTools.Handle("spawn_scenario", args, manager).IsError);
        Assert.Empty(Get<System.Collections.IList>(manager, "_bots"));
        cs.World.Apply(new MissionDef { Goals = new() { Artifacts = 1 } });
        var finale = System.Text.Json.JsonSerializer.SerializeToElement(manager.ConfigureFinale(new(), []));
        Assert.False(finale.GetProperty("enabled").GetBoolean());
    }
    [Fact]
    public void P832d_Empty_search_laps_cover_outer_respawns_without_leaving_legal_flight_volume()
    {
        // An 8200-unit respawn cannot be seen from the original 3600 ring by
        // a 2400-range scout. No artifact position is supplied to the policy.
        const float initial = 3600, limit = 10000, reach = 2400;
        var rings = Enumerable.Range(0, 12).Select(round => BotManager.ArtifactSearchRadius(initial, limit, round, 3)).ToArray();
        Assert.All(rings, r => Assert.InRange(r, initial, limit));
        Assert.Equal(initial, rings[0]);
        Assert.Contains(rings, r => Math.Abs(r - 8200) < reach);
        // Every radius between the inner ring and the bound is eventually
        // scanned; alternate sectors are visited before expanding a whole lap.
        for (int distance = 2400; distance <= limit; distance += 100)
            Assert.Contains(rings, r => Math.Abs(r - distance) <= reach);
        Assert.Equal(rings[0], rings[2]);
        Assert.Equal(rings[0], BotManager.ArtifactSearchRadius(initial, limit, 12, 3));
    }

}

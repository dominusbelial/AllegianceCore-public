using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    private static FlagCandidate Conqueror(int id, bool board = false, bool strike = true, bool cargo = false) =>
        new(832, 0, $"pilot-{id:D3}", (short)id, false, false, null, 0, Flags: cargo,
            Conquest: true, ConquestPercent: 80, ConquestGoal: 90,
            Stations: [new(90, 1, false, id * 10, board, strike, false), new(91, 1, true, id * 20, false, false, true)]);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void P832e_One_budget_across_workers_and_goals_leaves_180_of_200_in_clash(bool cargo)
    {
        var candidates = Enumerable.Range(0, 200).Select(i => Conqueror(i, i == 0, cargo: cargo)
            with { Artifacts = cargo, Thief = cargo ? (short)900 : null }).ToArray();
        var coordinator = new ScenarioCoordinator(BotManager.DefenseSettings.Read(_ => null), false);
        var states = Enumerable.Range(0, 4).Select(worker => new CoordinationState([], [], [], [], [],
            Flags: candidates.Where((_, i) => i % 4 == worker).ToArray()));
        var duties = coordinator.Decide(states, 1, false).Flags!;
        Assert.Equal(BotManager.PlanFlagDuties(candidates), duties);
        Assert.InRange(duties.Length, 1, 20);
        Assert.Equal(duties.Length, duties.DistinctBy(d => (d.Cookie, d.Side, d.Name)).Count());
        Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Hold);
        Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Siege);
        if (cargo) Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Collect);
    }

    [Theory]
    [InlineData(49, false)] [InlineData(50, true)]
    public void P832e_Default_100_percent_goal_defends_when_one_observed_station_is_enough(int percent, bool defends)
    {
        var candidates = Enumerable.Range(0, 20).Select(i => Conqueror(i)
            with { ConquestPercent = percent, ConquestGoal = 100 });
        Assert.Equal(defends, BotManager.PlanFlagDuties(candidates).Any(d => d.Duty == BotManager.FlagDuty.Hold));
    }

    [Fact]
    public void P832e_Capture_preferred_replaces_dead_pilot_and_retargets_after_capture_or_kill()
    {
        var candidates = Enumerable.Range(0, 30).Select(i => Conqueror(i, i == 0)
            with { ConquestPercent = 0 }).ToArray();
        var duties = BotManager.PlanFlagDuties(candidates);
        Assert.Contains(duties, d => d.Name == "pilot-000" && d.Duty == BotManager.FlagDuty.Board);
        Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Siege && d.Post == 0);
        Assert.DoesNotContain(BotManager.PlanFlagDuties(candidates.Skip(1)), d => d.Duty == BotManager.FlagDuty.Board);
        Assert.Contains(BotManager.PlanFlagDuties(candidates.Skip(1).Select(c => c with
            { Stations = c.Stations!.Select(s => s with { Strike = false }).ToArray() })), d => d.Duty == BotManager.FlagDuty.Siege);
        var captured = candidates.Select(c => c with { Stations = c.Stations!.Select(s => s with { Own = true }).ToArray() });
        Assert.DoesNotContain(BotManager.PlanFlagDuties(captured), d => d.Duty is BotManager.FlagDuty.Board or BotManager.FlagDuty.Siege);
        var rebuilt = candidates.Select(c => c with { Stations = [new(92, 1, false, 1, false, true, false)] });
        Assert.Contains(BotManager.PlanFlagDuties(rebuilt), d => d.Duty == BotManager.FlagDuty.Siege && d.Anchor == 92);
        Assert.DoesNotContain(BotManager.PlanFlagDuties(rebuilt), d => d.Anchor == 90);
    }

    [SkippableFact]
    public void P832e_Only_observed_flag_stations_are_targets_and_native_score_recalls_nearest_defender()
    {
        Require.Artwork();
        using var a = Session(true); using var b = Session(true);
        var sessions = new[] { a, b };
        var core = Catalog.RepresentativeCore();
        var flag = core.StationTypes.Values.First(s => s.HasCapability(DataStationTypeIGC.SabmFlag));
        var noFlag = core.StationTypes.Values.First(s => !s.HasCapability(DataStationTypeIGC.SabmFlag));
        var bots = sessions.Select((cs, i) =>
        {
            cs.World.Apply(new MissionDef { DwCookie = 832, Goals = new() { ConquestPercentage = 90 } });
            cs.World.Apply(new GameState { Conquest = [80, 20, 0, 0, 0, 0] });
            Seat(cs.World, x: i == 0 ? 9000 : 5000);
            cs.World.Apply(new ExportStation { Station = Station(91, 0, 1) with { StationTypeId = flag.StationTypeId } });
            cs.World.Apply(new ExportStation { Station = Station(92, 1, 1) with { StationTypeId = noFlag.StationTypeId } });
            cs.World.Apply(new ExportShip { ShipId = 77, SideId = 1, HullId = 15 });
            cs.World.Apply(new SingleShipUpdate { OidTarget = 77, Ship = Pose(77) });
            cs.World.Apply(new ShipStatusMessage { ShipId = 77, HullId = 15, SectorId = 1, StationId = -1, ParentId = -1 });
            var bot = Bot(cs); P700Weapon(bot); Set(bot, "Name", $"pilot-{i}"); return bot;
        }).ToArray();
        var manager = Manager(bots);
        ObjectivePass(manager, 1, bots);
        Assert.Equal(BotManager.FlagDuty.Hold, Get<BotManager.FlagDuty>(bots[1], "FlagTask"));
        Assert.Equal((short?)91, Get<short?>(bots[1], "FlagAnchor"));
        var sender = P700Sender(bots[1]);
        Assert.True((bool)Call("TryDriveObjective", manager, bots[1], b, 1.0)!);
        Assert.Equal((short?)77, Get<short?>(bots[1], "TargetId"));
        var returning = P700LastControls(sender).Payload;
        Assert.True(returning.Controls.Yaw != 0 || returning.Controls.Pitch != 0
            || returning.Controls.Throttle != 0 || returning.StateM != 0,
            "The nearer defender is 5,000 units from home and must steer back to its station's attacker");
        Assert.All(bots, bot => Assert.NotEqual((short?)92, Get<short?>(bot, "FlagAnchor")));
        // A native visibility delete invalidates the threatening pose. No server
        // station table is available to these decision fixtures.
        foreach (var cs in sessions) cs.World.Apply(new ShipDelete { ShipId = 77, Sdr = ShipDelete.SdrLeftSector });
        ObjectivePass(manager, 3, bots);
        Assert.DoesNotContain(bots, bot => Get<BotManager.FlagDuty>(bot, "FlagTask") == BotManager.FlagDuty.Hold);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void P832e_No_conquest_keeps_flags_and_artifacts_decision_trace(bool artifacts)
    {
        var baseline = Enumerable.Range(0, 200).Select(i => new FlagCandidate(832, 0, $"pilot-{i:D3}",
            (short)i, i < 20, i == 0, 901, i, Artifacts: artifacts, Flags: !artifacts)).ToArray();
        for (int tick = 0; tick < 100; tick++)
        {
            var input = baseline.Select(c => c with { Distance = (c.ShipId + tick) % 200 }).ToArray();
            Assert.Equal(BotManager.PlanFlagDuties(input), BotManager.PlanFlagDuties(input.Select(c => c with
            { Conquest = false, ConquestPercent = 100, ConquestGoal = 1, Stations = [new(999, 1, false, 0, true, true, true)] })));
        }
    }

    [SkippableFact]
    public void P832e_Releasing_a_refitted_strike_keeps_actual_hardware_for_normal_combat()
    {
        Require.Artwork(); var core = Catalog.RepresentativeCore(); var bot = Bot();
        var originalType = typeof(BotManager).GetNestedType("FinaleOriginal", System.Reflection.BindingFlags.NonPublic)!;
        var original = Activator.CreateInstance(originalType, Fields, null,
            [(short)15, "original fighter", Array.Empty<ExpandedPartData>(), (short?)null, (short?)null,
                0.0, (short)0, (short?)3, 0, "fighter"], null)!;
        var actual = Catalog.NativePreferredLoadout(core.HullTypes.Values.First(h => h.HullId != 15
            && h.MaxFixedWeapons > 0 && Showcase.IsPlayerBuyable(core, h)).HullId, "actual");
        Set(bot, "FinaleOriginal", original);
        Set(bot, "Granted", new BotManager.LoadoutGrant(actual.HullId, actual.Parts));
        Set(bot, "Role", "bomber");
        Call("RestoreFinaleLoadout", null, bot, true);
        Assert.Equal("fighter", Get<string>(bot, "Role"));
        Assert.Equal((short?)3, Get<short?>(bot, "TargetSector"));
        Assert.Equal(actual.HullId, Get<short>(bot, "HullId"));
        Assert.Equal(Catalog.FirstWeaponPartId(actual.Parts), Get<short?>(bot, "WeaponPartId"));
        Assert.Same(actual.Parts, Get<IReadOnlyList<ExpandedPartData>>(bot, "Parts"));
    }

    [SkippableFact]
    public void P832e_Below_goal_boarding_launches_again_even_after_the_planner_clears_the_old_duty()
    {
        Require.Artwork(); using var cs = Session(true);
        cs.World.Apply(new MissionDef { Goals = new() { ConquestPercentage = 100 } });
        cs.World.Apply(new DockedNotify { StationId = 90 });
        var bot = Bot(cs); var manager = Manager(bot);
        Set(bot, "FlagTrace", "Board:90"); Set(bot, "FlagTask", BotManager.FlagDuty.None);
        Assert.True((bool)Call("TryDriveObjective", manager, bot, cs, 1.0)!);
        Assert.True(Get<bool>(bot, "MissionLaunchPending"));
        Assert.Equal("", Get<string>(bot, "FlagTrace"));
    }

    [SkippableFact]
    public void P832e_Boarding_preference_buys_a_legal_native_board_hull_and_respects_wallet()
    {
        Require.Artwork(); var core = Catalog.RepresentativeCore();
        var civ = core.Civilizations.Values.First(c => TechLoadoutSelector.Select(core, c.CivilizationId,
            Showcase.HullRole.Fighter, core.ComputeRequiredTechUnion(), c.InitialStationTypeId,
            budget: 1_000_000, boarding: true) is not null);
        var shop = new BotManager.LoadoutShop(civ.CivilizationId, civ.InitialStationTypeId, core.ComputeRequiredTechUnion(), 1_000_000);
        var selected = BotManager.PickTechLoadout(shop, Showcase.HullRole.Fighter, boarding: true)!;
        Assert.True(core.HullTypes[selected.HullId].HasCapability(DataHullTypeIGC.HabmBoard));
        Assert.True(TechLoadoutSelector.Legal(core, shop.Civ, shop.Techs, shop.DockType, selected.HullId, selected.Parts, boarding: true));
        Assert.InRange(selected.Price, 0, shop.Budget);
        var fallback = BotManager.PickTechLoadout(shop with { Budget = 0, Techs = TechTreeBitMask.Empty }, Showcase.HullRole.Fighter, boarding: true);
        Assert.True(fallback is null || fallback.Price == 0);
    }

    [SkippableFact]
    public void P832e_Conquest_preset_requires_goal_and_skirmish_disables_timed_finale()
    {
        Require.Artwork(); using var cs = Session(true);
        var manager = Manager(); Set(manager, "<Probe>k__BackingField", cs);
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            """{"preset":"conquest","count_a":6}""")!;
        Assert.True(McpTools.Handle("spawn_scenario", args, manager).IsError);
        cs.World.Apply(new MissionDef { Goals = new() { ConquestPercentage = 75 } });
        Assert.False(System.Text.Json.JsonSerializer.SerializeToElement(manager.ConfigureFinale(new(), [])).GetProperty("enabled").GetBoolean());
    }
}

using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    private static void FlagPass(BotManager manager, double now, params object[] bots)
    {
        var roster = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) roster.SetValue(bots[i], i);
        Call("UpdateFlagDuties", manager, roster, now);
    }

    [Theory]
    [InlineData(0, 0, 0)] [InlineData(1, 1, 0)] [InlineData(2, 1, 1)]
    [InlineData(5, 3, 1)] [InlineData(15, 3, 2)] [InlineData(200, 8, 12)]
    public void P832c_Flag_roles_leave_the_bulk_of_a_large_fleet_on_existing_duties(int count, int raids, int guards)
        => Assert.Equal((raids, guards), BotManager.FlagShares(count));

    [SkippableTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void P832c_No_flags_preserves_the_master_decision_inputs_and_takes_no_tick(int otherGoal)
    {
        Require.Artwork();
        using var cs = Session(true);
        cs.World.Apply(new MissionDef { Goals = otherGoal switch
        { 1 => new MissionGoals { TeamMoney = 1 }, 2 => new MissionGoals { Artifacts = 1 }, _ => new() } });
        var bot = Bot(cs); var manager = Manager(bot);
        Set(bot, "TargetId", (short?)77); Set(bot, "TargetSector", (short?)2);
        var command = new Allegiance.ClientCore.State.QueuedCommandState(OrderChange.CidAttack, Allegiance.Core.IgcObjectType.Ship, 77);
        Set(bot, "OrderedCommand", command);
        for (int tick = 0; tick < 100; tick++)
        {
            FlagPass(manager, tick, bot);
            Assert.False((bool)Call("TryDriveFlags", manager, bot, cs, (double)tick)!);
            Assert.Equal((short?)77, Get<short?>(bot, "TargetId"));
            Assert.Equal((short?)2, Get<short?>(bot, "TargetSector"));
            Assert.Equal(command, Get<Allegiance.ClientCore.State.QueuedCommandState?>(bot, "OrderedCommand"));
            Assert.Equal(BotManager.FlagDuty.None, Get<BotManager.FlagDuty>(bot, "FlagTask"));
        }
        Assert.Equal(0d, Get<double>(manager, "_nextFlagAssignment"));
    }

    [SkippableFact]
    public void P832c_Closest_visible_fighter_intercepts_but_flag_identity_does_not_reveal_a_hidden_pose()
    {
        Require.Artwork();
        using var far = Session(true); using var near = Session(true);
        var sessions = new[] { far, near };
        for (int i = 0; i < sessions.Length; i++)
        {
            var cs = sessions[i];
            cs.World.Apply(new MissionDef { Goals = new() { Flags = 1 } });
            Seat(cs.World, sector: 1, x: i == 0 ? 5000 : 100);
            cs.World.Apply(new ExportShip { ShipId = 77, HullId = 15, SideId = 1 });
            // FULL_SHIP_UPDATE supplies a native-visible pose; GAIN_FLAG supplies ownership.
            cs.World.Apply(new SingleShipUpdate { OidTarget = 77, Ship = Pose(77) });
            cs.World.Apply(new ShipStatusMessage { ShipId = 77, HullId = 15, SectorId = 1, StationId = -1, ParentId = -1 });
            cs.World.Apply(new GainFlag { ShipidRecipient = 77, SideidFlag = 0 });
        }
        var a = Bot(far); var b = Bot(near);
        Set(a, "BotId", 1); Set(b, "BotId", 2);
        Set(a, "Name", "far"); Set(b, "Name", "near");
        var manager = Manager(a, b);
        FlagPass(manager, 1, a, b);
        Assert.Equal(BotManager.FlagDuty.Intercept, Get<BotManager.FlagDuty>(b, "FlagTask"));
        Assert.Equal((short?)77, Get<short?>(b, "FlagAnchor"));
        // Disappearance invalidates both the position and the tactical intercept.
        foreach (var cs in sessions) cs.World.Apply(new ShipDelete { ShipId = 77, Sdr = ShipDelete.SdrLeftSector });
        FlagPass(manager, 3, a, b);
        Assert.NotEqual(BotManager.FlagDuty.Intercept, Get<BotManager.FlagDuty>(a, "FlagTask"));
        Assert.NotEqual(BotManager.FlagDuty.Intercept, Get<BotManager.FlagDuty>(b, "FlagTask"));
    }
    [Fact]
    public void P832c_Workers_share_one_quota_and_pick_the_nearest_observer_across_partitions()
    {
        var candidates = Enumerable.Range(0, 200).Select(i => new FlagCandidate(832, 0,
            $"worker-{i % 4}-pilot-{i:D3}", (short)i, false, false, 900, 200 - i)).ToArray();
        var coordinator = new ScenarioCoordinator(BotManager.DefenseSettings.Read(_ => null), false);
        var states = Enumerable.Range(0, 4).Select(worker => new CoordinationState([], [], [], [], [],
            Flags: candidates.Where((_, i) => i % 4 == worker).ToArray()));
        var decision = coordinator.Decide(states, 1, false);
        Assert.Equal(20, decision.Flags!.Length);
        Assert.Equal(12, decision.Flags.Count(d => d.Duty == BotManager.FlagDuty.Intercept));
        Assert.All(decision.Flags.Where(d => d.Duty == BotManager.FlagDuty.Intercept), d =>
            Assert.Contains(d.Name, candidates.OrderBy(c => c.Distance).Take(12).Select(c => c.Name)));
        Assert.Single(decision.Flags, d => d.Duty == BotManager.FlagDuty.Raid);
        Assert.Equal(7, decision.Flags.Count(d => d.Duty == BotManager.FlagDuty.Escort));
        Assert.Equal(BotManager.PlanFlagDuties(candidates), decision.Flags);
    }

    [Fact]
    public void P832c_Dead_carrier_is_replaced_and_mission_cookies_keep_duties_separate()
    {
        var first = new FlagCandidate(1, 0, "a", 1, true, false, null, 0);
        var mate = new FlagCandidate(1, 0, "b", 2, true, false, null, 0);
        var guard = new FlagCandidate(1, 0, "guard", 3, false, false, null, 0);
        var extra = new FlagCandidate(1, 0, "extra", 4, false, false, null, 0);
        var returning = BotManager.PlanFlagDuties([first with { Carrying = true }, mate, guard, extra]);
        Assert.Contains(returning, d => d.Name == "a" && d.Duty == BotManager.FlagDuty.Return);
        Assert.Contains(returning, d => d.Duty == BotManager.FlagDuty.Escort && d.Anchor == 1);
        var afterDeath = BotManager.PlanFlagDuties([mate, guard, extra]);
        Assert.DoesNotContain(afterDeath, d => d.Anchor == 1 || d.Name == "a");
        Assert.Single(afterDeath, d => d.Duty == BotManager.FlagDuty.Raid);
        var rotated = BotManager.PlanFlagDuties([mate with { Cookie = 2 }, guard with { Cookie = 2 }]);
        Assert.All(rotated, d => Assert.Equal(2u, d.Cookie));
    }

    [SkippableFact]
    public void P832c_Preset_requires_server_goal_and_flags_skirmish_suppresses_bomber_finale()
    {
        Require.Artwork();
        using var cs = Session(true);
        var manager = Manager();
        Set(manager, "<Probe>k__BackingField", cs);
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            """{"preset":"flags","count_a":6}""")!;
        var result = McpTools.Handle("spawn_scenario", args, manager);
        Assert.True(result.IsError);
        Assert.Empty(Get<System.Collections.IList>(manager, "_bots"));
        cs.World.Apply(new MissionDef { Goals = new() { Flags = 1 } });
        var finale = System.Text.Json.JsonSerializer.SerializeToElement(manager.ConfigureFinale(new(), []));
        Assert.False(finale.GetProperty("enabled").GetBoolean());
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void P832c_AI_attack_orders_yield_to_flags_but_explicit_human_commanders_keep_control(bool human)
    {
        Require.Artwork();
        using var cs = Session(true);
        cs.World.Apply(new MissionDef { Goals = new() { Flags = 1 } });
        Seat(cs.World);
        var bot = Bot(cs);
        Set(bot, "OrderedCommand", new Allegiance.ClientCore.State.QueuedCommandState(
            OrderChange.CidAttack, Allegiance.Core.IgcObjectType.Ship, 77));
        var manager = Manager(bot);
        if (human) Set(manager, "_humanCommanderName", "human-commander");
        FlagPass(manager, 1, bot);
        Assert.Equal(human ? BotManager.FlagDuty.None : BotManager.FlagDuty.Raid,
            Get<BotManager.FlagDuty>(bot, "FlagTask"));
    }

    [Fact]
    public void P832c_Carrier_must_have_native_treasure_pickup_capability()
    {
        var pilots = new[]
        {
            new FlagCandidate(1, 0, "a-incapable", 1, true, false, null, 0, CanCarry: false),
            new FlagCandidate(1, 0, "b-capable", 2, false, false, null, 0, CanCarry: true),
            new FlagCandidate(1, 0, "c-escort", 3, false, false, null, 0, CanCarry: false),
            new FlagCandidate(1, 0, "d-escort", 4, false, false, null, 0, CanCarry: false),
        };
        Assert.Equal("b-capable", Assert.Single(BotManager.PlanFlagDuties(pilots), d => d.Duty == BotManager.FlagDuty.Raid).Name);
        Assert.DoesNotContain(BotManager.PlanFlagDuties(pilots.Select(c => c with { CanCarry = false })),
            d => d.Duty == BotManager.FlagDuty.Raid);
    }

}

using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    private static FlagCandidate Hunter(int id) => new(832, 0, $"pilot-{id:D3}", (short)id,
        false, false, null, 0, Flags: false, Kills: true, KillGoal: 20,
        KillTargets: [new(900, 1, id * 10, .3f, 10, 2, BotTargetClass.Fighter),
            new(901, 1, id * 10, .8f, 10, 2, BotTargetClass.Fighter)]);

    [SkippableFact]
    public void P832f_No_goal_preserves_the_master_decision_trace_for_100_ticks()
    {
        Require.Artwork(); using var cs = Session(true);
        var bot = Bot(cs); var manager = Manager(bot);
        Set(bot, "TargetId", (short?)77); Set(bot, "TargetSector", (short?)2);
        var command = new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, 77);
        Set(bot, "OrderedCommand", command);
        // Dispatch master's no-objective contract (P832d): no assignment tick,
        // no controls, target/order mutation, intel allocation or RNG draws.
        for (int tick = 0; tick < 100; tick++)
        {
            ObjectivePass(manager, tick, bot);
            Assert.False((bool)Call("TryDriveObjective", manager, bot, cs, (double)tick)!);
            Assert.Equal((short?)77, Get<short?>(bot, "TargetId"));
            Assert.Equal((short?)2, Get<short?>(bot, "TargetSector"));
            Assert.Equal(command, Get<QueuedCommandState?>(bot, "OrderedCommand"));
            Assert.Equal(BotManager.FlagDuty.None, Get<BotManager.FlagDuty>(bot, "FlagTask"));
        }
        Assert.Null(Get<object?>(bot, "KillScores"));
        Assert.Null(Get<object?>(manager, "_intelStore"));
        Assert.Equal(0, Get<double>(manager, "_nextFlagAssignment"));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(7)]
    public void P832f_Disabled_kills_does_not_change_other_goal_decisions(int goals)
    {
        var pilots = Enumerable.Range(0, 200).Select(i => Conqueror(i, board: i == 0) with
        { Flags = (goals & 1) != 0, Artifacts = (goals & 2) != 0, Conquest = (goals & 4) != 0 }).ToArray();
        Assert.Equal(BotManager.PlanFlagDuties(pilots), BotManager.PlanFlagDuties(pilots.Select(c => c with
        { Kills = false, Hull = .01f, EnemyKills = 19, KillGoal = 20, KillTargets = Hunter(0).KillTargets })));
    }

    [Fact]
    public void P832f_Healthy_support_keeps_repair_duty_and_damaged_support_withdraws()
    {
        var pilots = Enumerable.Range(0, 10).Select(i => Hunter(i) with { Support = i < 2, Hull = i == 0 ? .2f : 1 }).ToArray();
        var duties = BotManager.PlanFlagDuties(pilots);
        Assert.Contains(duties, d => d.Name == "pilot-000" && d.Duty == BotManager.FlagDuty.Recover);
        Assert.DoesNotContain(duties, d => d.Name == "pilot-001");
    }

    [Fact]
    public void P832f_Endgame_caution_uses_the_sides_best_observed_score_without_crossing_mission_boundaries()
    {
        var pilots = new[] { Hunter(0) with { Hull = .6f, EnemyKills = 19 }, Hunter(1) with { Hull = .6f },
            Hunter(2) with { Cookie = 833, Hull = .6f } };
        var duties = BotManager.PlanFlagDuties(pilots);
        Assert.All(duties.Where(d => d.Cookie == 832), d => Assert.Equal(BotManager.FlagDuty.Recover, d.Duty));
        Assert.Equal(BotManager.FlagDuty.Hunt, Assert.Single(duties, d => d.Cookie == 833).Duty);
    }

    [SkippableFact]
    public void P832f_A_kill_hunter_cannot_also_be_recruited_for_base_defense_and_AI_orders_release_pods()
    {
        Require.Artwork(); using var cs = Session(true);
        cs.World.Apply(new MissionDef { Goals = new() { TeamKills = 20 } });
        var bot = Bot(cs); var manager = Manager(bot);
        Set(bot, "Role", "fighter"); Set(bot, "FlagTask", BotManager.FlagDuty.Hunt);
        Assert.False((bool)Call("IsDefenseFighter", null, bot)!);
        Set(bot, "FlagTask", BotManager.FlagDuty.None);
        Assert.True((bool)Call("IsDefenseFighter", null, bot)!);
        var pod = Catalog.RepresentativeCore().HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        cs.World.Apply(new ExportShip { ShipId = 77, SideId = 1, HullId = pod.HullId });
        Set(bot, "OrderedTargetId", (short?)77);
        Set(bot, "OrderedCommand", new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, 77));
        Call("ReleaseUnscorableKillOrder", manager, bot, cs);
        Assert.Null(Get<short?>(bot, "OrderedTargetId"));
        Assert.Null(Get<QueuedCommandState?>(bot, "OrderedCommand"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void P832f_All_goals_share_one_budget_across_workers_without_duplicate_pilots(bool mixed)
    {
        var candidates = Enumerable.Range(0, 200).Select(i => Hunter(i) with
        {
            Flags = mixed, Artifacts = mixed, Conquest = mixed, ConquestPercent = 80, ConquestGoal = 90,
            Thief = mixed ? (short)999 : null, Artifact = 91, ArtifactDistance = i,
            Stations = [new(90, 1, false, i, i == 0, true, false), new(92, 1, true, i, false, false, true)]
        }).ToArray();
        var coordinator = new ScenarioCoordinator(BotManager.DefenseSettings.Read(_ => null), false);
        var states = Enumerable.Range(0, 4).Select(w => new CoordinationState([], [], [], [], [],
            Flags: candidates.Where((_, i) => i % 4 == w).ToArray()))
            .Select(s => System.Text.Json.JsonSerializer.Deserialize<CoordinationState>(System.Text.Json.JsonSerializer.Serialize(s))!);
        var duties = coordinator.Decide(states, 1, false).Flags!;
        Assert.Equal(BotManager.PlanFlagDuties(candidates), duties);
        Assert.Equal(120, duties.Length);
        Assert.Equal(duties.Length, duties.Select(d => d.Name).Distinct().Count());
        Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Hunt);
        if (mixed)
        {
            Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Siege);
            Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Collect);
            Assert.Contains(duties, d => d.Duty == BotManager.FlagDuty.Intercept);
        }
    }

    [Fact]
    public void P832f_Hunters_focus_weak_hulls_to_the_existing_cap_and_reject_bad_odds()
    {
        var pilots = Enumerable.Range(0, 20).Select(Hunter).ToArray();
        var duties = BotManager.PlanFlagDuties(pilots);
        Assert.Equal(3, duties.Count(d => d.Anchor == 900));
        Assert.Equal(3, duties.Count(d => d.Anchor == 901));
        Assert.Equal((short?)900, duties[0].Anchor);
        var cautious = BotManager.PlanFlagDuties(pilots.Select(c => c with { EnemyKills = 19,
            KillTargets = [new(900, 1, 1, .1f, 2, 2, BotTargetClass.Fighter)] }));
        Assert.All(cautious, d => Assert.Null(d.Anchor));
    }

    [Theory]
    [InlineData(.49f, false, 0, true)] [InlineData(.51f, false, 0, false)]
    [InlineData(.69f, false, 19, true)] [InlineData(.71f, false, 19, false)]
    [InlineData(.90f, true, 0, true)] [InlineData(.96f, true, 0, false)]
    public void P832f_Recovery_thresholds_have_hysteresis_and_endgame_margin(float hull, bool recovering, int enemy, bool expected)
        => Assert.Equal(expected, BotManager.KillWithdraw(hull, recovering, enemy, 20));

    [Fact]
    public void P832f_Returning_cargo_and_wounded_pilots_keep_unique_slots_even_in_an_emergency()
    {
        var pilots = Enumerable.Range(0, 10).Select(i => Hunter(i) with { Hull = .2f, Carrying = i == 0, Flags = true });
        var duties = BotManager.PlanFlagDuties(pilots);
        Assert.Equal(10, duties.Length);
        Assert.Single(duties, d => d.Duty == BotManager.FlagDuty.Return);
        Assert.Equal(9, duties.Count(d => d.Duty == BotManager.FlagDuty.Recover));
        Assert.Equal(10, duties.Select(d => d.Name).Distinct().Count());
    }

    [Fact]
    public void P832f_Public_kill_credits_include_drones_and_nonship_launchers_once_and_reset_per_mission()
    {
        using var cs = Session();
        var score = new BotManager.KillScore();
        cs.World.Apply(new KillShip { ShipId = 77, SideIdKiller = 1, TypeCredit = (short)IgcObjectType.Probe, BKillCredit = true });
        // Pods/teamkills/suicides have no native bKillCredit; death != credit.
        cs.World.Apply(new KillShip { ShipId = 78, SideIdKiller = 0, BKillCredit = false, BDeathCredit = true });
        score.Observe(cs.World); score.Observe(cs.World);
        Assert.Equal(1, score.For(1)); Assert.Equal(0, score.For(0));
        for (int i = 0; i < 510; i++)
        {
            cs.World.Apply(new KillShip { ShipId = 77, SideIdKiller = 0, BKillCredit = true });
            score.Observe(cs.World);
        }
        Assert.Equal(510, score.For(0)); // survives EventLog's 500-entry window
        cs.World.BeginNextMission(); score.Observe(cs.World);
        Assert.Equal(0, score.For(0)); Assert.Equal(0, score.For(1));
        cs.World.Apply(new PlayerInfo { ShipId = 2, ISide = 1, NKills = 4, CharacterName = "late-player" });
        score.Observe(cs.World);
        Assert.Equal(4, score.For(1));
        cs.World.Apply(new KillShip { ShipId = 77, SideIdKiller = 1, TypeCredit = (short)IgcObjectType.Ship, IdCredit = 2, BKillCredit = true });
        score.Observe(cs.World);
        Assert.Equal(5, score.For(1));
        cs.World.Apply(new PlayerInfo { ShipId = 2, ISide = 0, NKills = 5, CharacterName = "late-player" });
        score.Observe(cs.World);
        Assert.Equal(5, score.For(1)); Assert.Equal(0, score.For(0));
    }

    private static ServerShipUpdate KillPose(short id, float hull = 1, float x = 0)
    {
        var bytes = new byte[ServerShipUpdate.Width];
        var writer = new WireWriter(bytes);
        Pose(id, x).EncodeBody(ref writer);
        bytes[40] = (byte)(240 * hull);
        var reader = new WireReader(bytes);
        return ServerShipUpdate.DecodeBody(ref reader);
    }

    [Fact]
    public void P832f_An_older_decision_snapshot_cannot_rewind_the_public_kill_cursor()
    {
        using var live = Session(); using var older = Session();
        var score = new BotManager.KillScore();
        var kill = new KillShip { ShipId = 77, SideIdKiller = 1, BKillCredit = true };
        for (int i = 0; i < 5; i++) older.World.Apply(kill);
        for (int i = 0; i < 10; i++) live.World.Apply(kill);
        score.Observe(live.World);
        score.Observe(older.World); // a same-generation snapshot from an earlier control pass
        live.World.Apply(kill);
        score.Observe(live.World);
        Assert.Equal(11, score.For(1));
    }

    [SkippableFact]
    public void P832f_Damaged_pilot_drops_attack_flies_to_repair_and_launches_after_docking()
    {
        Require.Artwork(); using var cs = Session(true);
        cs.World.Apply(new MissionDef { Goals = new() { TeamKills = 20 } });
        cs.World.Apply(new SetCluster { SectorId = 1, Ship = KillPose(1, .2f, 2000) });
        cs.World.Apply(new ExportStation { Station = Station(90, 0, 1) });
        var bot = Bot(cs); var manager = Manager(bot);
        var sender = P700Sender(bot);
        Set(bot, "TargetId", (short?)77);
        ObjectivePass(manager, 1, bot);
        Assert.Equal(BotManager.FlagDuty.Recover, Get<BotManager.FlagDuty>(bot, "FlagTask"));
        Assert.True((bool)Call("TryDriveObjective", manager, bot, cs, 1.0)!);
        Assert.Null(Get<short?>(bot, "TargetId"));
        Assert.True(Get<bool>(bot, "KillRecovering"));
        Assert.Equal("Recover:90", Get<string>(bot, "FlagTrace"));
        var flight = P700LastControls(sender).Payload;
        Assert.Equal(0, flight.StateM & ShipControlState.AllWeapons);
        Assert.True(flight.Controls.Yaw != 0 || flight.Controls.Pitch != 0 || flight.Controls.Throttle != 0 || flight.StateM != 0);
        cs.World.Apply(new DockedNotify { StationId = 90 });
        cs.World.Apply(new SingleShipUpdate { OidTarget = 1, Ship = KillPose(1) });
        ObjectivePass(manager, 3, bot);
        Assert.True((bool)Call("TryDriveObjective", manager, bot, cs, 3.0)!);
        Assert.True(Get<bool>(bot, "MissionLaunchPending"));
        Assert.False(Get<bool>(bot, "KillRecovering"));
    }

    [SkippableFact]
    public void P832f_Hunters_ignore_pods_hidden_and_allied_contacts_but_accept_enemy_NPCs()
    {
        Require.Artwork(); using var cs = Session(true);
        cs.World.Apply(new MissionDef { Goals = new() { TeamKills = 20 } });
        cs.World.Apply(new SetCluster { SectorId = 1, Ship = KillPose(1) });
        var core = Catalog.RepresentativeCore();
        short pod = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).HullId;
        foreach (short id in new short[] { 77, 78, 79 })
        {
            cs.World.Apply(new ExportShip { ShipId = id, SideId = 1, HullId = id == 78 ? pod : (short)15 });
            cs.World.Apply(new ShipStatusMessage { ShipId = id, SectorId = 1, HullId = id == 78 ? pod : (short)15, StationId = -1, ParentId = -1 });
            cs.World.Apply(new SingleShipUpdate { OidTarget = id, Ship = KillPose(id, .3f) });
        }
        cs.World.Apply(new ShipDelete { ShipId = 79, Sdr = ShipDelete.SdrLeftSector });
        var bot = Bot(cs); var manager = Manager(bot);
        var targets = (KillContact[])Call("KillContacts", null, bot)!;
        Assert.Equal((short)77, Assert.Single(targets).Id);
        cs.World.Apply(new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] });
        Assert.Empty((KillContact[])Call("KillContacts", null, bot)!);
        cs.World.Apply(new ChangeAlliances { Allies = [-1, -1, -1, -1, -1, -1] });
        cs.World.Apply(new ShipDelete { ShipId = 77, Sdr = ShipDelete.SdrLeftSector });
        Assert.Empty((KillContact[])Call("KillContacts", null, bot)!);
    }

    [SkippableFact]
    public void P832f_Kills_preset_requires_goal_and_showcase_skirmish_suppresses_timed_finale()
    {
        Require.Artwork(); using var cs = Session(true);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "p832f-kills.json"))) root = root.Parent;
        Assert.NotNull(root);
        var config = Allegiance.Server.ServerConfigLoader.Load(Path.Combine(root.FullName, "tools", "p832f-kills.json"), []);
        Assert.Equal((short)3, config.Games[0].Goals!.TeamKills);
        Assert.Equal("research", config.Showcase.TechMode);
        Assert.False(config.LiveSettings.TestHarness?.Enabled ?? false);
        Assert.False(config.LiveSettings.TestUnlock?.Enabled ?? false);
        var manager = Manager(); Set(manager, "<Probe>k__BackingField", cs);
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            """{"preset":"kills","count_a":6}""")!;
        Assert.True(McpTools.Handle("spawn_scenario", args, manager).IsError);
        cs.World.Apply(new MissionDef { Goals = new() { TeamKills = 3 } });
        Assert.False(System.Text.Json.JsonSerializer.SerializeToElement(manager.ConfigureFinale(new(), [])).GetProperty("enabled").GetBoolean());
    }
}

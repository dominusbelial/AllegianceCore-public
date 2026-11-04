using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [Fact]
    public void P926_Valid_target_on_same_map_preserves_trial_behavior_and_changed_map_activates_intel_reset()
    {
        var manager = Manager(); var world = new ClientWorld();
        Set(manager, "_mapBattleTarget", true); Set(manager, "_battleTargetQuery", "Rendezvous");
        Set(manager, "_battleTargetsLogged", new HashSet<(long, short)>());
        static void Fill(ClientWorld world, short offset)
        {
            world.BeginNextMission();
            world.Apply(new ClusterInfo { Clusters = [new(offset, true), new((short)(offset + 1), false), new((short)(offset + 2), true)],
                Warps = [new(offset, (short)(offset + 1)), new((short)(offset + 1), (short)(offset + 2))] });
            for (short i = 0; i < 3; i++)
                world.Apply(new ExportCluster { Cluster = new(1, [0, 1, 0], 0, 0, 0, (short)(offset + i), 0, 0,
                    i == 1 ? "Rendezvous" : $"Home{i}", "", "", 0, 0, 0, true, i != 1) });
        }
        Fill(world, 0);
        Assert.Equal((short)1, Call("ResolveShowcaseTarget", manager, world));
        Assert.False(Get<bool>(manager, "_refreshMapIntel"));
        Fill(world, 0);
        Assert.Equal((short)1, Call("ResolveShowcaseTarget", manager, world));
        Assert.False(Get<bool>(manager, "_refreshMapIntel"));
        Fill(world, 10);
        Assert.Equal((short)11, Call("ResolveShowcaseTarget", manager, world));
        Assert.True(Get<bool>(manager, "_refreshMapIntel"));
    }

    [Fact]
    public void P926_Map_rendezvous_preserves_small_skirmish_meeting_engagement()
    {
        using var cs = Session(connected: true); var bot = Bot(cs); var manager = Manager(bot);
        Set(manager, "_scenarioTargetOverride", (short?)2);
        Set(manager, "_meetingEngagement", true);
        Set(bot, "UseStaging", true);
        var bots = Array.CreateInstance(BotType, 1); bots.SetValue(bot, 0);
        Call("AssignClashTargets", manager, bots, 0.0);
        Assert.True(Get<bool>(bot, "MeetingEngagement"));
        Assert.False(Get<bool>(bot, "UseStaging"));
    }

    [Fact]
    public void P926_Intel_generation_detects_restart_without_resetting_for_late_joiners_or_repeated_samples()
    {
        using var first = Session(); using var late = Session(); var manager = Manager();
        Set(manager, "_mapBattleTarget", true);
        Set(manager, "_refreshMapIntel", true);
        P831FreshWorld(first.World, 2, 0, 1, []);
        Assert.False((bool)Call("ObserveIntelGeneration", manager, first)!);
        Assert.False((bool)Call("ObserveIntelGeneration", manager, first)!);
        P831FreshWorld(first.World, 3, 0, 1, []);
        Assert.True((bool)Call("ObserveIntelGeneration", manager, first)!);
        P831FreshWorld(late.World, 4, 0, 1, []);
        Assert.False((bool)Call("ObserveIntelGeneration", manager, late)!);
        Assert.False((bool)Call("ObserveIntelGeneration", manager, first)!);
        var fixedMap = Manager(); Set(fixedMap, "_mapBattleTarget", true);
        Assert.False((bool)Call("ObserveIntelGeneration", fixedMap, first)!);
        P831FreshWorld(first.World, 5, 0, 1, []);
        Assert.False((bool)Call("ObserveIntelGeneration", fixedMap, first)!, "fixed-map valid-target behavior stays unchanged");
        var store = new Allegiance.Tools.McpSpawner.SideIntelStore();
        store.Get(1, 0).MarkSector(1052, 1);
        store.Get(1, 1).MarkSector(1051, 1);
        store.Clear();
        Assert.Empty(store.Get(1, 0).KnownSectors);
        Assert.Empty(store.Get(1, 1).KnownSectors);
    }

    [SkippableFact]
    public void P926_Showcase_rederives_missing_named_target_on_each_new_map()
    {
        Require.Artwork();
        using var cs = Session(); var bot = Bot(cs); var manager = Manager(bot);
        Set(manager, "_mapBattleTarget", true); Set(manager, "_battleTargetQuery", "Alderan");
        Set(manager, "_battleTargetsLogged", new HashSet<(long, short)>());
        Set(bot, "Role", "Fighter");
        Call("RefreshLife", manager, bot, cs, 0.0);
        Set(bot, "TargetSector", (short?)1052); Set(bot, "ObjectiveName", "Alderan");
        P831FreshWorld(cs.World, 2, 0, 1, []);
        Assert.True((bool)Call("RefreshLife", manager, bot, cs, 1.0)!);
        Assert.Equal((short?)2, Get<short?>(bot, "TargetSector"));
        var cluster = cs.World.Clusters.Values.First();
        cs.World.BeginNextMission(); Seat(cs.World, 3, 10);
        cs.World.Apply(new ExportCluster { Cluster = cluster with { ClusterId = 10 } });
        cs.World.Apply(new ClusterInfo { Clusters = [new(10, true), new(11, false), new(12, true)], Warps = [new(10, 11), new(11, 12)] });
        Assert.True((bool)Call("RefreshLife", manager, bot, cs, 2.0)!);
        Assert.Equal((short?)11, Get<short?>(bot, "TargetSector"));
        Assert.False(Get<bool>(bot, "ObjectivesPending"));
        // The shared battle fallback must not mask the bomber's deducible
        // enemy home on the fresh graph.
        Set(bot, "Role", "bomber");
        P831FreshWorld(cs.World, 4, 0, 1, [P831Station(10, 0, 1)]);
        Assert.True((bool)Call("RefreshLife", manager, bot, cs, 3.0)!);
        Assert.Equal((short?)4, Get<short?>(bot, "TargetSector"));
        Assert.Equal((short?)0, Allegiance.Tools.McpSpawner.BotManager.SupportPhaseFrom(true, 0, false, -1, [], []).AnchorSector);
        Assert.Equal((short?)0, Allegiance.Tools.McpSpawner.BotManager.SupportPhaseFrom(false, null, true, 0, [], []).AnchorSector);
        Assert.Equal((short?)0, Allegiance.Tools.McpSpawner.BotManager.SupportPhaseFrom(false, null, false, -1, [(0, 2)], []).AnchorSector);
    }
}

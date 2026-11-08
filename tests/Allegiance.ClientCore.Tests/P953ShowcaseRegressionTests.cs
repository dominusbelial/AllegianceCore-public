using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData((short)2, null)]
    [InlineData((short)3, (short)3)]
    public void P953_Early_frontier_does_not_divert_the_automatic_march_before_rendezvous_is_scouted(
        short? initialTarget, short? assignedClash)
    {
        using var cs = Session(connected: true);
        cs.World.Apply(new ClusterInfo
        {
            Clusters = [new(0, true), new(1, false), new(2, false), new(3, false), new(4, true)],
            Warps = [new(0, 1), new(1, 2), new(2, 3), new(3, 4)]
        });
        var bots = Enumerable.Range(1, 4).Select(i => Bot(cs, (short)i)).ToArray();
        foreach (var bot in bots)
        {
            Set(bot, "TargetSector", initialTarget);
            Set(bot, "AssignedClashSector", assignedClash);
        }
        var manager = Manager(bots);
        Set(manager, "_mapBattleTarget", true);
        Set(manager, "_scenarioTargetOverride", (short?)2);
        Set(manager, "_openingStartedAt", 0.0);
        Set(manager, "_clash", new Dictionary<(uint, short), ClashChoice>());
        Set(manager, "_clashRockCounts", new Dictionary<(uint, short), int>());
        Set(manager, "_clashLogged", new HashSet<(uint, short)>());
        var store = new SideIntelStore(); Set(manager, "_intelStore", store);
        var intel = store.Get(0, 0); intel.OwnHome = 0; intel.EnemyHome = 4;
        intel.ObserveRock(new(1, 3, 5000, 5000, 3), 0);
        var array = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) array.SetValue(bots[i], i);
        Call("AssignClashTargets", manager, array, 100.0);
        Assert.Equal((short?)2, Get<short?>(bots[0], "TargetSector"));

        intel.MarkSector(2, 101);
        Call("AssignClashTargets", manager, array, 101.0);
        Assert.Equal((short?)3, Get<short?>(bots[0], "TargetSector"));
    }

    [Fact]
    public void P953_Recon_completes_enemy_sighting_and_raid_then_scouts_the_existing_rendezvous()
    {
        Assert.Equal((short?)4, ScoutOpening.ReconDestination(0, 4, -1, 0, 100, 100, 2, false));
        Assert.Null(ScoutOpening.ReconDestination(0, 4, -1, 0, 500, 500, 2, false));
        Assert.Null(ScoutOpening.ReconDestination(0, 4, 100, 0, 101, 101, 2, false));
        Assert.Equal((short?)2, ScoutOpening.ReconDestination(0, 4, 100, 1, 101, 101, 2, false));
        Assert.Equal((short?)2, ScoutOpening.ReconDestination(0, 4, 100, 0, 191, 191, 2, false));
        Assert.Null(ScoutOpening.ReconDestination(0, 4, 100, 1, 101, 101, 2, true));
        Assert.Null(ScoutOpening.ReconDestination(0, 4, 100, 1, 101, 101, null, false));
        Assert.Null(ScoutOpening.ReconDestination(0, 4, 100, 1, 101, 101, 0, false));
    }

    [Fact]
    public void P953_Automatic_rendezvous_preserves_meeting_engagement_before_a_clash_is_available()
    {
        using var cs = Session(connected: true); var bot = Bot(cs); var manager = Manager(bot);
        Set(manager, "_mapBattleTarget", true);
        Set(manager, "_scenarioTargetOverride", (short?)2);
        Set(manager, "_meetingEngagement", true);
        Set(bot, "TargetSector", (short?)2);
        Set(bot, "UseStaging", true);
        var bots = Array.CreateInstance(BotType, 1); bots.SetValue(bot, 0);
        Call("AssignClashTargets", manager, bots, 0.0);
        Assert.True(Get<bool>(bot, "MeetingEngagement"));
        Assert.False(Get<bool>(bot, "UseStaging"));
        Assert.Equal((short?)2, Get<short?>(bot, "TargetSector"));
    }

    [Theory]
    [InlineData((short)0, (short)4, (short)1)]
    [InlineData((short)4, (short)0, (short)3)]
    public void P953_Later_richer_observed_resources_replace_the_first_frontier_without_churn(
        short ownHome, short enemyHome, short frontier)
    {
        using var cs = Session(connected: true);
        cs.World.Apply(new ClusterInfo
        {
            Clusters = [new(0, true), new(1, false), new(2, false), new(3, false), new(4, true)],
            Warps = [new(0, 1), new(1, 2), new(2, 3), new(3, 4)]
        });
        var bots = Enumerable.Range(1, 4).Select(i => Bot(cs, (short)i)).ToArray();
        var manager = Manager(bots);
        Set(manager, "_openingStartedAt", 0.0);
        Set(manager, "_clash", new Dictionary<(uint, short), ClashChoice>());
        Set(manager, "_clashRockCounts", new Dictionary<(uint, short), int>());
        Set(manager, "_clashLogged", new HashSet<(uint, short)>());
        var store = new SideIntelStore(); Set(manager, "_intelStore", store);
        var intel = store.Get(0, 0); intel.OwnHome = ownHome; intel.EnemyHome = enemyHome;
        intel.ObserveRock(new(1, frontier, 5000, 5000, 3), 0);
        var array = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) array.SetValue(bots[i], i);
        Call("AssignClashTargets", manager, array, 100.0);
        Assert.Equal((short?)frontier, Get<short?>(bots[0], "TargetSector"));

        intel.ObserveRock(new(2, 2, 1_000_000, 1_000_000, 3), 101);
        Call("AssignClashTargets", manager, array, 101.0);
        Assert.Equal((short?)2, Get<short?>(bots[0], "TargetSector"));

        // A further discovery can trigger re-scoring; ore depletion and miner
        // activity must not drag the fleet back to the old frontier.
        intel.ObserveRock(new(2, 2, 0, 1_000_000, 3), 102);
        intel.ObserveRock(new(3, frontier, 100, 100, 3), 102);
        Call("AssignClashTargets", manager, array, 102.0);
        Assert.Equal((short?)2, Get<short?>(bots[0], "TargetSector"));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "Rendezvous", false)]
    [InlineData(false, null, false)]
    public void P953_Only_an_automatic_rendezvous_yields_to_the_scouted_resource_clash(
        bool mapTarget, string? query, bool resourceClash)
    {
        using var cs = Session(connected: true);
        cs.World.Apply(new ClusterInfo
        {
            Clusters = [new(0, true), new(1, false), new(2, false), new(3, false), new(4, true)],
            Warps = [new(0, 1), new(1, 2), new(2, 3), new(3, 4)]
        });
        var bots = Enumerable.Range(1, 4).Select(i => Bot(cs, (short)i)).ToArray();
        foreach (var bot in bots) Set(bot, "TargetSector", (short?)2);
        // This independent order must keep ownership when a clash is assigned.
        Set(bots[3], "TargetSector", (short?)1);
        var manager = Manager(bots);
        Set(manager, "_mapBattleTarget", mapTarget);
        Set(manager, "_battleTargetQuery", query);
        Set(manager, "_scenarioTargetOverride", (short?)2);
        Set(manager, "_openingStartedAt", 0.0);
        var choices = new Dictionary<(uint, short), ClashChoice>();
        Set(manager, "_clashRockCounts", new Dictionary<(uint, short), int>());
        Set(manager, "_clash", choices);
        Set(manager, "_clashLogged", new HashSet<(uint, short)>());
        var intel = new SideIntelStore();
        Set(manager, "_intelStore", intel);
        var side = intel.Get(0, 0);
        side.OwnHome = 0; side.EnemyHome = 4;
        side.MarkSector(2, 0);
        side.ObserveRock(new(1, 3, 12000, 12000, 3), 0);
        var array = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) array.SetValue(bots[i], i);

        Call("AssignClashTargets", manager, array, 100.0);

        Assert.Equal((short?)(resourceClash ? 3 : 2), Get<short?>(bots[0], "TargetSector"));
        Assert.Equal((short?)1, Get<short?>(bots[3], "TargetSector"));
        if (resourceClash)
        {
            var choice = Assert.Single(choices).Value;
            Assert.Equal((short)3, choice.Sector);
            Assert.True(choice.Resource > 0);
        }
        else Assert.Empty(choices);
    }
}

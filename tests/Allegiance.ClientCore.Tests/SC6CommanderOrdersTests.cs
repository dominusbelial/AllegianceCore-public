using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class SC6CommanderOrdersTests
{
    [Fact]
    public void Strike_attacks_enemy_station_while_fighter_intercepts_home_bomber()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots = [new(1, 0, Vector3.Zero, false, true), new(2, 0, Vector3.Zero, false, false)];
        ResearchTactics.Target[] targets = [new(10, IgcObjectType.Station, 0, Vector3.Zero, true),
            new(11, IgcObjectType.Station, 1, Vector3.Zero, false),
            new(20, IgcObjectType.Ship, 0, new(100, 0, 0), false),
            new(21, IgcObjectType.Ship, 0, new(3000, 0, 0), false, Bomber: true)];
        var orders = tactics.Tick(0, true, pilots, targets);
        Assert.Contains(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Station, 11), orders);
        Assert.Contains(new ResearchTactics.Order(2, OrderChange.CidAttack, IgcObjectType.Ship, 21), orders);
        var local = new ResearchTactics().Tick(0, true, [pilots[1]], targets.Where(t => t.Id != 21).ToArray());
        Assert.Equal((short)20, Assert.Single(local).Target);
        Assert.Empty(new ResearchTactics().Tick(0, true, [pilots[1]], targets.Where(t => t.Type == IgcObjectType.Station).ToArray()));
    }

    [Fact]
    public void Repair_prioritizes_damaged_friendly_station_then_local_ship_and_excludes_self()
    {
        ResearchTactics.Pilot[] pilots = [new(1, 0, Vector3.Zero, true, false)];
        ResearchTactics.Target[] targets = [new(1, IgcObjectType.Station, 1, Vector3.Zero, true, true),
            new(1, IgcObjectType.Ship, 0, Vector3.Zero, true, true),
            new(2, IgcObjectType.Ship, 0, Vector3.Zero, true, true),
            new(3, IgcObjectType.Ship, 0, Vector3.Zero, false, true),
            new(4, IgcObjectType.Ship, 0, Vector3.Zero, true),
            new(5, IgcObjectType.Ship, 1, Vector3.Zero, true, true)];
        Assert.Equal(new(1, OrderChange.CidRepair, IgcObjectType.Station, 1),
            Assert.Single(new ResearchTactics().Tick(0, true, pilots, targets)));
        Assert.Equal(new(1, OrderChange.CidRepair, IgcObjectType.Ship, 2),
            Assert.Single(new ResearchTactics().Tick(0, true, pilots, targets.Skip(1).ToArray())));
        Assert.Empty(new ResearchTactics().Tick(0, true, pilots, targets.Skip(3).ToArray()));
    }

    [Fact]
    public void Orders_wait_for_start_throttle_repeats_and_share_bandwidth_across_pilots()
    {
        var tactics = new ResearchTactics();
        var pilots = Enumerable.Range(1, 5).Select(i => new ResearchTactics.Pilot((short)i, 0, Vector3.Zero, false, true)).ToArray();
        ResearchTactics.Target[] targets = [new(10, IgcObjectType.Station, 1, Vector3.Zero, false)];
        Assert.Empty(tactics.Tick(0, false, pilots, targets));
        Assert.Equal(new short[] {1, 2}, tactics.Tick(0, true, pilots, targets).Select(o => o.Recipient));
        Assert.Equal(new short[] {3, 4}, tactics.Tick(.25, true, pilots, targets).Select(o => o.Recipient));
        Assert.Equal((short)5, Assert.Single(tactics.Tick(.5, true, pilots, targets)).Recipient);
        Assert.Empty(tactics.Tick(1, true, pilots, targets));
        Assert.Equal(2, tactics.Tick(15, true, pilots, targets).Length);
        Assert.Empty(tactics.Tick(16, true, [], targets));
        Assert.Equal(2, tactics.Tick(16, true, pilots, targets).Length);
    }

    [Fact]
    public void Repair_acceptance_releases_hostile_focus_and_preserves_named_order()
    {
        var repair = new QueuedCommandState(OrderChange.CidRepair, IgcObjectType.Ship, 99);
        var decision = BotManager.DecideAcceptTranslate(repair, OrderChange.CidNone, 42);
        Assert.True(decision.ShouldSendAccept);
        Assert.Null(decision.OrderedTarget);
        Assert.False(BotManager.DecideAcceptTranslate(repair, OrderChange.CidRepair, null, repair).ShouldSendAccept);
    }
}

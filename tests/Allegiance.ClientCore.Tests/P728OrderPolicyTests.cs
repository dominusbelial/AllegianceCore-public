using Allegiance.Core.Collision;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public class P728OrderPolicyTests
{
    [Fact]
    public void Defend_acquires_nearest_enemy_to_the_protectee_within_native_radius()
    {
        (short Id, Vector3 Position)[] enemies = [(1, new(1000, 0, 0)), (2, new(400, 0, 0)), (3, new(1600, 0, 0))];
        Assert.Equal((short)2, BotManager.PickCommanderDefenseTarget(enemies, Vector3.Zero, null));
        Assert.Null(BotManager.PickCommanderDefenseTarget(enemies, new(5000, 0, 0), null));
    }

    [Fact]
    public void Defend_keeps_the_chase_inside_native_leash_then_returns_or_switches()
    {
        (short Id, Vector3 Position)[] enemies = [(1, new(3000, 0, 0)), (2, new(400, 0, 0))];
        Assert.Equal((short)1, BotManager.PickCommanderDefenseTarget(enemies, Vector3.Zero, 1));
        Assert.Equal((short)2, BotManager.PickCommanderDefenseTarget(enemies, new(-1000, 0, 0), 1));
        Assert.Null(BotManager.PickCommanderDefenseTarget(enemies, new(-2000, 0, 0), 1));
    }
    [Fact]
    public void New_queued_target_with_the_same_command_id_supersedes_the_previous_target()
    {
        var previous = new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, 42);
        var next = new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, 99);
        var decision = BotManager.DecideAcceptTranslate(next, OrderChange.CidAttack, 42, previous);
        Assert.True(decision.ShouldSendAccept);
        Assert.Equal((short)99, decision.OrderedTarget);
        Assert.False(BotManager.DecideAcceptTranslate(next, OrderChange.CidAttack, 99, next).ShouldSendAccept);
    }

    [Theory]
    [InlineData((int)IgcObjectType.Station)]
    [InlineData((int)IgcObjectType.Warp)]
    [InlineData((int)IgcObjectType.Asteroid)]
    public void New_object_order_releases_the_previous_focus_fire_target(int type)
    {
        var order = new QueuedCommandState(OrderChange.CidGoto, (IgcObjectType)type, 99);
        var result = BotManager.DecideAcceptTranslate(order, OrderChange.CidNone, 42);
        Assert.True(result.ShouldSendAccept);
        Assert.Null(result.OrderedTarget);
    }

}

using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P651EquipmentFeedbackTests
{
    [Fact]
    public void Unknown_magazine_state_is_not_ready()
    {
        Assert.False(new ClientWorld().MissileLock.IsReady);
    }

    [Theory]
    [InlineData(IgcObjectType.Station)] [InlineData(IgcObjectType.Warp)]
    public void Static_target_identity_position_and_reload_readiness_reach_client(IgcObjectType type)
    {
        var world = new ClientWorld();
        var position = new Vector3(1, 2, 3);
        var message = MissileLockUpdate.From(1, 42, 0.5f, position, "Hunter", type, isReady: false);
        world.Apply(MissileLockUpdate.Decode(message.Encode()));
        Assert.Equal(35, message.Encode().Length);
        Assert.Equal(type, world.MissileLock.TargetType);
        Assert.Equal(position, world.MissileLock.TargetPosition);
        Assert.Equal(42, world.MissileLock.TargetId);
        Assert.True(world.MissileLock.HasTarget);
        Assert.False(world.MissileLock.IsReady);
        world.Apply(MissileLockUpdate.From(1, 42, 1, position, "Hunter", type, isReady: true));
        Assert.True(world.MissileLock.IsReady);
        Assert.True(world.MissileLock.IsFullLock);
    }

    [Fact]
    public void Changing_object_type_with_same_id_suppresses_lost_tip()
    {
        var world = new ClientWorld();
        world.Apply(MissileLockUpdate.From(1, 42, 1, Vector3.Zero, "Hunter"));
        world.Apply(MissileLockUpdate.From(1, 42, 0, Vector3.Zero, "Hunter", IgcObjectType.Station));
        Assert.DoesNotContain(world.Events.Chats, c => c.Message == "Missile lock lost.");
    }
}

using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P424 — <see cref="ClientWorld.MyQueuedCommand"/>: the local player's own
/// pending queued order, learned from the owner-only QUEUED_COMMAND_UPDATE(215)
/// message. Native exposes this as the <c>c_cmdQueued</c> slot on the client's
/// own ship copy (SetQueuedCommand, WinTrek.cpp:10713-10727); this renderer-only
/// client has no sim to derive it from, so the server streams it and the
/// MonoGame ACCEPT/REJECT/CLEAR keys read it. Pure-domain (construct-then-Apply,
/// no sockets — the ClientWorldUnitTests pattern).
/// </summary>
public class ClientWorldQueuedCommandTests
{
    [Fact]
    public void Queued_update_populates_my_queued_command()
    {
        var world = new ClientWorld();
        world.Apply(new QueuedCommandUpdate
        {
            CommandId = OrderChange.CidGoto,
            TargetType = IgcObjectType.Ship,
            TargetId = 444,
        });

        var pending = Assert.NotNull(world.MyQueuedCommand);
        Assert.Equal(OrderChange.CidGoto, pending.CommandId);
        Assert.Equal(IgcObjectType.Ship, pending.TargetType);
        Assert.Equal((short)444, pending.TargetId);
    }

    [Fact]
    public void Clear_update_nullifies_my_queued_command()
    {
        var world = new ClientWorld();
        world.Apply(new QueuedCommandUpdate { CommandId = OrderChange.CidGoto, TargetType = IgcObjectType.Ship, TargetId = 444 });
        Assert.NotNull(world.MyQueuedCommand);

        // A CidNone notification (accepted / rejected / auto-rejected) clears it.
        world.Apply(new QueuedCommandUpdate { CommandId = OrderChange.CidNone, TargetType = (IgcObjectType)(-1), TargetId = -1 });
        Assert.Null(world.MyQueuedCommand);
    }

    [Fact]
    public void Dispatch_routes_through_apply_ifedmessage()
    {
        // Apply(IFedMessage) must reach the per-type overload (the P264 dispatch
        // lesson) — send via the interface, not the concrete overload.
        var world = new ClientWorld();
        IFedMessage msg = new QueuedCommandUpdate { CommandId = OrderChange.CidGoto, TargetType = IgcObjectType.Asteroid, TargetId = 505 };
        world.Apply(msg);
        var pending = Assert.NotNull(world.MyQueuedCommand);
        Assert.Equal(IgcObjectType.Asteroid, pending.TargetType);
        Assert.Equal((short)505, pending.TargetId);
    }
}

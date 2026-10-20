using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// HUD-8b — <see cref="ClientWorld.MyCurrentCommand"/> is the client mirror of
/// native <c>c_cmdCurrent</c> (the current TARGET COMMAND, shipIGC.h:721-772),
/// the slot the red order turn arrow reads (indicator.cpp:411-457). It is
/// independent of both the accepted slot and the client-local autopilot target:
/// a <c>CmdCurrent</c> ORDER_CHANGE populates it, <c>objectID=NA</c> clears it,
/// and a <c>CmdAccepted</c> ORDER_CHANGE must not touch it.
/// </summary>
public class ClientWorldCurrentCommandTests
{
    [Fact]
    public void Set_current_command_populates_and_clears_the_cmd_current_slot()
    {
        var world = new ClientWorld();

        world.SetCurrentCommand(new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, 7));
        Assert.Equal(OrderChange.CidAttack, world.MyCurrentCommand!.Value.CommandId);
        Assert.Equal(IgcObjectType.Ship, world.MyCurrentCommand.Value.TargetType);
        Assert.Equal((short)7, world.MyCurrentCommand.Value.TargetId);

        world.SetCurrentCommand(null);
        Assert.Null(world.MyCurrentCommand);
    }

    [Fact]
    public void Cmd_current_order_change_populates_and_clears_without_touching_accepted()
    {
        var world = new ClientWorld();
        SetMyShipId(world, 55);

        world.Apply(new OrderChange
        {
            ShipID = 55,
            Command = OrderChange.CmdCurrent,
            CommandId = OrderChange.CidGoto,
            ObjectType = IgcObjectType.Ship,
            ObjectID = 66,
        });

        Assert.NotNull(world.MyCurrentCommand);
        Assert.Equal(OrderChange.CidGoto, world.MyCurrentCommand.Value.CommandId);
        Assert.Equal(IgcObjectType.Ship, world.MyCurrentCommand.Value.TargetType);
        Assert.Equal((short)66, world.MyCurrentCommand.Value.TargetId);
        Assert.Null(world.MyAcceptedCommand);   // current and accepted are separate slots

        // objectID=NA clears the slot (native SetTarget(NULL, cid), WinTrek.cpp:8832-8836).
        world.Apply(new OrderChange
        {
            ShipID = 55,
            Command = OrderChange.CmdCurrent,
            CommandId = OrderChange.CidNone,
            ObjectType = (IgcObjectType)(-1),
            ObjectID = -1,
        });
        Assert.Null(world.MyCurrentCommand);
    }

    [Fact]
    public void Cmd_accepted_order_change_does_not_populate_current()
    {
        var world = new ClientWorld();
        SetMyShipId(world, 55);

        world.Apply(new OrderChange
        {
            ShipID = 55,
            Command = OrderChange.CmdAccepted,
            CommandId = OrderChange.CidAttack,
            ObjectType = IgcObjectType.Ship,
            ObjectID = 66,
        });

        Assert.NotNull(world.MyAcceptedCommand);
        Assert.Null(world.MyCurrentCommand);
    }

    private static void SetMyShipId(ClientWorld world, short id)
    {
        var property = typeof(ClientWorld).GetProperty(nameof(ClientWorld.MyShipId))
            ?? throw new System.MissingMemberException(nameof(ClientWorld), nameof(ClientWorld.MyShipId));
        property.SetMethod!.Invoke(world, new object?[] { (short?)id });
    }
}

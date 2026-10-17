using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>The production command-key transition, independent of graphics.
/// WinTrek/WinTrek.cpp:10389-10412,10627-10696. Caller holds the world gate.</summary>
internal sealed class CommandActionInput
{
    private readonly ActionPressEdges _edges = new();
    private static readonly string[] Actions = ["AcceptCommand", "ExecuteCommand", "RejectCommand", "ClearCommand", "TargetCommand"];
    internal readonly record struct Result(OrderChange? Request = null, bool ChangeSelection = false,
        QueuedCommandState? Selection = null, bool Engage = false, short? JoinWing = null, PlayerDonate? Donation = null);

    internal void Prime(KeyboardState keys, ActionMap map) =>
        _edges.Prime(Actions.Select(a => (a, map.IsDownMostSpecific(keys, a))));

    internal Result Step(KeyboardState keys, bool suppressed, ActionMap map, ClientWorld world)
    {
        bool accept = Edge("AcceptCommand"), execute = Edge("ExecuteCommand"), reject = Edge("RejectCommand"),
            clear = Edge("ClearCommand"), target = Edge("TargetCommand");
        bool Edge(string action) => _edges.Observe(action, map.IsDownMostSpecific(keys, action), suppressed);
        if (world.MyShipId is not { } me) return default;
        if (world.PendingMoneyRequest is not null)
        {
            if (accept || execute) return new(Donation: world.AcceptMoneyRequest());
            if (reject) { world.RejectMoneyRequest(); return default; }
        }
        if ((accept || execute) && world.AcceptQueuedCommand() is { } command)
        {
            short? wing = command.CommandId == OrderChange.CidJoin ? world.Ships[me].WingId : null;
            return new(new OrderChange { ShipID = me, Command = OrderChange.CmdQueued, CommandId = command.CommandId,
                ObjectType = (Allegiance.Core.IgcObjectType)(-1), ObjectID = -1 },
                wing is null, wing is null ? command : null, execute && world.CanExecuteCommand(command), wing);
        }
        if (reject)
        {
            world.RejectQueuedCommand();
            return new(new OrderChange { ShipID = me, Command = OrderChange.CmdQueued, CommandId = OrderChange.CidNone,
                ObjectType = (Allegiance.Core.IgcObjectType)(-1), ObjectID = -1 });
        }
        if (clear)
        {
            world.ClearAcceptedCommand();
            return new(new OrderChange { ShipID = me, Command = OrderChange.CmdAccepted, CommandId = OrderChange.CidNone,
                ObjectType = (Allegiance.Core.IgcObjectType)(-1), ObjectID = -1 });
        }
        return target ? new(ChangeSelection: true, Selection: world.MyAcceptedCommand) : default;
    }
}

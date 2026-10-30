using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>The accepted slot is independent of both selection and the queued
    /// slot (WinTrek.cpp:10408-10412,10668-10681). Access under the session gate.</summary>
    public QueuedCommandState? MyAcceptedCommand { get; private set; }

    /// <summary>HUD-8b — the native <c>c_cmdCurrent</c> slot: the current TARGET
    /// COMMAND (<c>SetTarget</c>, WinTrek.cpp:8823-8852), read by the red order
    /// turn arrow (<c>indicator.cpp:411-457,783-791</c>). It is independent of the
    /// client-local autopilot target (<c>Game1._autoTarget</c>): selecting a target
    /// (T / class keys / <c>TK_TargetCommand</c>) sets it, issuing a command sets it
    /// to the accepted target, and autopilot engage/disengage does NOT touch it.
    /// Access under the session gate.</summary>
    public QueuedCommandState? MyCurrentCommand { get; private set; }

    public void ClearAcceptedCommand() => MyAcceptedCommand = null;

    /// <summary>HUD-8b — install/clear the current target command mirror of native
    /// <c>SetCommand(c_cmdCurrent, …)</c> (shipIGC.h:721-772). A <c>null</c> target
    /// clears the slot exactly like native <c>SetCommand(c_cmdCurrent, NULL,
    /// c_cidNone)</c>. Access under the session gate.</summary>
    public void SetCurrentCommand(QueuedCommandState? command) => MyCurrentCommand = command;

    /// <summary>Native RejectQueuedCommand(true, true), WinTrek.cpp:10583-10623.
    /// P755 also sends the native voNegativeSound reply to the order's sender.</summary>
    public void RejectQueuedCommand()
    {
        if (_lastCommandOrMoneySender != MyShipId && _ships.ContainsKey(_lastCommandOrMoneySender))
            SendNegativeChat(_lastCommandOrMoneySender);
        _myQueuedCommand = null;
        _lastCommandOrMoneySender = Na;
    }

    /// <summary>Native AcceptQueuedCommand, WinTrek.cpp:10659-10712. Returns the
    /// order only when it was actually consumed — everything (the accepted slot,
    /// the wing change, the caller's accept sound / autopilot engage) is gated on
    /// <c>pmodel && LegalCommand(cid, pmodel)</c> (WinTrek.cpp:10662-10699); an
    /// illegal order is not consumed and the caller must not accept it. The
    /// voAffirmativeSound reply still goes out for any pending order, legal or
    /// not (WinTrek.cpp:10703-10706). P755.</summary>
    public QueuedCommandState? AcceptQueuedCommand()
    {
        if (_myQueuedCommand is not { } command)
        {
            _lastCommandOrMoneySender = Na;
            return null;
        }
        // native sends the affirmative whenever the order's sender is not the
        // local ship, legal or not (WinTrek.cpp:10703-10706), and then drops the
        // sender (10709) — a second accept press replies no more.
        if (_lastCommandOrMoneySender != MyShipId && _ships.ContainsKey(_lastCommandOrMoneySender))
            SendAffirmativeChat(_lastCommandOrMoneySender);
        _lastCommandOrMoneySender = Na;
        if (!LegalQueuedCommand(command))
        {
            // native consumes nothing for an illegal order; the pending order
            // stays until its 15 s expiry (WinTrek.cpp:10662,10699).
            return null;
        }
        if (command.CommandId == OrderChange.CidJoin)
        {
            if (MyShipId is { } me) Upsert(me).WingId = _ships[command.TargetId].WingId;
        }
        else MyAcceptedCommand = command;
        _myQueuedCommand = null;
        return command;
    }

    // Native local buoy creation precedes SetQueuedCommand (mission4.cpp:333-337).
    // CHATBUOY imports the same geometry for ordinary remote waypoint orders.
    private readonly Dictionary<short, DataBuoyIGC> _commandBuoys = new();
    public IReadOnlyDictionary<short, DataBuoyIGC> CommandBuoys => _commandBuoys;
    public void RegisterCommandBuoy(DataBuoyIGC buoy) => _commandBuoys[buoy.BuoyId] = buoy;

    public bool TryCommandTarget(QueuedCommandState command, out short sector, out string name)
    {
        sector = -1; name = "";
        switch (command.TargetType)
        {
            case IgcObjectType.Ship when _ships.TryGetValue(command.TargetId, out var ship) && !ship.IsDead:
                sector = ship.SectorId; name = ship.CharacterName; return true;
            case IgcObjectType.Station when _stations.TryGetValue(command.TargetId, out var station):
                sector = station.ClusterId; name = station.Name; return true;
            case IgcObjectType.Asteroid when _asteroids.TryGetValue(command.TargetId, out var asteroid):
                sector = asteroid.ClusterId; name = asteroid.Name; return true;
            case IgcObjectType.Warp when _warps.TryGetValue(command.TargetId, out var warp):
                sector = warp.ClusterId; name = warp.Name; return true;
            case IgcObjectType.Buoy when _commandBuoys.TryGetValue(command.TargetId, out var buoy):
                sector = buoy.ClusterId; name = "waypoint"; return true;
            case IgcObjectType.Probe when _probes.TryGetValue(command.TargetId, out var probe):
                sector = probe.ClusterId; name = $"probe {probe.ProbeId}"; return true;
            case IgcObjectType.Treasure when _treasures.TryGetValue(command.TargetId, out var treasure):
                sector = treasure.ClusterId; name = $"treasure {treasure.TreasureId}"; return true;
            default: return false;
        }
    }

    // Igc/shipIGC.h:1604-1717: player command legality. Ship-type abilities
    // come from the mission's static core, never from guessed hull names.
    public bool LegalQueuedCommand(QueuedCommandState command)
    {
        if (MyShipId is not { } me || !_ships.TryGetValue(me, out var own)
            || !TryCommandTarget(command, out _, out _) || (command.TargetType == IgcObjectType.Ship && command.TargetId == me)
            || (own.Latest is { } pose && (pose.StateM & 0x100000) != 0)) return false;
        short? side = command.TargetType switch
        {
            IgcObjectType.Ship => _ships[command.TargetId].SideId,
            IgcObjectType.Station => _stations[command.TargetId].SideId,
            IgcObjectType.Probe => _probes[command.TargetId].SideId,
            _ => null,
        };
        bool friendly = side is { } other && (own.SideId == other || AreAllied(own.SideId, other));
        return command.CommandId switch
        {
            OrderChange.CidDefault or OrderChange.CidGoto or OrderChange.CidDefend => true,
            OrderChange.CidAttack => command.TargetType is not (IgcObjectType.Warp or IgcObjectType.Treasure) && !friendly
                && (command.TargetType != IgcObjectType.Ship || _ships[command.TargetId].SectorId >= 0),
            OrderChange.CidCapture => command.TargetType == IgcObjectType.Station && !friendly,
            OrderChange.CidRepair => command.TargetType is IgcObjectType.Ship or IgcObjectType.Station && friendly,
            OrderChange.CidJoin => command.TargetType == IgcObjectType.Ship,
            OrderChange.CidPickup => own.ParentId < 0 && (command.TargetType == IgcObjectType.Treasure
                || (command.TargetType == IgcObjectType.Ship && CanPickup(own, _ships[command.TargetId]))),
            _ => false,
        };
    }

    private bool CanPickup(ShipEntity own, ShipEntity target)
    {
        if (MissionCore is not { } core || !core.HullTypes.TryGetValue(own.HullId, out var a)
            || !core.HullTypes.TryGetValue(target.HullId, out var b)) return false;
        return ((a.HabmCapabilities & DataHullTypeIGC.HabmRescue) != 0 && (b.HabmCapabilities & DataHullTypeIGC.HabmLifepod) != 0)
            || ((b.HabmCapabilities & DataHullTypeIGC.HabmRescue) != 0 && (a.HabmCapabilities & DataHullTypeIGC.HabmLifepod) != 0);
    }

    // GetCluster resolves the target or last-known ship sector; it does not
    // require a warp route. WinTrek.cpp:10683-10691; clintlib/clintlib.h:1031-1048.
    public bool CanExecuteCommand(QueuedCommandState command)
    {
        if (command.CommandId == OrderChange.CidJoin || MyShipId is not { } me || !_ships.TryGetValue(me, out var own)
            || own.StationId >= 0 || own.ParentId >= 0 || own.SectorId < 0
            || !TryCommandTarget(command, out short targetSector, out _) || targetSector < 0) return false;
        return true;
    }

    /// <summary>P761b — the ship's accepted-command text, the native
    /// <c>CurrentCommandText(pship)</c> (<c>WinTrek/teampane.cpp:1660-1676</c>):
    /// <c>szVerb</c> plus " " + the target's model name, or just the verb when
    /// the slot has no target. Empty when the slot is unset (<c>c_cidNone</c>) or
    /// carries an id outside native's <c>[0, c_cidMax)</c> verb table
    /// (<c>Igc/common.cpp:41-52</c>) — the same empty string native returns from
    /// its <c>!LegalCommand</c> guard.</summary>
    public string AcceptedCommandText(short shipId)
    {
        // P761c — the OWN ship's accepted text comes from the client-local
        // MyAcceptedCommand, exactly like native (the server never echoes an
        // ORDER_CHANGE back to the commanded ship; appmsg.cpp:1838). The other
        // ships' text comes from the relayed entity slot.
        if (shipId == MyShipId)
            return MyAcceptedCommand is { } mine
                ? OrderChange.AcceptedCommandText(mine.CommandId,
                    mine.TargetId == Na ? null : TargetNameFor((short)mine.TargetType, mine.TargetId))
                : "";
        return _ships.TryGetValue(shipId, out var ship)
            ? OrderChange.AcceptedCommandText(ship.AcceptedCommandId,
                ship.AcceptedTargetType is { } type
                    ? TargetNameFor(type, ship.AcceptedTargetId)
                    : null)
            : "";
    }

    /// <summary>The model name of a command target, or null when the id does not
    /// resolve in this client's tables (native <c>GetModelName(NULL)</c> → the
    /// bare-verb branch). Reuses <see cref="TryCommandTarget"/>'s per-type name
    /// resolution.</summary>
    public string? TargetNameFor(short objectType, short objectId)
        => TryCommandTarget(
                new QueuedCommandState(OrderChange.CidNone, (IgcObjectType)objectType, objectId),
                out _, out string name)
            ? name
            : null;

    private void Apply(OrderChange order)
    {
        // P761b — native applies ORDER_CHANGE to ANY ship in the client core
        // (clintlib/appmsg.cpp:1831-1855: `if (pship != m_ship)
        // pship->SetCommand(...)`); the server forwards every accepted-command
        // change to the whole side (FedSrv/FedSrv.CPP:13695-13732). Storing the
        // slot on the ship entity is what lets the team pane's OBJECTIVE column
        // sort by command text (CurrentCommandText, WinTrek/teampane.cpp:1660-1676).
        // P761c — NEVER write the entity slot for the OWN ship: native's client
        // skips `pship == m_ship` (appmsg.cpp:1838), and an echo would wipe a
        // station order's locally-set target. Own text reads MyAcceptedCommand.
        if (order.Command == OrderChange.CmdAccepted && order.ShipID != MyShipId
            && _ships.TryGetValue(order.ShipID, out var commanded))
        {
            commanded.AcceptedCommandId = order.CommandId;
            commanded.AcceptedTargetType = order.ObjectID == Na ? null : (short)order.ObjectType;
            commanded.AcceptedTargetId = order.ObjectID;
        }
        if (order.ShipID != MyShipId) return;
        if (order.Command == OrderChange.CmdAccepted)
        {
            MyAcceptedCommand = order.CommandId == OrderChange.CidNone ? null
                : new QueuedCommandState(order.CommandId, order.ObjectType, order.ObjectID);
        }
        else if (order.Command == OrderChange.CmdCurrent)
        {
            // HUD-8b — the current target command slot (WinTrek.cpp:8838-8844:
            // SetTarget sends command=c_cmdCurrent; objectID=NA clears it).
            MyCurrentCommand = order.ObjectID == Na
                ? null
                : new QueuedCommandState(order.CommandId, order.ObjectType, order.ObjectID);
        }
    }
}

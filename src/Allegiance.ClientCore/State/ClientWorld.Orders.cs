using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>The accepted slot is independent of both selection and the queued
    /// slot (WinTrek.cpp:10408-10412,10668-10681). Access under the session gate.</summary>
    public QueuedCommandState? MyAcceptedCommand { get; private set; }

    public void ClearAcceptedCommand() => MyAcceptedCommand = null;

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

    private void Apply(OrderChange order)
    {
        if (order.ShipID != MyShipId || order.Command != OrderChange.CmdAccepted) return;
        MyAcceptedCommand = order.CommandId == OrderChange.CidNone ? null
            : new QueuedCommandState(order.CommandId, order.ObjectType, order.ObjectID);
    }
}

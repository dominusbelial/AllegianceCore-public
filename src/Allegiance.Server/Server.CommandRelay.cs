using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P761b — the accepted-command relay: the port of native
/// <c>FedSrvSiteBase::CommandChangedEvent</c> (<c>FedSrv/FedSrv.CPP:13695-13732</c>),
/// which forwards a ship's <c>c_cmdAccepted</c> slot to the WHOLE side group
/// whenever it changes ("forward the command change to everyone on the side").
/// The receiving client applies it to whichever ship it names
/// (<c>clintlib/appmsg.cpp:1831-1855</c>: <c>if (pship != m_ship)
/// pship-&gt;SetCommand(...)</c>), which is what gives the team pane's OBJECTIVE
/// column something to sort by (<c>CurrentCommandText</c>,
/// <c>WinTrek/teampane.cpp:1528-1534, 1660-1676</c>).
///
/// <para><b>Shape.</b> Native is event-driven (the server's IGC site fires on
/// every <c>SetCommand</c>). This port's sim owns its ships without a site
/// callback for that slot, so the relay is a per-tick edge SWEEP over the ships
/// the server can enumerate — the same observation-send shape as the P493
/// ship-status and P349 cross-side export sweeps. Only an actual change is sent
/// (a latch per ship), so an idle side sees no traffic.</para>
///
/// <para><b>Scope.</b> Player ships carry their accepted slot in
/// <c>Ship.AcceptedShipCommandId</c>/<c>AcceptedShipTarget</c> (P154) and NPCs in
/// <c>Ship.AcceptedCommandId</c>/<c>AcceptedTarget</c> (P150); both are relayed.
/// Native's second arm — <c>c_cmdCurrent</c> for an area-of-effect weapon
/// (<c>FedSrv.CPP:13703-13705</c>) — is not relayed: this port's
/// <c>c_cmdCurrent</c> slot is the client-local weapon-aim
/// <c>Ship.CommandTarget</c>, and the team pane's objective text reads only
/// <c>c_cmdAccepted</c>.</para>
/// </summary>
public sealed partial class Server
{
    /// <summary>Last accepted command relayed per ship (reference-keyed: one
    /// entry per live ship object, dropped when the ship leaves the world).
    /// The value is the exact (CommandID, ObjectType, ObjectID) triple that went
    /// on the wire.</summary>
    private readonly Dictionary<Ship, (short Cid, short ObjectType, short ObjectId)>
        _lastRelayedAcceptedCommand = new(ReferenceEqualityComparer.Instance);

    // P761c — the sweep is a sim-thread step (Server.Broadcast.cs), so its
    // per-tick scratch is reused rather than allocated every tick.
    private readonly HashSet<Ship> _relayLiveShips = new(ReferenceEqualityComparer.Instance);
    private readonly List<Ship> _relayGoneShips = new();
    private readonly List<ConnectionId> _acceptedCommandTargets = new();

    /// <summary>Per-tick edge sweep: relay every ship's changed accepted command
    /// to the side's connected peers (<c>c_cmdAccepted</c>,
    /// <c>FedSrv.CPP:13695-13732</c>).</summary>
    private void SweepAcceptedCommands()
    {
        if (_peerShips.Count == 0)
        {
            // Nothing to send; keep no stale latches while the server is empty.
            if (_lastRelayedAcceptedCommand.Count > 0) _lastRelayedAcceptedCommand.Clear();
            return;
        }

        var live = _relayLiveShips;
        live.Clear();
        foreach (var mission in _missions)
        {
            // Index loops: foreach over an IReadOnlyList boxes its enumerator,
            // and this sweep runs every tick.
            var sectors = mission.Sectors;
            for (int i = 0; i < sectors.Count; i++)
            {
                var models = sectors[i].Models;
                for (int j = 0; j < models.Count; j++)
                    if (models[j] is Ship s && s.SideId is not null)
                        live.Add(s);
            }
            // A DOCKED player's ship is out of its cluster (docking removes it
            // from the sector's model list) but keeps its side and its accepted
            // slot — native still relays it, the side group is location-blind.
            foreach (var peer in _peerShips.Values)
                if (ReferenceEquals(peer.Ship.Mission, mission) && peer.Ship.SideId is not null)
                    live.Add(peer.Ship);
        }

        // Drop latches for ships that left the world (destroyed / despawned), so
        // the dictionary never pins dead ship objects. The gone list is reused:
        // removing from the dictionary while enumerating its keys is not legal.
        if (_lastRelayedAcceptedCommand.Count > 0)
        {
            _relayGoneShips.Clear();
            foreach (var ship in _lastRelayedAcceptedCommand.Keys)
                if (!live.Contains(ship))
                    _relayGoneShips.Add(ship);
            foreach (var gone in _relayGoneShips)
                _lastRelayedAcceptedCommand.Remove(gone);
        }

        foreach (var ship in live)
        {
            var triple = AcceptedCommandOf(ship);
            // The FIRST observation only latches: native's CommandChangedEvent
            // fires on a TRANSITION, so a ship that joins with a command already
            // set is not re-announced here (a joiner learns state through the
            // join roster + SHIP_STATUS, exactly as native does).
            bool changed = _lastRelayedAcceptedCommand.TryGetValue(ship, out var previous)
                && previous != triple;
            _lastRelayedAcceptedCommand[ship] = triple;
            if (changed)
                SendAcceptedCommand(ship, triple.Cid, triple.ObjectType, triple.ObjectId);
        }
    }

    /// <summary>The ship's native <c>c_cmdAccepted</c> slot as a wire triple.</summary>
    internal static (short Cid, short ObjectType, short ObjectId) AcceptedCommandOf(Ship ship)
    {
        short cid;
        // OT_invalid = -1 (Igc/igc.h:114-160): "no target".
        short objectType = (short)(IgcObjectType)(-1);
        short objectId = -1;
        if (ship.IsNpc)
        {
            // P150 slot: an NPC's return-to-base station.
            cid = ship.AcceptedCommandId;
            if (ship.AcceptedTarget is { } station)
            {
                objectType = (short)IgcObjectType.Station;
                objectId = station.StationId;
            }
        }
        else
        {
            // P154 slot: a real player's menu-accepted order target.
            cid = ship.AcceptedShipCommandId;
            if (ship.AcceptedShipTarget is { } model)
            {
                // P761c — the type comes from the model, never a hardcoded Ship:
                // a Probe is not a Ship, and a station/asteroid/warp target is
                // an EquipmentTarget whose ObjectType is the real native OT_*.
                objectType = (short)EquipmentTarget.TypeOf(model);
                objectId = model.Id;
            }
            else if (ship.AcceptedShipWireId >= 0)
            {
                // P761c — a non-Model accepted target (station/asteroid/warp),
                // carried from the queued order's original wire triple because
                // this port has no Model for those kinds.
                objectType = (short)ship.AcceptedShipWireType;
                objectId = ship.AcceptedShipWireId;
            }
        }
        return (cid, objectType, objectId);
    }

    /// <summary>Send one accepted-command change to the ship's side group
    /// (<c>CFSSide::FromIGC(pship-&gt;GetSide())-&gt;GetGroup()</c>, guaranteed,
    /// <c>FedSrv.CPP:13724-13731</c>).</summary>
    private void SendAcceptedCommand(Ship ship, short cid, short objectType, short objectId)
    {
        if (ship.SideId is not { } sideId || ship.Mission is not { } mission) return;
        var targets = _acceptedCommandTargets;
        targets.Clear();
        foreach (var kv in _peerShips)
        {
            // Native's client ignores its OWN ship in ORDER_CHANGE
            // (clintlib/appmsg.cpp:1838: `if (pship != m_ship)`), so the server
            // does not echo the commanded ship back to its own connection. The
            // port's own order text comes from MyAcceptedCommand, set locally
            // when the order is issued/accepted; an echo would overwrite it
            // (and a station order's (cid, -1, -1) would wipe the target).
            if (ReferenceEquals(kv.Value.Ship, ship)) continue;
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId != sideId) continue;
            targets.Add(kv.Key);
        }
        if (targets.Count == 0) return;
        var msg = new OrderChange
        {
            Command = OrderChange.CmdAccepted,
            ShipID = ship.Id,
            CommandId = cid,
            ObjectType = (IgcObjectType)objectType,
            ObjectID = objectId,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
    }
}

using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    /// <summary>The ship half of Reset(false), Igc/shipIGC.h:487-540.
    /// The server promotes any passengers first (FedSrv/fsmission.cpp:904-921).
    /// Player score/deaths remain owned by the player's mission record.</summary>
    internal void ResetForSideDeparture()
    {
        ResetKillBonus();
        Flag = null;
        _damageTrack = null;
        SetCommand(OrderChange.CmdQueued, null, OrderChange.CidNone);
        SetCommand(OrderChange.CmdAccepted, null, OrderChange.CidNone);
        SetCommand(OrderChange.CmdCurrent, null, OrderChange.CidNone);
        SetCommand(OrderChange.CmdPlan, null, OrderChange.CidNone);
        QueuedTarget = null;
        QueuedShipTarget = null;
        QueuedCommandId = OrderChange.CidNone;
        QueuedShipWireType = (IgcObjectType)(-1);
        QueuedShipWireId = -1;
        AcceptedTarget = null;
        AcceptedCommandId = OrderChange.CidNone;
        PlanTarget = null;
        GotoWaypoint = null; // multiplayer's waypoint representation of c_cmdPlan
        PlanCommandTarget = null;
        _planCommandId = OrderChange.CidNone;
        StationId = null;
        SetRipcordModel(null);
        Sector?.Remove(this);
        Sector = null;
        if (ParentShip is { } parent)
        {
            parent.Weapons.FirstOrDefault(w => w.Gunner == this)?.SetGunner(null);
            parent.RemoveChildShip(this);
            ParentShip = null;
            TurretId = null;
        }
        ClearLoadout();
        RefundableHullValue = 0;
        if (SideId is { } sideId && Mission is { } mission
            && mission.Sides.FirstOrDefault(s => s.Id == sideId) is { } side
            && mission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var pod))
            ChangeHull(pod);
        // Retire controls while absent; old client updates cannot fly the lobby pod.
        StateM = 0;
        Velocity = Vector3.Zero;
    }
}

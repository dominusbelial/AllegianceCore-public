using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    private int _buoyCommandHolds;
    private bool _updatingTrainingPlan;
    private Model? _acceptedShipTarget, _queuedShipTarget, _planCommandTarget;

    // Shared by SetCommand and the server/AI's direct slot properties. Training
    // alone has the native four-slot buoy lifetime; multiplayer retains its
    // existing lazy Goto ownership. No tick sweep or per-command allocation.
    // Igc/shipIGC.h:731-782: acquire the replacement before releasing the old
    // target (including an identical-target write).
    private void ReplaceCommandTarget(ref Model? slot, Model? value, short command)
    {
        if (_updatingTrainingPlan) return;
        if (ReferenceEquals(slot, value)) return;
        if (slot is not Buoy && value is not Buoy)
        {
            slot = value;
            return;
        }
        int bit = 1 << command;
        bool held = (_buoyCommandHolds & bit) != 0;
        var previous = slot;
        slot = value;
        _buoyCommandHolds &= ~bit;
        if (Mission?.IsTraining == true && value is Buoy buoy)
        {
            buoy.AddConsumer();
            _buoyCommandHolds |= bit;
        }
        if (held) ((Buoy)previous!).ReleaseConsumer();
    }

    // Native ResetWaypoint translates the canonical plan into execution goals
    // (Igc/shipIGC.h:750-763; shipIGC.cpp:3984-4151). The legacy typed goal
    // helpers also write selection/accepted properties. During this translation
    // those writes must not overwrite independent native command slots.
    private void ResetTrainingExecutionPlan(Model? target, short cid)
    {
        var previousGoto = GotoWaypoint;
        var previousLayer = LayerBuildOrder;
        _updatingTrainingPlan = true;
        try
        {
            GotoWaypoint = null;
            _planTarget = null;
            BuilderAsteroidTarget = null; MiningAsteroidTarget = null; LayerAsteroidTarget = null;
            AttackTarget = null; _attackStaticTarget = null; DefendTarget = null; PickupTarget = null;
            RepairTarget = null; RepairStationTarget = null; LayerBuildOrder = null;
            _wingmanInterruptedGoto = null;
            if (PilotType < PilotTypePlayer) _isDrilling = false;

            if (cid == OrderChange.CidAttack && target is Ship or Probe or EquipmentTarget { Body: Station })
                SetWingmanPlanAttack(target);
            else if (cid == OrderChange.CidDefend && target is Ship charge)
                DefendTarget = charge;
            else if (cid == OrderChange.CidRepair)
            {
                RepairTarget = target as Ship;
                RepairStationTarget = (target as EquipmentTarget)?.Body as Station;
            }
            else if (cid == OrderChange.CidMine && target is EquipmentTarget { Body: Asteroid mine })
                MiningAsteroidTarget = mine;
            else if (cid == OrderChange.CidBuild)
            {
                if (target is EquipmentTarget { Body: Asteroid rock }) SetBuilderBuild(rock);
                else if (PilotType == PilotTypeLayer && target is Buoy buoy)
                    SetLayerBuild(new LayerBuildGoal(buoy.Data.ClusterId, buoy.Position));
                else if (PilotType == PilotTypeLayer && target is EquipmentTarget { Body: ResolvedWarp } && previousLayer is { } lay)
                    SetLayerBuild(lay); // the training host supplied native warp-relative spread
            }
            else if (cid == OrderChange.CidPickup && target is Ship pod)
                PickupTarget = pod;
            else if (cid == OrderChange.CidGoto && target is not null)
            {
                if (target is EquipmentTarget { Body: Station station }) _planTarget = station.Data;
                else
                {
                    short? sector = target switch
                    {
                        Buoy b => b.Data.ClusterId, Ship s => s.Sector?.Id,
                        EquipmentTarget t => t.OwnerSector.Id, Probe p => p.OwnerSector.Id,
                        Mine m => m.OwnerSector.Id, Treasure t => t.OwnerSector.Id, _ => null,
                    };
                    if (sector is { } cluster)
                    {
                        var type = EquipmentTarget.TypeOf(target);
                        var goal = previousGoto is { TargetType: { } oldType, TargetId: { } oldId } old
                            && oldType == type && oldId == target.Id ? old
                            : new GotoOrder(GotoKind.Waypoint, cluster, target.Position, TargetType: type, TargetId: target.Id);
                        SetGoto(goal);
                    }
                }
            }
            _acceptedWingmanAttackTarget = AcceptedShipCommandId == OrderChange.CidAttack ? AcceptedShipTarget : null;
            // SetGoto/SetBuilderBuild clear their legacy station-plan id. The
            // canonical native slot retains the command being translated.
            _planCommandId = cid;
            _nativeWaypoints.Reset();
        }
        finally { _updatingTrainingPlan = false; }
    }

    /// <summary>The model-valued native plan slot, alongside the existing
    /// typed simulation goals (Igc/shipIGC.h:711-719).</summary>
    public Model? PlanCommandTarget
    {
        get => _planCommandTarget;
        set => ReplaceCommandTarget(ref _planCommandTarget, value, OrderChange.CmdPlan);
    }

    private void ClearTrainingCommands()
    {
        if (Mission?.IsTraining != true) return;
        // Reset and mission retirement must use the same ownership path as a
        // replacement order (Igc/shipIGC.h:503-506).
        SetCommand(OrderChange.CmdQueued, null, OrderChange.CidNone);
        SetCommand(OrderChange.CmdAccepted, null, OrderChange.CidNone);
        SetCommand(OrderChange.CmdCurrent, null, OrderChange.CidNone);
        SetCommand(OrderChange.CmdPlan, null, OrderChange.CidNone);
    }

    internal void RetireTrainingCommands() => ClearTrainingCommands();

    private void ArriveTrainingSector(Sector? sector)
    {
        if (Mission is not { IsTraining: true } mission || mission.TrainingPlayerShipId != Id || sector is null) return;
        // WinTrek/trekigc.cpp:2773-2785: arriving at a cluster buoy completes
        // those slots, while an ordinary waypoint survives sector travel.
        bool completedPlan = PlanCommandTarget is Buoy plan && plan.Data.Type == DataBuoyIGC.CBuoyCluster && plan.Data.ClusterId == sector.Id;
        ClearArrived(OrderChange.CmdQueued, QueuedShipTarget);
        ClearArrived(OrderChange.CmdAccepted, AcceptedShipTarget);
        ClearArrived(OrderChange.CmdCurrent, CommandTarget);
        ClearArrived(OrderChange.CmdPlan, PlanCommandTarget);
        if (completedPlan) GotoWaypoint = null;
        void ClearArrived(short command, Model? target)
        {
            if (target is Buoy b && b.Data.Type == DataBuoyIGC.CBuoyCluster && b.Data.ClusterId == sector.Id)
                SetCommand(command, null, OrderChange.CidNone);
        }
    }

    /// <summary>Complete travel through the same matching-slot retirement
    /// when the training adapter owns movement (Igc/shipIGC.cpp:2795-2807).</summary>
    public void CompleteGotoOrder()
    {
        CompleteTrainingGoto();
        SetGoto(null);
    }

    private void CompleteTrainingGoto()
    {
        if (Mission?.IsTraining != true || PlanCommandTarget is not { } target) return;
        // Igc/shipIGC.cpp:2795-2807: completion clears every slot sharing the
        // completed plan, not unrelated accepted/queued commands.
        if (ReferenceEquals(QueuedShipTarget, target)) SetCommand(OrderChange.CmdQueued, null, OrderChange.CidNone);
        if (ReferenceEquals(AcceptedShipTarget, target)) SetCommand(OrderChange.CmdAccepted, null, OrderChange.CidNone);
        if (ReferenceEquals(CommandTarget, target)) SetCommand(OrderChange.CmdCurrent, null, OrderChange.CidNone);
        if (ReferenceEquals(PlanCommandTarget, target)) SetCommand(OrderChange.CmdPlan, null, OrderChange.CidNone);
    }
}

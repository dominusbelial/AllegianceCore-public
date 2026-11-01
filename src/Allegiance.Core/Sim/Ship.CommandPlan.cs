using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    /// <summary>The live NPC plan, rather than its retained accepted order.
    /// Igc/shipIGC.h:707-718; training/mission6.cpp:112-127 reads c_cmdPlan.
    /// Materializes a navigation buoy only when the plan is actually observed.</summary>
    public (Model? Target, short CommandId) GetNpcPlan()
    {
        if (ActiveWingmanTarget is { } attack) return (attack, OrderChange.CidAttack);
        if (GotoWaypoint is { } go)
        {
            if (go.TargetStationId is { } station)
                return (Mission?.Sectors.Select(s => EquipmentTarget.Resolve(s, IgcObjectType.Station, station))
                    .FirstOrDefault(m => m is not null), OrderChange.CidGoto);
            if (go.TargetType is { } type && go.TargetId is { } id)
            {
                var model = type == IgcObjectType.Buoy ? Mission?.GetBuoy(id)
                    : Mission?.Sectors.Select(s => EquipmentTarget.Resolve(s, type, id)).FirstOrDefault(m => m is not null);
                return (model, OrderChange.CidGoto);
            }
            if (_gotoBuoyMission is { } mission) return (mission.GetBuoy(_gotoBuoyId), OrderChange.CidGoto);
        }
        if (PlanTarget is { } plan)
            return (Mission?.Sectors.Select(s => EquipmentTarget.Resolve(s, IgcObjectType.Station, plan.StationId))
                .FirstOrDefault(m => m is not null), PlanCommandId);
        if (RepairTarget is { } repair) return (repair, OrderChange.CidRepair);
        if (DefendTarget is { } defend) return (defend, OrderChange.CidDefend);
        return (null, OrderChange.CidNone);
    }
}

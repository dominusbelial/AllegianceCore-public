using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Harness stealth policy, using native equipment gates. Energy reserve and
/// decloaking before fire/boost are pilot choices, not additional IGC rules.</summary>
internal static class CloakPolicy
{
    internal static int Apply(StaticCore core, short hullId, IReadOnlyList<ExpandedPartData>? parts,
        float energyFraction, bool flying, bool ripcord, bool approaching, bool held, int state)
    {
        state &= ~ShipControlState.CloakActive;
        if (!approaching || !flying || ripcord || !float.IsFinite(energyFraction)
            || (state & (ShipControlState.AllWeapons | ShipControlState.OneWeapon
                | ShipControlState.MissileFire | ShipControlState.MineFire | ShipControlState.AfterburnerButton)) != 0
            || !core.HullTypes.TryGetValue(hullId, out var hull)
            || Showcase.ClassifyHull(hull) is not (Showcase.HullRole.Scout or Showcase.HullRole.Bomber)) return state;
        // Igc/hullTypeIGC.h:163-213; cloakigc.cpp:62,80-81. Cargo isn't an emitter.
        var mounted = parts?.FirstOrDefault(p => p.MountId == 0 && p.Amount > 0
            && p.FractionMounted >= ExpandedPartData.FullyMounted
            && core.PartTypes.TryGetValue(p.PartId, out var type)
            && type.Type is DataPartTypeIGC.Cloak && hull.CanMountPart(type, 0));
        if (mounted is not { Amount: > 0 } part
            || core.PartTypes[part.PartId].Type is not DataPartTypeIGC.Cloak cloak || cloak.MaxCloaking <= 0) return state;
        // Native cloakigc.cpp:89-103 throttles strength when energy runs short.
        // Hysteresis bounds cloak use and avoids toggling on wire quantization.
        float reserve = held ? Math.Max(hull.MaxEnergy * .05f, cloak.EnergyConsumption)
            : Math.Max(hull.MaxEnergy * .2f, cloak.EnergyConsumption * 4);
        return energyFraction * hull.MaxEnergy > reserve ? state | ShipControlState.CloakActive : state;
    }
}

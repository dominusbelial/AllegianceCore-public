using Allegiance.Core.Wire;

namespace Allegiance.Core;

public enum FlightPartAction { Mount, Unmount, Replace, Drop }

/// <summary>P641. Native WinTrek.cpp:9090-9138 selects compatible cargo for a
/// hardpoint; hullTypeIGC.cpp CanMount enforces the individual socket mask.
/// The generic TK part commands are declared but unhandled in WinTrek. This
/// planner gives them an inventory selection over native SWAP_PART/DROP_PART.</summary>
public static class InFlightPartRules
{
    public const int CargoCapacity = 5; // Igc/igc.h c_maxCargo

    public static bool CanMount(DataHullTypeIGC hull, DataPartTypeIGC part, int mount)
    {
        if (mount < 0) return mount >= -CargoCapacity;
        if (part.EquipmentType == EquipmentType.Weapon)
            return mount < hull.MaxWeapons && mount < hull.Hardpoints.Length
                && (part.PartMask & ~hull.Hardpoints[mount].PartMask) == 0;
        return mount == 0 && hull.CanMountPart(part);
    }

    public static SwapPart? Plan(FlightPartAction action, ExpandedPartData selected,
        IReadOnlyList<ExpandedPartData> inventory, DataHullTypeIGC hull,
        Func<short, DataPartTypeIGC?> resolve, int preferredWeapon = 0)
    {
        if (!inventory.Contains(selected) || resolve(selected.PartId) is not { } part) return null;
        int old = unchecked((sbyte)selected.MountId);
        bool Occupied(int mount) => inventory.Any(p => unchecked((sbyte)p.MountId) == mount
            && (mount < 0 || resolve(p.PartId)?.EquipmentType == part.EquipmentType));
        int destination = int.MinValue;
        if (action == FlightPartAction.Unmount && old >= 0)
            destination = Enumerable.Range(1, CargoCapacity).Select(i => -i)
                .FirstOrDefault(m => !Occupied(m), int.MinValue);
        if (old < 0 && action is FlightPartAction.Mount or FlightPartAction.Replace)
        {
            var mounts = Enumerable.Range(0, part.EquipmentType == EquipmentType.Weapon ? hull.MaxWeapons : 1)
                .OrderBy(m => m == preferredWeapon ? 0 : 1);
            destination = mounts.FirstOrDefault(m => CanMount(hull, part, m)
                && Occupied(m) == (action == FlightPartAction.Replace), int.MinValue);
        }
        return destination == int.MinValue ? null : new SwapPart
        { EtOld = part.EquipmentType, MountOld = (sbyte)old, MountNew = (sbyte)destination };
    }
}

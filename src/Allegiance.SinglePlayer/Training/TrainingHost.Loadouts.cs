using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

public sealed partial class TrainingHost
{
    private readonly record struct CachedTrainingPart(DataPartTypeIGC Type, sbyte Mount);
    private readonly Dictionary<short, CachedTrainingPart[]> _savedTrainingLoadouts = new();

    private static IEnumerable<(MountedPart Part, sbyte Mount)> TrainingInventory(Ship ship)
    {
        foreach (var part in ship.Parts) yield return (part, unchecked((sbyte)part.MountId));
        // CargoHold stores unmounted records with MountId=0; its list index,
        // rather than that sentinel, carries the signed native cargo mount.
        for (int i = 0; i < ship.CargoHold.Count; i++)
            if (ship.CargoHold[i] is { } part) yield return (part, (sbyte)(-i - 1));
    }

    // Native cache is client/mission-wide and keyed by BASE hull, not drone id
    // or tech level. Replacing it is observable when another same-hull ship
    // later lands on a carrier (clintlib/clintlib.h:1437-1477,1640-1724).
    private void SaveTrainingLoadout(Ship ship)
        => _savedTrainingLoadouts[ship.HullType.HullId] = TrainingInventory(ship)
            .Select(p => new CachedTrainingPart(p.Part.Type, p.Mount))
            .Reverse().OrderBy(p => p.Mount).ToArray(); // native inserts before an equal mount

    private void RestoreTrainingLoadoutOnCarrier(Ship ship)
    {
        if (_savedTrainingLoadouts.TryGetValue(ship.HullType.HullId, out var cached))
        {
            var duplicated = new bool[cached.Length];
            // Preserve every present part, refill it and match the same mount
            // first; a matching part moved elsewhere consumes one cache entry.
            foreach (var (part, mount) in TrainingInventory(ship).ToArray())
            {
                if (mount < 0) ship.SetCargoPartAmount(Ship.CargoSlotForMount(mount), short.MaxValue);
                else ship.SetPartAmount(part, short.MaxValue);
                int match = -1;
                for (int i = cached.Length - 1; i >= 0; i--)
                {
                    if (duplicated[i] || cached[i].Type.PartId != part.PartId) continue;
                    match = i;
                    if (cached[i].Mount == mount) break;
                }
                if (match >= 0) duplicated[match] = true;
            }
            int cargo = -Ship.CargoCapacity;
            for (int i = 0; i < cached.Length; i++)
            {
                if (duplicated[i]) continue;
                var part = cached[i];
                if (part.Mount >= 0 && !ship.Parts.Any(p => p.EquipmentType == part.Type.EquipmentType && p.MountId == part.Mount)
                    && ship.HullType.CanMountPart(part.Type, part.Mount))
                    ship.MountPart(part.Type, short.MaxValue, (byte)part.Mount, ExpandedPartData.FullyMounted);
                else
                {
                    while (cargo < 0 && ship.CargoHold[Ship.CargoSlotForMount(cargo)] is not null) cargo++;
                    if (cargo < 0)
                        ship.MountPart(part.Type, short.MaxValue, unchecked((byte)cargo++), ExpandedPartData.FullyMounted);
                }
            }
        }
        else
            // No saved hull: native falls back to the side's station/default
            // loadout (clintlib/clintlib.h:1726-1744).
            EquipDefaultTrainingLoadout(ship);

        // Local training bypasses the multiplayer successor/desired-loadout
        // restock. WinTrek/trekigc.cpp:4176-4189.
        ship.Ammo = ship.HullType.MaxAmmo;
        ship.SetFuel(float.MaxValue);
        ship.Energy = ship.EffectiveMaxEnergy();
        ship.SetFraction(1);
        ship.SetShieldFraction(1);
    }
}

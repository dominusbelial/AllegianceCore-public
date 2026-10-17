using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>Installed before networking by the host, including launcher parts.</summary>
    public Func<short, DataPartTypeIGC?> PartTypeResolver { get; set; } = _ => null;

    // Native appmsg.cpp:1411-1456 skips own echoes after optimistic mutation.
    // We apply guaranteed echoes for ALL ships, including self, exactly once.
    // This avoids a second swap undoing a predicted first swap.
    private void Apply(SwapPart message)
    {
        if (!Ships.TryGetValue(message.ShipID, out var ship)) return;
        var inventory = (ship.Loadout ?? []).Concat(ship.Cargo ?? []).ToList();
        int Find(int mount) => inventory.FindIndex(p => unchecked((sbyte)p.MountId) == mount
            && (mount < 0 || PartTypeResolver(p.PartId)?.EquipmentType == message.EtOld));
        int source = Find(message.MountOld), target = Find(message.MountNew);
        if (source < 0 || message.MountOld < -5 || message.MountNew < -5) return;
        if (source == target) return;
        inventory[source] = Move(inventory[source], message.MountNew);
        if (target >= 0) inventory[target] = Move(inventory[target], message.MountOld);
        StoreInventory(ship, inventory);
    }

    // Native appmsg.cpp:1468-1566 applies entries in wire order, consuming
    // packs, moving orphan launchers, or topping up a matching mounted launcher.
    // Pose fractions remain authoritative in subsequent server ship updates.
    private void ApplyReloadInventory(ShipEntity ship, Reload reload)
    {
        var inventory = (ship.Loadout ?? []).Concat(ship.Cargo ?? []).ToList();
        foreach (var entry in reload.Entries)
        {
            if (entry.Mount is < -5 or >= 0) continue;
            int source = inventory.FindIndex(p => unchecked((sbyte)p.MountId) == entry.Mount);
            if (source < 0 || PartTypeResolver(inventory[source].PartId) is not { } type) continue;
            var cargo = inventory[source];
            int transfer = entry.AmountTransfered;
            bool pack = type.Type is DataPartTypeIGC.Pack;
            int target = inventory.FindIndex(p => p.MountId == 0 && PartTypeResolver(p.PartId)?.EquipmentType == type.EquipmentType);
            if (!pack && type.EquipmentType is not (EquipmentType.Magazine or EquipmentType.ChaffLauncher or EquipmentType.Dispenser)) continue;
            if (transfer == -1 && !pack)
            {
                if (target >= 0 && inventory[target].Amount != 0) continue;
                inventory[source] = Move(cargo, 0);
                if (target >= 0) inventory.RemoveAt(target);
                continue;
            }
            if (transfer != -1 && (transfer <= 0 || transfer > cargo.Amount || pack && transfer == cargo.Amount)) continue;
            if (!pack)
            {
                if (target < 0 || inventory[target].PartId != cargo.PartId) continue;
                inventory[target] = inventory[target] with
                { Amount = (short)Math.Min(short.MaxValue, inventory[target].Amount + transfer), FractionMounted = 0 };
            }
            else
            {
                bool fuel = ((DataPartTypeIGC.Pack)type.Type).PackType == 1;
                for (int i = 0; i < inventory.Count; i++)
                {
                    if (inventory[i].MountId >= 128 || PartTypeResolver(inventory[i].PartId) is not { } equipped) continue;
                    if (fuel ? equipped.EquipmentType == EquipmentType.Afterburner
                        : equipped.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 })
                        inventory[i] = inventory[i] with { FractionMounted = 0 };
                }
            }
            if (transfer == -1 || transfer == cargo.Amount) inventory.RemoveAt(source);
            else inventory[source] = cargo with { Amount = (short)(cargo.Amount - transfer) };
        }
        StoreInventory(ship, inventory);
    }

    private void Apply(DropPart message)
    {
        if (!Ships.TryGetValue(message.ShipID, out var ship)) return;
        int mount = unchecked((sbyte)message.Mount);
        var inventory = (ship.Loadout ?? []).Concat(ship.Cargo ?? []).ToList();
        inventory.RemoveAll(p => p.MountId == message.Mount && (mount < 0
            || PartTypeResolver(p.PartId)?.EquipmentType == message.Et));
        StoreInventory(ship, inventory);
    }

    private static ExpandedPartData Move(ExpandedPartData part, int mount) =>
        new(part.PartId, part.Amount, unchecked((byte)mount), 0); // shipIGC.cpp:1567

    private static void StoreInventory(ShipEntity ship, List<ExpandedPartData> inventory)
    {
        ship.Loadout = inventory.Where(p => p.MountId < 128).ToList();
        ship.Cargo = inventory.Where(p => p.MountId >= 128).ToList();
    }
}

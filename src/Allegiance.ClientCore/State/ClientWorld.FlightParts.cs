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

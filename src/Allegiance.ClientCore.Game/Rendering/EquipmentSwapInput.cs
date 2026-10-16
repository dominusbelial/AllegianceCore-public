using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Native WinTrek.cpp:9075-9150, trekigc.cpp:2603-2719.
/// Selection includes the first empty slot and groups consumables by part type.</summary>
public sealed class EquipmentSwapInput
{
    public static readonly string[] Actions = ["SwapWeapon1", "SwapWeapon2", "SwapWeapon3", "SwapWeapon4",
        "SwapTurret1", "SwapTurret2", "SwapTurret3", "SwapTurret4", "SwapMissile", "SwapChaff",
        "SwapShield", "SwapCloak", "SwapAfterburner", "SwapMine"];
    private readonly HashSet<string> _held = [];

    public void Prime(KeyboardState keys, ActionMap map)
    {
        _held.Clear();
        foreach (string action in Actions) if (map.IsDownMostSpecific(keys, action)) _held.Add(action);
    }

    public string? Step(KeyboardState keys, ActionMap map, bool suppressed)
    {
        string? pressed = null;
        foreach (string action in Actions)
        {
            bool down = map.IsDownMostSpecific(keys, action);
            bool edge = down && _held.Add(action);
            if (!down) _held.Remove(action);
            if (edge && !suppressed) pressed ??= action;
        }
        return pressed;
    }

    public readonly record struct Decision(SwapPart Request, int SelectedCargoMount, IReadOnlyList<ReloadData> Reloads);

    public static Decision? Plan(string action, DataHullTypeIGC hull,
        IReadOnlyList<ExpandedPartData> inventory, Func<short, DataPartTypeIGC?> resolve, int selectedCargo,
        float fuel = float.MaxValue, Func<DataPartTypeIGC, int>? launcherCapacity = null)
    {
        int index = Array.IndexOf(Actions, action);
        if (index < 0) return null;
        int mount = 0;
        EquipmentType kind;
        if (index < 8)
        {
            kind = EquipmentType.Weapon;
            mount = index < 4 ? index : hull.MaxFixedWeapons + index - 4;
            if (index < 4 ? mount >= hull.MaxFixedWeapons : mount >= hull.MaxWeapons) return null;
        }
        else kind = index switch { 8 => EquipmentType.Magazine, 9 => EquipmentType.ChaffLauncher,
            10 => EquipmentType.Shield, 11 => EquipmentType.Cloak, 12 => EquipmentType.Afterburner,
            _ => EquipmentType.Dispenser };

        var parts = inventory.ToList();
        ExpandedPartData? At(int m) => parts.Where(p => unchecked((sbyte)p.MountId) == m
            && (m < 0 || resolve(p.PartId)?.EquipmentType == kind)).Cast<ExpandedPartData?>().FirstOrDefault();
        // NextCargoPart groups packs/magazines/dispensers and empty slots only.
        List<int> Slots()
        {
            var slots = new List<int>();
            var seen = new HashSet<short?>();
            for (int m = -1; m >= -5; m--)
            {
                var entry = At(m);
                var type = entry is { } e ? resolve(e.PartId) : null;
                bool grouped = entry is null || type?.EquipmentType is EquipmentType.Pack or EquipmentType.Magazine or EquipmentType.Dispenser;
                if (!grouped || seen.Add(entry?.PartId)) slots.Add(m);
            }
            return slots;
        }
        int Normalize(int current, List<int> slots) => slots.FirstOrDefault(m => m <= current, slots[0]);
        int Next(int current, List<int> slots) => slots.FirstOrDefault(m => m < current, slots[0]);
        bool Select(ref int current, short? excluded)
        {
            var slots = Slots();
            current = Normalize(current, slots);
            int start = current;
            do
            {
                var entry = At(current);
                if (entry is null ? excluded is not null : entry.Value.PartId != excluded
                    && resolve(entry.Value.PartId) is { } part && part.EquipmentType == kind
                    && InFlightPartRules.CanMount(hull, part, mount)) return true;
                current = Next(current, slots);
            } while (current != start);
            return false;
        }
        int selection = selectedCargo is >= -5 and <= -1 ? selectedCargo : -1;
        var old = At(mount);
        if (!Select(ref selection, old?.PartId)) return null;
        var incoming = At(selection);
        if (old is null && incoming is null) return null;
        var request = new SwapPart { EtOld = kind,
            MountOld = (sbyte)(old is not null ? mount : selection),
            MountNew = (sbyte)(old is not null ? selection : mount) };
        // Native predicts locally before selecting the NEXT candidate. The port
        // applies its own guaranteed echo once (ClientWorld.FlightParts.cs).
        // Use a temporary inventory for selection; never predict into ClientWorld.
        parts = parts.Select(p => p.Equals(old) ? p with { MountId = unchecked((byte)selection) }
            : p.Equals(incoming) ? p with { MountId = (byte)mount } : p).ToList();
        // clintlib.h:2401-2406 reloads only when the source was cargo and
        // destination equipment. Replacements send equipment -> cargo instead.
        var reloads = new List<ReloadData>();
        if (request.MountNew >= 0 && incoming is { } loaded)
        {
            if (kind == EquipmentType.Afterburner)
            {
                int max = (int)hull.MaxFuel;
                int amount = (int)Math.Clamp(fuel, 0, max);
                foreach (var p in parts.Where(p => unchecked((sbyte)p.MountId) < 0).OrderBy(p => unchecked((sbyte)p.MountId)))
                {
                    if (amount >= max) break;
                    if (resolve(p.PartId)?.Type is not DataPartTypeIGC.Pack { PackType: 1 }) continue;
                    int transfer = Math.Min(p.Amount, max - amount);
                    reloads.Add(new((short)(transfer == p.Amount ? -1 : transfer), unchecked((sbyte)p.MountId)));
                    amount += transfer;
                }
            }
            else if (kind is EquipmentType.Magazine or EquipmentType.ChaffLauncher or EquipmentType.Dispenser)
            {
                int amount = loaded.Amount;
                int max = launcherCapacity?.Invoke(resolve(loaded.PartId)!.Value) ?? (kind switch
                { EquipmentType.Magazine => hull.CapacityMagazine, EquipmentType.ChaffLauncher => hull.CapacityChaffLauncher,
                    _ => hull.CapacityDispenser });
                foreach (var p in parts.Where(p => unchecked((sbyte)p.MountId) < 0 && p.PartId == loaded.PartId)
                    .OrderBy(p => unchecked((sbyte)p.MountId)))
                {
                    if (amount >= max) break;
                    int transfer = Math.Min(p.Amount, max - amount);
                    reloads.Add(new((short)transfer, unchecked((sbyte)p.MountId)));
                    amount += transfer;
                }
            }
        }
        // Selection follows reload's local cargo consumption, as in native.
        foreach (var reload in reloads)
        {
            int i = parts.FindIndex(p => unchecked((sbyte)p.MountId) == reload.Mount);
            if (i < 0) continue;
            int remaining = reload.AmountTransfered < 0 ? 0 : parts[i].Amount - reload.AmountTransfered;
            if (remaining <= 0) parts.RemoveAt(i);
            else parts[i] = parts[i] with { Amount = (short)remaining };
        }
        selection = Next(selection, Slots());
        Select(ref selection, incoming?.PartId);
        return new(request, selection, reloads);
    }
}

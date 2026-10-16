using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame.Flight;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly EquipmentSwapInput _equipmentSwapInput = new();
    private readonly HashSet<string> _flightPartHeld = new();
    private ExpandedPartData? _inventorySelection;
    private bool _boosterToggled;
    private static readonly string[] FlightPartActions =
        ["ToggleWeapon1", "ToggleWeapon2", "ToggleWeapon3", "ToggleWeapon4", "ToggleBooster",
         "MountPart", "UnmountPart", "ReplacePart", "DropPart"];

    private DataPartTypeIGC? ResolveFlightPart(short id)
    {
        if (_shopCatalog is not { } catalog) return null;
        foreach (var part in catalog.Parts) if (part.PartId == id) return part;
        if (catalog.Launchers is { } launchers)
            foreach (var launcher in launchers) if (launcher.Part.PartId == id) return launcher.Part;
        return null;
    }

    private List<ExpandedPartData> OwnInventory()
    {
        lock (_session.Gate)
            return _session.World.MyShipId is { } id && _session.World.Ships.TryGetValue(id, out var ship)
                ? (ship.Loadout ?? []).Concat(ship.Cargo ?? []).ToList() : [];
    }

    private void CycleInventory(bool next)
    {
        var inventory = OwnInventory();
        if (inventory.Count == 0) { _inventorySelection = null; return; }
        int index = _inventorySelection is { } selected ? inventory.IndexOf(selected) : -1;
        _inventorySelection = inventory[index < 0 ? (next ? 0 : inventory.Count - 1)
            : (index + (next ? 1 : inventory.Count - 1)) % inventory.Count];
    }

    private bool CanChangeFlightParts()
    {
        lock (_session.Gate)
            return _state == ClientState.Flying && _session.World.MyShipId is { } id
                && _session.World.Ships.TryGetValue(id, out var ship)
                && ship.ParentId < 0 && ship.StationId < 0 && !ship.IsDead;
    }

    private void ToggleFixedWeapon(int mount)
    {
        if (!CanChangeFlightParts()) return;
        bool occupied = OwnInventory().Any(p => p.MountId == mount
            && ResolveFlightPart(p.PartId)?.EquipmentType == EquipmentType.Weapon);
        (_groupFire, _selectedMount) = FlightInput.ToggleWeapon(
            _groupFire, _selectedMount, mount, FixedMountCount(), occupied);
        Console.WriteLine($"[p641] weapon {mount + 1}: {(_groupFire ? "GROUP" : "SINGLE")}, selected={_selectedMount + 1}");
    }

    private void ChangeFlightPart(FlightPartAction action)
    {
        if (!CanChangeFlightParts()) return;
        var inventory = OwnInventory();
        if (_inventorySelection is not { } selected || !inventory.Contains(selected))
        {
            lock (_session.Gate) _session.World.PostSystemLine("Select an inventory part first (F6: NextPart / PrevPart).");
            return;
        }
        if (action == FlightPartAction.Drop)
        {
            if (ResolveFlightPart(selected.PartId) is not { } part) return;
            _session.SendDropPart(part.EquipmentType, selected.MountId);
        }
        else
        {
            short hullId;
            lock (_session.Gate) hullId = _session.World.Ships[_session.World.MyShipId!.Value].HullId;
            var hull = _shopCatalog?.Hulls.FirstOrDefault(h => h.HullId == hullId);
            if (hull is not { } h || h.HullId != hullId) return;
            var swap = InFlightPartRules.Plan(action, selected, inventory, h, ResolveFlightPart, _selectedMount);
            if (swap is null)
            {
                lock (_session.Gate) _session.World.PostSystemLine("No compatible slot: mount needs empty equipment, replace needs occupied equipment, unmount needs free cargo.");
                return;
            }
            _session.SendSwapPart(swap.EtOld, swap.MountOld, swap.MountNew);
        }
        Console.WriteLine($"[p641] {action} requested part={selected.PartId} mount={unchecked((sbyte)selected.MountId)}");
    }

    private void SwapFlightEquipment(string action)
    {
        if (!CanChangeFlightParts()) return;
        short hullId;
        float fuelFraction;
        lock (_session.Gate)
        {
            var ship = _session.World.Ships[_session.World.MyShipId!.Value];
            hullId = ship.HullId;
            fuelFraction = ship.Latest?.FuelFraction ?? 1;
        }
        var hull = _shopCatalog?.Hulls.Where(h => h.HullId == hullId).Cast<DataHullTypeIGC?>().FirstOrDefault();
        if (hull is not { } h) return;
        var decision = EquipmentSwapInput.Plan(action, h, OwnInventory(), ResolveFlightPart, _selectedCargoMount,
            fuelFraction * h.MaxFuel,
            part => Allegiance.Core.Sim.Ship.ClampPartAmount(h, _shopCatalog?.Core, part, short.MaxValue));
        if (decision is not { } swap)
        {
            lock (_session.Gate) _session.World.PostSystemLine("No different compatible cargo part or empty cargo slot.");
            return;
        }
        _session.SendSwapPart(swap.Request.EtOld, swap.Request.MountOld, swap.Request.MountNew);
        if (swap.Reloads.Count > 0) _session.SendReload(swap.Reloads);
        _selectedCargoMount = swap.SelectedCargoMount;
    }

    private void UpdateFlightParts(KeyboardState keys, bool composing)
    {
        var swapAction = _equipmentSwapInput.Step(keys, KeyBindingStore.Instance.Map, composing);
        if (swapAction is not null) SwapFlightEquipment(swapAction);
        foreach (string action in FlightPartActions)
        {
            bool down = ActionDown(keys, action);
            bool edge = down && _flightPartHeld.Add(action);
            if (!down) _flightPartHeld.Remove(action);
            if (!edge || composing) continue;
            if (action.StartsWith("ToggleWeapon", StringComparison.Ordinal)) ToggleFixedWeapon(action[^1] - '1');
            else if (action == "ToggleBooster") _boosterToggled = !_boosterToggled;
            else ChangeFlightPart(action switch
            {
                "MountPart" => FlightPartAction.Mount, "UnmountPart" => FlightPartAction.Unmount,
                "ReplacePart" => FlightPartAction.Replace, _ => FlightPartAction.Drop,
            });
        }
    }

    private void PrimeFlightParts(KeyboardState keys)
    {
        _equipmentSwapInput.Prime(keys, KeyBindingStore.Instance.Map);
        _flightPartHeld.Clear();
        foreach (var action in FlightPartActions) if (ActionDown(keys, action)) _flightPartHeld.Add(action);
        _boosterToggled = false;
    }

    private void DrawFlightPartsHud()
    {
        if (_directorActive || _state != ClientState.Flying) return;
        var inventory = OwnInventory();
        string text = $"WEAPONS: {(_groupFire ? "GROUP" : $"SINGLE {_selectedMount + 1}")}  BOOST: {(_boosterToggled ? "ON" : "OFF")}";
        foreach (var p in inventory.Where(p => p.MountId < FixedMountCount()
            && ResolveFlightPart(p.PartId)?.EquipmentType == EquipmentType.Weapon))
            text += $"  [{p.MountId + 1}:{ResolveFlightPart(p.PartId)?.Name ?? $"#{p.PartId}"}]";
        if (_inventorySelection is { } selected && inventory.Contains(selected))
            text += $"  PART: {ResolveFlightPart(selected.PartId)?.Name ?? $"#{selected.PartId}"} @{unchecked((sbyte)selected.MountId)} x{selected.Amount}";
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try { _spriteBatch.DrawString(_hudSmall, text, new Vector2(20, GraphicsDevice.Viewport.Height - 158), Color.LightCyan); }
        finally { _spriteBatch.End(); }
    }
}

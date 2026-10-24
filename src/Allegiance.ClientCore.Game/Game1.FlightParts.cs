using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Allegiance.Core.Model;
using Allegiance.ClientCore.State;
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

    private Rendering.ShopCatalog? _partCatalogCacheSource;
    private Dictionary<short, DataPartTypeIGC>? _partById;

    /// <summary>Resolve a part type id against the loaded shop catalogue. HUD-4b
    /// caches the Parts + launcher Parts into <see cref="_partById"/> once per
    /// catalogue (rebuilt only when <see cref="_shopCatalog"/> changes), so the
    /// radar treasure loop and the loadout scans do not rescan the whole
    /// catalogue per part per frame.</summary>
    private DataPartTypeIGC? ResolveFlightPart(short id)
    {
        if (_shopCatalog is not { } catalog) return null;
        _partById ??= new Dictionary<short, DataPartTypeIGC>();
        if (!ReferenceEquals(_partCatalogCacheSource, catalog))
        {
            _partById.Clear();
            foreach (var part in catalog.Parts) _partById[part.PartId] = part;
            if (catalog.Launchers is { } launchers)
                foreach (var launcher in launchers) _partById[launcher.Part.PartId] = launcher.Part;
            _partCatalogCacheSource = catalog;
        }
        return _partById.TryGetValue(id, out var partType) ? partType : null;
    }

    private List<ExpandedPartData> OwnInventory()
    {
        lock (_session.Gate)
            return _session.World.MyShipId is { } id && _session.World.Ships.TryGetValue(id, out var ship)
                ? (ship.Loadout ?? []).Concat(ship.Cargo ?? []).ToList() : [];
    }

    /// <summary>P786 — the own launcher arming clocks (native
    /// GetArmedFraction, consoledata.cpp:1189-1202), replayed each frame from
    /// the wire events the native client's own magazine/dispenser sim would
    /// act on — see <see cref="Rendering.OwnLauncherArming"/>.</summary>
    private readonly OwnLauncherArming _launcherArming = new();

    // Cached delegates — the frame path must not allocate (method groups and
    // closures over `this` would otherwise be rebuilt per frame).
    private Func<ExpandedPartData, (EquipmentType Type, double LoadTime)?>? _launcherResolve;
    private Func<EquipmentType, float, float>? _armedFractionQuery;

    /// <summary>The expendable load time of a mounted launcher part in seconds
    /// (magazine → its missile type's GetLoadTime, magazineigc.h:159-165;
    /// dispenser → its mine/probe type, dispenserigc.h:165-175 with the
    /// Ship.DispenserLoadTime subtype mapping). Null for non-launchers.
    /// Allocation-free.</summary>
    private (EquipmentType Type, double LoadTime)? LauncherLoadTime(DataPartTypeIGC part)
    {
        double? loadTime = part.Type switch
        {
            DataPartTypeIGC.MissileLauncher ml
                when _shopCatalog?.MissileTypes is { } mts && mts.TryGetValue(ml.ExpendableTypeId, out var mt) => mt.LoadTime,
            DataPartTypeIGC.ProbeLauncher pl
                when _shopCatalog?.ProbeTypes is { } pts && pts.TryGetValue(pl.ExpendableTypeId, out var pt) => pt.LoadTime,
            DataPartTypeIGC.MineLauncher ml2
                when _shopCatalog?.MineTypes is { } mts2 && mts2.TryGetValue(ml2.ExpendableTypeId, out var mt2) => mt2.LoadTime,
            _ => null,
        };
        return loadTime is { } lt ? (part.EquipmentType, lt) : null;
    }

    /// <summary>Replay the arming clocks for one frame from the live world
    /// (own missile-fire salvo count, own dispenser deploys, loadout mount
    /// transitions). Runs under the session gate; allocation-free.</summary>
    private void UpdateLauncherArming(ShipEntity entity)
    {
        var world = _session.World;
        if (entity.Loadout is not { Count: > 0 } loadout)
        {
            _launcherArming.Update(world.OwnMissilesFired, world.OwnDispenserDeployed,
                Array.Empty<ExpandedPartData>(), static _ => default((EquipmentType, double)?), 1.0,
                Environment.TickCount64 * 0.001);
            return;
        }
        // Native c_fcidMountRate = FloatConstants[10] (igc.h:69), default 1.0 —
        // the same constant and fallback Sim.Ship.MountRate uses.
        double mountRate = _shopCatalog?.Core?.Constants.FloatConstants is { Length: > 10 } fc && fc[10] > 0f
            ? fc[10] : 1.0;
        if (_launcherResolve is null)
            _launcherResolve = e => ResolveFlightPart(e.PartId) is { } part ? LauncherLoadTime(part) : null;
        _launcherArming.Update(world.OwnMissilesFired, world.OwnDispenserDeployed,
            loadout, _launcherResolve, mountRate, Environment.TickCount64 * 0.001);
    }

    /// <summary>HUD-7 — resolve the native flight equipment/inventory pane for
    /// the local source ship (WinTrek/inventoryimage.cpp:299-346), driven by the
    /// granted loadout + cargo, the core catalogue, the own hull, the side's
    /// global attributes and the live resource fractions. Null when the hull or
    /// catalogue is unavailable (the renderers then draw nothing).</summary>
    private HudInventoryPane? BuildInventoryPane(ShipEntity entity, ServerShipUpdate up)
    {
        var catalog = _shopCatalog;
        if (catalog is null) return null;
        var hull = catalog.Hulls.FirstOrDefault(h => h.HullId == entity.HullId);
        if (hull.HullId != entity.HullId) return null;
        // Native Rebuild resolves the source ship (inventoryimage.cpp:289); the
        // stats hull is GetSourceShip()->GetHullType() (consoledata.cpp:855,976)
        // — the carrier when the local pilot is a turret gunner (WinTrek.cpp:10215).
        DataHullTypeIGC? sourceHull = hull;
        if (entity.ParentId >= 0 && _session.World.Ships.TryGetValue(entity.ParentId, out var carrier))
        {
            var carrierHull = catalog.Hulls.FirstOrDefault(h => h.HullId == carrier.HullId);
            if (carrierHull.HullId == carrier.HullId) sourceHull = carrierHull;
        }
        var inventory = OwnInventory();
        var loadout = inventory.Where(e => !CargoSelector.IsCargoMount(e.MountId)).ToList();
        var cargo = inventory.Where(e => CargoSelector.IsCargoMount(e.MountId)).ToList();
        var attributes = _session.World.MySideId is { } sideId
            ? _session.World.GlobalAttributesFor(sideId) : GlobalAttributeSet.Identity;
        // HUD-20 — native GetCloaking(Me) = 100·(1−GetCloaking()) for the own or
        // parent ship only (consoledata.cpp:480-490), from the owner-only
        // OwnShipCloakUpdate(220) the HUD-12 path already reads.
        float? cloakStrength = _session.World.MyCloaking is { } cloaking
            ? Allegiance.Core.Wire.OwnShipCloakUpdate.StrengthPercent(cloaking)
            : null;
        // HUD-20 — native ProcessObservers counts the source ship's children with
        // GetTurretID() == NA (inventoryimage.cpp:141-169); the source ship is the
        // own ship, or the carrier when the local pilot is boarded
        // (GetSourceShip(), inventoryimage.cpp:290).
        int observers = 0;
        short sourceId = entity.ParentId >= 0 ? entity.ParentId : entity.ShipId;
        foreach (var other in _session.World.Ships.Values)
            if (other.ParentId == sourceId && other.BoardTurretId == ShipEntity.BoardTurretObserver)
                observers++;
        // P786 — the native GetArmedFraction replay for the arming bars.
        if (_armedFractionQuery is null) _armedFractionQuery = _launcherArming.ArmedFraction;
        return HudInventoryPaneBuilder.Build(
            catalog, hull, loadout, cargo, attributes,
            up.FuelFraction, up.EnergyFraction, up.ShieldFraction, up.AmmoFraction,
            _groupFire, _selectedMount, _selectedCargoMount,
            hull.Name, isPilot: !IsTurretGunner(), sourceHull: sourceHull,
            cloakStrengthPercent: cloakStrength, observerCount: observers,
            armedFraction: _armedFractionQuery);
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
        // P719 — SIM-0280/0281: native plays ungroupWeaponsSound when the pilot
        // switches to single-fire (WinTrek.cpp:9047) and groupWeaponsSound when
        // it returns to group fire (:9053).
        _sound?.PlayGroupFire(_groupFire);
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
            // P719 — SIM-0291: native plays dropSound on dropping a part
            // (WinTrek.cpp:9719).
            _sound?.PlayDropPart();
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
                // P719 — SIM-0284: native plays salCargoFullSound when a mount
                // is refused because cargo is full (WinTrek.cpp:9114). The
                // "no compatible slot" refusal maps to the same user-facing
                // condition (the planned swap had no room).
                _sound?.PlayCargoFull();
                lock (_session.Gate) _session.World.PostSystemLine("No compatible slot: mount needs empty equipment, replace needs occupied equipment, unmount needs free cargo.");
                return;
            }
            // P719 — SIM-0282/0283: native plays mountSound on a successful
            // mount and unmountSound on a successful unmount (WinTrek.cpp:9098,
            // 9102). Played optimistically on the accepted in-flight command.
            _sound?.PlayWarnPartSound(action);
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

    private string? BuildFlightPartsHudText()
    {
        if (LocalSpectating) return null;
        if (_directorActive || _state != ClientState.Flying) return null;
        var inventory = OwnInventory();
        string text = $"WEAPONS: {(_groupFire ? "GROUP" : $"SINGLE {_selectedMount + 1}")}  BOOST: {(_boosterToggled ? "ON" : "OFF")}";
        foreach (var p in inventory.Where(p => p.MountId < FixedMountCount()
            && ResolveFlightPart(p.PartId)?.EquipmentType == EquipmentType.Weapon))
            text += $"  [{p.MountId + 1}:{ResolveFlightPart(p.PartId)?.Name ?? $"#{p.PartId}"}]";
        if (_inventorySelection is { } selected && inventory.Contains(selected))
            text += $"  PART: {ResolveFlightPart(selected.PartId)?.Name ?? $"#{selected.PartId}"} @{unchecked((sbyte)selected.MountId)} x{selected.Amount}";
        return text;
    }

    private void DrawFlightPartsHud(Rendering.HudModel model)
    {
        if (!model.Visible || model.Context.FlightPartsLine is not { } text) return;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try { _spriteBatch.DrawString(_hudSmall, text, new Vector2(20, GraphicsDevice.Viewport.Height - 158), Color.LightCyan); }
        finally { _spriteBatch.End(); }
    }
}

using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private int _browserCargoMount;
    private ExpandedPartData? _browserInventoryPart;
    private bool _browserBoosterToggle;
    private readonly FlightCameraZoom _browserFlightZoom = new();

    // Same wire verbs, guards and pure planners as desktop Game1.InFlightUtility
    // and Game1.FlightParts. Native citations live on CargoSelector,
    // InFlightPartRules, FlightInput.ToggleWeapon and ConnectionSession.SendX.
    private void ApplyBrowserKeyboardUtility(KeyboardState keys, ShipEntity own)
    {
        bool Edge(string action) => ActionDown(keys, action) && !ActionDown(_lastFlightKeys, action);
        var world = _session.World;
        if (ActionDown(keys, "ZoomIn") || ActionDown(keys, "ZoomOut"))
        {
            _browserFlightZoom.Step((float)_flightElapsedSeconds, ActionDown(keys, "ZoomIn"), ActionDown(keys, "ZoomOut"), false);
            _browserChase.DistanceOverride = _browserFlightZoom.ExternalDistance;
        }
        if (Edge("InvertMouse")) SetInvertMouseY(!_mouseFlight.InvertPitch);
        if (Edge("MouseSensitivityUp")) StepMouseSensitivity(1);
        if (Edge("MouseSensitivityDown")) StepMouseSensitivity(-1);
        if (Edge("MouseDeadzone"))
        {
            var presets = MouseFlightController.DeadzonePresets;
            int i = Array.FindIndex(presets, d => MathF.Abs(d - _mouseFlight.Deadzone) < .0005f);
            _mouseFlight.Deadzone = presets[(i + 1) % presets.Length];
        }
        if (Edge("MouseResponseCurve")) _mouseFlight.LinearControls = !_mouseFlight.LinearControls;
        if (Edge("OccupyNextTurret") && own.ParentId >= 0) _session.SendBoardShip(own.ParentId);
        if (Edge("VoteYes") || Edge("VoteNo"))
        {
            if (world.ActiveBallot is { } vote) _session.SendVote(vote.BallotId, Edge("VoteYes"));
            else world.PostSystemLine("No active vote to cast.");
        }
        if (Edge("Suicide")) _session.SendSuicide();
        if (StarmapOpen && (Edge("SelectSectorNext") || Edge("SelectSectorPrev")))
        {
            var sectors = world.Clusters.Keys.OrderBy(c => c).ToArray();
            int i = Array.IndexOf(sectors, SelectedMapSector ?? own.SectorId);
            if (sectors.Length > 0) SelectMapSector(sectors[(i + (Edge("SelectSectorNext") ? 1 : sectors.Length - 1)) % sectors.Length]);
        }
        if (Edge("Ripcord") && SelectedMapSector is { } sector) _session.SendRipcordRequest(sector);
        if (own.ParentId >= 0 || own.IsDead || own.StationId >= 0) return;
        if (Edge("ToggleBooster")) _browserBoosterToggle = !_browserBoosterToggle;
        if (Edge("DropMine")) _session.SendFireExpendable(EquipmentType.Dispenser);
        if (Edge("DropChaff")) _session.SendFireExpendable(EquipmentType.ChaffLauncher);
        if (!InputActionCatalog.Definitions.Any(d => d.Category == "INVENTORY / CARGO" && Edge(d.Name))
            && !Enumerable.Range(1, 4).Any(m => Edge("ToggleWeapon" + m))) return;
        var cargo = own.Cargo ?? [];
        var inventory = (own.Loadout ?? []).Concat(cargo).ToList();
        if (Edge("NextCargo"))
        {
            var mounts = CargoSelector.OccupiedMounts(cargo);
            _browserCargoMount = mounts.Count == 0 ? 0 : CargoSelector.Next(_browserCargoMount, mounts);
        }
        if (Edge("NextPart") || Edge("PrevPart"))
        {
            int i = _browserInventoryPart is { } selected ? inventory.IndexOf(selected) : -1;
            bool next = Edge("NextPart");
            _browserInventoryPart = inventory.Count == 0 ? null : inventory[i < 0 ? next ? 0 : inventory.Count - 1
                : (i + (next ? 1 : inventory.Count - 1)) % inventory.Count];
        }
        if (Edge("DropCargo") && cargo.Count > 0)
        {
            if (!cargo.Any(c => unchecked((sbyte)c.MountId) == _browserCargoMount))
                _browserCargoMount = CargoSelector.FirstOccupied(CargoSelector.OccupiedMounts(cargo));
            _session.SendDropCargo((sbyte)_browserCargoMount);
        }
        if (Edge("Reload") && cargo.Count > 0)
            _session.SendReload(cargo.Where(c => CargoSelector.IsCargoMount(c.MountId))
                .Select(c => new ReloadData(-1, CargoSelector.SignedMount(c.MountId))).ToArray());
        if (_browserInventoryPart is { } part && inventory.Contains(part) && ResolveFlightPart(part.PartId) is { } type)
        {
            if (Edge("DropPart")) _session.SendDropPart(type.EquipmentType, part.MountId);
            foreach (var action in new[] { FlightPartAction.Mount, FlightPartAction.Unmount, FlightPartAction.Replace })
                if (Edge(action + "Part") && _shopCatalog?.Core?.HullTypes.TryGetValue(own.HullId, out var hull) == true
                    && InFlightPartRules.Plan(action, part, inventory, hull, ResolveFlightPart, _selectedMount) is { } swap)
                    _session.SendSwapPart(swap.EtOld, swap.MountOld, swap.MountNew);
        }
        for (int mount = 0; mount < 4; mount++)
            if (Edge("ToggleWeapon" + (mount + 1)))
                (_groupFire, _selectedMount) = FlightInput.ToggleWeapon(_groupFire, _selectedMount, mount, FixedMountCount(),
                    inventory.Any(p => p.MountId == mount && ResolveFlightPart(p.PartId)?.EquipmentType == EquipmentType.Weapon));

    }
}

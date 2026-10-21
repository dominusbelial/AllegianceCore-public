using Allegiance.Core;
using Allegiance.Core.Audio;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    private bool _waitingForRestart;
    // appmsg.cpp:1384-1409. Native skips self after HitTreasure's optimistic
    // mutation; this port applies the authoritative echo to self as well.
    private void Apply(AddPart message)
    {
        if (!InGame || !_ships.TryGetValue(message.ShipID, out var ship) || ship.ParentId != -1
            || ResolveInterfacePart(message.NewPartData.PartId) is not { } type) return;
        var part = message.NewPartData;
        if (unchecked((sbyte)part.MountId) < -5 || part.Amount <= 0) return;
        var inventory = (ship.Loadout ?? []).Concat(ship.Cargo ?? []).ToList();
        int index = inventory.FindIndex(p => p.MountId == part.MountId
            && (unchecked((sbyte)part.MountId) < 0 || ResolveInterfacePart(p.PartId)?.EquipmentType == type.EquipmentType));
        if (index >= 0)
        {
            if (inventory[index].PartId != part.PartId) return;
            inventory[index] = inventory[index] with
            { Amount = (short)Math.Min(short.MaxValue, inventory[index].Amount + part.Amount) };
        }
        else inventory.Add(new(part.PartId, part.Amount, part.MountId, 0));
        StoreInventory(ship, inventory);
    }

    private DataPartTypeIGC? ResolveInterfacePart(short id) => PartTypeResolver(id)
        ?? (MissionCore?.PartTypes.TryGetValue(id, out var part) == true ? part
            : MissionCore?.TryBuildExpendableLauncher(id, out var launcher) == true ? launcher : null);

    // appmsg.cpp:1617-1642. The port does not decrement on send, so self's
    // guaranteed echo follows the same single-decrement path as remote ships.
    private void Apply(FireExpendable message)
    {
        if (!InGame || !_ships.TryGetValue(message.LauncherId, out var ship)
            || message.EquipmentType is not (EquipmentType.Dispenser or EquipmentType.ChaffLauncher)) return;
        var inventory = (ship.Loadout ?? []).Concat(ship.Cargo ?? []).ToList();
        int index = inventory.FindIndex(p => p.MountId == 0
            && ResolveInterfacePart(p.PartId)?.EquipmentType == message.EquipmentType);
        if (index < 0 || inventory[index].Amount <= 0) return;
        inventory[index] = inventory[index] with { Amount = (short)(inventory[index].Amount - 1) };
        StoreInventory(ship, inventory);

        // P755 — SIM-0446: native BaseClient::FireExpendable plays deployChaffSound
        // for the local ship when the expendable type is chaff (clintlib.cpp:3249).
        if (message.LauncherId == MyShipId && message.EquipmentType == EquipmentType.ChaffLauncher)
            RecordSoundEvent(ClientSoundEventKind.DeployChaff, message.LauncherId);
    }

    // appmsg.cpp:2631-2636 resolves on our SIDE, rather than the whole roster.
    private void Apply(RequestMoney message)
    {
        short? ownSide = MySideId ?? (MyShipId is { } me && _ships.TryGetValue(me, out var own) ? own.SideId : null);
        if (ownSide is not { } side || side < 0
            || !_ships.TryGetValue(message.ShipIdRequest, out var ship) || ship.SideId != side) return;
        GetMoneyRequest(message.ShipIdRequest, message.Amount, message.HidFor);
    }

    // appmsg.cpp:132-176: rearm, unparent, loadout, pose, own cookie and camera.
    // SHIP_RESET's existing one-shot latch also reseeds our local flight host.
    private void Apply(RelaunchShip message)
    {
        if (!InGame || !_ships.TryGetValue(message.ShipId, out var ship)
            || !_ships.TryGetValue(message.CarrierId, out var carrier)) return;
        var previous = ship.Latest;
        ship.ParentId = -1;
        ship.BoardTurretId = -1;   // HUD-20 — relaunched, no longer boarded
        ship.HasPendingEject = false;
        ship.HullId = message.HullId;
        StoreInventory(ship, message.Parts.ToList());
        ship.StationId = -1;
        ship.State = ShipStatusMessage.StateFlying;
        ship.SectorId = carrier.SectorId;
        ship.StatusSectorKnown = carrier.StatusSectorKnown;   // HUD-18 — carrier's real sector
        ship.PoseSectorId = carrier.PoseSectorId ?? (carrier.SectorId >= 0 ? carrier.SectorId : null);
        ship.Unknown = false;
        var pose = ServerShipUpdate.FromValues(message.ShipId, previous?.StateM ?? 0,
            previous?.Controls ?? default, previous?.Time ?? 0, message.Position, message.Orientation,
            message.Velocity, [0, 0, 0], previous?.HullFraction ?? 1, previous?.ShieldFraction ?? 1,
            1, 1, 1, previous?.OreFraction ?? 0, previous?.Power ?? 1);
        Apply(new ShipReset { Ship = pose, Cookie = message.ShipId == MyShipId ? message.Cookie : ship.Cookie });
        ReseedRemoteReckoner(ship);
        if (message.ShipId == MyShipId)
        {
            _cameraOverrideRequests.Add(new(message.CarrierId, NowSeconds(), 3));
            if (_cameraOverrideRequests.Count > MaxCameraOverrideRequests) _cameraOverrideRequests.RemoveAt(0);
            // Native notification: salRepairedAtCarrierSound (sounddef id 1399).
            var voice = NativeVoiceCatalog.Resolve(1399, "");
            if (voice is not null) RecordSoundEvent(ClientSoundEventKind.ChatVoiceOver, 1399, voice: voice);
        }
    }

    public readonly record struct PlayerConnectionData(ushort Ping, ushort Loss);
    private readonly Dictionary<short, PlayerConnectionData> _playerConnections = new();
    public IReadOnlyDictionary<short, PlayerConnectionData> PlayerConnections => _playerConnections;
    /// <summary>Incremented by PINGDATA(-1,1), after all data records arrive.</summary>
    public long PlayerPingsRevision { get; private set; }
    public IReadOnlyList<string> PlayerPingLines { get; private set; } = [];

    // WinTrek.cpp:11075-11087: existing players only; -1/1 opens the report.
    private void Apply(PingData message)
    {
        if (message.ShipId != -1)
        {
            if (_ships.TryGetValue(message.ShipId, out var ship) && ship.Player is not null)
                _playerConnections[message.ShipId] = new(message.Ping, message.Loss);
            return;
        }
        if (message.Ping != 1) return;
        // WinTrek.cpp:6007-6135: own first, humans, native display clamps and
        // integer average. Keep the raw wire values in PlayerConnections.
        var lines = new List<string>();
        int pingTotal = 0, lossTotal = 0, count = 0;
        foreach (var ship in _ships.Values.Where(s => s.Player is { PilotType: PlayerInfo.PilotTypePlayer })
                     .OrderBy(s => s.ShipId == MyShipId ? 0 : 1).ThenBy(s => s.ShipId))
        {
            var data = _playerConnections.GetValueOrDefault(ship.ShipId);
            int ping = data.Ping > 1000 ? 999 : Math.Max(10, (int)data.Ping);
            int loss = data.Loss > 100 ? 99 : data.Loss;
            lines.Add($"{(ship.ShipId == MyShipId ? "You" : ship.CharacterName)}: {ping}ms / {loss}% loss");
            pingTotal += ping; lossTotal += loss; count++;
        }
        if (count > 0) lines.Add($"Average: {pingTotal / count}ms / {lossTotal / count}% loss");
        PlayerPingLines = lines;
        PlayerPingsRevision++;
    }

    private void ResetFlightInterface()
    {
        _playerConnections.Clear();
        PlayerPingsRevision = 0;
        PlayerPingLines = [];
    }
}

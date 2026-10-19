using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

/// <summary>A mutable world object backing <see cref="FakeTrainingSite"/>.</summary>
public sealed class FakeObject : ITrainingObject
{
    public TargetRef Ref { get; }
    public float Radius { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public Vector3 Forward { get; set; }
    public int SectorId { get; set; }

    public FakeObject(TargetRef r, Vector3 position, Vector3 forward, float radius = 1f)
    {
        Ref = r;
        Position = position;
        Forward = Vector3.Normalize(forward);
        Radius = radius;
    }
}

/// <summary>
/// A fully scriptable <see cref="ITrainingSite"/> for unit-testing the training framework and
/// <see cref="Mission2"/> without any host. The test drives <see cref="TimeSeconds"/>, the player ship's
/// geometry/controls/state, object positions, key presses and sound lifetimes.
/// </summary>
public sealed class FakeTrainingSite : ITrainingSite
{
    private readonly Dictionary<(ObjectType Type, int Id), FakeObject> _objects = new();
    private readonly Dictionary<int, bool> _autopilot = new();
    private readonly Dictionary<int, int> _stateM = new();
    private readonly Dictionary<int, ControlData> _shipControls = new();
    private readonly HashSet<int> _pendingKeys = new();
    private readonly HashSet<int> _pendingChats = new();
    private readonly Dictionary<int, (int SoundId, float EndTime)> _sounds = new();

    private int _nextSoundInstance;
    private int _nextObjectId = 1000;

    /// <summary>Player's raw input controls (g_inputControls).</summary>
    public ControlData InputControls { get; set; } = ControlData.Neutral;

    /// <summary>How long a played sound lingers before it is considered finished (seconds).</summary>
    public float SoundDuration { get; set; } = 0.010f;

    public float TimeSeconds { get; set; }

    public ITrainingObject PlayerShip { get; set; }

    public List<string> Messages { get; } = new();
    public List<int> PlayedSounds { get; } = new();
    public List<(int BuoyId, Vector3 Pos, int Sector, int Type, bool Visible)> Waypoints { get; } = new();
    public List<(int Id, string Name, int Hull, int Side, int Pilot, int Behaviour, int StationType, int TechLevel)> Drones { get; } = new();
    public List<(int ShipId, int Sector, Vector3 Pos)> PlacedDrones { get; } = new();
    public List<(int ShipId, int Command, TargetRef? Target, int CommandId)> IssuedCommands { get; } = new();
    public Dictionary<int, TargetRef?> CommandTargets { get; } = new();
    public Dictionary<int, int> CommandIds { get; } = new();
    public List<string> ActionsLog { get; } = new();
    private readonly HashSet<int> _destroyedShips = new();
    private readonly HashSet<(int ShipId, int PartId)> _mountedParts = new();
    private readonly Dictionary<int, float> _stationFractions = new();

    /// <summary>Mark a ship destroyed (the "armour fraction reaches 0" outcome).</summary>
    public void DestroyShip(int id) => _destroyedShips.Add(id);

    public FakeTrainingSite()
    {
        PlayerShip = new FakeObject(new TargetRef(ObjectType.Ship, 1), Vector3.Zero, Vector3.UnitX);
        Register(PlayerShip);
    }

    /// <summary>Register a live object so <see cref="Resolve"/> finds it.</summary>
    public FakeObject Register(ITrainingObject obj)
    {
        var fake = (FakeObject)obj;
        _objects[(obj.Ref.Type, obj.Ref.Id)] = fake;
        return fake;
    }

    /// <summary>Create and register a new scriptable object.</summary>
    public FakeObject AddObject(ObjectType type, int id, Vector3 position, Vector3 forward, float radius = 1f)
    {
        var o = new FakeObject(new TargetRef(type, id), position, forward, radius);
        Register(o);
        return o;
    }

    /// <summary>Advance the clock by <paramref name="dt"/> and expire any sounds whose time has passed.</summary>
    public void Advance(float dt) => TimeSeconds += dt;

    public void PressKey(int key) => _pendingKeys.Add(key);

    public void SendChat(int recipient) => _pendingChats.Add(recipient);

    public bool IsAutopilot(int shipId) => _autopilot.TryGetValue(shipId, out var v) && v;

    public void SetAutopilot(int shipId, bool on) => _autopilot[shipId] = on;

    public int GetStateM(int shipId) => _stateM.TryGetValue(shipId, out var v) ? v : 0;

    public void SetStateBit(int shipId, int bits) => _stateM[shipId] = GetStateM(shipId) | bits;

    public void SetShipControls(int shipId, ControlData controls) => _shipControls[shipId] = controls;

    public void ClearMessages() => Messages.Clear();

    // ── ITrainingSite ────────────────────────────────────────────────────────────

    public ITrainingObject? Resolve(TargetRef r)
        => _objects.TryGetValue((r.Type, r.Id), out var o) ? o : null;

    public bool GetAutopilot(TargetRef ship) => _autopilot.TryGetValue(ship.Id, out var v) && v;

    public int GetStateM(TargetRef ship) => _stateM.TryGetValue(ship.Id, out var v) ? v : 0;

    public ControlData GetShipControls(TargetRef ship)
        => _shipControls.TryGetValue(ship.Id, out var c) ? c : ControlData.Neutral;

    public ControlData GetInputControls() => InputControls;

    public TargetRef? GetCommandTarget(TargetRef ship, int command)
        => CommandTargets.TryGetValue(ship.Id, out var t) ? t : null;

    // ── M3/M4 ship/combat state ────────────────────────────────────────────────
    public bool IsShipDestroyed(TargetRef ship)
        => _destroyedShips.Contains(ship.Id) || Resolve(ship) is null;

    public bool HasCommand(TargetRef ship, int commandId)
        => CommandIds.TryGetValue(ship.Id, out var c) && c == commandId;

    public int GetSector(TargetRef model) => Resolve(model)?.SectorId ?? -1;

    public bool ShipSees(TargetRef ship, TargetRef target) => false;

    // ── M5/M6 command-view / escalation state (scriptable) ─────────────────────
    /// <summary>The model the player has picked (GetPickedCondition).</summary>
    public TargetRef? PickedTarget { get; set; }
    public bool GetPicked(TargetRef model)
        => PickedTarget is { } p && p.Type == model.Type && p.Id == model.Id;
    public int ViewSector { get; set; } = 0;
    public int GetViewSector() => ViewSector;
    public int CommandAcknowledged { get; set; } = TrainingMissionIds.CmdDoNothing;
    public float GetFraction(TargetRef model)
    {
        if (model.Type == ObjectType.Station)
            return StationFraction(model.Id);
        return (_destroyedShips.Contains(model.Id) || Resolve(model) is null) ? 0f : 1f;
    }
    public int GetCommandAcknowledged() => CommandAcknowledged;

    // ── M3/M4 combat / equipment / UI actions (recorded for assertion) ────────
    public void DealDamage(bool toHull) => ActionsLog.Add($"DealDamage({toHull})");
    public void ToggleWeapon(int mount) => ActionsLog.Add($"ToggleWeapon({mount})");
    public void SwapWeapon(int equipmentType, int mount) => ActionsLog.Add($"SwapWeapon({equipmentType},{mount})");
    public void RepairAndRearm() => ActionsLog.Add("RepairAndRearm");
    public void ShowPane(int paneMask) => ActionsLog.Add($"ShowPane(0x{paneMask:x})");
    public void SetEyeball(bool shown) => ActionsLog.Add($"SetEyeball({shown})");
    public void SetSignature(ObjectType type, int id, float signature) => ActionsLog.Add($"SetSignature({type},{id},{signature})");

    // ── M8 nanite / repair / station seams (scriptable) ──────────────────────
    public bool HasPartMounted(TargetRef ship, int partId) => _mountedParts.Contains((ship.Id, partId));
    public void MountPart(TargetRef ship, int partId, int mount = 0) => _mountedParts.Add((ship.Id, partId));
    public void DamageStation(int stationId, float amount)
        => _stationFractions[stationId] = Math.Clamp(Fraction(stationId) - amount / 20000f, 0f, 1f);
    public float StationFraction(int stationId) => Fraction(stationId);
    public void RepairStation(int stationId) => _stationFractions[stationId] = 1f;
    private float Fraction(int id) => _stationFractions.TryGetValue(id, out var f) ? f : 1f;

    public bool ConsumeKey(int key) => _pendingKeys.Remove(key);

    public void RecordKeyPress(int key) => _pendingKeys.Add(key);

    public bool ConsumeChat(int recipient) => _pendingChats.Remove(recipient);

    public void RecordChat(int recipient) => _pendingChats.Add(recipient);

    public void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible)
    {
        Waypoints.Add((buoyId, position, sectorId, buoyType, visible));
        AddObject(ObjectType.Buoy, buoyId, position, Vector3.UnitX);
    }

    public void CreateProbe(int probeId, int typeId, Vector3 position, int sideId, int sectorId)
        => AddObject(ObjectType.Probe, probeId, position, Vector3.UnitX);

    public int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType,
        int behaviour = 0, int stationType = -1, int techLevel = 1)
    {
        int id = shipId < 0 ? _nextObjectId++ : shipId;
        Drones.Add((id, name, hullId, sideId, pilotType, behaviour, stationType, techLevel));
        return id;
    }

    public void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up, TargetRef? commandTarget, int commandId)
    {
        PlacedDrones.Add((shipId, sectorId, position));
        if (commandTarget is { } t)
            CommandTargets[shipId] = t;
        AddObject(ObjectType.Ship, shipId, position, forward);
    }

    public void IssueCommand(TargetRef ship, int command, TargetRef? target, int commandId)
    {
        IssuedCommands.Add((ship.Id, command, target, commandId));
        CommandIds[ship.Id] = commandId;
        if (target is { } t)
            CommandTargets[ship.Id] = t;
    }

    public void SetAutopilot(TargetRef ship, bool on) => _autopilot[ship.Id] = on;

    public void SetDisplayMode(int cameraMode) => DisplayMode = cameraMode;

    public void SetHudOverlay(int overlay) => HudOverlay = overlay;

    public int DisplayMode { get; private set; } = -1;

    public int HudOverlay { get; private set; } = -1;

    public void ShowMessage(string text) => Messages.Add(text);

    public int PlaySound(int soundId)
    {
        int instance = _nextSoundInstance++;
        _sounds[instance] = (soundId, TimeSeconds + SoundDuration);
        PlayedSounds.Add(soundId);
        return instance;
    }

    public bool IsSoundPlaying(int instance)
        => _sounds.TryGetValue(instance, out var s) && TimeSeconds < s.EndTime;

    public void StopSound(int instance) => _sounds.Remove(instance);

    public (float Yaw, float Pitch, float Roll) TurnToFace(TargetRef ship, TargetRef target) => (0f, 0f, 0f);
}

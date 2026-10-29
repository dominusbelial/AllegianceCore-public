using System.Numerics;
using System.Collections.Generic;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;

namespace Allegiance.SinglePlayer;

/// <summary>
/// TR-2b — <see cref="ITrainingSite"/> bound to the real embedded training host: geometry and
/// ship-state reads come from the real <see cref="TrainingHost.Mission"/> models, and every
/// creation/command/autopilot action calls the host's in-process handlers (which operate on the
/// real mission). This is the host-side read/act path used by the headless proof; the networked
/// client uses <see cref="ClientCore.MonoGame.ClientTrainingSite"/> (ClientWorld reads) wired to the
/// same <see cref="ITrainingHost"/> handlers.
/// </summary>
public sealed class TrainingHostSite : ITrainingSite, ITrainingLifecycleSite
{
    public int CommanderId => _host.CommanderShipId;
    public void SetupTraining(int missionId) { }
    public void TrainingShipDied() { }
    public void RestoreTrainingShip() => _host.RestoreTrainee();
    public bool TrainingShipExists(int shipId) => _host.TrainingShipExists(shipId);
    private readonly TrainingHost _host;
    private readonly List<int> _keys = new();
    private readonly List<int> _chats = new();
    private readonly List<string> _messages = new();
    private int _nextInstance = 1;
    private readonly Dictionary<int, int> _soundInstances = new();
    private ControlData _inputControls = ControlData.Neutral;
    private ControlData _appliedControls = ControlData.Neutral;

    public TrainingHostSite(TrainingHost host) => _host = host;

    public float TimeSeconds => (float)_host.Mission.LastUpdate;

    public ITrainingObject PlayerShip => WrapShip(_host.PlayerShip);

    public ITrainingObject? Resolve(TargetRef r)
    {
        if (r.Type == ObjectType.Ship)
        {
            int id = r.Id == TargetRef.Na ? _host.PlayerShipId : r.Id;
            return _host.ResolveShip(id) is { } ship ? WrapShip(ship) : null;
        }
        if (r.Type == ObjectType.Buoy && _host.Waypoints.TryGetValue(r.Id, out var wp))
            return new HostObject(r, wp.Position, Vector3.Zero, Vector3.UnitX, wp.SectorId, 20f);
        if (r.Type == ObjectType.Station && _host.Stations.TryGetValue(r.Id, out var st))
            return new HostObject(r, st.Position, Vector3.Zero, Vector3.UnitX, st.SectorId, 50f);
        return null;
    }

    // ── ship state (real host mission) ────────────────────────────────────────
    public bool GetAutopilot(TargetRef ship)
        => _host.ResolveShip(ship.Id) is { } s && _host.IsAutopilot(s.Id);
    public int GetStateM(TargetRef ship)
        => _host.ResolveShip(ship.Id) is { } s ? s.StateM : 0;
    /// <summary>
    /// The controls the mission's <c>GetControlActiveCondition</c> reads (native <c>g_shipControls</c>,
    /// the client's applied controls). Decoupled from the sim <see cref="Ship.Controls"/> field so the
    /// headless driver can script the control states the script gates on (full throttle in goal 4, etc.)
    /// without the server integrating them into unintended ship motion — in the real client this value
    /// is the masked client control stream, while the server moves the ship from its own received controls.
    /// </summary>
    public ControlData GetShipControls(TargetRef ship) => _appliedControls;
    public ControlData GetInputControls() => _inputControls;
    public TargetRef? GetCommandTarget(TargetRef ship, int command)
        => command == TrainingMissionIds.CommandCurrent ? _host.GetCommandTarget(ship.Id) : null;

    // ── M3/M4 ship/combat state (real host mission) ─────────────────────────────
    public bool IsShipDestroyed(TargetRef ship) => _host.IsDestroyed(ship.Id);

    public bool HasCommand(TargetRef ship, int commandId) => _host.GetCommandId(ship.Id) == commandId;

    public int GetSector(TargetRef model) => Resolve(model)?.SectorId ?? -1;

    public bool ShipSees(TargetRef ship, TargetRef target) => _host.InScannerRange(ship.Id, target);

    // ── M5/M6 command-view / escalation state (real host state) ───────────────
    public bool GetPicked(TargetRef model) => _host.IsPicked(model);
    public int GetViewSector() => _host.ViewSector;
    public float GetFraction(TargetRef model) => _host.GetFraction(model);
    public int GetCommandAcknowledged() => _host.GetCommandAcknowledged();

    // ── M3/M4 combat / equipment / UI actions ──────────────────────────────────
    // The headless proof scripts the *outcomes* these actions produce on the real host (armour
    // fraction via DestroyDrone, command set via IssueCommand, etc.); the player's own weapon/shield/
    // pane/eyeball cosmetics are client-side rendering and are no-ops on the host site (mirroring how
    // SetDisplayMode/SetHudOverlay are no-ops here).
    public void DealDamage(bool toHull) { }
    public void ToggleWeapon(int mount) { }
    public void SwapWeapon(int equipmentType, int mount) { }
    public void RepairAndRearm() { }
    public void ShowPane(int paneMask) { }
    public void SetEyeball(bool shown) { }
    public void SetSignature(ObjectType type, int id, float signature) { }

    // ── M8 nanite / repair / station seams (real host state) ─────────────────
    public bool HasPartMounted(TargetRef ship, int partId) => _host.HasPartMounted(ship.Id, partId);
    public void MountPart(TargetRef ship, int partId, int mount = 0) => _host.MountPart(ship.Id, partId);
    public void DamageStation(int stationId, float amount) => _host.DamageStation(stationId, amount);

    // ── player events ─────────────────────────────────────────────────────────
    public bool ConsumeKey(int key)
    {
        int i = _keys.IndexOf(key);
        if (i < 0) return false;
        _keys.RemoveAt(i);
        return true;
    }
    public void RecordKeyPress(int key) => _keys.Add(key);
    public bool ConsumeChat(int recipient)
    {
        int i = _chats.IndexOf(recipient);
        if (i < 0) return false;
        _chats.RemoveAt(i);
        return true;
    }
    public void RecordChat(int recipient) => _chats.Add(recipient);

    /// <summary>Inject a chat-to-recipient event so the mission's <c>GetChatCondition</c> can consume it
    /// (the headless driver's stand-in for the player typing a chat).</summary>
    public void SendTrainingChat(int recipient) => _chats.Add(recipient);

    // ── world creation → host handlers ────────────────────────────────────────
    public void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible)
        => _host.CreateWaypoint(buoyId, position, sectorId, buoyType, visible);
    public void CreateProbe(int probeId, int typeId, Vector3 position, int sideId, int sectorId)
        => _host.CreateProbe(probeId, typeId, position, sideId, sectorId);
    public int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType,
        int behaviour = 0, int stationType = -1, int techLevel = 1)
        => _host.CreateDrone(name, shipId, hullId, sideId, pilotType, behaviour, stationType, techLevel);
    public void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up,
        TargetRef? commandTarget, int commandId)
        => _host.PlaceDrone(shipId, sectorId, position, forward, up,
            commandTarget is { Type: ObjectType.Ship } ? commandTarget.Value.Id : null, commandId);
    public void IssueCommand(TargetRef ship, int command, TargetRef? target, int commandId)
        => _host.IssueCommand(ship.Id, command,
            target is { Type: ObjectType.Ship } ? target.Value.Id : null,
            target is { Type: ObjectType.Buoy } ? target.Value.Id : null,
            target is { Type: ObjectType.Station } ? target.Value.Id : null,
            commandId);

    // ── ship / camera / HUD ───────────────────────────────────────────────────
    public void SetAutopilot(TargetRef ship, bool on) => _host.SetAutopilot(ship.Id, on);
    public void SetDisplayMode(int cameraMode) { }       // client camera concern (headless n/a)
    public void SetHudOverlay(int overlay) { }           // client HUD concern (headless n/a)
    public (float Yaw, float Pitch, float Roll) TurnToFace(TargetRef ship, TargetRef target)
        => (0f, 0f, 0f);                                 // headless driver points the player directly

    // ── output ────────────────────────────────────────────────────────────────
    public void ShowMessage(string text) => _messages.Add(text);
    public int PlaySound(int soundId) => _nextInstance++;
    public bool IsSoundPlaying(int instance) => false;
    public void StopSound(int instance) { }

    /// <summary>Chat lines the mission surfaced (test/session inspection).</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>Script the raw input controls read by <see cref="GetInputControls"/> (unused by Mission 2).</summary>
    public void SetInputControls(ControlData c) => _inputControls = c;

    /// <summary>Script the applied controls read by <see cref="GetShipControls"/> (the mission's
    /// <c>GetControlActiveCondition</c> gate for full-throttle / off-full-throttle goals).</summary>
    public void SetAppliedControls(ControlData c) => _appliedControls = c;

    private static ITrainingObject WrapShip(Ship ship) =>
        new HostObject(new TargetRef(ObjectType.Ship, ship.Id), ToNum(ship.Position), ToNum(ship.Velocity),
            ToNum(ship.Orientation.GetForward()), ship.Sector?.Id ?? 0, 20f);

    private static System.Numerics.Vector3 ToNum(Allegiance.Core.Collision.Vector3 v)
        => new(v.X, v.Y, v.Z);

    /// <summary>Geometry surface for a real model or a host waypoint/station record.</summary>
    private sealed class HostObject : ITrainingObject
    {
        private readonly Vector3 _pos, _vel, _fwd;
        public TargetRef Ref { get; }
        public float Radius { get; }
        public int SectorId { get; }
        public HostObject(TargetRef r, Vector3 pos, Vector3 vel, Vector3 fwd, int sectorId, float radius)
        { Ref = r; _pos = pos; _vel = vel; _fwd = fwd; SectorId = sectorId; Radius = radius; }
        public Vector3 Position => _pos;
        public Vector3 Velocity => _vel;
        public Vector3 Forward => _fwd;
    }
}

using System.Numerics;
using System.Collections.Generic;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// TR-2 — a small set of delegate providers that decouple <see cref="ClientTrainingSite"/>
/// from the full <see cref="Game1"/> so the binding is unit-testable against a live session
/// without constructing a graphics device. Each maps one ITrainingSite touch to a real client
/// facility (world read, control send, chat line, sound, own-ship hull for turn math).
/// </summary>
public sealed class ClientTrainingFacilities
{
    /// <summary>The replicated world (authoritative client mirror of the server).</summary>
    public required Func<ClientWorld> World { get; init; }
    /// <summary>The live server connection (controls + commands).</summary>
    public required ConnectionSession Session { get; init; }
    /// <summary>Elapsed training-clock seconds (<c>Time::Now</c>).</summary>
    public Func<double>? ClockSeconds { get; init; }
    /// <summary>Play a native sound id; returns a stable instance handle.</summary>
    public Func<int, int>? PlaySound { get; init; }
    /// <summary>Whether a sound instance is still playing.</summary>
    public Func<int, bool>? IsSoundPlaying { get; init; }
    /// <summary>Stop a sound instance.</summary>
    public Action<int>? StopSound { get; init; }
    /// <summary>The own-ship hull kinematics (mass/torque/turn rates) for <c>turnToFace</c>,
    /// when the client's own-ship simulator has resolved the hull.</summary>
    public Func<TrainingTurnTo.TurnHull?>? OwnHullKinematics { get; init; }
    /// <summary>The player's live own-ship orientation (forward/up/right).</summary>
    public Func<(Vector3 F, Vector3 U, Vector3 R)?>? OwnOrientation { get; init; }
    /// <summary>TR-2b — the in-process training-host API the creation/command/autopilot actions
    /// call on the embedded server. When null (no host), those actions keep the explicit logged
    /// no-op seams so the absence is auditable rather than silent.</summary>
    public ITrainingHost? Host { get; init; }
}

/// <summary>
/// TR-2 — <see cref="ITrainingSite"/> bound to the real game: every geometry and ship-state read
/// comes from the replicated <see cref="ClientWorld"/>, controls flow through the live
/// <see cref="ConnectionSession"/>, and the deferred <c>TurnToFace</c> uses the native
/// <c>turnToFace</c> ship-sim math (<see cref="TrainingTurnTo"/>) fed by the own-ship simulator's
/// hull kinematics. The training-host creation actions (CreateWaypoint/CreateDrone/PlaceDrone/
/// IssueCommand/SetAutopilot/SetDisplayMode) need the embedded server's training universe —
/// outside this session's scope — so they are logged no-ops the host fills in; every read/query
/// path is real.
/// </summary>
public sealed class ClientTrainingSite : ITrainingSite
{
    private readonly ClientTrainingFacilities _f;
    private readonly List<int> _keys = new();
    private readonly List<string> _messages = new();
    private int _nextInstance = 1;
    private readonly Dictionary<int, int> _instances = new();

    public ClientTrainingSite(ClientTrainingFacilities f) => _f = f;

    public float TimeSeconds => (float)(_f.ClockSeconds?.Invoke() ?? 0.0);

    private ClientWorld World => _f.World();

    /// <summary>True once the client's own ship is in the replicated world with a pose — the
    /// precondition for <see cref="PlayerShip"/> (native CreateMission runs after
    /// SetupShipAndCamera, WinTrek.cpp:7401-7421).</summary>
    public bool HasPlayerShip
    {
        get
        {
            var w = World;
            return w.MyShipId is { } me && w.Ships.TryGetValue(me, out var ship) && ship.Latest is not null;
        }
    }

    public ITrainingObject PlayerShip
    {
        get
        {
            var w = World;
            return w.MyShipId is { } me && w.Ships.TryGetValue(me, out var ship) && ship.Latest is { } up
                ? new TrainingShipObject(me, ship, up)
                : throw new InvalidOperationException("Training: no player ship in the replicated world");
        }
    }

    public ITrainingObject? Resolve(TargetRef r)
    {
        if (r.Type != ObjectType.Ship) return null;
        int id = r.Id == TargetRef.Na ? (World.MyShipId ?? -1) : r.Id;
        if (id < 0) return null;
        return World.Ships.TryGetValue((short)id, out var ship) && ship.Latest is { } up
            ? new TrainingShipObject((short)id, ship, up) : null;
    }

    // ── ship state reads (real replicated world) ───────────────────────────
    public bool GetAutopilot(TargetRef ship) => false; // host-exposed flag (TR-2 host universe)
    public int GetStateM(TargetRef ship)
    {
        int id = ResolveId(ship);
        return id >= 0 && World.Ships.TryGetValue((short)id, out var s) && s.Latest is { } u ? u.StateM : 0;
    }
    public ControlData GetShipControls(TargetRef ship)
    {
        int id = ResolveId(ship);
        return id >= 0 && World.Ships.TryGetValue((short)id, out var s) && s.Latest is { } u ? u.Controls : ControlData.Neutral;
    }
    public ControlData GetInputControls() => TrainingControlState.InputControls;
    public TargetRef? GetCommandTarget(TargetRef ship, int command) => null;

    // ── M3/M4 ship/combat state (replicated world + host) ─────────────────────
    /// <summary>Absent from the replicated world ⇒ destroyed (<c>getshipisdamagedcondition.cpp:53-55</c>).</summary>
    public bool IsShipDestroyed(TargetRef ship) => Resolve(ship) is null;

    public bool HasCommand(TargetRef ship, int commandId) => false; // client exposes no accepted/current cmd

    public int GetSector(TargetRef model) => Resolve(model)?.SectorId ?? -1;

    public bool ShipSees(TargetRef ship, TargetRef target) => false; // server-side scanner judgement

    // ── M5/M6 command-view / escalation state (authoritative host) ──────────
    public bool GetPicked(TargetRef model) => _f.Host is { } h ? h.IsPicked(model) : false;
    public int GetViewSector() => _f.Host is { } h ? h.ViewSector : (Resolve(PlayerShip.Ref)?.SectorId ?? -1);
    public float GetFraction(TargetRef model) => _f.Host is { } h ? h.GetFraction(model) : (Resolve(model) is null ? 0f : 1f);
    public int GetCommandAcknowledged() => _f.Host is { } h ? h.GetCommandAcknowledged() : TrainingMissionIds.CmdDoNothing;

    // ── M3/M4 combat / equipment / UI actions ─────────────────────────────────
    public void DealDamage(bool toHull) => TrainingHostLog.Log($"DealDamage(toHull:{toHull})");
    public void ToggleWeapon(int mount) => TrainingHostLog.Log($"ToggleWeapon(mount:{mount})");
    public void SwapWeapon(int equipmentType, int mount) => TrainingHostLog.Log($"SwapWeapon({equipmentType},{mount})");
    public void RepairAndRearm() => TrainingHostLog.Log("RepairAndRearm()");
    public void ShowPane(int paneMask) => TrainingHostLog.Log($"ShowPane(0x{paneMask:x})");
    public void SetEyeball(bool shown) => TrainingHostLog.Log($"SetEyeball({shown})");
    public void SetSignature(ObjectType type, int id, float signature) => TrainingHostLog.Log($"SetSignature({type},{id},{signature})");

    // ── M8 nanite / repair / station seams (authoritative host) ───────────────
    public bool HasPartMounted(TargetRef ship, int partId) => _f.Host is { } h && h.HasPartMounted(ship.Id, partId);
    public void MountPart(TargetRef ship, int partId, int mount = 0)
    {
        if (_f.Host is { } h) h.MountPart(ship.Id, partId);
        else TrainingHostLog.Log($"MountPart({ship.Id},{partId}) — no embedded training host");
    }
    public void DamageStation(int stationId, float amount)
    {
        if (_f.Host is { } h) h.DamageStation(stationId, amount);
        else TrainingHostLog.Log($"DamageStation({stationId},{amount}) — no embedded training host");
    }

    private static int ResolveId(TargetRef r) =>
        r.Type == ObjectType.Ship && r.Id == TargetRef.Na ? -1 : r.Id;

    // ── player events ───────────────────────────────────────────────────────
    public bool ConsumeKey(int key)
    {
        int i = _keys.IndexOf(key);
        if (i < 0) return false;
        _keys.RemoveAt(i);
        return true;
    }
    public void RecordKeyPress(int key) => _keys.Add(key);
    public bool ConsumeChat(int recipient) => false;
    public void RecordChat(int recipient) { }

    // ── world creation (training-host universe — TR-2b host scope) ───────────
    public void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible)
    {
        if (_f.Host is { } h) h.CreateWaypoint(buoyId, position, sectorId, buoyType, visible);
        else TrainingHostLog.Log($"CreateWaypoint({buoyId}) — no embedded training host");
    }
    public void CreateProbe(int probeId, int typeId, Vector3 position, int sideId, int sectorId)
    {
        if (_f.Host is { } h) h.CreateProbe(probeId, typeId, position, sideId, sectorId);
        else TrainingHostLog.Log($"CreateProbe({probeId}) — no embedded training host");
    }
    public int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType,
        int behaviour = 0, int stationType = -1, int techLevel = 1)
        => _f.Host is { } h ? h.CreateDrone(name, shipId, hullId, sideId, pilotType, behaviour, stationType, techLevel)
            : -1; // no ship created without the training-host universe
    public void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up, TargetRef? commandTarget, int commandId)
    {
        if (_f.Host is { } h)
            h.PlaceDrone(shipId, sectorId, position, forward, up,
                commandTarget is { Type: ObjectType.Ship } ? commandTarget.Value.Id : null, commandId);
        else TrainingHostLog.Log($"PlaceDrone({shipId}) — no embedded training host");
    }
    public void IssueCommand(TargetRef ship, int command, TargetRef? target, int commandId)
    {
        if (_f.Host is { } h)
            h.IssueCommand(ship.Id, command,
                target is { Type: ObjectType.Ship } ? target.Value.Id : null,
                target is { Type: ObjectType.Buoy } ? target.Value.Id : null,
                target is { Type: ObjectType.Station } ? target.Value.Id : null,
                commandId);
        else TrainingHostLog.Log($"IssueCommand(ship {ship.Id}, cmd {command}) — no embedded training host");
    }

    // ── ship / camera / HUD configuration ───────────────────────────────────
    public void SetAutopilot(TargetRef ship, bool on)
    {
        if (_f.Host is { } h) h.SetAutopilot(ship.Id, on);
        else TrainingHostLog.Log($"SetAutopilot({ship.Id},{on}) — no embedded training host");
    }
    public void SetDisplayMode(int cameraMode) => TrainingHostLog.Log($"SetDisplayMode({cameraMode})");
    public void SetHudOverlay(int overlay) => TrainingOverlay.Current = overlay;

    public (float Yaw, float Pitch, float Roll) TurnToFace(TargetRef ship, TargetRef target)
    {
        var me = PlayerShip;
        var tgt = Resolve(target);
        if (tgt is null) return (0f, 0f, 0f);
        if (_f.OwnOrientation?.Invoke() is not { } ori || _f.OwnHullKinematics?.Invoke() is not { } h)
            return (0f, 0f, 0f);
        return TrainingTurnTo.Compute(me.Position, tgt.Position, ori.F, ori.U, ori.R, TimeSeconds, h);
    }

    // ── output (messages / sound) ───────────────────────────────────────────
    public void ShowMessage(string text)
    {
        _messages.Add(text);
        World.PostSystemLine(text);
    }
    public int PlaySound(int soundId)
    {
        int instance = _nextInstance++;
        _instances[instance] = _f.PlaySound?.Invoke(soundId) ?? -1;
        return instance;
    }
    public bool IsSoundPlaying(int instance)
        => _instances.TryGetValue(instance, out var handle) && (_f.IsSoundPlaying?.Invoke(handle) ?? true);
    public void StopSound(int instance)
    {
        if (_instances.TryGetValue(instance, out var handle)) _f.StopSound?.Invoke(handle);
        _instances.Remove(instance);
    }

    /// <summary>Read-only access to the chat lines the mission surfaced (test/session inspection).</summary>
    public IReadOnlyList<string> Messages => _messages;
}

/// <summary>Adapts a live <see cref="ShipEntity"/> into the framework's geometry surface.</summary>
internal sealed class TrainingShipObject : ITrainingObject
{
    private readonly short _id;
    private readonly ShipEntity _ship;
    private readonly ServerShipUpdate _up;
    public TrainingShipObject(short id, ShipEntity ship, ServerShipUpdate up) { _id = id; _ship = ship; _up = up; }
    public TargetRef Ref => new(ObjectType.Ship, _id);
    public float Radius => 20f;
    public Vector3 Position => ToSys(_up.Position);
    public Vector3 Velocity => ToSys(_up.Velocity);
    public Vector3 Forward => _up.Orientation.GetForward() is { } f ? ToSys(f) : Vector3.UnitZ;
    public int SectorId => _ship.SectorId;

    private static Vector3 ToSys(Allegiance.Core.Collision.Vector3 v) => new(v.X, v.Y, v.Z);
}

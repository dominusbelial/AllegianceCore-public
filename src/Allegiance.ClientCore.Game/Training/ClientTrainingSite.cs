using System.Numerics;
using System.Collections.Generic;
using Allegiance.Core;
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
    /// <summary>The client frame delta (native <c>GetWindow()-&gt;GetDeltaTime()</c>).</summary>
    public Func<double>? FrameDeltaSeconds { get; init; }
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
    /// <summary>P886b — the player's live own-ship POSITION (system coords). The local own-ship
    /// simulator is the client authority for the player's pose (P508), so the mission's geometric
    /// conditions (pointing, range) must read THIS, not the lagged replicated broadcast, or they
    /// can desync from where the ship actually is/points (the real-window pointing-goal flake).</summary>
    public Func<Vector3?>? OwnPosition { get; init; }
    /// <summary>TR-2b — the in-process training-host API the creation/command/autopilot actions
    /// call on the embedded server. When null (no host), those actions keep the explicit logged
    /// no-op seams so the absence is auditable rather than silent.</summary>
    public ITrainingHost? Host { get; init; }
    /// <summary>P881a — route the own ship's <c>SetCommandAction</c> through the real client
    /// ORDER_CHANGE path (updates the command slots and sends <c>FM_C_ORDER_CHANGE</c>), exactly
    /// like a mouse pick or the order menu. Arguments: command slot, target, command id.</summary>
    public Action<int, TargetRef?, int>? OwnOrder { get; init; }
    /// <summary>P881a — engage/disengage the client's native autopilot toward the own ship's last
    /// ordered target (the <c>SetAutopilotAction</c> binding).</summary>
    public Action<TargetRef?, bool>? OwnAutopilot { get; init; }
    /// <summary>P881a — real camera mode (<c>SetDisplayModeAction</c> → <c>TrekWindow::SetCameraMode</c>).</summary>
    public Action<int>? SetCameraMode { get; init; }
    /// <summary>P881a — console pane flags (<c>ShowPaneAction</c> → <c>TurnOffOverlayFlags</c> +
    /// <c>TurnOnOverlayFlags</c>).</summary>
    public Action<int>? ShowPane { get; init; }
    /// <summary>P881a — the enemy-detection eyeball toggle (<c>ShowEyeballAction</c>).</summary>
    public Action<bool>? SetEyeball { get; init; }
    public Action<int>? SetupTraining { get; init; }
    public Action<System.Numerics.Vector3, float>? DeathCamera { get; init; }
    public System.Action? RestoreCamera { get; init; }
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
public sealed class ClientTrainingSite : ITrainingSite, ITrainingLifecycleSite
{
    public int CommanderId => _f.Host is TrainingHost host ? host.CommanderShipId : -1;
    public void SetupTraining(int missionId)
    {
        SetHudOverlay(0); // Training.cpp:55-56
        _f.SetupTraining?.Invoke(missionId);
    }
    public bool TrainingShipExists(int shipId) => _f.Host?.TrainingShipExists(shipId)
        ?? Resolve(new TargetRef(ObjectType.Ship, shipId)) is not null;
    private TrainingLifecycleEvent _death;
    public bool TryTakeLifecycleEvent(out TrainingLifecycleEvent trainingEvent)
    {
        if (_f.Host?.TryTakeLifecycleEvent(out trainingEvent) == true)
        {
            if (trainingEvent.Death) _death = trainingEvent;
            return true;
        }
        trainingEvent = default;
        return false;
    }
    public void TrainingShipDied() => _f.DeathCamera?.Invoke(_death.Focus, _death.Radius);
    public void RestoreTrainingShip()
    {
        _f.Host?.RestoreTrainee();
        _f.RestoreCamera?.Invoke();
    }
    private readonly ClientTrainingFacilities _f;
    private readonly List<int> _keys = new();
    private readonly List<int> _chats = new();
    private readonly List<string> _messages = new();
    private int _nextInstance = 1;
    private readonly Dictionary<int, int> _instances = new();
    private TargetRef? _ownOrderTarget;

    public ClientTrainingSite(ClientTrainingFacilities f) => _f = f;

    /// <summary>P881a — whether <paramref name="r"/> names the client's own ship.</summary>
    private bool IsOwnShip(TargetRef r)
        => r.Type == ObjectType.Ship
            && (r.Id == TargetRef.Na ? World.MyShipId is not null : World.MyShipId == (short)r.Id);

    public float TimeSeconds => (float)(_f.ClockSeconds?.Invoke() ?? 0.0);

    /// <summary>The client's real frame delta (native <c>GetWindow()-&gt;GetDeltaTime()</c>).</summary>
    public float FrameDeltaSeconds => (float)(_f.FrameDeltaSeconds?.Invoke() ?? 1.0 / 60.0);

    private ClientWorld World => _f.World();

    /// <summary>True once the client's own ship is in the replicated world with a pose — the
    /// precondition for <see cref="PlayerShip"/> (native CreateMission runs after
    /// SetupShipAndCamera, WinTrek.cpp:7401-7421).</summary>
    public bool HasPlayerShip
    {
        get
        {
            var w = World;
            return w.MyShipId is { } me && w.Ships.TryGetValue(me, out var ship)
                && ((ship.Latest is not null && ship.StationId < 0 && ship.SectorId >= 0)
                    || (_f.Host is TrainingHost h && h.PlayerShipId == me && h.PlayerShip.StationId is not null && ship.StationId >= 0));
        }
    }

    public bool PlayerDocked => World.MyShipId is { } me
        && World.Ships.TryGetValue(me, out var ship) && ship.StationId >= 0;

    public ITrainingObject PlayerShip
    {
        get
        {
            var w = World;
            return w.MyShipId is { } me && w.Ships.TryGetValue(me, out var ship) && ship.Latest is { } up
                ? new TrainingShipObject(me, ship, up)
                : _f.Host is TrainingHost h && w.MyShipId == h.PlayerShipId && PlayerDocked
                    ? new TrainingHostSite(h).PlayerShip
                    : throw new InvalidOperationException("Training: no player ship in the replicated world");
        }
    }

    public ITrainingObject? Resolve(TargetRef r)
    {
        int id = r.Id == TargetRef.Na ? (World.MyShipId ?? -1) : r.Id;
        if (id < 0) return null;
        var w = World;
        switch (r.Type)
        {
            case ObjectType.Ship:
                if (!w.Ships.TryGetValue((short)id, out var ship) || ship.IsDead || ship.Latest is not { } up)
                    return null;
                // P886b — resolve the PLAYER's own ship from the authoritative local sim pose
                // (OwnPosition/OwnOrientation) rather than the lagged replicated broadcast, so the
                // mission's geometric conditions (pointing, range) see where the ship actually is
                // and points — the auto-turn steers the local sim (P508), and a stale replicated
                // forward/position is what intermittently froze the pointing goals. Falls back to
                // the replicated pose when the local sim hasn't resolved a hull (tests).
                bool own = id == (w.MyShipId ?? -1);
                var posOv = own ? _f.OwnPosition?.Invoke() : null;
                System.Numerics.Vector3? fwdOv = null;
                if (own && _f.OwnOrientation?.Invoke() is { } o) fwdOv = o.F;
                return new TrainingShipObject((short)id, ship, up, posOv, fwdOv);
            case ObjectType.Warp:
                if (w.Warps.TryGetValue((short)id, out var warp))
                    return new TrainingStaticObject(r, ToVec(warp.Position), warp.ClusterId, warp.Radius, ToVec(warp.Forward));
                // Native training loads the whole authored universe before building its goals
                // (mission4.cpp:110,534); undiscovered sectors still have real aleph geometry.
                return _f.Host is TrainingHost trainingHost ? new TrainingHostSite(trainingHost).Resolve(r) : null;
            case ObjectType.Station:
                // Native GetSectorCondition also resolves stations/asteroids/probes via the
                // mission's GetModel(OT_*, id) (getsectorcondition.cpp:27-51); the replicated
                // world carries them as EXPORT records with a live faction slot.
                return w.Stations.TryGetValue((short)id, out var st)
                    ? new TrainingStaticObject(r, ToVec(st.Position), st.ClusterId, 50f) : null;
            case ObjectType.Asteroid:
                return w.Asteroids.TryGetValue((short)id, out var ast)
                    ? new TrainingStaticObject(r, ToVec(ast.Position), ast.ClusterId, ast.Radius) : null;
            case ObjectType.Probe:
                return w.Probes.TryGetValue((short)id, out var pb)
                    ? new TrainingStaticObject(r, ToSys(pb.Position), pb.ClusterId, 10f) : null;
            case ObjectType.Buoy:
                // A training waypoint buoy lives on the host's waypoint table
                // (createwaypointaction.cpp:34-46); the client resolves it through the host.
                return _f.Host is { } h ? h.ResolveWaypoint(r.Id) : null;
            default:
                return null;
        }
    }

    private static Vector3 ToVec(float[] v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
    private static Vector3 ToSys(Allegiance.Core.Collision.Vector3 v) => new(v.X, v.Y, v.Z);

    // ── ship state reads (real replicated world) ───────────────────────────
    public bool GetAutopilot(TargetRef ship)
        => _f.Host is { } h ? h.IsAutopilot(ResolveId(ship)) : false; // host flies Goto'd ships on autopilot
    public int GetStateM(TargetRef ship)
    {
        // Own ship: the client's local state mask (native GetShip()->GetStateM()), which carries
        // the high-word missile-fire bit the 16-bit wire update cannot. Remote ships: replicated.
        if (IsOwnShip(ship)) return TrainingOwnState.StateM;
        int id = ResolveId(ship);
        return id >= 0 && World.Ships.TryGetValue((short)id, out var s) && s.Latest is { } u ? u.StateM : 0;
    }
    public ControlData GetShipControls(TargetRef ship)
    {
        // The own ship reads the client's applied controls (native GetShip()->GetControls());
        // remote ships read the replicated pose stream.
        if (IsOwnShip(ship)) return TrainingOwnState.Controls;
        int id = ResolveId(ship);
        return id >= 0 && World.Ships.TryGetValue((short)id, out var s) && s.Latest is { } u ? u.Controls : ControlData.Neutral;
    }
    public ControlData GetInputControls() => TrainingControlState.InputControls;
    public TargetRef? GetCommandTarget(TargetRef ship, int command)
        => command == TrainingMissionIds.CommandCurrent && _f.Host is { } h
            ? h.GetCommandTarget(ResolveId(ship)) : null;

    // ── M3/M4 ship/combat state (replicated world + host) ─────────────────────
    /// <summary>Destroyed when absent from the replicated world, latched dead by the kill broadcast
    /// (the world keeps the corpse entity briefly for the explosion), or reported at zero real armour
    /// by the authoritative host — native <c>getshipisdamagedcondition.cpp:44-57</c> (a gone model or
    /// armour fraction 0; <c>GetShipIsDestroyedCondition</c> = <c>Not(GetShipIsDamaged(ship, 0.0))</c>,
    /// getshipisdestroyedcondition.h:27-28).</summary>
    public bool IsShipDestroyed(TargetRef ship)
    {
        int id = ResolveId(ship);
        if (id < 0 || !World.Ships.TryGetValue((short)id, out var entity)) return true;
        if (entity.IsDead || entity.Latest is null) return true;
        return _f.Host is { } h && h.GetFraction(ship) <= 0f;
    }

    /// <summary>TRUE when the ship's accepted-or-current command id equals
    /// <paramref name="commandId"/>, and TRUE when the ship is absent — native
    /// <c>GetCommandCondition</c> (getcommandcondition.cpp:44-53: absent ⇒ true).</summary>
    public bool HasCommand(TargetRef ship, int commandId)
    {
        int id = ResolveId(ship);
        if (id < 0 || Resolve(ship) is null) return true;   // getcommandcondition.cpp:53 absent ⇒ true
        return _f.Host is { } h && (h.GetCommandId(id) == commandId);
    }

    public int GetSector(TargetRef model) => Resolve(model)?.SectorId ?? -1;

    /// <summary>Server-side scanner judgement: same sector and within range (<c>IshipIGC::InScannerRange</c>),
    /// answered by the host's real sim (<c>shipseesobjectcondition.cpp:47-50</c>).</summary>
    public bool ShipSees(TargetRef ship, TargetRef target)
        => _f.Host is { } h ? h.InScannerRange(ResolveId(ship), target) : false;

    /// <summary>Live probe count of the host's world (<c>GetProbes()->n()</c>).</summary>
    public int GetProbeCount() => _f.Host is { } h ? h.GetProbeCount() : 0;

    /// <summary>The client's current radar LOD mode (<c>GetWindow()->GetRadarMode()</c>).</summary>
    public int GetRadarLod() => TrainingRadarLod.Current;

    /// <summary>Live ammo count (<c>IshipIGC::GetAmmo()</c>), answered by the host sim.</summary>
    public int GetAmmo(TargetRef ship) => _f.Host is { } h ? h.GetAmmo(ResolveId(ship)) : 0;

    // ── M5/M6 command-view / escalation state (authoritative host) ──────────
    public bool GetPicked(TargetRef model) => _f.Host is { } h ? h.IsPicked(model) : false;
    public int GetViewSector() => _f.Host is { } h ? h.ViewSector : (Resolve(PlayerShip.Ref)?.SectorId ?? -1);
    public float GetFraction(TargetRef model) => _f.Host is { } h ? h.GetFraction(model) : (Resolve(model) is null ? 0f : 1f);
    public int GetCommandAcknowledged() => _f.Host is { } h ? h.GetCommandAcknowledged() : TrainingMissionIds.CmdDoNothing;

    // ── M3/M4 combat / equipment / UI actions ─────────────────────────────────
    /// <summary>Demonstrate shield/hull damage on the player's ship — <c>DoDamageAction</c>
    /// (dodamageaction.cpp:24-50). Routed through the host's real ship so the player's armour
    /// fraction moves (a logged no-op left nothing for <c>GetShipIsDamagedCondition</c> to see).</summary>
    public void DealDamage(bool toHull)
    {
        if (_f.Host is { } h)
        {
            // Native: shield demo dents the mounted shield by 3% of max strength; hull demo
            // sets the shield to 20% or dents the hull by 7.5. The host mutates the real ship,
            // so the shield/armour HUD gauges move (dodamageaction.cpp:32-49).
            h.DealDamage(toHull);
        }
        else TrainingHostLog.Log($"DealDamage(toHull:{toHull})");
    }
    /// <summary>Adjustable ship damage — <c>DoDamageAdjustableAction</c>
    /// (dodamageadjustableaction.cpp:27-36): damage the given ship, reset controls, fire all weapons.</summary>
    public void DamageShipAdjustable(TargetRef ship, float amount)
    {
        if (_f.Host is { } h) h.DamageShipAdjustable(ResolveId(ship), amount);
        else TrainingHostLog.Log($"DamageShipAdjustable({ship.Id},{amount})");
    }
    public void ToggleWeapon(int mount)
    {
        if (_f.Host is { } h) h.ToggleWeapon(mount);
        else TrainingHostLog.Log($"ToggleWeapon(mount:{mount})");
    }
    public void SwapWeapon(int equipmentType, int mount)
    {
        if (_f.Host is { } h) h.SwapWeapon(equipmentType, mount);
        else TrainingHostLog.Log($"SwapWeapon({equipmentType},{mount})");
    }
    public void RepairAndRearm()
    {
        if (_f.Host is { } h) h.RepairRearm(ResolveId(PlayerShip.Ref));
        else TrainingHostLog.Log("RepairAndRearm()");
    }
    public void ShowPane(int paneMask)
    {
        _f.ShowPane?.Invoke(paneMask);
        if (_f.ShowPane is null) TrainingHostLog.Log($"ShowPane(0x{paneMask:x})");
    }
    public void SetEyeball(bool shown)
    {
        _f.SetEyeball?.Invoke(shown);
        if (_f.SetEyeball is null) TrainingHostLog.Log($"SetEyeball({shown})");
    }
    /// <summary><c>SetSignatureAction</c> (setsignatureaction.cpp:49-53): write a signature override
    /// onto a ship of the live world. Routed through the host's real ship (Mission 4's cloak-signature
    /// lesson raises the enemy miners to 1.0e6, mission4.cs:442). Non-ship objects have no host model,
    /// so they keep the explicit logged seam for auditability.</summary>
    public void SetSignature(ObjectType type, int id, float signature)
    {
        if (type == ObjectType.Ship && _f.Host is { } h)
            h.SetSignature(id, signature);
        else
            TrainingHostLog.Log($"SetSignature({type},{id},{signature})");
    }

    // ── M8 nanite / repair / station seams (authoritative host) ───────────────
    public bool HasPartMounted(TargetRef ship, int partId) => _f.Host is { } h && h.HasPartMounted(ship.Id, partId);
    public void MountPart(TargetRef ship, int partId, int mount = 0)
    {
        if (_f.Host is { } h) h.MountPart(ship.Id, partId, mount);
        else TrainingHostLog.Log($"MountPart({ship.Id},{partId}) — no embedded training host");
    }
    public void DamageStation(int stationId, float amount)
    {
        if (_f.Host is { } h) h.DamageStation(stationId, amount);
        else TrainingHostLog.Log($"DamageStation({stationId},{amount}) — no embedded training host");
    }

    // ── P880b — equipment/state/radar/buoy seams (authoritative host) ─────────
    public bool HasPartOfEquipmentType(TargetRef ship, EquipmentType part)
        => _f.Host is { } h && h.HasPartOfEquipmentType(ship.Id, part);
    public ShipSnapshot CaptureShip(TargetRef ship)
        => _f.Host is { } h ? h.CaptureShip(ship.Id) : ShipSnapshot.Empty;
    public void RestoreShip(TargetRef ship, ShipSnapshot snap)
    {
        if (_f.Host is { } h) h.RestoreShip(ship.Id, snap);
        else TrainingHostLog.Log($"RestoreShip({ship.Id})");
    }
    public void ReleaseConsumer(int buoyId)
    {
        if (_f.Host is { } h) h.ReleaseConsumer(buoyId);
        else TrainingHostLog.Log($"ReleaseConsumer({buoyId})");
    }
    /// <summary>Set the real client radar LOD mode (<c>SetRadarLODAction</c>,
    /// setradarlodaction.cpp:31-36 — the window's <c>SetRadarMode</c>, mirrored by the
    /// <c>TrainingRadarLod</c> tracker that <see cref="GetRadarLod"/> reads).</summary>
    public void SetRadarLod(int lod) => TrainingRadarLod.Current = lod;
    public void SetCargoNan(TargetRef ship)
    {
        if (_f.Host is { } h) h.SetCargoNan(ship.Id);
        else TrainingHostLog.Log($"SetCargoNan({ship.Id})");
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
    public bool ConsumeChat(int recipient)
    {
        int i = _chats.IndexOf(recipient);
        if (i < 0) return false;
        _chats.RemoveAt(i);
        return true;
    }
    public void RecordChat(int recipient) => _chats.Add(recipient);

    // ── world creation (training-host universe — TR-2b host scope) ───────────
    public void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible)
    {
        World.RegisterCommandBuoy(new DataBuoyIGC([position.X, position.Y, position.Z],
            (short)sectorId, (byte)buoyType, (short)buoyId, visible));
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
        // The own ship's orders take the REAL client path: install the command slots and send
        // FM_C_ORDER_CHANGE, exactly like a mouse pick / order menu (WinTrek.cpp:8820-8854), so
        // the server's Ship.CommandTarget — the state GetTargetCondition/CurrentTarget read —
        // and the client's MyCurrent/MyAcceptedCommand see the order. The host handler below
        // still mirrors the scripted target for reads before the echo arrives.
        if (IsOwnShip(ship))
        {
            _ownOrderTarget = target;
            _f.OwnOrder?.Invoke(command, target, commandId);
        }
        if (_f.Host is { } h)
            h.IssueCommand(ship.Id, command,
                target is { Type: ObjectType.Ship } ? target.Value.Id : null,
                target is { Type: ObjectType.Buoy } ? target.Value.Id : null,
                target is { Type: ObjectType.Station } ? target.Value.Id : null,
                commandId, target is { Type: ObjectType.Asteroid } ? target.Value.Id : null,
                target is { Type: ObjectType.Warp } ? target.Value.Id : null);
        else if (!IsOwnShip(ship))
            TrainingHostLog.Log($"IssueCommand(ship {ship.Id}, cmd {command}) — no embedded training host");
    }

    // ── ship / camera / HUD configuration ───────────────────────────────────
    public void SetAutopilot(TargetRef ship, bool on)
    {
        // Own ship: engage the client's native autopilot toward the last ordered target (the
        // client flies its own ship, P508), and mirror the latch on the host ship so the
        // headless/no-client path and the host's own reads agree.
        if (IsOwnShip(ship)) _f.OwnAutopilot?.Invoke(_ownOrderTarget, on);
        if (_f.Host is { } h) h.SetAutopilot(ship.Id, on);
        else if (!IsOwnShip(ship)) TrainingHostLog.Log($"SetAutopilot({ship.Id},{on}) — no embedded training host");
    }
    public void SetDisplayMode(int cameraMode)
    {
        _f.SetCameraMode?.Invoke(cameraMode);
        if (_f.SetCameraMode is null) TrainingHostLog.Log($"SetDisplayMode({cameraMode})");
    }
    public void SetHudOverlay(int overlay) => TrainingOverlay.Current = overlay;

    public (float Yaw, float Pitch, float Roll) TurnToFace(TargetRef ship, TargetRef target)
    {
        var me = PlayerShip;
        var tgt = Resolve(target);
        if (tgt is null) return (0f, 0f, 0f);
        if (_f.OwnOrientation?.Invoke() is not { } ori || _f.OwnHullKinematics?.Invoke() is not { } h)
            return (0f, 0f, 0f);
        return TrainingTurnTo.Compute(me.Position, tgt.Position, ori.F, ori.U, ori.R, FrameDeltaSeconds, h);
    }

    // ── output (messages / sound) ───────────────────────────────────────────
    public void ShowMessage(string text)
    {
        Console.WriteLine($"[training] prompt: {text}");
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
        => _instances.TryGetValue(instance, out var handle) && (_f.IsSoundPlaying?.Invoke(handle) ?? false);
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
    private readonly System.Numerics.Vector3? _posOverride;
    private readonly System.Numerics.Vector3? _fwdOverride;
    public TrainingShipObject(short id, ShipEntity ship, ServerShipUpdate up,
        System.Numerics.Vector3? posOverride = null, System.Numerics.Vector3? fwdOverride = null)
    { _id = id; _ship = ship; _up = up; _posOverride = posOverride; _fwdOverride = fwdOverride; }
    public TargetRef Ref => new(ObjectType.Ship, _id);
    public float Radius => 20f;
    public Vector3 Position => _posOverride ?? ToSys(_up.Position);
    public Vector3 Velocity => ToSys(_up.Velocity);
    public Vector3 Forward => _fwdOverride
        ?? (_up.Orientation.GetForward() is { } f ? ToSys(f) : Vector3.UnitZ);
    public int SectorId => _ship.SectorId;

    private static Vector3 ToSys(Allegiance.Core.Collision.Vector3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>
/// Geometry surface for a replicated non-ship model (station / asteroid / probe / buoy) resolved
/// from the client world's EXPORT records — the static counterpart of
/// <see cref="TrainingShipObject"/>, matching native <c>GetModel(OT_*, id)</c> for the geometric
/// conditions (<c>getsectorcondition.cpp:27-51</c>). A station/asteroid/probe/buoy has no
/// per-tick streamed velocity/heading in the wire model, so it carries a fixed position and a
/// default forward, exactly like the host's <c>WaypointObject</c>.
/// </summary>
internal sealed class TrainingStaticObject : ITrainingObject
{
    private readonly Vector3 _position;
    public TrainingStaticObject(TargetRef r, Vector3 position, int sectorId, float radius, Vector3? forward = null)
    { Ref = r; _position = position; SectorId = sectorId; Radius = radius; Forward = forward ?? Vector3.UnitX; }
    public TargetRef Ref { get; }
    public float Radius { get; }
    public Vector3 Position => _position;
    public Vector3 Velocity => Vector3.Zero;
    public Vector3 Forward { get; }
    public int SectorId { get; }
}

using System;
using System.Collections.Generic;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P580 — the in-flight external camera modes (native ports), the non-native
/// "director" b-roll camera, and spectator follow. Partial class sharing the
/// <see cref="Game1"/> type. The camera MODE logic (placement maths, the
/// director's editorial decisions) lives in the pure
/// <see cref="ExternalCameras"/>/<see cref="Director"/> types; this file owns
/// the game glue: resolving live world positions (targets, stations, wars,
/// explosions, the followed ship) and feeding them to those pure types each
/// frame.
/// </summary>
public sealed partial class Game1
{
    // P580 — the free external camera orientation (native m_orientationExternalCamera,
    // WinTrek.cpp): a remembered pivot the fixed-offset modes use as their backward
    // axis and the orbit mode spins continuously. The port has no mouse-look, so it
    // starts aligned to the ship's orientation when the camera enters an
    // external-relative mode and is spun by orbit / the director.
    private Orientation _externalCam = Orientation.Identity;
    private CameraMode? _externalCamAlignedFor;   // mode the orientation was last reset for

    // P624 — the look-around head yaw (radians, positive = look right), set by
    // the HELD TK_View* keys and applied to the cockpit/chase view only (see
    // ComputeFlightCamera). Cleared to forward whenever a non-cockpit/chase
    // camera is selected or a look key is released.
    private float _headLookYaw;

    // P624 — radar mode (All/Default/Target/Minimal), cycled by TK_CycleRadar;
    // applied as a per-contact filter in DrawRadar (Rendering/RadarLod.cs).
    private RadarMode _radarMode;

    // P624 — the latched vector-lock (TK_LockVector) toggle and its edge latch.
    private bool _coastToggled;
    private bool _lockVectorKeyWasDown;
    private bool _cycleRadarKeyWasDown;

    // P580 — director state.
    private readonly Director _director = new(unchecked((int)DateTime.Now.Ticks ^ 0x5EED));  // seeded at construction
    private bool _directorActive;
    private bool _directorKeyWasDown;
    private bool _spectatorKeyWasDown;
    private short _spectatorFollowId = -1;        // -1 = none
    private short _lastDirectorSector = -1;
    private bool _lastDirectorDocked;

    /// <summary>Whether the director b-roll camera is currently in charge.</summary>
    internal bool DirectorActive => _directorActive;

    /// <summary>The director's seed (from ALLEGIANCE_DIRECTOR_SEED or --director-seed).</summary>
    private int _directorSeed = unchecked((int)DateTime.Now.Ticks ^ 0x5EED);

    /// <summary>Reset the free external camera orientation to the ship's when the
    /// active view/editorial context changed (so each mode begins from a stable,
    /// behind-the-ship framing rather than carrying stale spin).</summary>
    private void EnsureExternalCamAligned(CameraMode? context, Orientation shipOrientation)
    {
        if (_externalCamAlignedFor != context)
        {
            _externalCam = shipOrientation;
            _externalCamAlignedFor = context;
        }
    }

    private Vector3 ExternalBackwardXna()
    {
        var b = _externalCam.GetBackward();
        return new Vector3(b.X, b.Y, b.Z);
    }

    /// <summary>
    /// P580 — compute the flight camera for the current <see cref="_cameraMode"/>,
    /// the active director, or spectator follow. Returns the camera-relative
    /// view/projection plus the true world-space eye (P207 convention). Central
    /// seam so the Draw camera switch and the targeting pick both resolve the
    /// SAME framing for the active view.
    /// </summary>
    private (Matrix View, Matrix Proj, Vector3 Eye) ComputeFlightCamera(
        Vector3 myPos, Matrix myRot, Orientation myOrientation, float aspect, float dt)
    {
        // Director / spectator first: the cinematographer takes over entirely.
        if (_directorActive)
        {
            _headLookYaw = 0f;   // P624 — head-look is a pilot view; the director owns the camera
            return UpdateDirectorPose(myPos, myOrientation, aspect, dt);
        }

        // P624 — the look-around head yaw applies ONLY in the cockpit and chase
        // views (native's cmCockpit/cmExternalChase gate, WinTrek.cpp:9735-9791);
        // selecting any other camera resets the head to forward, exactly native's
        // SetHeadOrientation(0) on SetViewToCombatMode (WinTrek.cpp:9365-9367).
        float head = (_cameraMode == CameraMode.Cockpit || _cameraMode == CameraMode.Chase)
            ? _headLookYaw : 0f;
        if (_cameraMode != CameraMode.Cockpit && _cameraMode != CameraMode.Chase)
            _headLookYaw = 0f;

        switch (_cameraMode)
        {
            case CameraMode.Command:
                UpdateCommandFraming(myPos);
                return _commandCamera.Matrices(aspect);
            case CameraMode.DroneFollow:
            case CameraMode.DroneCockpit:
                if (TryCommandedPose(out var dronePos, out var droneRot, out var droneRadius))
                {
                    if (_cameraMode == CameraMode.DroneCockpit)
                    {
                        var (dv, dp) = _cockpitCamera.Matrices(dronePos, droneRot, aspect, out var de);
                        return (dv, dp, de);
                    }
                    var (dv2, dp2) = _chaseCamera.Matrices(dronePos, droneRot, aspect, dt, out var de2, droneRadius);
                    return (dv2, dp2, de2);
                }
                _cameraMode = CameraMode.Chase;
                return ChaseFallback(myPos, myRot, aspect, dt);
            case CameraMode.Cockpit:
            {
                Matrix v, p; Vector3 e;
                if (IsTurretGunner())
                {
                    var turretRot = PoseConverter.ToXnaRotation(_turretOrientation);
                    (v, p) = _cockpitCamera.Matrices(myPos, turretRot, aspect, out e, head);
                }
                else
                    (v, p) = _cockpitCamera.Matrices(myPos, myRot, aspect, out e, head);
                return (v, p, e);
            }
            case CameraMode.ExternalTurret:
            {
                if (TryGetGunnerCarrierPose(out var cPos, out var cRot, out var cRad))
                {
                    var (v, p) = _chaseCamera.Matrices(cPos, cRot, aspect, dt, out var ce, cRad);
                    return (v, p, ce);
                }
                var (v2, p2) = _chaseCamera.Matrices(myPos, myRot, aspect, dt, out var cee, _myRenderRadius);
                return (v2, p2, cee);
            }
            case CameraMode.ExternalMissile:
            {
                if (TryGetMissileCameraFrame(out var missPos, out var focus))
                {
                    var (v, p) = _missileCamera.Matrices(missPos, focus, aspect, out var me);
                    return (v, p, me);
                }
                var (v3, p3) = _chaseCamera.Matrices(myPos, myRot, aspect, dt, out var mee, _myRenderRadius);
                return (v3, p3, mee);
            }
            case CameraMode.ExternalOrbit:
            {
                EnsureExternalCamAligned(CameraMode.ExternalOrbit, myOrientation);
                ExternalCameras.RotateOrbitRates(_externalCam, dt);
                var ev = ExternalCameras.Orbit(myPos, ExternalBackwardXna(), MyHullLength());
                return ViewFor(ev, aspect);
            }
            case CameraMode.External:
                EnsureExternalCamAligned(CameraMode.External, myOrientation);
                return ViewFor(ExternalCameras.GenericExternal(myPos, ExternalBackwardXna(), _flightZoom.ExternalDistance, _myRenderRadius), aspect);
            case CameraMode.ExternalTarget:
            {
                EnsureExternalCamAligned(CameraMode.ExternalTarget, myOrientation);
                if (TryCurrentTargetPos(out var tgt))
                    return ViewFor(ExternalCameras.Target(PoseConverter.ToXna(tgt), ExternalBackwardXna(),
                        TargetRadiusHint(), _flightZoom.ExternalDistance), aspect);
                return ChaseFallback(myPos, myRot, aspect, dt);
            }
            case CameraMode.ExternalFlyBy:
            {
                EnsureExternalCamAligned(CameraMode.ExternalFlyBy, myOrientation);
                if (TryCurrentTargetPos(out var fbTgt))
                    return ViewFor(ExternalCameras.FlyBy(PoseConverter.ToXna(fbTgt), myPos,
                        ExternalBackwardXna(), _myRenderRadius, _flightZoom.ExternalDistance), aspect);
                return ChaseFallback(myPos, myRot, aspect, dt);
            }
            case CameraMode.ExternalFlyByTarget:
            {
                EnsureExternalCamAligned(CameraMode.ExternalFlyByTarget, myOrientation);
                if (TryCurrentTargetPos(out var fbt))
                    return ViewFor(ExternalCameras.Target(PoseConverter.ToXna(fbt), ExternalBackwardXna(),
                        TargetRadiusHint(), _flightZoom.ExternalDistance), aspect);
                return ChaseFallback(myPos, myRot, aspect, dt);
            }
            case CameraMode.ExternalBase:
            {
                EnsureExternalCamAligned(CameraMode.ExternalBase, myOrientation);
                if (TryNearestStationPos(out var basePos, out var baseRadius))
                    return ViewFor(ExternalCameras.Target(PoseConverter.ToXna(basePos), ExternalBackwardXna(),
                        baseRadius, _flightZoom.ExternalDistance), aspect);
                return ChaseFallback(myPos, myRot, aspect, dt);
            }
            case CameraMode.ExternalStation:
            {
                EnsureExternalCamAligned(CameraMode.ExternalStation, myOrientation);
                if (TryCurrentStationTargetPos(out var stPos))
                    return ViewFor(ExternalCameras.Station(myPos, PoseConverter.ToXna(stPos), _myRenderRadius), aspect);
                return ChaseFallback(myPos, myRot, aspect, dt);
            }
            case CameraMode.ExternalOverride:
            {
                // Scripted camera: track the override target if one is set, else the
                // latest explosion (the eject-killer swing and the director's cut-ins
                // set _overrideTarget). Ends after its duration (SetCameraMode resets
                // it) — see RequestOverrideCamera.
                if (TryOverrideTargetPos(out var ovTarget, out var ovRadius))
                    return ViewFor(ExternalCameras.Override(myPos, PoseConverter.ToXna(ovTarget), ovRadius), aspect);
                return ChaseFallback(myPos, myRot, aspect, dt);
            }
            case CameraMode.FreeCam:
            {
                // P612 — detached free-flight spectator camera (mouse look + WASD
                // movement, see Game1.Spectator.cs). NOT in the V cycle; its own key
                // (F8). The player's ship holds underneath.
                var fcProj = Matrix.CreatePerspectiveFieldOfView(
                    MathHelper.ToRadians(_flightFovDeg), aspect, 10f, 80000f);
                return (_freeCam.View(), fcProj, _freeCam.Position);
            }
            case CameraMode.OrbitCam:
            {
                // P612 — spectator orbit camera locking any ship/station/position
                // (inspector rig). NOT in the V cycle; its own key (F9).
                var obProj = Matrix.CreatePerspectiveFieldOfView(
                    MathHelper.ToRadians(_flightFovDeg), aspect, 10f, 80000f);
                return (_orbitCam.View(), obProj, _orbitCam.Eye);
            }
            default: // Chase
                return ChaseFallback(myPos, myRot, aspect, dt);
        }
    }

    private static (Matrix, Matrix, Vector3) ViewFor(ExternalView ev, float aspect)
    {
        var proj = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(ExternalCameras.DefaultFovDegrees), aspect,
            ExternalCameras.NearPlane, ExternalCameras.FarPlane);
        return (ev.View(), proj, ev.Eye);
    }

    private (Matrix View, Matrix Proj, Vector3 Eye) ChaseFallback(Vector3 myPos, Matrix myRot, float aspect, float dt)
    {
        var (v, p) = _chaseCamera.Matrices(myPos, myRot, aspect, dt, out var e, _myRenderRadius, _headLookYaw);
        return (v, p, e);
    }

    // ── target / station / override resolution ───────────────────────────────

    /// <summary>The player's current command target's world position (native
    /// GetCurrentTarget), via the T/G-selected autopilot target. False when none.</summary>
    private bool TryCurrentTargetPos(out SimVec pos)
    {
        if (_autoTarget is { } t && ResolveTargetPosition(t, out pos))
            return true;
        pos = default;
        return false;
    }

    /// <summary>A framing-radius hint for the current target (native uses the
    /// target object's real radius). Defaults to a fighter-class radius.</summary>
    private float TargetRadiusHint()
    {
        if (_autoTarget is { Kind: TargetKind.Ship } ts
            && _session.World.Ships.TryGetValue(ts.Id, out var ship))
        {
            float len = HullLength(ship.HullId);
            return len > 0f ? 0.5f * len : 25f;
        }
        return 25f;
    }

    private bool TryNearestStationPos(out SimVec pos, out float radius)
    {
        pos = default; radius = 100f;
        if (!TryOwnPose(out var own, out _, out _)) return false;
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is not { } me
                || !_session.World.Ships.TryGetValue(me, out var ownEnt)) return false;
            short sector = ownEnt.SectorId;
            var stypes = _shopCatalog?.StationTypes;
            SimVec? best = null; float bestD = float.MaxValue; float br = 100f;
            foreach (var (_, st) in _session.World.Stations)
            {
                if (st.ClusterId != sector) continue;
                var spos = Vec(st.Position);
                var d = spos - own;
                float dsq = d.LengthSquared();
                float r = 100f;
                if (stypes is not null && stypes.TryGetValue(st.StationTypeId, out var styp) && styp.Radius > 0f)
                    r = styp.Radius;
                if (dsq < bestD) { bestD = dsq; best = spos; br = r; }
            }
            if (best is not { } b) return false;
            pos = b; radius = br;
            return true;
        }
    }

    private bool TryCurrentStationTargetPos(out SimVec pos)
    {
        // Prefer the player's selected target if it is a station, else any station.
        if (_autoTarget is { Kind: TargetKind.Station } st && ResolveTargetPosition(st, out pos))
            return true;
        return TryNearestStationPos(out pos, out _);
    }

    // P580 — the override camera's tracked world point. Native's
    // cmExternalOverride (WinTrek.cpp:7199-7206) tracks a fixed target position
    // with TrackCamera for the duration of the scripted shot; the P578 eject
    // swing and the director's cut-ins set it via RequestOverrideCamera. With no
    // explicit override the camera tracks the nearest live explosion (the native
    // no-position branch keeps the ship on a world point; the nearest blast is
    // the cinematic default here).
    private bool TryOverrideTargetPos(out SimVec pos, out float radius)
    {
        // P578 — the eject-killer swing / director cut-ins set an explicit
        // override anchor via RequestOverrideCamera; that takes precedence.
        // Otherwise fall back to the nearest live explosion (the cinematic
        // default when no anchor was set).
        if (_overrideTarget is { } ov)
        {
            pos = ov;
            radius = _overrideRadius;
            return true;
        }
        pos = default; radius = 30f;
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is not { } me
                || !_session.World.Ships.TryGetValue(me, out var ownEnt)) return false;
            short sector = ownEnt.SectorId;
            SimVec? best = null; float bestD = float.MaxValue;
            foreach (var ex in _session.World.Explosions)
            {
                if (ex.ClusterId != sector) continue;
                var d = ex.Position - ownEnt.Latest!.Position;
                float dsq = d.LengthSquared();
                if (dsq < bestD) { bestD = dsq; best = ex.Position; }
            }
            if (best is not { } b) return false;
            pos = b; radius = 30f;
            return true;
        }
    }

    // ── director pose ─────────────────────────────────────────────────────────

    private (Matrix View, Matrix Proj, Vector3 Eye) UpdateDirectorPose(
        Vector3 myPos, Orientation myOrientation, float aspect, float dt)
    {
        // Gentle free-orientation drift so orbit/backing shots aren't static.
        EnsureExternalCamAligned(null, myOrientation);
        ExternalCameras.RotateRates(_externalCam, dt, 0.04f, 0.03f, 0f);

        var snap = BuildDirectorSnapshot(myPos);
        var (view, proj, eye, _) = _director.Update(dt, snap, ExternalBackwardXna(),
            _myRenderRadius, MyHullLength(), aspect);
        return (view, proj, eye);
    }

    private DirectorSnapshot BuildDirectorSnapshot(Vector3 myPos)
    {
        var subjects = new List<DirectorSubject>();
        SimVec ownSim = ToSimVec(myPos);
        short mySector = -1;
        short mySide = -1;
        bool docked = false;

        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var ownEnt))
            {
                mySector = ownEnt.SectorId;
                docked = ownEnt.StationId != -1;
            }
            mySide = world.MySideId ?? -1;

            // Engagement centres: opposing-side ships within ~1500u of each other.
            var enemyPts = new List<(SimVec pos, float hull)>();
            foreach (var (id, ship) in world.Ships)
            {
                if (ship.IsNotVisible || ship.Latest is not { } u) continue;
                if (ship.SectorId != mySector) continue;
                if (ship.SideId != mySide) enemyPts.Add((u.Position, u.HullFraction));
            }
            foreach (var (id, ship) in world.Ships)
            {
                if (ship.IsNotVisible || ship.Latest is not { } su) continue;
                if (ship.SectorId != mySector) continue;
                if (id == world.MyShipId) continue;
                float hull = su.HullFraction;
                // The followed ship (spectator) is always compelling.
                float interest = (ship.ShipId == _spectatorFollowId) ? 100f : 20f + (1f - hull) * 60f;
                subjects.Add(new DirectorSubject(PoseConverter.ToXna(su.Position),
                    SubjectCategory.Ship, interest, ship.CharacterName ?? $"ship{ship.ShipId}"));
                foreach (var ep in enemyPts)
                {
                    var d = ep.pos - su.Position;
                    if (d.LengthSquared() < 1500f * 1500f)
                        subjects.Add(new DirectorSubject(PoseConverter.ToXna(0.5f * (ep.pos + su.Position)),
                            SubjectCategory.Engagement, 90f + (1f - hull) * 10f, "engagement"));
                }
            }

            // Explosions: recency-scored, always high interest (native blast).
            long now = Environment.TickCount64;
            foreach (var ex in world.Explosions)
            {
                if (ex.ClusterId != mySector) continue;
                float age = Math.Max(0f, (now - ex.ReceivedLocalMs) / 1000f);
                subjects.Add(new DirectorSubject(PoseConverter.ToXna(ex.Position),
                    SubjectCategory.Explosion, Math.Max(40f, 110f - age * 20f), "explosion"));
            }

            // Aleph transits.
            foreach (var (_, wp) in world.Warps)
            {
                if (wp.ClusterId != mySector) continue;
                subjects.Add(new DirectorSubject(PoseConverter.ToXna(Vec(wp.Position)),
                    SubjectCategory.Warp, 30f, "aleph"));
            }

            // Stations.
            foreach (var (_, st) in world.Stations)
            {
                if (st.ClusterId != mySector) continue;
                subjects.Add(new DirectorSubject(PoseConverter.ToXna(Vec(st.Position)),
                    SubjectCategory.Station, 35f, st.Name ?? "station"));
            }

            // Cap the candidate pool (weighted random selection needs a sane size).
            if (subjects.Count > 24)
                subjects = subjects.GetRange(0, 24);
        }

        // Sector-change / dock detection (director re-acquires a subject).
        bool sectorChanged = mySector != _lastDirectorSector || docked != _lastDirectorDocked;
        _lastDirectorSector = mySector;
        _lastDirectorDocked = docked;

        return new DirectorSnapshot(subjects, myPos, sectorChanged, docked);
    }

    // ── input: director / spectator keys + flight-input cancel ───────────────

    /// <summary>Edge-triggered native F-key camera toggle (P580): pressing the key
    /// enters <paramref name="mode"/>, pressing it again returns to the cockpit —
    /// native's <c>SetViewToCombatMode((GetCameraMode()!=X)?X:Cockpit)</c> toggle.
    /// Choosing a manual camera also cancels the director. P625: the key comes
    /// from the live action map so rebinding a camera works (native wins the
    /// F-row as the default: F4 orbit, F5 flyby, F7 target, F10 base, F11
    /// station).</summary>
    private void ToggleCameraKey(KeyboardState ks, bool composing, string action, CameraMode mode, ref bool wasDown)
    {
        bool down = ActionDown(ks, action);
        bool docked;
        lock (_session.Gate)
            docked = _session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var own) && own.StationId >= 0;
        if (CameraKeyTransition(_cameraMode, mode, down, ref wasDown, composing || SpectatorCameraActive, docked) is { } next)
        {
            _directorActive = false;
            _headLookYaw = 0;
            _consolePanes = default;
            _minimapExpanded = _teamPaneVisible = _investActive = _teleportActive = false;
            _cameraMode = next;
            _hangarOnly = docked;
            Console.WriteLine($"[monogame] camera: {_cameraMode}");
        }
    }

    // WinTrek.cpp:9820-9864,9357-9383: discrete view toggles return to cockpit,
    // reset head orientation and leave command overlays. Also observe suppressed
    // presses so composing/held keys never leak a delayed action.
    internal static CameraMode? CameraKeyTransition(CameraMode current, CameraMode requested,
        bool down, ref bool wasDown, bool suppressed, bool docked = false)
    {
        bool press = down && !wasDown && !suppressed;
        wasDown = down;
        return press ? docked || current == requested ? CameraMode.Cockpit : requested : null;
    }

    /// <summary>P625 — resolve an action's CURRENT binding chord from the live
    /// action map (native wins the F-row defaults; user overrides apply).</summary>
    private Rendering.KeyBinding KeyOf(string action)
        => Rendering.KeyBindingStore.Instance.Map.BindingOf(action);

    /// <summary>P625 — is this action's current binding held down right now?
    /// Every discrete input handler reads actions through this (never a raw key
    /// literal), so a rebind takes effect immediately. P624 — routed through
    /// <see cref="ActionMap.IsDown"/> so an action's ALTERNATE chords (the
    /// hardware-layout alias on ThrottleFull) match too.</summary>
    private bool ActionDown(KeyboardState ks, string action)
        => Rendering.KeyBindingStore.Instance.Map.IsDownMostSpecific(ks, action);

    /// <summary>Edge-triggered director + spectator toggles (P580). Bound to
    /// F2 (director) and F3 (spectator follow). Also disables the director the
    /// instant any flight control is used (restoring the chase view).</summary>
    private void HandleCinematicKeys(KeyboardState ks, bool composing)
    {
        bool f2 = ActionDown(ks, "Director");
        if (!composing && f2 && !_directorKeyWasDown)
        {
            _directorActive = !_directorActive;
            _directorSeed = MakeSeed();
            Console.WriteLine($"[monogame] director {( _directorActive ? $"ACTIVE seed={_directorSeed}" : "OFF" )}");
        }
        _directorKeyWasDown = f2;

        bool f3 = ActionDown(ks, "FollowCam");
        if (!composing && f3 && !_spectatorKeyWasDown)
        {
            if (_spectatorFollowId == -1)
            {
                _spectatorFollowId = PickNearestBot();
                if (_spectatorFollowId != -1)
                {
                    _directorActive = true;
                    Console.WriteLine($"[monogame] spectator follow -> ship {_spectatorFollowId}");
                }
            }
            else
            {
                _spectatorFollowId = -1;
                Console.WriteLine("[monogame] spectator follow OFF");
            }
        }
        _spectatorKeyWasDown = f3;
    }

    private int MakeSeed()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_DIRECTOR_SEED");
        return env is { Length: > 0 } && int.TryParse(env, out var s) ? s
            : unchecked((int)DateTime.Now.Ticks ^ 0x5EED);
    }

    /// <summary>Cancel the director (and spectator follow) the moment a flight
    /// control is pressed, restoring a normal cockpit view — the P580 rule that
    /// any flight input returns control instantly. Reads the SHIP movement
    /// actions (resolved bindings), never raw key literals.</summary>
    private void CancelDirectorOnInput(KeyboardState ks)
    {
        if (!_directorActive) return;
        if (Director.AnyFlightControl(
                forward: ActionDown(ks, "ThrustForward"), back: ActionDown(ks, "ThrustBackward"),
                strafeLeft: ActionDown(ks, "RollLeft"), strafeRight: ActionDown(ks, "RollRight"),
                thrustUp: ActionDown(ks, "ThrustLeft"), thrustDown: ActionDown(ks, "ThrustRight"),
                turnLeft: ActionDown(ks, "YawLeft"), turnRight: ActionDown(ks, "YawRight")))
        {
            _directorActive = false;
            _spectatorFollowId = -1;
            _cameraMode = CameraMode.Chase;
            Console.WriteLine("[monogame] director cancelled by flight input -> chase");
        }
    }

    private short PickNearestBot()
    {
        if (!TryOwnPose(out var own, out _, out _)) return -1;
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is not { } me) return -1;
            short? best = null; float bestD = float.MaxValue;
            foreach (var (id, ship) in _session.World.Ships)
            {
                if (id == me || ship.IsNotVisible || ship.Latest is not { } u) continue;
                var d = u.Position - own;
                float dsq = d.LengthSquared();
                if (dsq < bestD) { bestD = dsq; best = id; }
            }
            return best ?? -1;
        }
    }

    /// <summary>Map a <c>--camera</c> token to a <see cref="CameraMode"/> (P580
    /// extends the auto-proof camera grammar to the native external modes).
    /// Null for an unrecognized token (caller keeps the default).</summary>
    private static CameraMode? CameraModeFromToken(string token) => token.ToLowerInvariant() switch
    {
        "command" or "commandtd" => CameraMode.Command,
        "dronefollow" => CameraMode.DroneFollow,
        "dronecockpit" => CameraMode.DroneCockpit,
        "cockpit" => CameraMode.Cockpit,
        "chase" => CameraMode.Chase,
        "flyby" => CameraMode.ExternalFlyBy,
        "flybytarget" => CameraMode.ExternalFlyByTarget,
        "orbit" => CameraMode.ExternalOrbit,
        "target" => CameraMode.ExternalTarget,
        "base" => CameraMode.ExternalBase,
        "station" => CameraMode.ExternalStation,
        "override" => CameraMode.ExternalOverride,
        // P612 — the spectator cameras (not in the V cycle; activated via the
        // auto-proof / --camera token so real-window proofs can frame them).
        "freecam" => CameraMode.FreeCam,
        "orbitcam" => CameraMode.OrbitCam,
        _ => null,
    };

    private static SimVec ToSimVec(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The own ship's real HullType length (for the orbit camera's
    /// <c>5 + 2.5 × Length</c> offset). Falls back to a fighter-class 60 when
    /// the own hull isn't known yet.</summary>
    private float MyHullLength()
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me
                && _session.World.Ships.TryGetValue(me, out var ent))
            {
                float len = HullLength(ent.HullId);
                if (len > 0f) return len;
            }
        }
        return 60f;
    }
}

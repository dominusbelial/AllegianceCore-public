using System;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P612 — the spectator cameras (free-flight and orbit), the smooth camera
/// transition layer, and the HUD hide toggle. Partial class sharing the
/// <see cref="Game1"/> type.
/// <list type="bullet">
/// <item><b>FreeCam</b> (F8): a detached 6-DOF rig (<see cref="FreeCam"/>) with
/// mouse look and WASD/QE movement that roams the sector; the player's ship
/// holds (neutral controls) underneath it. Admins may additionally switch
/// sectors via the starmap, streaming the requested sector through the
/// existing <c>FM_C_VIEW_CLUSTER(104)</c> request.</item>
/// <item><b>OrbitCam</b> (F9): locks onto a ship/station/position using the
/// inspector's <see cref="InspectorOrbitCamera"/> rig (mouse orbit, wheel zoom,
/// auto-orbit; PageUp cycles the locked subject among nearby ships).</item>
/// <item><b>Transitions</b>: when the camera subject or framing changes (mode
/// switch, target change, missile-cam start/end), the pose eases from the
/// previous framing to the new one with <see cref="CameraEasing"/> (critically
/// damped, ~0.7 s) instead of cutting; an "instant" setting restores the cut.</item>
/// <item><b>HUD toggle</b> (F1): hides the HUD overlays for clean frames.</item>
/// </list>
/// </summary>
public sealed partial class Game1
{
    // ── free-flight spectator camera ────────────────────────────────────────
    private readonly FreeCam _freeCam = new();
    private bool _freeCamActive;
    private bool _freeCamKeyWasDown;

    // ── orbit spectator camera ──────────────────────────────────────────────
    private readonly InspectorOrbitCamera _orbitCam = new();
    private bool _orbitCamActive;
    private bool _orbitCamKeyWasDown;
    private bool _orbitAuto = true;
    private short _orbitSubjectId = -1;       // -1 = none locked
    private bool _orbitPgUpWasDown;

    // ── HUD hide toggle ─────────────────────────────────────────────────────
    private bool _hudHidden;
    private bool _hudKeyWasDown;

    // mouse capture bookkeeping for the spectator cameras
    private bool _specMouseInit;
    private int _specPrevMouseX, _specPrevMouseY, _specPrevScroll;

    // ── camera transition state (P612 smoothing) ────────────────────────────
    private bool _camSmoothInit;
    private CameraPose _camDisplay;           // pose actually shown last frame
    private CameraPose _camTransFrom;
    private bool _camTransActive;
    private double _camTransElapsed;
    private double _camTransDuration = 0.7;   // named transition duration (s)
    private string _lastFramingKey = "";

    /// <summary>Whether either spectator camera is active.</summary>
    private bool SpectatorCameraActive => _freeCamActive || _orbitCamActive;

    /// <summary>P612 — "instant" transition setting (native purists): no easing,
    /// the camera cuts. Off by default; enabled via ALLEGIANCE_INSTANT_CAMERA=1.</summary>
    private bool _instantCameraTransitions;

    private void InitSpectatorCameras()
    {
        _instantCameraTransitions = Environment.GetEnvironmentVariable("ALLEGIANCE_INSTANT_CAMERA") == "1";
    }

    /// <summary>Resolve the camera's framing subject (a ship) for the free cam's
    /// starting position and the orbit cam's lock. Reads the own ship's live
    /// pose under the gate lock.</summary>
    private bool TryOwnSpectatorPose(out Vector3 pos)
    {
        pos = default;
        if (TryOwnPose(out var sim, out _, out _))
        {
            pos = PoseConverter.ToXna(sim);
            return true;
        }
        return false;
    }

    /// <summary>Toggle the free-flight spectator camera on/off (F8). Entering
    /// positions the rig near the player's ship looking forward; leaving
    /// returns to the chase view (one-key return-to-ship).</summary>
    private void ToggleFreeCam()
    {
        if (_freeCamActive)
        {
            _freeCamActive = false;
            _freeCam.ResetVelocity();
            _cameraMode = CameraMode.Chase;
            Console.WriteLine("[monogame] free cam OFF -> chase");
            return;
        }
        // Deactivate any other spectator camera first.
        _orbitCamActive = false;
        if (TryOwnSpectatorPose(out var own))
        {
            var fwd = _ownShipPrediction && _ownSim.HaveHull ? _ownSim.Orientation.GetForward() : default;
            var dir = new Vector3(fwd.X, fwd.Y, fwd.Z);
            if (dir.LengthSquared() < 1e-6f) dir = Vector3.Forward;
            dir.Normalize();
            // start a few ship-lengths behind and above the ship, facing it.
            _freeCam.Position = own - dir * 120f + Vector3.Up * 60f;
            // face the ship: compute yaw/pitch toward it
            var toShip = Vector3.Normalize(own - _freeCam.Position);
            _freeCam.Yaw = MathF.Atan2(toShip.X, toShip.Z);
            _freeCam.Pitch = MathF.Asin(Math.Clamp(toShip.Y, -1f, 1f));
        }
        else
        {
            _freeCam.Teleport(Vector3.Zero);
        }
        _freeCamActive = true;
        _cameraMode = CameraMode.FreeCam;
        Console.WriteLine("[monogame] FREE CAM ACTIVE (F8)");
    }

    /// <summary>Toggle the orbit spectator camera on/off (F9). Entering locks
    /// onto the nearest ship (or the current target if any).</summary>
    private void ToggleOrbitCam()
    {
        if (_orbitCamActive)
        {
            _orbitCamActive = false;
            _orbitSubjectId = -1;
            _cameraMode = CameraMode.Chase;
            Console.WriteLine("[monogame] orbit cam OFF -> chase");
            return;
        }
        _freeCamActive = false;
        _orbitCamActive = true;
        _orbitAuto = true;
        // lock onto the current target if it is a ship, else nearest ship.
        if (_autoTarget is { Kind: TargetKind.Ship } ts)
            _orbitSubjectId = ts.Id;
        else
            _orbitSubjectId = PickNearestOrbitSubject();
        if (_orbitSubjectId != -1)
        {
            CenterOrbitOnSubject(_orbitSubjectId);
        }
        else if (TryOwnSpectatorPose(out var own))
        {
            _orbitCam.Focus(own);
            _orbitCam.Reset(500f);
        }
        _cameraMode = CameraMode.OrbitCam;
        Console.WriteLine($"[monogame] ORBIT CAM ACTIVE (F9) subject={_orbitSubjectId}");
    }

    /// <summary>Nearest ship (excluding own) to lock the orbit camera on.</summary>
    private short PickNearestOrbitSubject()
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

    /// <summary>Repoint the orbit rig at the given ship's live position.</summary>
    private void CenterOrbitOnSubject(short id)
    {
        lock (_session.Gate)
        {
            if (_session.World.Ships.TryGetValue(id, out var ship) && ship.Latest is { } u)
            {
                var pos = PoseConverter.ToXna(u.Position);
                float len = HullLength(ship.HullId);
                float radius = len > 0f ? len : 60f;
                _orbitCam.Focus(pos);
                _orbitCam.FrameRadius(radius, MathHelper.ToRadians(50f));
                return;
            }
        }
    }

    /// <summary>Cycle the orbit camera's locked subject through the ships of the
    /// player's sector (nearest first, wrapping). PageUp while orbit-active.</summary>
    private void CycleOrbitSubject()
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } me) return;
            // collect in-sector ships sorted by distance to current subject (or own)
            SimVec refPos = default;
            if (_orbitSubjectId != -1 && world.Ships.TryGetValue(_orbitSubjectId, out var cur) && cur.Latest is { } cu)
            { refPos = cu.Position; }
            else if (world.Ships.TryGetValue(me, out var own) && own.Latest is { } ou)
            { refPos = ou.Position; }

            var cands = new List<short>();
            foreach (var (id, ship) in world.Ships)
            {
                if (id == me || ship.IsNotVisible || ship.Latest is not { }) continue;
                cands.Add(id);
            }
            if (cands.Count == 0) { _orbitSubjectId = -1; return; }
            // sort by distance to reference, then find next after current subject
            cands.Sort((a, b) =>
            {
                float da = DistSq(world, a, refPos);
                float db = DistSq(world, b, refPos);
                return da.CompareTo(db);
            });
            int idx = _orbitSubjectId != -1 ? cands.IndexOf(_orbitSubjectId) : -1;
            int next = (idx + 1) % cands.Count;
            _orbitSubjectId = cands[next];
        }
        CenterOrbitOnSubject(_orbitSubjectId);
        Console.WriteLine($"[monogame] orbit subject -> ship {_orbitSubjectId}");
    }

    private static float DistSq(Allegiance.ClientCore.State.ClientWorld world, short id, SimVec refPos)
    {
        if (world.Ships.TryGetValue(id, out var s) && s.Latest is { } u)
            return (u.Position - refPos).LengthSquared();
        return float.MaxValue;
    }

    /// <summary>Toggle the HUD hide (F1) — hides the HUD overlays for clean frames.</summary>
    private void ToggleHudHide()
    {
        _hudHidden = !_hudHidden;
        Console.WriteLine(_hudHidden ? "[monogame] HUD HIDDEN (F1)" : "[monogame] HUD shown");
    }

    /// <summary>F8/F9/F1 toggle edge detection plus the free-cam movement / orbit
    /// mouse. Called each flying frame from <c>UpdateFlying</c> (after the F-key
    /// camera toggles).</summary>
    private void HandleSpectatorKeys(KeyboardState ks, bool composing, double dt)
    {
        // F8 free cam
        bool f8 = ActionDown(ks, "FreeCam");
        if (!composing && f8 && !_freeCamKeyWasDown)
            ToggleFreeCam();
        _freeCamKeyWasDown = f8;

        // F9 orbit cam
        bool f9 = ActionDown(ks, "OrbitCam");
        if (!composing && f9 && !_orbitCamKeyWasDown)
            ToggleOrbitCam();
        _orbitCamKeyWasDown = f9;

        // F1 HUD hide
        bool f1 = ActionDown(ks, "ToggleHud");
        if (!composing && f1 && !_hudKeyWasDown)
            ToggleHudHide();
        _hudKeyWasDown = f1;

        if (!_freeCamActive && !_orbitCamActive)
        {
            _specMouseInit = false;
            return;
        }

        // Spectator camera active: the ship holds (neutral controls), no stray input.
        // Mouse capture + look.
        var ms = Mouse.GetState();
        if (!_specMouseInit)
        {
            _specMouseInit = true;
            _specPrevMouseX = ms.X; _specPrevMouseY = ms.Y; _specPrevScroll = ms.ScrollWheelValue;
        }
        int dx = ms.X - _specPrevMouseX;
        int dy = ms.Y - _specPrevMouseY;
        int scroll = ms.ScrollWheelValue - _specPrevScroll;
        _specPrevMouseX = ms.X; _specPrevMouseY = ms.Y; _specPrevScroll = ms.ScrollWheelValue;

        if (_freeCamActive)
        {
            // mouse look
            if (dx != 0 || dy != 0)
                _freeCam.Look(dx, dy);
            RecenterMouse();

            // speed modifier: Shift boost, Ctrl slow (dedicated free-cam actions)
            _freeCam.SpeedModifier = 1f;
            if (ActionDown(ks, "FreeCamSpeedUp")) _freeCam.SpeedModifier = 4f;
            else if (ActionDown(ks, "FreeCamSpeedSlow")) _freeCam.SpeedModifier = 0.25f;

            // movement: WASD + Q/E (dedicated free-cam movement actions)
            float fwd = (ActionDown(ks, "FreeCamForward") ? 1f : 0f) + (ActionDown(ks, "FreeCamBack") ? -1f : 0f);
            float right = (ActionDown(ks, "FreeCamRight") ? 1f : 0f) + (ActionDown(ks, "FreeCamLeft") ? -1f : 0f);
            float up = (ActionDown(ks, "FreeCamUp") ? 1f : 0f) + (ActionDown(ks, "FreeCamDown") ? -1f : 0f);
            _freeCam.Move(fwd, right, up, (float)dt);
        }
        else if (_orbitCamActive)
        {
            // left-drag orbit, right-drag pan, wheel zoom
            bool left = ms.LeftButton == ButtonState.Pressed;
            bool right = ms.RightButton == ButtonState.Pressed;
            if (left) _orbitCam.Orbit(dx, dy);
            else if (right) _orbitCam.Pan(dx, dy);
            if (scroll != 0) _orbitCam.Zoom(scroll);
            if (_orbitAuto) _orbitCam.Step(dt);

            // PageUp cycles the locked subject
            bool pgUp = ActionDown(ks, "OrbitSubjectNext");
            if (!composing && pgUp && !_orbitPgUpWasDown)
                CycleOrbitSubject();
            _orbitPgUpWasDown = pgUp;
        }
    }

    private void RecenterMouse()
    {
        var vp = GraphicsDevice.Viewport;
        Mouse.SetPosition(vp.Width / 2, vp.Height / 2);
        // reset the delta baseline to the centre to avoid a jump next frame
        _specPrevMouseX = vp.Width / 2;
        _specPrevMouseY = vp.Height / 2;
    }

    /// <summary>
    /// P612 — auto-proof driver for the spectator cameras (used with
    /// <c>--camera freecam</c> / <c>--camera orbitcam</c> in a real-window live
    /// proof). Lazily activates the rig once the ship is airborne, then flies the
    /// free camera around the battle centroid with a gentle pan (so the timed
    /// autoshots capture the fight from several angles) or auto-orbits the orbit
    /// camera around its locked subject.
    /// </summary>
    private void DriveAutoProofSpectator(double dt)
    {
        if (_autoPlan is null) return;
        bool flying = _state == ClientState.Flying;
        string cam = _autoPlan.Camera;
        if (string.Equals(cam, "freecam", StringComparison.OrdinalIgnoreCase))
        {
            if (!_freeCamActive && flying) ToggleFreeCam();
            if (_freeCamActive)
            {
                _cameraMode = CameraMode.FreeCam;
                // Keep framing the battle: nudge toward the in-sector ship centroid
                // when it drifts too far, so the shots stay on the fight.
                if (TryBattleCentroid(out var centroid))
                {
                    var toC = centroid - _freeCam.Position;
                    float dist = toC.Length();
                    if (dist > 1200f)
                    {
                        toC.Normalize();
                        _freeCam.Position += toC * MathF.Min(dist - 800f, 400f * (float)dt);
                    }
                }
                _freeCam.Yaw += 0.014f * (float)dt;
                _freeCam.Move(0.5f, 0f, 0f, (float)dt);
            }
        }
        else if (string.Equals(cam, "orbitcam", StringComparison.OrdinalIgnoreCase))
        {
            if (!_orbitCamActive && flying) ToggleOrbitCam();
            if (_orbitCamActive) { _cameraMode = CameraMode.OrbitCam; _orbitCam.AutoOrbit = true; _orbitCam.Step(dt); }
        }
    }

    /// <summary>Centroid of all visible in-sector ships (the battle focus), or
    /// false when none. Reads under the gate lock.</summary>
    private bool TryBattleCentroid(out Vector3 centroid)
    {
        centroid = default;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } me
                || !world.Ships.TryGetValue(me, out var own))
                return false;
            short sector = own.SectorId;
            int n = 0;
            var acc = SimVec.Zero;
            foreach (var (id, ship) in world.Ships)
            {
                if (id == me || ship.IsNotVisible || ship.Latest is not { } u) continue;
                if (ship.SectorId != sector) continue;
                acc += u.Position;
                n++;
            }
            if (n == 0) return false;
            centroid = PoseConverter.ToXna(acc / n);
            return true;
        }
    }

    /// <summary>Free-cam sector switch (admins only effective server-side): tell
    /// the server which sector the camera is now viewing via FM_C_VIEW_CLUSTER so
    /// an admin spectator streams all ships in it. Non-admins are ignored by the
    /// server (no information leak).</summary>
    private void SpectatorRequestSector(short sectorId)
    {
        _session.SendViewClusterRequest(sectorId);
        Console.WriteLine($"[monogame] spectator requesting sector {sectorId}");
    }

    // ── smooth transition layer ─────────────────────────────────────────────

    /// <summary>
    /// Detect a camera framing change and ease toward the new framing. Mutates
    /// <paramref name="view"/>/<paramref name="proj"/>/<paramref name="eye"/> in
    /// place so the caller draws the eased camera. When nothing changed, passes
    /// the pose through unchanged (native camera behaviour preserved).</summary>
    private void ApplyCameraSmoothing(ref Matrix view, ref Matrix proj, ref Vector3 eye, float aspect, double dt)
    {
        string key = CurrentCameraFramingKey();
        bool changed = _camSmoothInit && key != _lastFramingKey;
        _lastFramingKey = key;

        var raw = CameraPose.FromViewProjection(view, proj, eye);
        if (!_camSmoothInit)
        {
            _camSmoothInit = true;
            _camDisplay = raw;
            _camTransActive = false;
        }
        else if (changed && !_instantCameraTransitions)
        {
            _camTransFrom = _camDisplay;
            _camTransElapsed = 0;
            _camTransActive = true;
        }

        if (_camTransActive && !_instantCameraTransitions)
        {
            _camTransElapsed += dt;
            if (_camTransElapsed >= _camTransDuration)
            {
                _camTransActive = false;
                _camDisplay = raw;
            }
            else
            {
                var t = _camTransElapsed;
                var dur = _camTransDuration;
                _camDisplay = new CameraPose(
                    CameraEasing.Blend(_camTransFrom.Eye, raw.Eye, t, dur),
                    CameraEasing.Blend(_camTransFrom.Orient, raw.Orient, t, dur),
                    CameraEasing.Blend(_camTransFrom.FovDeg, raw.FovDeg, t, dur));
            }
        }
        else
        {
            // No transition running: pass the raw camera through untouched.
            _camDisplay = raw;
            return;
        }

        // The flight views are camera-relative (CreateLookAt(Zero, …), draws
        // subtract the returned eye), so rebuild rotation-only — View() would
        // subtract the eye a second time and drift the scene.
        view = _camDisplay.RelativeView();
        proj = _camDisplay.Projection(aspect, CameraPose.NearFromProjection(proj), CameraPose.FarFromProjection(proj));
        eye = _camDisplay.Eye;
    }

    /// <summary>
    /// A stable identity for the current camera framing — used to detect a
    /// framing change (mode switch, target change, missile start/end, orbit
    /// subject change) that should trigger a smooth transition instead of a cut.
    /// </summary>
    private string CurrentCameraFramingKey()
    {
        var m = _cameraMode;
        string s = m.ToString();
        switch (m)
        {
            case CameraMode.Command:
                s += ":" + _commandCamera.TopDown;
                if (_commandCamera.Tracking) s += ":track:" + _autoTarget;
                lock (_session.Gate) s += ":sector:" + RenderSector(CurrentOwnSector() ?? -1);
                break;
            case CameraMode.DroneFollow:
            case CameraMode.DroneCockpit:
                s += ":" + _commandee?.Id;
                break;
            case CameraMode.ExternalTarget:
            case CameraMode.ExternalFlyBy:
            case CameraMode.ExternalFlyByTarget:
                s += ":" + (_autoTarget is { } t ? $"{(int)t.Kind}:{t.Id}" : "none");
                break;
            case CameraMode.ExternalStation:
                s += ":st" + (_autoTarget is { Kind: TargetKind.Station } st ? st.Id.ToString() : "any");
                break;
            case CameraMode.ExternalMissile:
                s += ":miss" + (TryGetMissileCameraFrame(out _, out _) ? "1" : "0");
                break;
            case CameraMode.ExternalOverride:
                s += ":ov" + (_overrideTarget is { } ? "1" : "0");
                break;
            case CameraMode.OrbitCam:
                s += ":subj" + _orbitSubjectId;
                break;
            case CameraMode.ExternalOrbit:
                break;
            default:
                break;
        }
        if (_directorActive) s += ";dir";
        return s;
    }
}

using Allegiance.ClientCore.MonoGame.Assets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Content;
using Allegiance.Core.Rendering;
using Allegiance.Core.Visual;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The rendering slice of <see cref="Game1"/> (P384 stage-4 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{
    /// <summary>P586 — one treasure queued for the native draw pass: its world
    /// position (camera-relative later), the treasure's unique ObjectId (the
    /// deterministic-spin seed) and its resolved native visual.</summary>
    private readonly record struct TreasureDrawItem(Vector3 Position, short Id, Rendering.TreasureVisual Visual);

    /// <summary>P586 — log an unresolvable treasure (a part/development id the
    /// client's catalogue cannot resolve, or one whose native model is missing)
    /// exactly once per ObjectId, then fall back to the gold-gem placeholder —
    /// the task's "log once per unresolved case" (never a spammy per-frame log).</summary>
    private void LogUnresolvedTreasureOnce(Allegiance.ClientCore.State.TreasureEntity te)
    {
        if (_treasureUnresolvedLogged.Add(te.TreasureId))
            Console.WriteLine($"[loot] treasure obj={te.TreasureId} code={te.Code} payload={te.PayloadId}: no resolvable native visual — falling back to the gold gem");
    }


    // P297 — the per-ship emission inputs that drive one frame of the continuous
    // damage-debris smoke stream (native AddDamageParticles): a damaged visible
    // ship's real position/velocity/orientation + its native vent count (from
    // its real hull fraction via Flight.DamageDebrisTracker).
    private readonly record struct DamageEmitter(
        Allegiance.Core.Collision.Vector3 Position,
        Allegiance.Core.Collision.Vector3 Velocity,
        Allegiance.Core.Sim.Orientation Orientation,
        int VentCount,
        float Radius);   // P338 — the damaged ship's real rendered radius (0.5×HullType.Length)

    /// <summary>Record or clear a ship's damage-debris emitter for this frame:
    /// vents (and thus streams) only while the ship's real hull fraction is at
    /// or below native's 0.9 threshold; a healthy ship contributes nothing.
    /// Called under the Gate lock in the pose-gather pass. P448b — the emitter is
    /// fed the ship's RENDER pose (dead-reckoned for remote ships) so debris never
    /// detaches from the drawn hull; <paramref name="velocity"/> stays the last-known
    /// wire velocity (fine for particle direction). <paramref name="hullFraction"/>
    /// stays the wire value — venting is driven by damage, not by the render pose.</summary>
    private void GatherDamageEmitter(short id, float hullFraction,
        Allegiance.Core.Collision.Vector3 position,
        Allegiance.Core.Collision.Vector3 velocity, Allegiance.Core.Sim.Orientation orientation, float radius)
    {
        int vents = Flight.DamageDebrisTracker.DamageFrameCount(hullFraction);
        if (vents > 0)
            _damageEmitters[id] = new DamageEmitter(position, velocity, orientation, vents, radius);
        else
            _damageEmitters.Remove(id);
    }

    /// <summary>P358 — the per-ship emission inputs that drive one frame of the
    /// ship engine-thrust trail (native AddThrustParticles, "rocket" case): a
    /// thrusting visible ship's real position/velocity/orientation + its real
    /// rendered radius (0.5 × HullType.Length).</summary>
    private readonly record struct EngineTrailEmitter(
        Allegiance.Core.Collision.Vector3 Position,
        Allegiance.Core.Collision.Vector3 Velocity,
        Allegiance.Core.Sim.Orientation Orientation,
        float Radius);

    /// <summary>P358 — update a ship's afterburner power ramp (native
    /// CafterburnerIGC::Update) from its real afterburner-button bit and, when
    /// <paramref name="visible"/> and the ramped power is still &gt; 0, record an
    /// engine-trail emitter for this frame. Runs under the Gate lock in the
    /// pose-gather pass for every visible ship (own ship only outside the cockpit,
    /// matching the P297 damage-debris local-ship camera gate). P448b — the emitter
    /// is fed the ship's RENDER pose (dead-reckoned for remote ships) so the trail
    /// never detaches from the drawn hull; <paramref name="thrusting"/> is the wire
    /// afterburner-bit and <paramref name="velocity"/> the last-known wire velocity.</summary>
    private void UpdateEngineTrail(short id, bool thrusting,
        Allegiance.Core.Collision.Vector3 position,
        Allegiance.Core.Collision.Vector3 velocity, Allegiance.Core.Sim.Orientation orientation,
        float radius, float dtSec, bool visible)
    {
        float power = _shipThrustPower.GetValueOrDefault(id);
        power = thrusting
            ? Math.Min(1f, power + ThrustPowerOnRate * dtSec)
            : Math.Max(0f, power - ThrustPowerOffRate * dtSec);
        _shipThrustPower[id] = power;
        if (visible && power > 0f)
            _engineTrailEmitters[id] = new EngineTrailEmitter(position, velocity, orientation, radius);
        else
            _engineTrailEmitters.Remove(id);
    }

    /// <summary>P365 — per-ship inputs that drive one frame of the engine-nozzle
    /// glow decal: the thrusting visible ship's real position/orientation, its
    /// real rendered radius (0.5 × HullType.Length) and its current glow
    /// <c>value</c> in [0,1] (native's UpdateGlow input).</summary>
    private readonly record struct EngineNozzleGlowEmitter(
        Allegiance.Core.Collision.Vector3 Position,
        Allegiance.Core.Sim.Orientation Orientation,
        float Radius,
        float Value);

    /// <summary>P365 — native UpdateGlow's <c>value</c> for one ship: the real
    /// throttle axis mapped by <c>SetThrust(0.5·(jsValues[3]+1))</c>
    /// (thinggeo.cpp:8008), forced to 1.0 whenever the afterburner is engaged
    /// (<c>m_fThrustPower ≠ 0</c>, thinggeo.cpp:452-455). Clamped to [0,1].</summary>
    private static float NozzleGlowValue(int stateM, float throttle)
    {
        if ((stateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0) return 1f;
        return Math.Clamp(0.5f * (throttle + 1f), 0f, 1f);
    }

    /// <summary>P365 — gather one visible ship's nozzle-glow emitter for this frame
    /// (native RenderGlow's per-ship scope: every thrusting ship the client
    /// renders, not just the local hull). Runs under the Gate lock in the
    /// pose-gather pass; the glow is recorded only when the value is above 0
    /// (native's <c>m_scaleGlow &gt; 0</c> render gate) and the hull is known.
    /// The local ship is gated to non-cockpit camera modes (its hull isn't drawn
    /// there), matching P358/P297. P448b — the emitter is fed the ship's RENDER
    /// pose (dead-reckoned for remote ships) so the glow never detaches; the glow
    /// VALUE stays wire-driven (<paramref name="stateM"/>/<paramref name="throttle"/>).</summary>
    private void UpdateNozzleGlow(short id, int stateM, float throttle,
        Allegiance.Core.Collision.Vector3 position, Allegiance.Core.Sim.Orientation orientation,
        float radius, bool visible)
    {
        if (!visible || radius <= 0f)
        {
            _nozzleGlowEmitters.Remove(id);
            return;
        }
        float value = NozzleGlowValue(stateM, throttle);
        if (value > 0f)
            _nozzleGlowEmitters[id] = new EngineNozzleGlowEmitter(position, orientation, radius, value);
        else
            _nozzleGlowEmitters.Remove(id);
    }

    /// <summary>P367 — per-ship inputs that drive one frame of its navigation
    /// strobe lights: the visible ship's real position/orientation and its real
    /// rendered radius (0.5 × HullType.Length). Unlike the thrust-driven effects,
    /// strobes are always blinking, so no thrust/throttle input is needed.</summary>
    private readonly record struct NavStrobeEmitter(
        Allegiance.Core.Collision.Vector3 Position,
        Allegiance.Core.Sim.Orientation Orientation,
        float Radius);

    /// <summary>P367 — gather one visible ship's strobe emitter for this frame
    /// (native LightsGeo's per-ship scope: every visible ship the client renders,
    /// not just the local hull). Runs under the Gate lock in the pose-gather pass;
    /// the emitter is recorded whenever the hull is known and visible. The local
    /// ship is gated to non-cockpit camera modes (its hull isn't drawn there),
    /// matching P358/P365. P448b — the emitter is fed the ship's RENDER pose
    /// (dead-reckoned for remote ships) so the strobes never detach.</summary>
    private void UpdateNavStrobes(short id, Allegiance.Core.Collision.Vector3 position,
        Allegiance.Core.Sim.Orientation orientation, float radius, bool visible)
    {
        if (!visible || radius <= 0f)
        {
            _navStrobeEmitters.Remove(id);
            return;
        }
        _navStrobeEmitters[id] = new NavStrobeEmitter(position, orientation, radius);
    }

    /// <summary>P367 — compute each strobe anchor's current pulse (the real native
    /// LightsGeo::Render envelope, <see cref="Rendering.NavStrobeMath.Pulse"/>) and
    /// append a strobe decal view when the pulse is &gt; 0 (native's render gate,
    /// efapp.cpp:208). The pulse scales the decal's SIZE only — faithful to native's
    /// <c>0.5·value</c> decal scale (efapp.cpp:215), which never dims the colour —
    /// normalized to real hull units by <see cref="Rendering.NavStrobeLayout.SizeFactor"/>.</summary>
    private void AddNavStrobeAnchors(System.Collections.Generic.IReadOnlyList<Rendering.NavStrobeAnchor> anchors, float radius)
    {
        float clock = (float)_nowSeconds;
        foreach (var a in anchors)
        {
            float pulse = Rendering.NavStrobeMath.Pulse(clock, a.Timing);
            if (pulse <= 0f) continue;
            float half = pulse * radius * Rendering.NavStrobeLayout.SizeFactor;
            _drawNavStrobes.Add(new Rendering.NavStrobeView(a.Position, half, new Vector3(a.R, a.G, a.B)));
        }
    }

    /// <summary>P363 — feed one visible ship's side-coloured motion-trail ribbon
    /// for this frame: its anchor (native <c>m_vecPosition</c>), its ribbon
    /// right vector (<c>orientation.GetRight() · radius · 0.25</c>, the native
    /// half-width per side) and its real side colour (resolved from the roster
    /// via <paramref name="sideId"/>; falls back to the neutral gray if no
    /// roster is known yet). Runs under the Gate lock in the pose-gather pass
    /// for every visible ship — own ship only outside the cockpit (the hull
    /// isn't drawn there), matching the P358/P297 local-ship camera gate.
    /// P448b — the anchor is the ship's RENDER position (dead-reckoned for
    /// remote ships) and the right vector its RENDER orientation, so the ribbon
    /// never detaches from the drawn hull.</summary>
    private void UpdateMotionTrail(short id, Allegiance.Core.Collision.Vector3 position,
        Allegiance.Core.Sim.Orientation orientation, float radius, short sideId, bool visible)
    {
        if (!visible) return;
        if (!_session.World.TryGetSideColor(sideId, out float r, out float g, out float b))
        {
            // No roster colour yet — neutral gray (native's absent-side gray).
            r = 0.5f; g = 0.5f; b = 0.5f;
        }
        var right = orientation.GetRight() * (radius * TrailWidthFraction);
        _motionTrail.SetAnchor(id, position, right, r, g, b);
    }

    /// <summary>
    /// P449b — resolve whether the OWN ship has an afterburner mounted and is
    /// currently boosting, for the camera jiggle. Native gates the afterburner
    /// term on <c>GetMountedPart(ET_Afterburner, 0) != NULL</c> and reads its
    /// <c>GetPower()</c>; the port's analogue of \"mounted\" is the own ship's
    /// granted loadout (<c>BUY_LOADOUT_ACK</c>) containing an Afterburner part,
    /// and \"boosting\" is the <c>AfterburnerButton</c> StateM bit on the own
    /// ship's latest update (the pose source the camera uses). Returns false
    /// when the own ship isn't known yet (nothing to jiggle off).
    /// </summary>
    private bool TryOwnAfterburnerForJiggle(out bool mounted, out bool active)
    {
        mounted = false;
        active = false;
        var world = _session.World;
        lock (_session.Gate)
        {
            if (RenderShipId(world) is not { } me) return false;
            if (!world.Ships.TryGetValue(me, out var mine)) return false;
            if (mine.Latest is not { } up) return false;
            active = (up.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0;
            mounted = OwnShipLoadoutHasAfterburner(mine);
            return true;
        }
    }

    /// <summary>
    /// P449b — whether the own ship's granted loadout contains an Afterburner
    /// part, per the shop catalogue (mirrors the predictor's afterburner scan,
    /// <see cref="Game1.Prediction"/>'s <c>TryResolveOwnPredictionHull</c>).
    /// Absent catalogue / empty loadout / hull-only → false (native's
    /// <c>GetMountedPart(ET_Afterburner,0) == NULL</c>).
    /// </summary>
    private bool OwnShipLoadoutHasAfterburner(Allegiance.ClientCore.State.ShipEntity mine)
    {
        var cat = _shopCatalog;
        if (cat is null || mine.Loadout is not { Count: > 0 } lo) return false;
        foreach (var p in lo)
        {
            foreach (var pt in cat.Parts)
            {
                if (pt.PartId != p.PartId) continue;
                if (pt.Type is Allegiance.Core.DataPartTypeIGC.Afterburner) return true;
                break;
            }
        }
        return false;
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_benchReplay is { } replay)
            gameTime = new(TimeSpan.FromSeconds(replay.Frame / 60d), TimeSpan.FromSeconds(1d / 60));
        _gpuTimer?.Poll(_receiveGpu!);
        _frameTelemetry?.BeginDraw();
        _scaledThisFrame = false;
        // P306 — headless capture: once Flying, after a short settle grace (so
        // the client has received teammate exports/positions for every bot and
        // the overview camera framing is stable), redirect the WHOLE frame into
        // the offscreen target and save a real PNG sequence (the save happens
        // at the end of this method). capThis is deliberately a local so the
        // save block at the bottom can test it.
        bool captureArmed = _captureSpec is not null && _state == ClientState.Flying;
        if (captureArmed && !_capFlyMarked)
        {
            _capFlyMarked = true;
            _capFlyWallMs = ClientNowMilliseconds;
        }
        bool capThis = captureArmed && ClientNowMilliseconds - _capFlyWallMs >= CapSettleGraceMs;
        if (capThis)
        {
            if (!_capStarted)
            {
                _capStarted = true;
                _capActive = true;
                _capClock.Restart();
                _capLastWrite = -1e9;
                Console.WriteLine($"[capture] begin capture: dir={_capDir} duration={_captureSpec!.DurationSeconds:0.##}s maxFps={_captureSpec.MaxFps}");
            }
            // Auto-dismiss the mission-briefing popup so it doesn't obscure the fleet.
            if (IsBriefingVisible(out _)) DismissBriefing();
            GraphicsDevice.SetRenderTarget(_capTarget!);
        }
        GraphicsDevice.Clear(Color.Black);

        // P579 — real-window frame-time stats (avg ms + 1%-low) when
        // ALLEGIANCE_VOL_PERF is set; inert otherwise.
        MaybeLogFrameStats(gameTime.ElapsedGameTime.TotalMilliseconds);

        // P565 — the offline hull inspector renders its own self-contained frame
        // (camera, hull, VFX, HUD, screenshot) and never touches the flight scene.
        if (_state == ClientState.Inspecting)
        {
            DrawInspector(gameTime);
            return;
        }

        // P757 — the splash screen: title + loading, until the timeout/keypress
        // advances to the intro (native splashscreen.cpp). Flushes any queued
        // autoshot like the main path's Draw end (the early return would
        // otherwise defer it past the splash's one visible frame).
        if (_state == ClientState.Splash)
        {
            DrawSplash();
            FlushScheduledAutoshot();
            return;
        }

        _lockBracket = null;   // P230 — recomputed only when a lock target projects into view this frame
        _leadIndicator = null; // P266 — recomputed only when a lead point projects into view this frame
        _targetSelectionBracket = null; // P345 — recomputed only when a T-selected target projects into view this frame
        _velocityMarker = null; // HUD-8 — recomputed each frame from the own velocity direction
        _chaseAim = null;       // HUD-8 — recomputed only in chase/missile without a target
        _orderArrows = null;    // HUD-8 — recomputed from accepted/current order targets
        _lastBaseHud = [];
        _targetBearingScreenDir = null; // P359 — recomputed whenever a T-selected target is resolvable this frame

        if (_state == ClientState.Lobby)
        {
            DrawLobby();
        }
        else if (_state == ClientState.Intro)
        {
            DrawIntro();   // P731 — the intro screen
        }
        else if (_state == ClientState.Connecting)
        {
            DrawConnecting();
        }
        else if (_state == ClientState.ConnectFailed)
        {
            DrawConnectFailed();
        }
        else if (_state == ClientState.SideSelect)
        {
            DrawSideSelect();
        }
        else if (_state == ClientState.ReadyCheck)
        {
            DrawReadyCheck();
        }
        else if (_state == ClientState.Disconnected)
        {
            DrawConnectionLost();   // P320 — clear on-screen session-loss indication
        }
        else // Flying
        {
            // HUD-21b — the preview is null until this frame actually renders an
            // eligible target. Without this, a frame that fails TrySceneReference
            // or the sector-generation gate composited the LAST target's RT again
            // (a stale model in the pane).
            _hudTargetModelPreview = null;
            var world = _session.World;
            if (TrySceneReference(world, out short me, out var myPose)
                && _flightEffects.Generation == world.SectorGeneration)
            {
                var myPos = PoseConverter.ToXna(myPose.Position);
                var myRot = PoseConverter.ToXnaRotation(myPose.Orientation);
                var aspect = GraphicsDevice.Viewport.AspectRatio;
                // P252 — real wall-clock delta in seconds, used to advance each
                // asteroid's client-side spin (WarpRenderer's accumulate-real-dt
                // precedent, WarpRenderer.cs:158-165).
                float dtSec = (float)gameTime.ElapsedGameTime.TotalSeconds;

                // P530 — the local sector's native bidirectional lighting for the
                // WHOLE 3D scene, computed ONCE per frame and read by every 3D
                // renderer (ships, stations, missiles, probes, building effects,
                // asteroids). It is re-derived from the current sector's cluster
                // every frame, so a warp (sector change) swaps the light for all
                // of them on the frame of the transition — native's per-sector
                // lighting. Falls back to a sane fixed white light when the
                // cluster is unknown, so objects never render black.
                _sectorLighting = TrySceneSector(world, me, out short lightSector)
                    && world.Clusters.TryGetValue(lightSector, out var dcLite)
                        ? Allegiance.Core.Rendering.SectorLighting.FromCluster(dcLite.LightDirection, dcLite.LightColor)
                        : Allegiance.Core.Rendering.SectorLighting.FromCluster(null, 0xFFFFFFFF);

                // Camera selection: P211's chase (third-person) / cockpit
                // (first-person, full orientation with roll), plus P295's two new
                // external views (ExternalTurret follows the carrier you're
                // gunning on; ExternalMissile watches the latest live missile in
                // your sector). All honour the same camera-relative convention
                // and all supply a cameraEye that every draw subtracts from
                // object positions.
                Matrix view; Matrix proj; Vector3 cameraEye;
                float chaseDt = (float)gameTime.ElapsedGameTime.TotalSeconds;
                // P306 — capture mode: default is a fixed tracking overview of the
                // whole fleet (video shows N ships in frame at once). P338 — a
                // `chase` capture instead drives the local ship's REAL chase
                // camera so the live-proof frames exercise the production
                // framing path (radius-adaptive; see ChaseCamera).
                // P580 — otherwise the flight camera is resolved centrally by
                // ComputeFlightCamera, which handles every native external mode,
                // the director b-roll camera and spectator follow (see
                // Game1.Director.cs).
                if (_benchmark is not null && (_benchMeasuring || _benchSuggestion is not null))
                {
                    ComputeBenchmarkCamera(myPos, myRot, myPose.Orientation, aspect, chaseDt, out view, out proj, out cameraEye);
                }
                else if (_captureSpec is { } cap && !cap.Chase)
                {
                    ComputeCaptureCamera(aspect, out view, out proj, out cameraEye);
                }
                else
                {
                    (view, proj, cameraEye) = ComputeFlightCamera(
                        myPos, myRot, myPose.Orientation, aspect, chaseDt);
                    // P612 — smooth camera transitions: when the framing changes
                    // (target switch, missile-cam start/end, mode change) ease the
                    // eye/target/FOV from the previous framing over ~0.7 s instead
                    // of cutting. Instant when ALLEGIANCE_INSTANT_CAMERA=1. Passes
                    // the pose through unchanged when nothing changed (native
                    // camera behaviour preserved). Applied AFTER the raw camera so
                    // every mode benefits; jiggle below perturbs the smoothed view.
                    ApplyCameraSmoothing(ref view, ref proj, ref cameraEye, aspect, chaseDt);
                }
                if ((_benchmark is not null || _benchmarkApplied) && _cameraMode != CameraMode.Command)
                    proj = WithBenchmarkViewDistance(proj, _benchmarkViewDistance);
                // P449b — native camera jiggle (WinTrek.cpp:374-399). Native sets
                // bJiggle = InternalCamera(m_cm), and InternalCamera is true only for
                // cmCockpit (Treki.h:145) — so native shakes the COCKPIT view, not the
                // external chase/missile views. Perturbing the shared view matrix means
                // the world AND the world-projected HUD elements (lead indicator,
                // lock/selection brackets) shake together, while the screen-anchored
                // centre reticle stays put — exactly how native projects the HUD
                // against the jiggled camera. Visual only: the target-picking ray in
                // Game1.Targeting.cs uses the un-jiggled camera, so aiming is stable.
                if (_cameraMode == CameraMode.Cockpit
                    && TryOwnAfterburnerForJiggle(out bool abMounted, out bool abActive))
                {
                    // ServerShipUpdate.Power is a hardcoded flat 1.0 in this port (no
                    // server-side afterburner-power ramp exists; the simulator treats the
                    // afterburner as binary boost), so the native GetPower() term is
                    // approximated as the AfterburnerButton bit — see the P449b report.
                    var (jYaw, jPitch) = _flightJiggle.Step(dtSec, abActive ? 1f : 0f, abMounted);
                    if (jYaw != 0f || jPitch != 0f)
                        view = Rendering.FlightJiggle.PerturbView(view, jYaw, jPitch);
                }
                // P561 — per-frame visibility rules for the ENHANCED smoke:
                // a camera-mode-aware near fade (cockpit clears at-the-eye smoke;
                // chase keeps the own ship's plume visible and delegates the
                // "don't cover the ship" job to the occluder), a far-distance
                // fade (distant trails read as a line, not beads), and — in the
                // chase/external views only — the own-ship occluder that fades
                // exactly the smoke lying between the camera and the ship's
                // screen footprint, so the hull stays readable in the chase view.
                var vfxNear = _cameraMode == CameraMode.Cockpit ? ParticleNearFade.Flight
                                                                : ParticleNearFade.Chase;
                var vfxFar = ParticleFarFade.Flight;
                var vfxOccluder = _cameraMode == CameraMode.Cockpit
                    ? default
                    : ChaseOccluder.Create(myPos, _myRenderRadius > 0f ? _myRenderRadius : 1f, cameraEye, view, proj);

                // P230 — project the server's lock target into this frame's view and
                // stage the target-box descriptor for DrawHud (null when no target or
                // it is out of view). Uses the same camera-relative projection as the
                // P212 target-pick.
                _lockBracket = ComputeLockBracket(view, proj, cameraEye);
                // P266 — project the server-solved lead point AND the target's
                // current position into this frame's view and stage the reticle
                // descriptor for DrawHud (null when no lead is granted or either
                // point is out of view).
                _leadIndicator = ComputeLeadIndicator(view, proj, cameraEye);
                // HUD-8 — stage the velocity-direction marker (cockpit gate is
                // applied in HudModel), the chase aim without target, and the
                // accepted/current order turn arrows for this frame's snapshot.
                _velocityMarker = ComputeVelocityMarker(view, proj);
                _chaseAim = _cameraMode is CameraMode.Chase or CameraMode.ExternalMissile
                    ? ComputeChaseAim(view, proj, cameraEye)
                    : null;
                _orderArrows = ComputeOrderArrows(view, proj, cameraEye);
                // P345 — project the player's OWN T-selected autopilot target into
                // this frame's view and stage the selection-bracket descriptor for
                // DrawHud (null when no target is selected or it is out of view).
                // Uses the same shared camera-relative projection (ProjectToScreen)
                // as ComputeLockBracket and P266's lead, but is a separate method
                // because the two concepts are simultaneously active and visually
                // distinct (server missile-lock vs the player's own target).
                _targetSelectionBracket = ComputeTargetSelectionBracket(view, proj, cameraEye);
                // P359 — the bearing arrow is computed EVEN when the target is
                // off-screen (it is exactly what you need then), so it lives apart
                // from the bracket rather than piggy-backing its in-view gate.
                _targetBearingScreenDir = ComputeTargetBearing(view, proj, cameraEye);
                ComputeLastBaseHud(view, proj, cameraEye);

                // P339 B1 — make the Flying 3D pass self-contained. The previous
                // frame's last draw is a SpriteBatch HUD/overlay pass, and
                // SpriteBatch.Begin sets DepthStencilState.None /
                // BlendState.AlphaBlend and does NOT restore them on End(), so
                // the live probe at this exact point showed blend=AlphaBlend
                // depth=None raster=CullCounterClockwise on every frame — i.e.
                // the opaque starfield/ship/asteroid draws were running with
                // DEPTH TESTING AND WRITES DISABLED (visible only as painter's-
                // order artifacts in sparse scenes; opaque a=255 geometry still
                // paints solid under AlphaBlend, which masked it). Explicitly
                // reset all three states so depth is truly enabled for the whole
                // opaque pass and the additive passes that follow can rely on it.
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;

                // P547 — bloom redirect: when bloom is on the WHOLE 3D scene
                // (opaque geometry + particles) renders into the scene target,
                // so the HUD/SpriteBatch UI (drawn after the composite below)
                // never blooms. Soft particles are active only in Enhanced with
                // the depth shader + target ready.
                var outputViewport = GraphicsDevice.Viewport;
                bool fsr = PrepareUpscaling(outputViewport.Width, outputViewport.Height);
                bool sceneTarget = fsr || (_bloomEnabled && _post is { BloomReady: true });
                var sceneRt = fsr ? _fsr!.Scene : _post?.SceneRT;
                bool bloom = _bloomEnabled && _post is { BloomReady: true };
                bool soft = _softEnabled && _post is { SoftReady: true };

                // P559 — gather the opaque scene geometry ONCE (ships, asteroids,
                // stations — the same per-object world transforms, spins advanced
                // exactly once) so the linear-depth pass and the color pass below
                // share identical geometry. Depth-gather lists are reset here so
                // each frame re-collects exactly its own geometry.
                // Gather real visible-sector events before opaque hulls are lit.
                // Low does no world scan, allocations or additional GPU work.
                if (_effectLights.Detail != EffectsDetail.Low)
                {
                    _effectLights.Clear();
                    long lightNow = ClientNowMilliseconds;
                    _lightImpacts.RemoveAll(hit => lightNow - hit.ReceivedLocalMs >= 160);
                    lock (_session.Gate)
                    {
                        short flashSector = TrySceneSector(world, me, out var visibleSector) ? visibleSector : (short)-1;
                        foreach (var blast in world.Explosions) _effectLights.Explosion(blast, flashSector, lightNow, cameraEye);
                        foreach (var hit in world.HullHits)
                        {
                            if (hit.ClusterId == flashSector && _lightImpacts.Count < 64) _lightImpacts.Add(hit);
                        }
                        foreach (var hit in _lightImpacts) _effectLights.Impact(hit, flashSector, lightNow, cameraEye);
                    }
                }
                PrepareSceneGeometry(me, cameraEye, dtSec);
                // HUD-21 — render the selected target's 3-D model preview into
                // its own render target HERE, before the colour target is bound,
                // so the RT excursion is lossless (nothing has been drawn to the
                // colour target yet; the frame cleared it at :317). The preview
                // renderer RESTORES this exact colour target itself, so the
                // caller keeps exactly one rebind (HUD-21b — HUD-21 rebound here
                // a second time). Feed the RT to the HUD this frame when a model
                // rendered, else null.
                RenderTarget2D? colourTarget = sceneTarget ? sceneRt : (capThis ? _capTarget : null);
                _hudTargetModelPreview = RenderTargetModelPreview(me, cameraEye, view, colourTarget)
                    ? _targetModelPreviewRenderer.Target : null;
                GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                _gpuTimer?.Start(_frameTelemetry!.FrameId, post: false);

                // P559 — run the linear-depth pass BEFORE the opaque color pass.
                // P547 placed it mid-frame (after the treasure draw), switching
                // the active render target away from the color target and back;
                // with MonoGame's backbuffer RenderTargetUsage.DiscardContents the
                // opaque scene drawn before the switch (ships/asteroids/stations)
                // was wiped — only particles/lights/HUD (drawn after) survived,
                // i.e. the reported invisibility. Rendering depth first means the
                // color target is set exactly once and never switched away from,
                // preserving the opaque scene in every path (backbuffer, the
                // SceneRT bloom target, and the capture target). The color target
                // is restored after, so the smoke/fire below samples the depth
                // target. Falls back to near-fade-only (never render nothing)
                // when soft particles are inactive/unavailable.
                if (soft)
                {
                    _softContext = _post!.Context(Rendering.LinearDepth.DefaultNear, Rendering.LinearDepth.DefaultFar);
                    // P582 — the depth render target must NOT still be bound as a
                    // texture here: a feedback loop (renderer that sampled it last
                    // frame forgetting to unbind) breaks this pass and hides the
                    // opaque world (asteroids/stations vanish).
                    Rendering.DepthTextureBinding.AssertDepthNotBoundAsTexture(GraphicsDevice, _post.DepthRT, "color depth pass");
                    GraphicsDevice.SetRenderTarget(_post!.DepthRT);
                    GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer,
                        Rendering.VfxPostProcessor.DepthClearFar, 1f, 0);
                    DrawSceneDepth(view, proj, cameraEye);
                    GraphicsDevice.SetRenderTarget(sceneTarget ? sceneRt : (capThis ? _capTarget : (RenderTarget2D?)null));
                    GraphicsDevice.BlendState = BlendState.Opaque;
                    GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                }
                else
                {
                    _softContext = null;
                }

                if (sceneTarget)
                {
                    GraphicsDevice.SetRenderTarget(sceneRt);
                    GraphicsDevice.Clear(Color.Black);
                }

                DrawSkyP576(view, proj, cameraEye);

                // P449a — ambient space dust (native DebrisGeo). Step the field
                // with the ACTIVE camera's eye + orientation basis + focal length
                // (proj.M22 = cot(vFOV/2)) so it follows whatever pose the camera
                // uses — including the P447b predicted render pose — in every flight
                // camera mode. Grains are placed ahead of the eye in a forward cone
                // and never move; the field is drawn as additive soft dots behind the
                // ships, over the starfield (native renders it among the geos). A
                // >100 m camera jump (warp/teleport/sector change) clears it (the
                // native rule), so the field never smears across a transit.
                _debrisGeo.Step(
                    new SimVec(cameraEye.X, cameraEye.Y, cameraEye.Z),
                    new SimVec(-view.M31, -view.M32, -view.M33),   // camera forward
                    new SimVec(view.M11, view.M12, view.M13),      // camera right
                    new SimVec(view.M21, view.M22, view.M23),      // camera up
                    proj.M22,                                      // focus = cot(vFOV/2)
                    (float)_gameSeconds);
                float debrisFocus = proj.M22;
                _drawDustViews.Clear();
                foreach (var pt in _debrisGeo.Points)
                {
                    float age = (float)_gameSeconds - pt.StartTime;
                    float bright = Rendering.DebrisGeo.Brightness(age);
                    if (bright <= 0f) continue;
                    float rx = pt.Position.X - cameraEye.X, ry = pt.Position.Y - cameraEye.Y, rz = pt.Position.Z - cameraEye.Z;
                    float dsq = rx * rx + ry * ry + rz * rz;
                    // Native Render skips grains within 20 m of the camera (debrisgeo.cpp:177).
                    if (dsq <= Rendering.DebrisGeo.NearCameraSkipSquared) continue;
                    // P503: native DrawDecal scale = 0.1/focus is a CONSTANT world
                    // half-extent for every grain (context.cpp:2317, debrisgeo.cpp:166);
                    // the perspective divide then makes a grain subtend 0.1/distance of
                    // the screen height — smaller the farther away. The P449a formula
                    // (0.1/focus)·distance·Calibration gave every grain a constant
                    // ~1.3% of screen height regardless of depth → the "bubbles".
                    float half = Rendering.AmbientDustRenderer.DecalHalfExtent(debrisFocus);
                    _drawDustViews.Add(new Rendering.AmbientDustView(
                        pt.Position,
                        half,
                        new Vector3(bright * pt.Colour.X, bright * pt.Colour.Y, bright * pt.Colour.Z)));
                }
                _debrisRenderer.Draw(_drawDustViews, cameraEye, view, proj);

                // P628 — per-frame LOD bookkeeping reset (counts + far sprite list).
                _lodFullCount = 0;
                _lodFarCount = 0;
                _lodDrawCalls = 0;
                _drawFarSprites.Clear();

                foreach (var (id, pose) in _poses)
                {
                    // In cockpit mode the local hull is not drawn — you are inside
                    // it; every OTHER known ship still renders normally.
                    if ((_cameraMode == CameraMode.Cockpit && id == me) || (_cameraMode == CameraMode.DroneCockpit && id == _commandee?.Id))
                        continue;

                    if (!ShipInDrawnSector(id, me)) continue;
                    var xnaPos = PoseConverter.ToXna(pose.Position);
                    if (OutsideBenchmarkView(xnaPos, cameraEye)) continue;
                    var eyeRel = xnaPos - cameraEye;                     // P612b camera-relative
                    var worldMat = PoseConverter.ToXnaWorldRelative(xnaPos, pose.Orientation, cameraEye);
                    // P270 — alpha-blend cloaked ships (subtle shimmer over
                    // elapsed wall-clock); fully solid otherwise. The server
                    // already withheld undetectable enemies, so anything here is
                    // legitimately visible to this client's side.
                    float shipOpacity = _cloakedShips.Contains(id)
                        ? Flight.CloakVisual.Opacity(gameTime.TotalGameTime.TotalSeconds)
                        : 1f;

                    // P628 — level-of-detail selection. The projected on-screen
                    // pixel radius (native GetImageRadius, context.cpp:1858-1871)
                    // drives both the far-tier crossfade and native LODGeo level.
                    float viewH = GraphicsDevice.Viewport.Height;
                    float worldRadius = _shipRadiusById.TryGetValue(id, out var wr) && wr > 0f ? wr : 1f;
                    float px = Rendering.ShipLod.ProjectedPixelRadius(eyeRel, worldRadius, view, proj, viewH);
                    // P759 — apply the LOD slider bias before tier/LODGeo selection
                    // (native SetLOD(screenRadius * lodBias), thinggeo.cpp:1624).
                    _shipLodProfile.LodBias = _lodControl.Bias;
                    px = Rendering.ShipLod.ApplyLodBias(px, _shipLodProfile);
                    float farAmt = Rendering.ShipLod.FarAmount(px, _shipLodProfile);
                    bool wasFar = _shipLodFar.TryGetValue(id, out var wf) && wf;
                    bool isFar = Rendering.ShipLod.NextFarState(px, wasFar, _shipLodProfile);
                    _shipLodFar[id] = isFar;
                    if (isFar) _lodFarCount++; else _lodFullCount++;

                    // P628 — draw the FULL model component whenever the far tier
                    // hasn't fully taken over (crossfade: model fades out as the
                    // sprite fades in → no popping). Within the full tier the
                    // native LODGeo level is chosen by projected pixel radius.
                    if (farAmt < 1f)
                    {
                        float modelOp = shipOpacity * Rendering.ShipLod.ModelAlpha(farAmt, _shipLodProfile);
                        var shipModel = _shipModelById.TryGetValue(id, out var m) ? m : null;
                        // P566 — NATIVE ship path (default): draw the original .mdl
                        // mesh + native texture at native scale/orientation under
                        // pure native sector lighting. When the hull's native art
                        // failed to decode (ref null / geometry null) it falls
                        // through to the Meshy content path below.
                        if (ShipsNativeFor(id)
                            && _nativeShipRefById.TryGetValue(id, out var nref) && nref is not null
                            && _nativeShipRenderer.MeshFor(nref.ModelStem!) is { } ngeo0)
                        {
                            var ntex0 = nref.TextureStem is not null ? _nativeShipRenderer.TextureFor(nref.TextureStem) : null;
                            // P628 native LOD: select the LODGeo level by projected
                            // pixel radius (geometry.cpp LODGeoImpl::Render), only
                            // when fully in the near tier (during the crossfade the
                            // base mesh is kept so the fade-out is seamless).
                            var drawGeo = ngeo0;
                            if (farAmt <= 0f && _nativeShipRenderer.LodLevelCount(nref.ModelStem!) > 0)
                            {
                                int lev = Rendering.ShipLod.NativeLodLevelFor(px, _nativeShipRenderer.LodPixelRadii(nref.ModelStem!));
                                if (lev > 0)
                                    drawGeo = _nativeShipRenderer.LodLevel(nref.ModelStem!, lev - 1) ?? ngeo0;
                            }
                            _nativeShipRenderer.Draw(drawGeo, ntex0, worldMat, view, proj, modelOp, nref.NativeScale, _sectorLighting, nref.ModelStem, nref.TextureStem);
                            _lodDrawCalls++;
                        }
                        else
                        {
                            // P333 — PURE VISUAL scale: normalize this ship's model to
                            // its real hull size (1.0 when unknown). Affects rendering
                            // only; the server's collision radius is untouched.
                            float scale = _shipScaleById.TryGetValue(id, out var s) && s > 0f ? s : 1f;
                            // P337 — the real texture bound to this ship's model (paired
                            // by the same resolved asset); fallback texture when the ship
                            // fell back to the fallback model (resolved asset null).
                            var shipTex = _shipTextureById.TryGetValue(id, out var t) && t is not null
                                ? t : _fallbackTexture;
                            // P607 — never pass null to ShipRenderer.Draw: when a
                            // ship has no native art (failed resolution) and no
                            // Meshy pack fallback (both shipModel and _fallbackModel
                            // null — the P607 live crash), draw the guaranteed
                            // procedural stand-in so the ship stays visible and
                            // targetable, and log it once by ship id.
                            var drawModel = shipModel ?? _fallbackModel;
                            if (drawModel is null)
                            {
                                drawModel = _shipStandInModel;
                                if (_standInLoggedShips.Add(id))
                                    Console.WriteLine($"[client] P607 unresolvable ship #{id} (no native art, no Meshy pack) — rendering procedural stand-in");
                            }
                            _shipRenderer.Draw(drawModel, worldMat, view, proj, modelOp, scale, shipTex, _sectorLighting);
                            _lodDrawCalls++;
                        }
                    }

                    // P628 — draw the FAR sprite component (lit, side-tinted point)
                    // batched one call per hull type; no trails/ribbons at this tier.
                    if (farAmt > 0f)
                    {
                        var (sr2, sg2, sb2) = _shipSideRgbById.TryGetValue(id, out var sc) ? sc : (1f, 1f, 1f);
                        float alpha = Rendering.ShipLod.SpriteAlpha(farAmt, px, _shipLodProfile);
                        // Size the billboard so it projects to a small readable point.
                        float targetPx = 5f;
                        float dist = eyeRel.Length();
                        float scalePx = (0.5f * viewH) * proj.M22;
                        float half = dist > 1e-4f && scalePx > 0f ? targetPx * dist / scalePx : worldRadius;
                        half = MathF.Min(half, worldRadius * 3f);

                        string lookKey = "";
                        if (ShipsNativeFor(id)
                            && _nativeShipRefById.TryGetValue(id, out var fnref) && fnref is not null
                            && fnref.TextureStem is { } tstem
                            && _nativeShipRenderer.TextureFor(tstem) is { } ftex)
                        {
                            lookKey = tstem;
                            RegisterLodTexture(lookKey, ftex);   // one draw call per hull type
                        }
                        var col = new Color(sr2, sg2, sb2, alpha);
                        _drawFarSprites.Add(new Rendering.FarSprite(lookKey, eyeRel, half, col, 0f));
                    }
                }

                // P628 — draw the batched far-tier sprites (one call per hull type)
                // AFTER the opaque scene so nearer geometry still occludes them via
                // the depth test.
                if (_shipLodSpriteRenderer is { } spr)
                    _lodDrawCalls += spr.Draw(_drawFarSprites, view, proj);

                // P218 — asteroids. ClientWorld.Asteroids is NOT sector-filtered
                // (fine for the targeting code, which frustum-projects every
                // object anyway; wrong for a draw loop, which would otherwise try
                // to render rocks from all 13 sectors of the "Combat Demo" map at
                // once — 134 total, per vizfight's own [boot] log). So filter to
                // the local player's OWN sector: ast.ClusterId == the own ship's
                // SectorId (the same cluster concept set from SET_CLUSTER/
                // SHIP_STATUS — ClientWorld.cs:164,180). World access rides the
                // same Gate lock as every other read; the actual draws happen
                // after the lock is released. World access + the per-rock
                // depth-world gather (with the spin advance) now happen once in
                // PrepareSceneGeometry at the top of the frame, so the depth pass
                // and this color pass share the exact same world matrices.
                // Asteroids are static (exported once at join, never re-sent), so
                // their pose/radius are real wire data, and each rock is scaled by
                // its true Radius so bigger asteroids actually look bigger.
                foreach (var (spin, rPos, rOri, rRadius, variant, type, modelStem, textureName) in _drawRooks)
                {
                    var rXnaPos = PoseConverter.ToXna(rPos);
                    if (OutsideBenchmarkView(rXnaPos, cameraEye, rRadius)) continue;
                    var rRot = PoseConverter.ToXnaRotation(rOri);
                    // P252 — apply the accumulated spin (already advanced for this
                    // frame in PrepareSceneGeometry) about the rock's own
                    // (body-local) axis as a post-multiply of the base
                    // orientation, exactly native's o.PostRotate(axis, angle*dt)
                    // composed over time.
                    var spinAxis = new Vector3(spin.Axis.X, spin.Axis.Y, spin.Axis.Z);
                    var spinRot = Matrix.CreateFromAxisAngle(spinAxis, spin.Angle);
                    var rWorld = rRot * spinRot * Matrix.CreateScale(rRadius) * Matrix.CreateTranslation(rXnaPos - cameraEye);
                    var rWorldNoScale = rRot * spinRot * Matrix.CreateTranslation(rXnaPos - cameraEye);
                    // P567 — render each asteroid from its OWN native .mdl mesh +
                    // native texture (the P566 native pipeline), folding the radius
                    // into the native uniform scale radius/meshRadius. Falls back to
                    // the imported variant (then procedural) when native can't decode,
                    // or when --asteroids imported forces the shared-variant path.
                    var nativeGeo = _asteroidsNative ? _nativeAsteroidRenderer.GeometryFor(modelStem) : null;
                    if (nativeGeo is not null)
                    {
                        float nativeScale = Rendering.NativeAsteroidResolver.ScaleForRadius(rRadius, nativeGeo.MeshRadius);
                        string? texStem = _nativeAsteroidRenderer.TextureStemFor(modelStem, textureName);
                        var nativeTex = texStem is null ? null : _nativeAsteroidRenderer.TextureFor(texStem);
                        _nativeAsteroidRenderer.Draw(nativeGeo, nativeTex, rWorldNoScale, view, proj, nativeScale, _sectorLighting, modelStem, texStem);
                    }
                    else
                    {
                        // P286 — draw the asteroid's chosen imported mesh variant
                        // with the diffuse generated for its resource type, so a
                        // sector shows real shape + colour variety. Textures are
                        // built lazily and cached (a handful of types, not per rock).
                        _asteroidRenderers[variant].Draw(rWorld, view, proj, EnsureAsteroidTexture(type), _sectorLighting);
                    }
                }

                // P227 — warps. Same sector-filter discipline as the P218
                // asteroids above: ClientWorld.Warps is populated from every
                // OT_warp on join (ExportWarp) regardless of sector, so a draw
                // loop must filter to the local player's OWN sector
                // (warp.ClusterId == mySector) or it would render every warp on
                // the map at once. Unlike an asteroid, a warp carries no Up
                // vector — only Forward + a Rotation roll (igc.h:2506 layout) —
                // and the gate is rotationally symmetric about its forward, so
                // the Orientation is built from the single-arg Set(forward)
                // (the eject-branch precedent, Orientation.cs:124) with no
                // Scale = the warp's real Radius, then P237's
                // VISUAL-ONLY multiplier (9.5) applied to the rim so the funnel
                // reads at Domingo's accepted ~10x-the-size-of-a-ship figure
                // (see WarpRenderer.VisualScaleMultiplier for the calibration
                // against the ~190-unit rendered ship model). The real gameplay
                // Radius is untouched everywhere else.
                _drawWarps.Clear();
                _warpBombRemaining.Clear();
                lock (_session.Gate)
                {
                    if (TrySceneSector(world, me, out short myWarpSector))
                    {
                        // P758 — snapshot the live warp-bomb countdowns with
                        // the same gate hold as the warp table itself.
                        double serverNow = world.ServerClockSeconds;
                        foreach (var (bombId, bomb) in world.WarpBombs)
                            _warpBombRemaining[bombId] = bomb.ExplosionServerSeconds - serverNow;
                        foreach (var (_, wp) in world.Warps)
                        {
                            if (wp.ClusterId != myWarpSector) continue;
                            var wo = new Orientation();
                            wo.Set(Vec(wp.Forward));
                            _drawWarps.Add((wp.WarpId, Vec(wp.Position), wo, wp.Radius, wp.TextureName));
                        }
                    }
                }
                double dt = gameTime.ElapsedGameTime.TotalSeconds;
                _flightEffects.Advance((float)dt);
                foreach (var (wId, wPos, wOri, wRadius, wTex) in _drawWarps)
                {
                    var wXnaPos = PoseConverter.ToXna(wPos);
                    var wRot = PoseConverter.ToXnaRotation(wOri);
                    // P562 — the whole-mesh rigid spin is gone; the vortex now
                    // flows from the native animated UVs inside WarpRenderer.
                    var wWorld = wRot * Matrix.CreateScale(wRadius * Rendering.WarpRenderer.VisualScaleMultiplier)
                                 * Matrix.CreateTranslation(wXnaPos - cameraEye);
                    // P585 — an active transit pulse on this warp brightens its
                    // funnel (1.0 idle, no change when the glimpse/opt-in is off).
                    float funnelBoost = 1f + (Rendering.WarpRenderer.PeakFunnelBoost - 1f)
                                        * _warpPulses.Strength(wId, _gameSeconds);
                    // P758 — a bombed aleph destabilizes (WARP_BOMB(188) →
                    // AddBomb, appmsg.cpp:858-866): pulse the funnel red until
                    // the carried detonation deadline.
                    float destabilize = _warpBombRemaining.TryGetValue(wId, out double remaining)
                        ? Rendering.WarpBombVisual.Pulse(remaining, _gameSeconds)
                        : 0f;
                    _warpRenderer.Draw(wWorld, view, proj, dt,
                        wRadius * Rendering.WarpRenderer.VisualScaleMultiplier, wTex, wId, funnelBoost, destabilize);
                }

                // P289 — stations. Same sector-filter discipline as the P218
                // asteroids / P227 warps above: ClientWorld.Stations is keyed
                // by StationId and populated from every OT_station on join,
                // regardless of sector, so a draw loop must filter to the local
                // player's OWN sector (st.ClusterId == mySector) or it would
                // render every station on the map at once. A station carries
                // Forward + Up (unlike a warp), so the full Orientation is
                // built the same way as the P218 asteroid loop. Scale = the
                // real DataStationTypeIGC.Radius for the station's type (via
                // the shop catalogue's station-type table, loaded once at boot
                // from the same PCore001.igc the server boots); friendly vs
                // enemy/neutral tint mirrors this client's side-colour
                // convention (friendly = HUD teal accent, hostile = warm). The
                // world access + depth-world gather happen once in
                // PrepareSceneGeometry at the top of the frame.
                // P367 — clear the per-frame nav-strobe draw list up front; ship
                // strobes are appended in the ship-effects pass below (drawn once).
                _drawNavStrobes.Clear();
                foreach (var (sPos, sOri, sSide, sRadius, sModel, sTex) in _drawStations)
                {
                    var sXnaPos = PoseConverter.ToXna(sPos);
                    if (OutsideBenchmarkView(sXnaPos, cameraEye, sRadius)) continue;
                    var sRot = PoseConverter.ToXnaRotation(sOri);
                    // P564 — draw each station from its ORIGINAL native .mdl station
                    // model + native texture at native scale/orientation (native
                    // RY(π) flip + uniform scale radius/meshRadius folded in by the
                    // renderer), lit by the sector light, with NO side tint (native
                    // does not tint station meshes). When the type's art is missing /
                    // undecodable (null stem / geometry) fall back to the procedural
                    // placeholder hub+ring, side-tinted as before (logged once).
                    if (!string.IsNullOrEmpty(sModel) && _nativeStationRenderer.MeshFor(sModel) is { } ngeo)
                    {
                        var ntexStem = _nativeStationRenderer.TextureStemFor(sModel, sTex);
                        var ntex = ntexStem is null ? null : _nativeStationRenderer.TextureFor(ntexStem);
                        float nscale = Rendering.NativeStationResolver.ScaleForRadius(sRadius, ngeo.MeshRadius);
                        var sWorldNS = sRot * Matrix.CreateTranslation(sXnaPos - cameraEye);
                        _nativeStationRenderer.Draw(ngeo, ntex, sWorldNS, view, proj, nscale, _sectorLighting, sModel, ntexStem);
                        if (Rendering.NativeStationLights.IsVisible(sXnaPos - cameraEye, sRadius,
                                view, proj, GraphicsDevice.Viewport.Width))
                            Rendering.NativeStationLights.Append(_nativeStationRenderer.LightsFor(sModel),
                                sRot * Matrix.CreateTranslation(sXnaPos), nscale, (float)_nowSeconds, _drawNavStrobes);
                    }
                    else
                    {
                        var sWorld = sRot * Matrix.CreateScale(sRadius) * Matrix.CreateTranslation(sXnaPos - cameraEye);
                        var sTint = (world.MySideId ?? -1) == sSide
                            ? Rendering.StationRenderer.FriendlyColor
                            : Rendering.StationRenderer.EnemyColor;
                        _stationRenderer.Draw(sWorld, view, proj, sTint, _sectorLighting);
                    }


                }

                // P294 — in-progress station constructions. Like stations/probes,
                // ClientWorld.BuildingEffects is keyed by the target asteroid id
                // and populated from every OT_buildingEffect regardless of
                // sector, so the draw loop filters to the local player's OWN
                // sector (be.ClusterId == mySector). A construction reuses the
                // station mesh (BuildingEffectRenderer) — rendered translucent in
                // native's building-effect violet and scaled by its progress
                // fraction so it grows toward the finished station, reading
                // unambiguously "under construction" rather than a finished base.
                // Completed builds are pruned here (they vanish at
                // BuildingEffect.BuildDuration); interruption removes them via
                // BUILDINGEFFECT_DESTROYED(181) in ClientWorld. Drawn right after
                // the opaque stations so the alpha blend lands over them.
                _drawBuilds.Clear();
                lock (_session.Gate)
                {
                    long buildNow = ClientNowMilliseconds;
                    world.PruneExpiredBuildingEffects(buildNow);
                    if (TrySceneSector(world, me, out short myBeSector))
                    {
                        foreach (var (_, be) in world.BuildingEffects)
                        {
                            if (be.ClusterId != myBeSector) continue;
                            var phase = be.Phase(buildNow);
                            _drawBuilds.Add((PoseConverter.ToXna(phase.Position), phase.Radius, phase.InnerAlpha));
                        }
                    }
                }
                foreach (var (bPos, bRadius, bAlpha) in _drawBuilds)
                {
                    var bWorld = Matrix.CreateScale(bRadius) * Matrix.CreateTranslation(bPos - cameraEye);
                    _buildingEffectRenderer.Draw(bWorld, view, proj, bAlpha, _sectorLighting);
                }

                // P231 + P465b — projectiles. Unlike asteroids/warps a projectile
                // carries no ClusterId/sector on the wire, so the "sector filter"
                // analog here is a distance-based visibility test against the
                // local ship (the closest the wire supports to "is this in my
                // sector" — a same-side teammate's projectile in another sector
                // would otherwise render). Each is positioned by its real
                // creation Position advanced along its Velocity by the elapsed
                // seconds since receipt (PositionAt — simple extrapolation, the
                // brief's explicit scope, so fast shots don't snap between
                // broadcasts), oriented along its actual velocity (falling back
                // to its Forward heading for a near-static bolt), and sized:
                // length ∝ speed (faster shots read as longer streaks) with the
                // type's real Width (ProjectileStyle.For) for the cross-section
                // and the type's real tint. The per-type style is looked up in
                // the loaded static core (cached once in _projectileTypes):
                // real width/colour where the core carries them, else the amber
                // tracer fallback (PCore001 carries no colour and ~no width, so
                // its bolts stay amber at the default 0.35 — see ProjectileStyle
                // remarks). Pruning + gather both ride the same Gate lock as
                // every other world read.
                _drawBolts.Clear();
                lock (_session.Gate)
                {
                    long projNow = ClientNowMilliseconds;
                    world.PruneExpiredProjectiles(projNow);
                    if (world.Ships.TryGetValue(me, out var ownProj) && ownProj.Latest is { } meUp)
                    {
                        var anchorPos = meUp.Position;
                        // P542 — count how many received rounds survive to DRAW with
                        // the exact renderer cull rules (feed the [proj-diag] line).
                        world.DiagnoseDraw(anchorPos, new SimVec(cameraEye.X, cameraEye.Y, cameraEye.Z),
                            projNow, _projectileTypes);
                        if (Environment.GetEnvironmentVariable("ALLEGIANCE_PROJ_DIAG") == "1"
                            && world.Diag.TotalReceived > 0
                            && (LastProjDiagEmitMs <= 0 || projNow - LastProjDiagEmitMs >= 1000))
                        {
                            LastProjDiagEmitMs = projNow;
                            Console.WriteLine($"[proj-diag] {world.Diag.FormatLine()}");
                        }
                        foreach (var pj in world.Projectiles)
                        {
                            var pos = pj.PositionAt(projNow);
                            var toMe = pos - anchorPos;
                            if (toMe.LengthSquared() > ProjectileVisibilityRadiusSqr)
                                continue;

                            SimVec dir;
                            if (pj.Velocity.LengthSquared() > 1e-6f)
                                dir = pj.Velocity.Normalize();
                            else if (pj.Forward.LengthSquared() > 1e-6f)
                                dir = pj.Forward.Normalize();
                            else
                                dir = new SimVec(0f, 0f, -1f);

                            float len = Math.Clamp(pj.Velocity.Length() * BoltLengthPerUnitSeconds, MinBoltLength, MaxBoltLength);

                            // P465b — per-type style ... replaced by P544's native
                            // visual: resolve the per-type render path (model /
                            // directional / billboard / box) once (cached) and its
                            // accumulated screen spin = rotation rad/s × elapsed.
                            var visual = ResolveProjectileVisual(pj.ProjectileTypeId);
                            float elapsed = Math.Max(0f, (projNow - pj.ReceivedLocalMs) / 1000f);
                            float spin = visual.Rotation * elapsed;

                            // P239 — skip any bolt whose solid geometry is inside
                            // (or within ProjectileNearCamClearance of) the ACTIVE
                            // camera eye. Without this, a bolt spawned at ship
                            // center (no muzzle offset) momentarily engulfs the
                            // 14-unit-forward cockpit eye and floods the screen
                            // (see ProjectileCameraCull). Applies to both camera
                            // modes via the active eye; chase-cam's eye is far
                            // behind the ship so it rarely triggers there.
                            if (ProjectileCameraCull.ShouldCull(
                                    cameraEye.X, cameraEye.Y, cameraEye.Z,
                                    pos.X, pos.Y, pos.Z,
                                    dir.X, dir.Y, dir.Z,
                                    len, ProjectileCullWidth(visual), ProjectileNearCamClearance))
                                continue;

                            _drawBolts.Add((pos, dir, len, spin, visual));
                        }

                        // P462 — the local own-shot echo's rounds render through
                        // the SAME projectile bolt path as the server echoes
                        // (identical culling, sizing, orientation, tint), so a
                        // shot pulled this frame shows a tracer immediately —
                        // no round trip. Its server echo is de-duped (OwnShotEcho.
                        // ShouldDropServerEcho), so it appears exactly once.
                        AddLocalBolts(projNow, anchorPos, cameraEye);
                    }
                }
                foreach (var (bPos, bDir, bLen, bSpin, bVis) in _drawBolts)
                {
                    var bXna = PoseConverter.ToXna(bPos);
                    var bXnaDir = new Vector3(bDir.X, bDir.Y, bDir.Z);

                    // P544 — dispatch each round to its native render path. A
                    // missing texture/model degrades to the box below (never an
                    // invisible round); the renderers restore render state after.
                    switch (bVis.Kind)
                    {
                        case ProjectileVisualKind.Billboard:
                        {
                            var tex = _projectileSpriteRenderer.TextureFor(bVis.TextureName);
                            if (tex is not null)
                            {
                                float half = bVis.Radius * Rendering.ProjectileSpriteRenderer.HalfExtentScale;
                                _projectileSpriteRenderer.DrawBillboard(
                                    bXna - cameraEye, tex, half, bSpin,
                                    new Vector3(bVis.R, bVis.G, bVis.B), view, proj);
                                continue;
                            }
                            break; // missing texture -> box
                        }
                        case ProjectileVisualKind.Directional:
                        {
                            var tex = _projectileSpriteRenderer.TextureFor(bVis.TextureName);
                            if (tex is not null)
                            {
                                // Beam length along travel = native radius (the
                                // directional decal's thing-site radius), cross =
                                // real width.
                                float length = bVis.Radius > 0f ? 2f * bVis.Radius : bLen;
                                _projectileSpriteRenderer.DrawDirectional(
                                    bXna - cameraEye, bXnaDir, tex, length, bVis.EffectiveWidth,
                                    bSpin, new Vector3(bVis.R, bVis.G, bVis.B), view, proj);
                                continue;
                            }
                            break; // missing texture -> box
                        }
                        case ProjectileVisualKind.Model:
                        {
                            var geo = _projectileModelRenderer.MeshFor(bVis.ModelName);
                            if (geo is not null)
                            {
                                var tex = _projectileModelRenderer.TextureFor(bVis.ModelName);
                                _projectileModelRenderer.Draw(geo, tex, bXna - cameraEye, bXnaDir, bSpin, view, proj, modelName: bVis.ModelName, lighting: _sectorLighting);
                                continue;
                            }
                            break; // missing mesh -> box
                        }
                        default:
                            break; // box kind -> box
                    }

                    // Fallback box (missing asset / unresolvable type): keep today's
                    // P465b amber tracer so the round never renders invisible.
                    var bOrient = new Orientation();
                    bOrient.Set(bDir);
                    var bRot = PoseConverter.ToXnaRotation(bOrient);
                    var boxTint = new Vector3(ProjectileVisual.Amber.R,
                                              ProjectileVisual.Amber.G,
                                              ProjectileVisual.Amber.B);
                    var bWorld = Matrix.CreateScale(bVis.EffectiveWidth, bVis.EffectiveWidth, bLen) * bRot
                                 * Matrix.CreateTranslation(bXna - cameraEye);
                    _projectileRenderer.Draw(bWorld, boxTint, view, proj);
                }

                // P462 — gather the local own-shot echo's live rounds into the
                // projectile bolt draw list through the IDENTICAL path as the
                // server-echo bolts above: distance-visibility against the local
                // ship, near-camera cull, and velocity-sized/oriented streaks. A
                // shot's tracer therefore appears on the trigger frame (no round
                // trip) and behaves exactly like a server bolt would have.
                void AddLocalBolts(long nowLocalMs, SimVec anchorPos, Vector3 cameraEye)
                {
                    foreach (var lr in _ownShotEcho.GetActiveRounds(nowLocalMs))
                    {
                        var pos = lr.Position + lr.Velocity * ((nowLocalMs - lr.EmittedLocalMs) / 1000.0f);
                        var toMe = pos - anchorPos;
                        if (toMe.LengthSquared() > ProjectileVisibilityRadiusSqr) continue;

                        SimVec dir;
                        if (lr.Velocity.LengthSquared() > 1e-6f)
                            dir = lr.Velocity.Normalize();
                        else if (lr.Forward.LengthSquared() > 1e-6f)
                            dir = lr.Forward.Normalize();
                        else
                            dir = new SimVec(0f, 0f, -1f);

                        float len = Math.Clamp(lr.Velocity.Length() * BoltLengthPerUnitSeconds, MinBoltLength, MaxBoltLength);

                        // P465b/P544 — the local own-shot echo styles its tracer by
                        // the SAME per-type native visual lookup as a server bolt,
                        // so pulling the trigger shows the real visual of the round
                        // that weapon actually fires (LocalRound.ProjectileTypeId),
                        // with the same elapsed spin.
                        var visual = ResolveProjectileVisual(lr.ProjectileTypeId);
                        float spin = visual.Rotation * Math.Max(0f, (nowLocalMs - lr.EmittedLocalMs) / 1000f);

                        if (ProjectileCameraCull.ShouldCull(
                                cameraEye.X, cameraEye.Y, cameraEye.Z,
                                pos.X, pos.Y, pos.Z,
                                dir.X, dir.Y, dir.Z,
                                len, ProjectileCullWidth(visual), ProjectileNearCamClearance))
                            continue;

                        _drawBolts.Add((pos, dir, len, spin, visual));
                    }
                }

                // P234 — missiles. Like asteroids/warps, ClientWorld.Missiles is
                // populated from every FIRE_MISSILE(70) / per-tick
                // MISSILE_UPDATE(205) regardless of sector, so the draw loop
                // filters to the local player's OWN sector: missile.ClusterId ==
                // mySector (the same cluster concept set from SET_CLUSTER/
                // SHIP_STATUS). Each instance is drawn at the server-authoritative
                // Position from the per-tick 205 stream (no extrapolation — the
                // client can't simulate homing and doesn't need to, at 20 Hz the
                // server re-positions it every tick), oriented nose-along its
                // Forward via the single-arg Set (the warp precedent: the wire
                // carries no Up, so the roll is chosen deterministically). The
                // real .mdl mesh is shared by every missile; only the world
                // transform differs per instance. Pruning rides the same Gate lock.
                //
                // P235 — while gathering, the same in-sector live missiles are
                // also handed to the exhaust-trail pool as TrailMissile samples
                // (id + position + heading + pool clock), so it can emit the
                // fire/smoke pair each frame and estimate each missile's velocity
                // by finite difference of the successive samples.
                _drawMissiles.Clear();
                _drawTrailMissiles.Clear();
                lock (_session.Gate)
                {
                    long mNow = ClientNowMilliseconds;
                    world.PruneExpiredMissiles(mNow);
                    if (TrySceneSector(world, me, out short myMissileSector))
                    {
                        foreach (var (_, ms) in world.Missiles)
                        {
                            if (ms.ClusterId != myMissileSector) continue;
                            var mo = new Orientation();
                            // P234 orientation: this .mdl's nose is at model +Z
                            // (unlike this port's nose-at--Z ship models), and
                            // ToXnaRotation maps model +Z -> -forward, so negate
                            // the heading to point the nose along real Forward.
                            mo.Set(-ms.Forward);
                            // P288 — per-type imported content model, else null (native demo).
                            _drawMissiles.Add((ms.TypeId, ms.Position, mo, PickMissileModel(ms.TypeId)));
                            _drawTrailMissiles.Add(new TrailMissile(
                                ms.MissileId, ms.Position, ms.Forward, _trailNowSec));
                        }
                    }
                }
                foreach (var (mType, mPos, mOri, mModel) in _drawMissiles)
                {
                    var mXna = PoseConverter.ToXna(mPos);
                    var mRot = PoseConverter.ToXnaRotation(mOri);
                    var mWorld = mRot * Matrix.CreateTranslation(mXna - cameraEye);
                    if (mModel is not null)
                    {
                        // P288 — one of the three imported content models
                        // (mis05/mis400/artifact), drawn like a ship model
                        // (cull-none FBX winding, mesh.ParentBone baked pose).
                        // P337 — bind its real Meshy baseColor (by singleton
                        // model instance, like the P336 scale lookup).
                        var mTex = _missileTextureByModel.TryGetValue(mModel, out var mmt) ? mmt : null;
                        DrawMissileContentModel(mModel, mWorld, view, proj, mTex, _sectorLighting);
                    }
                    else
                    {
                        // P593b — the per-type NATIVE path: resolve this type's
                        // real .mdl modelName + native texture and draw it textured
                        // (closing the lit-solid white-shard defect). Falls back to
                        // the legacy shared demo renderer only when the model truly
                        // can't be parsed (never an invisible missile).
                        var mName = Rendering.MissileModelSource.ResolveModelName(mType, _shopCatalog?.MissileTypes);
                        var mTexOverride = Rendering.MissileModelSource.ResolveTextureName(mType, _shopCatalog?.MissileTypes);
                        var mGeo = _nativeMissileRenderer.MeshFor(mName);
                        if (mGeo is not null)
                        {
                            var mTex = _nativeMissileRenderer.TextureFor(mName, mTexOverride);
                            _nativeMissileRenderer.Draw(mWorld, view, proj, mName, mTex, _sectorLighting);
                        }
                        else
                        {
                            _missileRenderer.Draw(mWorld, view, proj, _sectorLighting);
                        }
                    }
                }


                // P290 — probes. Like missiles, ClientWorld.Probes is keyed by
                // ProbeId from the deploy EXPORT(26)/OT_probe regardless of
                // sector, so the draw loop filters to the local player's OWN
                // sector: pe.ClusterId == mySector (the same cluster concept
                // as warps/asteroids/stations). A probe is static — its
                // EXPORT position is final for its whole life (probeigc.cpp:
                // 115,142) — so each is drawn at that real position with the
                // shared real native probe .mdl mesh (ProbeModelSource, parsed
                // at runtime; no per-instance rotation, the mesh's authored
                // upright is correct). Per-instance friendly/enemy tint follows
                // the probe's owning SideId vs our own (the station
                // side-colour convention), so a player's own deployed probe
                // reads in HUD teal.
                _drawProbes.Clear();
                lock (_session.Gate)
                {
                    if (TrySceneSector(world, me, out short myProbeSector))
                    {
                        foreach (var (_, pe) in world.Probes)
                        {
                            if (pe.ClusterId != myProbeSector) continue;
                            _drawProbes.Add((PoseConverter.ToXna(pe.Position), pe.SideId, pe.ProbetypeId));
                        }
                    }
                }
                foreach (var (pbPos, pbSide, pbType) in _drawProbes)
                {
                    var pbWorld = Matrix.CreateTranslation(pbPos - cameraEye);
                    var pbTint = (world.MySideId ?? -1) == pbSide
                        ? Rendering.StationRenderer.FriendlyColor
                        : Rendering.StationRenderer.EnemyColor;
                    var renderer = ProbeRendererFor(pbType) ?? _probeRenderer;
                    float scale = _shopCatalog?.ProbeTypes?.TryGetValue(pbType, out var type) == true && renderer.MeshRadius > 0
                        ? type.Radius / renderer.MeshRadius : 1;
                    renderer.Draw(Matrix.CreateScale(scale) * pbWorld, view, proj, pbTint, _sectorLighting);
                }

                // P291 — mines. Like probes/missiles, ClientWorld.Mines is keyed
                // by MineId from the deploy EXPORT(26)/OT_mine regardless of
                // sector, so the draw loop filters to the local player's OWN
                // sector: me.ClusterId == mySector. A mine is static — its
                // EXPORT position is final for its life — so each is drawn at
                // that real position as a native MineFieldGeo decal cloud
                // (MineModelSource / MineFieldGeometry / MineRenderer), sized by
                // the type's real MineRadius resolved from the static core.
                _drawMines.Clear();
                lock (_session.Gate)
                {
                    if (TrySceneSector(world, me, out short myMineSector))
                    {
                        var mineTypes = _shopCatalog?.MineTypes;
                        foreach (var (_, me2) in world.Mines)
                        {
                            if (me2.ClusterId != myMineSector) continue;
                            _drawMines.Add((
                                PoseConverter.ToXna(me2.Position),
                                Rendering.MineModelSource.ResolveRadius(me2.MinetypeId, mineTypes),
                                me2.MineId,
                                me2.Strength));   // P574 — decal cloud thins as the field wears
                        }
                    }
                }
                foreach (var (mnPos, mnRadius, mnSeed, mnStrength) in _drawMines)
                    _mineRenderer.DrawCloud(mnPos - cameraEye, mnRadius, mnStrength, mnSeed, cameraEye, view, proj);

                // P292 — chaff. ClientWorld.Chaff is transient (CREATE_CHAFF(109)
                // carries no id and no destroy message), so — like projectiles —
                // the draw loop prunes by local receipt time + the type's real
                // lifespan and uses the distance-from-own-ship visibility filter
                // (the "sector filter" analog for a wire that carries no chaff
                // cluster). Each chaff coasts at its real V0 (ChaffEntity.
                // PositionAt) and grows from a 1.0 floor to its type radius over
                // its lifespan (ChaffAnimation); the renderer draws it as a
                // single camera-facing decal of the type's real texture.
                _drawChaff.Clear();
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var ownChaff) && ownChaff.Latest is { } meChaffUp)
                    {
                        long chaffNow = ClientNowMilliseconds;
                        var chaffTypes = _shopCatalog?.ChaffTypes;
                        world.PruneExpiredChaff(chaffNow, chaffTypes);
                        var anchorChaff = meChaffUp.Position;
                        foreach (var c in world.Chaff)
                        {
                            var pos = c.PositionAt(chaffNow);
                            var toMe = pos - anchorChaff;
                            if (toMe.LengthSquared() > ChaffVisibilityRadiusSqr) continue;
                            float age = Math.Max(0f, (chaffNow - c.ReceivedLocalMs) / 1000.0f);
                            float lifespan = Rendering.ChaffModelSource.ResolveLifespan(c.Etid, chaffTypes);
                            float radius = Rendering.ChaffAnimation.CurrentRadius(
                                age, lifespan, Rendering.ChaffModelSource.ResolveRadiusCeiling(c.Etid, chaffTypes));
                            string stem = Rendering.ChaffModelSource.ResolveStem(c.Etid, chaffTypes);
                            _drawChaff.Add((PoseConverter.ToXna(pos), radius, stem, Rendering.ChaffAnimation.RollAngle(age)));
                        }
                    }
                }
                foreach (var (cfPos, cfRadius, cfStem, cfRoll) in _drawChaff)
                    _chaffRenderer.Draw(cfPos - cameraEye, cfRadius, cfStem, cfRoll, view, proj);

                // P293 — treasures. ClientWorld.Treasures is keyed by TreasureId
                // from the TREASURE(28) spawn broadcast (removed by
                // DESTROY_TREASURE(30)), so — like probes/mines — the draw loop
                // filters to the local player's OWN sector: te.ClusterId ==
                // mySector. Position follows the exported drift curve. P586 — each
                // is now drawn with its RESOLVED NATIVE visual (part/development
                // model, acs14 for cash/power-up, the artifact model, or the
                // biosflag decal for a flag; TreasureVisualResolver), tumbling
                // deterministically per ObjectId; a treasure whose native model
                // is unavailable falls back to the shared gold gem (below).
                _drawTreasureItems.Clear();
                lock (_session.Gate)
                {
                    if (TrySceneSector(world, me, out short myTreasureSector))
                    {
                        foreach (var (_, te) in world.Treasures)
                        {
                            if (te.ClusterId != myTreasureSector) continue;
                            var vis = Rendering.TreasureVisualResolver.Resolve(te.Code, te.PayloadId, _shopCatalog);
                            if (vis.Kind == Rendering.TreasureVisualKind.Gem)
                                LogUnresolvedTreasureOnce(te);
                            _drawTreasureItems.Add(new TreasureDrawItem(PoseConverter.ToXna(te.Position), te.TreasureId, vis));
                            _treasureAge.TryGetValue(te.TreasureId, out double age);
                            _treasureAge[te.TreasureId] = age + Math.Max(0.0, dt);
                        }
                    }
                }
                // Prune ages for treasures that left the sector (destroyed/picked/expired),
                // so a recycled ObjectId never inherits a stale spin phase.
                if (_treasureAge.Count > _drawTreasureItems.Count)
                {
                    var live = new System.Collections.Generic.HashSet<short>();
                    foreach (var it in _drawTreasureItems) live.Add(it.Id);
                    foreach (var id in _treasureAge.Keys.ToList())
                        if (!live.Contains(id)) _treasureAge.Remove(id);
                }
                foreach (var it in _drawTreasureItems)
                {
                    double age = _treasureAge.TryGetValue(it.Id, out double a) ? a : 0.0;
                    if (it.Visual.IsDecal)
                    {
                        if (_nativeTreasureRenderer.DrawDecal(it.Visual, it.Id, it.Position - cameraEye, view, proj, _sectorLighting))
                            continue;
                    }
                    else
                    {
                        var tworld = Matrix.CreateTranslation(it.Position - cameraEye);
                        if (_nativeTreasureRenderer.DrawModel(it.Visual, it.Id, tworld, view, proj, age, _sectorLighting))
                            continue;
                    }
                    // Fallback: the pre-P586 gold gem placeholder.
                    var gw = Matrix.CreateScale(Rendering.TreasureRenderer.DefaultRadius)
                             * Matrix.CreateTranslation(it.Position - cameraEye);
                    _treasureRenderer.Draw(gw, view, proj, dt);
                }
                // P559 — the linear-depth pass moved to the TOP of the 3D scene
                // (right after the geometry gather), so the color target is never
                // switched away from mid-frame; the smoke/fire passes below sample
                // the already-populated depth target via _softContext.

                // P235 — the exhaust trail. Advance the pool's clock by the real
                // elapsed frame time, emit/spawn + age from the gathered samples,
                // then draw every live particle as a camera-facing soft quad on
                // top of the missiles (additive, so the bright flame shows at the
                // tail). Both the pool step and the gather ride the same Gate lock
                // discipline (the gather above); the pool itself is mutated only
                // on this render thread.
                _trailNowSec += dt;
                _trailParticles.Update(_trailNowSec, (float)dt, _drawTrailMissiles, _trailRandom);
                _drawTrailViews.Clear();
                _drawTrailViews.AddRange(_trailParticles.Enumerate());
                // P546 — pass the sector light + near-fade so the ENHANCED smoke
                // path tints/clears it (native path ignores both).
                ReduceBenchmarkEffects(_drawTrailViews);
                ReduceBenchmarkEffects(_drawDebrisViews);
                ReduceBenchmarkEffects(_drawEngineTrailViews);
                _trailRenderer.Draw(_drawTrailViews, cameraEye, view, proj, _sectorLighting, vfxNear, vfxFar, vfxOccluder, _softContext);

                // P296 — the warp-transit teleport spark spray (native
                // AddTeleportParticles, thinggeo.cpp:619-696). If a transit
                // fired last frame's DrawHud (WarpTransitFlash.JustTransited),
                // consume the buffered state and emit one burst from the ship's
                // real velocity-derived spray axes; then age every live spark
                // and draw them additively on top of the scene — both the flash
                // (HUD) and the particle spray thus fire together per transit.
                if (_pendingTeleportBurst is { } burst)
                {
                    _pendingTeleportBurst = null;
                    // P338 — the burst is the LOCAL ship transiting, so size the
                    // spark spray to its real rendered radius (0.5 × HullType.Length)
                    // instead of the fixed 50-unit stand-in; fall back when the
                    // hull is unknown.
                    float transitRadius = _myRenderRadius > 0f ? _myRenderRadius : Rendering.WarpTransitParticles.ShipRadiusDefault;
                    _warpParticles.EmitBurst(
                        burst.Position, burst.Velocity, burst.Forward, burst.Right,
                        transitRadius,
                        Rendering.WarpTransitParticles.EmissionClockSeconds, _trailRandom);
                }

                // P781 — the lead-up spray (native AddTeleportParticles runs every
                // frame for every visible ripcording / aleph-closing ship,
                // thinggeo.cpp:619-696): emit from the emitters the gate-locked
                // pose pass gathered (_teleportEmitters), paced by the fixed 60 Hz
                // emission accumulator (native's per-rendered-frame clock would
                // scale the spray with fps — see WarpTransitParticles remarks).
                EmitTeleportLeadup((float)dt);
                _drawSparkViews.Clear();
                _warpParticles.CopyTo(_drawSparkViews);   // P781 — non-allocating gather (was AddRange(Enumerate()))
                // P781 — the benchmark reducer now runs AFTER the gather (it used
                // to trim the list before it was refilled, a no-op).
                ReduceBenchmarkEffects(_drawSparkViews);
                _warpParticleRenderer.Draw(_drawSparkViews, cameraEye, view, proj);

                // P297 — the continuous damage-debris smoke stream (native
                // AddDamageParticles, thinggeo.cpp:699-755). Emit one puff per
                // damage vent from every currently-damaged visible ship (the
                // real hull fraction → vent count gathered under the Gate
                // above), age every live puff, then draw them additively on top
                // of the scene — the stream runs continuously while a ship is
                // damaged and stops once it is repaired above native's 0.9
                // threshold (DamageDebrisTracker.DamageFrameCount).
                foreach (var kv in _damageEmitters)
                {
                    var em = kv.Value;
                    // P338 — vent offsets scale with the ship's REAL rendered
                    // radius (0.5 × HullType.Length) instead of the fixed 50-unit
                    // stand-in; fall back when the hull was unknown.
                    float emRadius = em.Radius > 0f ? em.Radius : Rendering.DamageDebrisParticles.ShipRadiusDefault;
                    _damageDebris.Emit(em.Position, em.Velocity, em.Orientation,
                        emRadius,
                        kv.Key, em.VentCount, (float)dt, _trailRandom);
                }
                _drawDebrisViews.Clear();
                _drawDebrisViews.AddRange(_damageDebris.Enumerate());
                _damageDebrisRenderer.Draw(_drawDebrisViews, cameraEye, view, proj, _sectorLighting, vfxNear, vfxFar, vfxOccluder, _softContext);

                // P358 — the ship engine-thrust trail (native AddThrustParticles,
                // "rocket" fire+smoke case, thinggeo.cpp:758-917). Emit one
                // fire+smoke pair from the aft of every currently-thrusting
                // visible ship (the emitters + per-ship ramped thrust power
                // gathered under the Gate above), age every live particle, then
                // draw them additively on top of the scene — the trail streams
                // behind a boosting ship and thins/stops as its power decays
                // after the afterburner button is released (the m_fThrustPower>0
                // gate), for the local hull AND every visible remote ship.
                foreach (var kv in _engineTrailEmitters)
                {
                    // P628 — no engine trails at the far tier (distant sprite ships).
                    if (IsFarLod(kv.Key)) continue;
                    var et = kv.Value;
                    _engineTrail.Emit(new Rendering.EngineTrailShip(
                        kv.Key, et.Position, et.Velocity, et.Orientation, et.Radius,
                        _shipThrustPower.GetValueOrDefault(kv.Key)), (float)dt, _trailRandom);
                }
                _drawEngineTrailViews.Clear();
                _drawEngineTrailViews.AddRange(_engineTrail.Enumerate());
                _engineTrailRenderer.Draw(_drawEngineTrailViews, cameraEye, view, proj, _sectorLighting, vfxNear, vfxFar, vfxOccluder, _softContext);

                // P365 — the engine nozzle glow decal (native ThingGeoImpl::
                // RenderGlow, thinggeo.cpp:1419-1443). A static camera-facing soft
                // sprite at the aft nozzle of every currently-thrusting visible
                // ship (the emitters gathered under the Gate above), each tinted
                // and scaled by the REAL HSB thrust math (EngineNozzleGlow) from
                // that ship's live value — so the glow shifts colour/size with
                // thrust (dim small red at low thrust → bright large yellow-white
                // at full thrust/afterburner), for the local hull AND every visible
                // remote ship. Distinct from (and alongside) the P358 exhaust
                // particle plume drawn just above.
                _drawNozzleGlows.Clear();
                foreach (var kv in _nozzleGlowEmitters)
                {
                    var ng = kv.Value;
                    var (nr, ng_, nb, scale) = Rendering.EngineNozzleGlow.Compute(ng.Value);
                    var backward = ng.Orientation.GetBackward();
                    // P566 — anchor the glow at the REAL native engine-nozzle frame
                    // when this ship renders natively (the .mdl FrameData rocket/
                    // thrust/smoke point), instead of the guessed aft-centre. Transforms
                    // the model-space frame through the native RY(π) flip + scale + the
                    // ship's orientation to a world offset. Falls back to the guessed
                    // aft-centre when no native art/frame exists.
                    Allegiance.Core.Collision.Vector3 glowPos;
                    if (ShipsNativeFor(kv.Key) && _nativeShipRefById.TryGetValue(kv.Key, out var ngRef) && ngRef is not null
                        && NativeAftWorldOffset(ngRef, ng.Orientation) is { } nwo)
                        glowPos = ng.Position + nwo;
                    else
                        glowPos = ng.Position + (backward * (ng.Radius * Rendering.EngineNozzleGlowRenderer.AftCenterFraction));
                    float half = scale * ng.Radius * Rendering.EngineNozzleGlowRenderer.GlowSizeFactor;
                    _drawNozzleGlows.Add(new Rendering.EngineGlowView(
                        glowPos, half, new Vector3(nr, ng_, nb)));
                }
                _nozzleGlowRenderer.Draw(_drawNozzleGlows, cameraEye, view, proj);

                // P367 — the navigation strobe lights (native LightsGeo, efapp.cpp:
                // 99-286). Append this frame's ship strobes (the station beacons
                // were added in the station pass above) and draw the combined set
                // once: a small tinted decal at every visible ship's wingtips+stern
                // and the station's authored lights, each sized by the REAL
                // LightsGeo::Render pulse envelope (NavStrobeMath.Pulse) from the
                // elapsed game clock — so the red-port/green-starboard/white-tail
                // lights genuinely blink (ramp up → flash → ramp down → off,
                // alternating sides), for the local hull AND every visible remote
                // ship/structure.
                foreach (var kv in _navStrobeEmitters)
                {
                    var ns = kv.Value;
                    AddNavStrobeAnchors(Rendering.NavStrobeLayout.ShipAnchors(ns.Position, ns.Orientation, ns.Radius), ns.Radius);
                }
                _navStrobeRenderer.Draw(_drawNavStrobes, cameraEye, view, proj);

                // P363 — the ship side-coloured motion trail (native TrailGeo
                // ribbon, efgeo.cpp:492-661). Advance the pool clock (the native
                // `Time` feeding Evaluate), snapshot the per-ship ribbons and
                // draw them additively on top of the scene: a real continuous
                // strip of the ship's real side colour (at half intensity)
                // stretches behind every moving visible ship and fades/retracts
                // when it stops. The anchor/right/colour were gathered under the
                // Gate above.
                _drawMotionTrailStrips.Clear();
                // P628 — no motion ribbons at the far tier (distant sprite ships);
                // filter the enumerated strips by ship id.
                foreach (var strip in _motionTrail.Enumerate())
                    if (!IsFarLod(strip.ShipId)) _drawMotionTrailStrips.Add(strip);
                _motionTrailRenderer.Draw(_drawMotionTrailStrips, cameraEye, view, proj);

                // P263 — explosions. Under the Gate, prune finished blasts and
                // snapshot the local ship's own-sector explosions; the renderer
                // plays them additively on top of the scene with native
                // size/duration/fade (the real sprite strips, not procedural).
                short ownSectorId = -1;
                long nowLocalMs = 0;
                _drawExplosions.Clear();
                lock (_session.Gate)
                {
                    nowLocalMs = ClientNowMilliseconds;
                    world.PruneExpiredExplosions(nowLocalMs);
                    if (TrySceneSector(world, me, out ownSectorId))
                    {
                        foreach (var ex in world.Explosions)
                            if (ex.ClusterId == ownSectorId) _drawExplosions.Add(ex);
                    }
                }
                // P579 — real-window proof hook: deterministically injects
                // synthetic explosions at camera-relative distances (env-gated,
                // inert by default) so brightness + frame-time are measurable in
                // the real backbuffer. Adds to the draw list; the render gate
                // below then draws them like any live blast.
                InjectVolumetricProof(_drawExplosions, ownSectorId, cameraEye, view, nowLocalMs);
                if (_drawExplosions.Count > 0)
                {
                    // P573 — the Volumetric tier replaces the fireball/smoke ball
                    // with raymarched (A) or displaced-sphere (B) volumes; the
                    // native shock-wave ring and CPU sparks/debris are unchanged.
                    if (_effectsQuality == EffectsQuality.Volumetric
                        && _volumetricTech == VolumetricTechnique.RaymarchA && _volumetricRenderer is { } vol)
                        vol.Draw(_drawExplosions, ownSectorId, cameraEye, view, proj,
                            (float)_gameSeconds, _softContext ?? Rendering.SoftParticleContext.Off, nowLocalMs);
                    else if (_effectsQuality == EffectsQuality.Volumetric
                        && _volumetricTech == VolumetricTechnique.DisplacedSphereB && _displacedRenderer is { } disp)
                        disp.Draw(_drawExplosions, ownSectorId, cameraEye, view, proj,
                            (float)_gameSeconds, _softContext ?? Rendering.SoftParticleContext.Off, nowLocalMs);
                    else if (_enhancedExplosionRenderer is { } enhExp)
                        enhExp.Draw(_drawExplosions, ownSectorId, nowLocalMs, cameraEye, view, proj,
                            _sectorLighting, vfxNear, vfxFar, vfxOccluder, _softContext);
                    else
                        _explosionRenderer.Draw(_drawExplosions, ownSectorId, nowLocalMs, cameraEye, view, proj);
                }

                // P364 — the projectile-impact spark (native AddHullHit debris
                // bits, thinggeo.cpp:1324-1345). Under the Gate, drain the hits
                // the world buffered this frame (TakeNewHullHits) keeping only
                // those in the local ship's own sector (the same cluster gate as
                // explosions); then emit the native two-bit pair per hit at the
                // real impact point/normal, age every live bit, and draw them
                // additively on top of the scene. Runs on the render thread after
                // the explosions so a killing blast + the spark that caused it
                // both show; each drained hit is emitted exactly once.
                lock (_session.Gate)
                {
                    _pendingHullHits.Clear();
                    foreach (var hit in world.TakeNewHullHits())
                        if (hit.ClusterId == ownSectorId)
                            _pendingHullHits.Add(hit);
                    if (System.Environment.GetEnvironmentVariable("ALLEGIANCE_DEBUG_FLARE") == "1" && _pendingHullHits.Count > 0)
                        Console.WriteLine($"[flare] ingest {_pendingHullHits.Count} local hits (ownSector={ownSectorId}) firstCluster={_pendingHullHits[0].ClusterId} abs={_pendingHullHits.Count(h => h.AbsorbedByShield)}");
                }
                foreach (var hit in _pendingHullHits)
                {
                    // P463 — a shield-absorbed strike spawns NO hull spark
                    // (native AddHullHit is hull-or-worse only, projectileIGC.cpp:193);
                    // it is the P463b shield-flare signal instead. Only the absorbed
                    // flag being clear warrants the debris bits here.
                    if (hit.AbsorbedByShield) continue;
                    float hitRadius = hit.Radius > 0f
                        ? hit.Radius
                        : Rendering.HullHitSparkParticles.ShipRadiusDefault;
                    _hullHitParticles.Emit(hit.Position, hit.Normal, hitRadius, _trailRandom);
                }
                _drawHullHitViews.Clear();
                _drawHullHitViews.AddRange(_hullHitParticles.Enumerate());
                _drawHullHitFlashes.Clear();
                _drawHullHitFlashes.AddRange(_hullHitParticles.EnumerateFlashes());
                _hullHitSparkRenderer.Draw(_drawHullHitViews, _drawHullHitFlashes, cameraEye, view, proj);

                // P463b — the shield-hit flare (native ThingGeo::AddFlare,
                // thinggeo.cpp:1243-1301; fired from CshieldIGC::ApplyDamage,
                // src/Igc/shieldIGC.cpp:170-171). Drains the SAME this-frame
                // hits the spark pass used (_pendingHullHits, already filtered
                // to the local ship's own sector above), but only the
                // shield-ABSORBED case — a hull strike spawns the spark, never
                // the flare (native fires AddHullHit for hull-or-worse only,
                // projectileIGC.cpp:224). For each absorbed hit, resolve the
                // struck ship's real centre + render scale (P333
                // 0.5 × HullType.Length) under the Gate and emit a flare on its
                // shield toward the hit; the pool ages and fades each over
                // native's 0.75&#8209;s life and the renderer draws them additively on
                // top of the scene. Visible on our own ship in chase view
                // (TargetId == MyShipId is a normal world.Ships member) and on
                // every other ship.
                foreach (var hit in _pendingHullHits)
                {
                    if (!hit.AbsorbedByShield) continue;
                    SimVec flareCentre;
                    float shieldRadius;
                    Orientation? shipOrient = null;
                    short hullId = -1;
                    lock (_session.Gate)
                    {
                        if (world.Ships.TryGetValue(hit.TargetId, out var tgt)
                            && tgt.Latest is { } tu && !tgt.IsNotVisible)
                        {
                            flareCentre = tu.Position;
                            shipOrient = tu.Orientation;
                            hullId = tgt.HullId;
                            float tl = HullLength(tgt.HullId);
                            shieldRadius = tl > 0f
                                ? 0.5f * tl
                                : (hit.Radius > 0f ? hit.Radius : Rendering.ShieldFlareParticles.ShipRadiusDefault);
                        }
                        else
                        {
                            flareCentre = hit.Position;
                            shieldRadius = hit.Radius > 0f ? hit.Radius : Rendering.ShieldFlareParticles.ShipRadiusDefault;
                        }
                    }
                    // P569 — snapshot the struck ship's shield ellipsoid + mesh
                    // radius (native AddFlare inputs) so the renderer wraps the
                    // curved lens30 cap onto the shield.
                    var (ellipse, meshRadius) = ResolveShieldEllipse(hullId, shieldRadius);
                    _shieldFlareParticles.Emit(
                        hit.TargetId, flareCentre,
                        shipOrient ?? Orientation.Identity,
                        hit.Position, ellipse, shieldRadius, meshRadius);
                }
                _drawShieldFlares.Clear();
                _drawShieldFlares.AddRange(_shieldFlareParticles.Enumerate());
                _shieldFlareRenderer.Draw(_drawShieldFlares, cameraEye, view, proj);

                // P547 — bloom composite: bright-pass → blur → additive over the
                // FINAL target (backbuffer or the capture target), then the
                // radar + HUD + SpriteBatch overlays below draw on top of it so
                // they never bloom. When bloom is off nothing here runs.
                _gpuTimer?.Stop();
                _gpuTimer?.Start(_frameTelemetry!.FrameId, post: true);
                _frameTelemetry?.BeginPost();
                RenderTarget2D? finalTarget = capThis ? _capTarget : null;
                Texture2D? upscaleSource = sceneRt;
                if (bloom && _post is { BloomReady: true })
                {
                    GraphicsDevice.SetRenderTarget(fsr ? _fsr!.Bloomed : finalTarget);
                    _post.Bloom!.Render(sceneRt!);
                    if (fsr) upscaleSource = _fsr!.Bloomed;
                }
                if (fsr) _fsr!.Render(upscaleSource!, finalTarget, _upscaling.Sharpness);
                _frameTelemetry?.EndPost();
                _gpuTimer?.Stop();

                // P250 — local current-sector tactical radar. Drawn here (inside
                // the camera block, so view/proj/cameraEye are live) over the 3D
                // scene; the P211 HUD gauges render on top of it afterwards. It
                // only ever appears when the player has a ship + camera, which is
                // exactly when a local tactical picture is wanted.
                // P580 — suppressed while the director is recording (clean b-roll).
                DrawCommandGrid(view, proj, cameraEye);
                if (!_directorActive)
                    DrawRadar(view, proj, cameraEye, gameTime.TotalGameTime.TotalSeconds);

                // P576-B — the aleph destination-glimpse composites AFTER the
                // opaque scene (the warp funnels) so it shows inside each funnel
                // centre rather than being hidden behind the funnel geometry.
                // Drawn here, inside the camera block, so view/proj/cameraEye are
                // live; default OFF (--aleph-glimpse on).
                if (_alephGlimpse && TrySceneSector(world, me, out short glimpseSector)
                    && world.Clusters.TryGetValue(glimpseSector, out var gdc))
                {
                    DrawAlephGlimpses(view, proj, cameraEye, world, gdc);
                }
            }

            // 2D HUD overlay on top of the 3D scene (P211).
            var hudModel = DrawHud();
            DrawCommandHud(hudModel);

            // P501 — semi-transparent touch sticks + buttons, above the HUD but
            // beneath the starmap/chat overlays. Skipped internally when no touch
            // or a gamepad is connected.
            DrawTouchOverlay();

            // P249+P360 — whole-mission starmap / persistent mini-map, over the
            // HUD but beneath the chat overlay so typed text stays readable.
            // ALWAYS drawn in normal flight (no toggle key needed): a compact
            // corner mini-map by default, the full centered pane when Tab
            // expands it. DrawStarmap() internally picks the view from
            // _minimapExpanded.
        // P580 — the director is a clean recording view: suppress the starmap /
        // mini-map pane (and the radar above) while it is active. DrawStarmap()
        // internally picks the view from _minimapExpanded.
        if (!_directorActive)
            DrawStarmap(gameTime);

        // P214 — real chat overlay (log + composing line) on top of the HUD.
        if (!_directorActive && _hudSettings.Style == HudStyle.Classic)
            DrawChat(hudModel);

        // P626 — the in-flight cargo/inventory readout (selected part + hold
        // occupancy), a small line under the chat log so the pilot sees what the
        // NextCargo/NextPart/PrevPart/DropCargo keys are operating on.
        if (!_directorActive && _hudSettings.Style == HudStyle.Classic)
        {
            DrawCargoHud(hudModel);
            DrawFlightPartsHud(hudModel);
        }

        // P759 — the runtime LOD slider overlay (native TK_ToggleLODSlider /
        // m_pwrapImageLOD). A compact horizontal bar, drawn only while toggled
        // on and in flight.
        if (!_directorActive && _lodControl.Visible)
            DrawLodSlider();

            // P217 — the quick-chat voice-macro menu, on top of the chat log
            // (itself above the HUD). Only drawn while a menu level is open.
            if (_qcMenuOpen && _qcMenuNode is { } qn && _qcMenuPath.Count > 0)
                _quickChatRenderer.Draw(qn, _qcMenuPath, GraphicsDevice.Viewport);
        }

        // P752c — the game-over result panel replaces the in-flight UI, so every
        // in-flight pane (scoreboard / pilot info / briefing / team pane) must be
        // hidden while it is up — their translucent panels otherwise show
        // through the game-over panel's edges. The game-over draw below uses the
        // exact same condition.
        bool gameOverUp = (_state is ClientState.Flying or ClientState.ReadyCheck)
            && _session.World.GameOverResult is not null;

        // P265 — B scoreboard overlay: the real in-mission roster (name / side /
        // kills / deaths) from ClientWorld.ScoreRoster. Drawn as a compact panel
        // in the flying state (it is mission data); independent flag from the
        // starmap/help flags, so closing it restores the prior view.
        if (_state == ClientState.Flying && _scoreboardVisible && !gameOverUp)
        {
            var rows = Rendering.ScoreboardLayout.Build(
                _session.World.ScoreRoster, _session.World.RankTable, _session.World.IsSideActive);
            string? self = _session.World.Ships.TryGetValue(
                _session.World.MyShipId ?? -1, out var se) ? se.CharacterName : null;
            _scoreboardRenderer.Draw(GraphicsDevice.Viewport, rows, self);
        }

        // P275 — P Pilot Info overlay: the local player's OWN live stats (rank,
        // kills, deaths, ejections — P774b: the roster fields with a native
        // in-mission source) extracted from the same derived roster as the
        // scoreboard (P774: PLAYERINFO + KILL_SHIP, no 209), with their rank
        // NAME resolved from the RANK_INFO(13) table. Independent flag from the
        // scoreboard, so both can be open at once; drawn only while flying.
        if (_state == ClientState.Flying && _pilotInfoVisible && !gameOverUp)
        {
            var info = Rendering.PilotInfoLayout.Build(_session.World.ScoreRoster,
                _session.World.Ships.TryGetValue(_session.World.MyShipId ?? -1, out var pe) ? pe.CharacterName : null,
                _session.World.RankTable);
            // P761 — the profile-description section rides the same panel (the
            // applied text / live draft + apply hint).
            _pilotInfoRenderer.Draw(GraphicsDevice.Viewport, info, _pilotProfile);
        }

        // P271 — mission-briefing popup (pre-launch): the dismissible briefing
        // panel showing the real SET_BRIEFING_TEXT(9) body + the live "Mission
        // starts in Ns" countdown. Drawn above the HUD/starmap/chat/scoreboard so
        // the briefing is legible during the pre-launch "black screen" moment;
        // below the help keymap and game-over result. Title and body are both the
        // real starting-cluster name the server sends (P121 sends only that one
        // location string with FGenerateCivBriefing=true, so there is no separate
        // mission-title field to draw). Dismissed by Enter/Esc or auto-closed
        // when the countdown expires (IsBriefingVisible).
        // P761b — the briefing yields to the two screens that replace the whole
        // view in native: the character sheet (CharInfoScreen) and the team pane
        // (teampane). The port draws those as overlays, so an armed briefing
        // would show through them (and cover the sheet). Hidden, not dismissed:
        // the briefing state and its countdown are untouched and it reappears
        // when the pane closes, exactly as native's pre-launch briefing survives
        // a trip through a full-screen pane.
        bool paneReplacesBriefing = _pilotInfoVisible || _teamPaneVisible;
        if (_state == ClientState.Flying && !gameOverUp && !paneReplacesBriefing
            && IsBriefingVisible(out int? brRemaining))
        {
            // P759b — when the join-sync carried MISSION_BRIEFING_INFO(996), build
            // the native briefing from the artwork's missionbrief.mdl civ strings
            // (teamscreen.cpp:732-826); when the mdl/artwork is unavailable, fall
            // back to the pre-P759 rendering (the raw starting-cluster name).
            if (_session.World.MissionBriefing is { } briefing)
            {
                try
                {
                    // Native picks the tip once when the popup is built (GetMissionTip):
                    // build once per briefing, not every frame.
                    if (!ReferenceEquals(_builtBriefingFor, briefing))
                    {
                        _builtBriefing = Rendering.MissionBriefingGenerator.Build(briefing);
                        _builtBriefingFor = briefing;
                    }
                    var (title, objective, body) = _builtBriefing;
                    _briefingRenderer.Draw(GraphicsDevice.Viewport, title, body, brRemaining, objective);
                }
                catch (Exception ex) when (ex is ExternalResourceUnavailableException or IOException
                    or InvalidDataException or KeyNotFoundException)
                {
                    string briefText = _session.World.BriefingText;
                    _briefingRenderer.Draw(GraphicsDevice.Viewport, briefText, briefText, brRemaining);
                }
            }
            else
            {
                string briefText = _session.World.BriefingText;
                _briefingRenderer.Draw(GraphicsDevice.Viewport, briefText, briefText, brRemaining);
            }
        }

        // P257 — H keybinding-reference overlay, drawn TOP-MOST over every
        // other overlay (HUD, starmap, chat) so it is fully legible as the
        // reference panel the player consults briefly; it covers in both the
        // lobby and flying states. Because it is an independent flag, closing
        // it reveals the previous overlays exactly as they were.


        // P625 — the Controls (rebind) popup, drawn top-most (above even the
        // help overlay, which it hides on open).
        DrawKeyBindingOverlay();

        // P577 — the in-game Credits/Licences screen, drawn top-most (F12).
        if (_creditsVisible)
            _creditsOverlayRenderer.Draw(GraphicsDevice.Viewport);

        // P268/P273 — the docked/hangar screen, drawn top-most as the docked
        // replacement view (a fullscreen dark panel covers the stationary ship's
        // cockpit scene). Only present while the local ship is actually docked.
        // Its P273 sub-view is the teleport pane (board a teammate's turret /
        // teleport to a station); otherwise the P268 hangar loadout shows.
        if (_investActive && _investDefs is not null)
        {
            // P487 — the investment pane sub-view (native PurchasesPaneImpl),
            // drawn in the same docked-panel style as the teleport/hangar views.
            // P495 — reuses the model RefreshInvestState built this frame (so the
            // radar build-spot highlight and the list agree), with a fallback
            // build in case DrawRadar's camera block didn't run.
            var invModel = _investModel ?? BuildInvestmentModel();
            var invRows = Rendering.InvestmentLayout.Build(invModel).ToList();
            lock (_session.Gate)
                invRows.Insert(2, Rendering.ShopLayout.Row.Of(Rendering.ShopLayout.Kind.Hint,
                    _investmentFunding.Status(_session.World)));
            _dockedScreenRenderer.Draw(GraphicsDevice.Viewport, invRows, Rendering.InvestmentLayout.Detail(invModel), money: _session.World.MyMoney,
                backdropAlpha: Rendering.DockedScreenRenderer.InvestBackdropAlpha,
                footerHeight: _dockedActive ? 84f : 0f, splitDetailPanels: true,
                creditBox: _hudNativeTextures?.CreditBox, nativeMoneyPanel: true);
        }
        else if (_dockedActive && _cameraMode != CameraMode.Command && _teleportActive && _teleportModel is { } telPane)
        {
            var telRows = Rendering.TeleportLayout.Build(telPane);
            _dockedScreenRenderer.Draw(GraphicsDevice.Viewport, telRows, money: _session.World.MyMoney, footerHeight: 84f);
        }
        else if (_dockedActive && _cameraMode != CameraMode.Command && !_hangarOnly && _shopModel is { } dockShop)
        {
            // P519 — render the effective hull's real in-game model orbiting
            // behind the panes at native cmExternalOrbit rates; the translucent
            // panes overlay it so the UI stays readable over the moving preview.
            DrawDockedHullPreview(gameTime, dockShop);
            var shopRows = Rendering.ShopLayout.Build(dockShop);
            // P276 — right-side part-info / connector-compat panel for the
            // currently-highlighted shop row (real stats + fits/no-fit).
            var detailRows = Rendering.PartInfoLayout.Build(dockShop);
            _dockedScreenRenderer.Draw(GraphicsDevice.Viewport, shopRows, detailRows, _session.World.MyMoney,
                backdropAlpha: 0.62f, footerHeight: 84f);
        }

        DrawConsolePanes();
        DrawHangarControls();
        // P639 — Ctrl+T Team Pane: the full-roster screen (every side's pilots
        // with rank name, ship status, kills/deaths, plus money / active-ballot
        // footer). Built from the same ScoreRoster + RankTable as the scoreboard
        // plus the live ship entity table flattened to name→ShipBrief. Shown
        // while flying, independent of the scoreboard/pilot-info flags.
        if (_state == ClientState.Flying && _teamPaneVisible && !gameOverUp)
        {
            IReadOnlyList<Rendering.TeamPaneLayout.Line> lines;
            lock (_session.Gate)
            {
                string? activeBallotText = _session.World.ActiveBallot?.BallotText;
                var ships = _session.World.Ships;
                var briefs = new Dictionary<string, Rendering.TeamPaneLayout.ShipBrief>();
                foreach (var ent in ships.Values)
                    briefs[ent.CharacterName] = Rendering.TeamPaneLayout.BriefOf(_session.World, ent, HullNameForSort);
                // P752 — the pane's selection cursor and the leader's join
                // requests feed the same roster the controls act on.
                var rosterRows = _teamPaneControls.BuildRoster(_session.World, HullNameForSort);
                int sel = _teamPaneControls.Selection;
                string? selectedName = sel >= 0 && sel < rosterRows.Count ? rosterRows[sel].Name : null;
                var joinRequests = rosterRows.Where(r => r.IsJoinRequest).Select(r => r.Name).ToArray();
                lines = Rendering.TeamPaneLayout.Build(
                    _session.World.ScoreRoster, _session.World.RankTable, briefs,
                    _session.World.IsSideActive,
                    _session.World.MyMoney,
                    activeBallotText,
                    _session.World.IsTeamLeader,
                    ships.TryGetValue(_session.World.MyShipId ?? -1, out var tpe) ? tpe.CharacterName : null,
                    $"{KeyOf("TeamPane").Label} TOGGLE | {KeyOf("TeamPaneNextPilot").Label} SELECT | {KeyOf("TeamPaneSort").Label} SORT | D $100 / E $500 / A AUTO / X STOP | {KeyOf("TeamPaneAct").Label} GOTO/ACCEPT | {KeyOf("Scoreboard").Label} LEADERBOARD | {KeyOf("PilotInfo").Label} SHEET",
                    selectedName,
                    joinRequests,
                    sortKey: _teamPaneControls.SortKey);
                lines = lines.Append(new TeamPaneLayout.Line(TeamPaneLayout.LineKind.Hint, ModerationModel.Hint))
                    .Append(new TeamPaneLayout.Line(TeamPaneLayout.LineKind.Note, _moderation.Notice ?? "Steam moderator bans unavailable."))
                    .ToArray();
                if (_consolePanes.Team == TeamConsoleSize.Mini)
                    lines = TeamPaneLayout.Compact(lines, _session.World.ScoreRoster, _session.World.MySideId);
            }
            _teamPaneRenderer.Draw(GraphicsDevice.Viewport, lines, _consolePanes.Team == TeamConsoleSize.Mini);
        }


        if (_helpVisible) _helpOverlayRenderer.Draw(GraphicsDevice.Viewport, _helpManual);
        if ((_dockedActive || _investActive) && !LocalSpectating && !SpectatorCameraActive && !_hideHud && !_hudHidden && !_directorActive)
        {
            string[] objectives;
            lock (_session.Gate)
                objectives = _session.World.LastBaseObjectives().Select(o => o.Text).ToArray();
            _hudRenderer.DrawDockedObjectives(objectives, GraphicsDevice.Viewport);
        }
        DrawMainMenu();
        DrawHangarQuitConfirmation();

        // P262 — game-over result screen, drawn above EVERYTHING (even the help
        // keymap) so it is unmissable: the "why did everything stop" answer.
        // Shows only once the real GAME_OVER(62) wire message has arrived
        // (world.GameOverResult), resolving its real winner/reason via
        // GameOverPanelInfo. The local player's own side (MySideId) decides the
        // victorious/defeated/draw title; all text is real wire data.
        if (_state is ClientState.Flying or ClientState.ReadyCheck
            && _session.World.GameOverResult is { } go)
        {
            // P789 — latch the per-match performance summary once (game over,
            // or leaving early via StopSkirmish/Back) before drawing the panel.
            CaptureSessionPerformance();
            var info = Rendering.GameOverPanelInfo.From(go, _session.World.MySideId);
            // P752 — the two native result tables (side totals + players),
            // built from the live GAME_OVER(62) snapshot by the pure
            // GameOverTablesLayout: rank names from the rank table, side
            // civilizations and colours from the endgame side records, and the
            // commander set from each side's current leader (the port's
            // stand-in for native's endgame command-credit flag — the port has
            // no commander-score path). All reads under the gate lock.
            Rendering.GameOverScreenRenderer.Tables tables;
            lock (_session.Gate)
            {
                var world = _session.World;
                var sideColors = new Dictionary<short, Color>();
                for (int i = 0; i < go.Sides.Length && i < go.NumSides; i++)
                    sideColors[(short)i] = new Color(go.Sides[i].R, go.Sides[i].G, go.Sides[i].B, go.Sides[i].A);
                var commanders = new HashSet<string>();
                foreach (var sideKv in world.Ships.Values.GroupBy(s => s.SideId))
                {
                    short sid = sideKv.Key;
                    short? leaderId = world.TeamLeaderShipIdFor(sid);
                    if (leaderId is { } leader
                        && world.Ships.TryGetValue(leader, out var leaderEnt)
                        && !string.IsNullOrEmpty(leaderEnt.CharacterName))
                        commanders.Add(leaderEnt.CharacterName!);
                }

                short CivOfSide(short sideId) =>
                    sideId >= 0 && sideId < go.Sides.Length ? go.Sides[sideId].CivId : (short)-1;
                tables = new Rendering.GameOverScreenRenderer.Tables(
                    SideRows: Rendering.GameOverTablesLayout.BuildSides(go.Sides, go.NumSides, _gameOverSideSort),
                    PlayerRows: Rendering.GameOverTablesLayout.BuildPlayers(
                        world.GameOverPlayersList,
                        rank => Allegiance.Core.Wire.RankNameLookup.Lookup(world.RankTable, rank),
                        CivOfSide, commanders, go.EjectPods, _gameOverPlayerSort),
                    SideSort: _gameOverSideSort,
                    PlayerSort: _gameOverPlayerSort,
                    SideColors: sideColors);
            }
            _gameOverScreenRenderer.Draw(GraphicsDevice.Viewport, info, tables,
                _session.World.IsIntermission ? _session.World.CountdownRemainingSeconds() : null,
                _perfPanelShown ? _perfPanel : null);
        }

        // P306 — end of frame: if this render was a capture frame, restore the
        // backbuffer target and, at ≤ maxFps, persist the frame + timing. The
        // render-FPS counters accrue every capture render (independent of the
        // PNG write) so disk IO never perturbs the FPS measurement.
        if (capThis && _capActive)
        {
            GraphicsDevice.SetRenderTarget(null);
            _capRenderFrames++;
            double wall = _capClock.Elapsed.TotalSeconds;
            double writeInterval = 1.0 / Math.Max(1.0, _captureSpec!.MaxFps);
            if (wall - _capLastWrite >= writeInterval && _capFrame < _captureSpec.MaxFrames
                && _capSaver is { } saver && _capPixels is { } px && _capTarget is { } tgt)
            {
                _capLastWrite = wall;
                string fn = Path.Combine(_capDir, $"frame_{_capFrame:D6}.png");
                // Async: GetData (GPU readback) on the render thread, PNG encode on the worker.
                if (saver.EnqueueFromTarget(tgt, px, fn))
                {
                    _capWriteTimes.Add(wall);
                    _capFrame++;
                    if (_capFrame % 25 == 0 || _capFrame == 1)
                        Console.WriteLine($"[capture] wrote {fn} (t={wall:0.000}s writes={_capFrame})");
                }
            }
            if (wall >= _captureSpec.DurationSeconds || _capFrame >= _captureSpec.MaxFrames)
                FinishCapture();
        }

        // P417 — end of frame: if a screenshot was requested this frame (F11
        // key, latched in UpdateFlying), capture the now-completed backbuffer to
        // a timestamped PNG (native TK_ScrnShot). Done here — after every draw
        // pass but before Present — so the saved frame is the real rendered one.
        // Purely client-side render-target capture; no wire/server dependency.
        if (_screenshotRequested)
        {
            _screenshotRequested = false;
            try
            {
                string path = Capture.ScreenShot.CaptureBackbuffer(GraphicsDevice);
                Console.WriteLine($"[monogame] screenshot saved: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] screenshot FAILED: {ex.Message}");
            }
        }

        // P572 — a live-proof autoshot is queued: write the named, valid PNG
        // (SaveAsPng, not the P566 bad-CRC encoder) from this completed frame.
        FlushScheduledAutoshot();

        DrawSkirmishStatus();
        CaptureSkirmishProof();
        DrawBenchmark();
        base.Draw(gameTime);
    }

    /// <summary>
    /// P576 — draw the native per-sector sky (or the pre-P576 parallax starfield
    /// fallback before a cluster is known), switching the sky assets on sector
    /// change. The sky is the backdrop, drawn behind everything (the sky renderer
    /// pins its own depth/blend states). Also draws the optional aleph
    /// destination-glimpse when <c>--aleph-glimpse on</c> (default OFF).
    /// </summary>
    private void DrawSkyP576(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        var world = _session.World;
        DataClusterIGC? sky = null;
        if (TrySceneSector(world, RenderShipId(world) ?? -1, out short skySector)
            && world.Clusters.TryGetValue(skySector, out var dc))
        {
            sky = dc;
            if (skySector != _skySector)
            {
                _skySector = skySector;
                _blackSky.SetCluster(dc);
                _nativeSky.SetCluster(dc);
                _hdSky.SetCluster(dc);
            }
        }

        // P588 — the sky tier dispatch: black (default: point stars + native
        // planet, no dome) | native (P576 dome + planet) | hd (P577 telescope).
        bool black = string.Equals(_skyTier, "black", StringComparison.OrdinalIgnoreCase);
        bool hd = string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase);
        if (sky is not null && black)
        {
            _blackSky.Draw(view, projection, cameraEye);
            // (The aleph destination-glimpse composites after the opaque scene —
            // see DrawAlephGlimpses's call site in Draw — so the funnel doesn't
            // cover it.)
        }
        else if (sky is not null && !hd)
        {
            _nativeSky.Draw(view, projection, cameraEye);
            // (The aleph destination-glimpse composites after the opaque scene —
            // see DrawAlephGlimpses's call site in Draw — so the funnel doesn't
            // cover it.)
        }
        else if (sky is not null)
        {
            // P577 — the HD sky tier (real telescope data). Same per-sector
            // structure and cluster switch as native; only the textures and the
            // star layer differ (see HdSkyRenderer). SetCluster happens in the
            // shared sector-change block above.
            _hdSky.Draw(view, projection, cameraEye);
        }
        else
        {
            // No cluster yet — fall back to the old parallax field so there is
            // still a visible backdrop pre-flight.
            _starFar.Draw(view, projection, cameraEye);
            _starMid.Draw(view, projection, cameraEye);
            _starNear.Draw(view, projection, cameraEye);
        }
    }

    /// <summary>
    /// P576-B — gather the current sector's warps that have a resolvable
    /// destination cluster and hand them to the glimpse renderer (which draws a
    /// faint destination-sky view into each funnel centre). Nearest-first +
    /// bounded by the renderer. No-op when the glimpse setting is off (default).
    /// </summary>
    private void DrawAlephGlimpses(Matrix view, Matrix projection, Vector3 cameraEye,
        ClientWorld world, DataClusterIGC current)
    {
        var list = new List<(DataWarpIGC Warp, DataClusterIGC Dest, float DistSq)>();
        foreach (var kv in world.Warps)
        {
            var w = kv.Value;
            if (w.ClusterId != current.ClusterId) continue;   // only this sector's alephs
            if (!world.Clusters.TryGetValue(w.DestinationId, out var dest)) continue;
            float dx = w.Position[0] - cameraEye.X, dy = w.Position[1] - cameraEye.Y, dz = w.Position[2] - cameraEye.Z;
            list.Add((w, dest, dx * dx + dy * dy + dz * dz));
        }
        if (list.Count == 0) return;
        // P585 — pass the per-warp transit-pulse strength (drives the glimpse
        // open-wide + lens-flare) and the flare occlusion test (P547 depth).
        _alephGlimpseRenderer.Draw(list, cameraEye, view, projection, _ => default,
            wId => _warpPulses.Strength(wId, _nowSeconds),
            TransitFlareOccluded);
    }

    /// <summary>
    /// P585 — lens-flare occlusion test: is the aleph funnel centre at screen
    /// position <paramref name="sx"/>,<paramref name="sy"/> (top-left origin)
    /// hidden behind opaque geometry this frame? Samples the P547 linear-depth
    /// target at that pixel and compares against the aleph's own view depth
    /// (<paramref name="viewDepth"/>), both in normalized [0,1] units
    /// (<see cref="Rendering.LinearDepth"/>). When the depth pass did not run
    /// this frame (soft particles off) there is no trustworthy depth, so the
    /// flare is treated as unoccluded.
    /// </summary>
    private bool TransitFlareOccluded(int sx, int sy, float viewDepth)
    {
        var post = _post;
        var depth = post?.DepthRT;
        if (post is null || depth is null || _softContext is null)
            return false;                                  // no fresh depth this frame
        int w = depth.Width, h = depth.Height;
        sx = (int)(sx * (double)w / GraphicsDevice.Viewport.Width);
        sy = (int)(sy * (double)h / GraphicsDevice.Viewport.Height);
        if (sx < 0 || sy < 0 || sx >= w || sy >= h) return false;
        // OpenGL render targets are bottom-left origin; screen coords are
        // top-left, so flip Y before sampling.
        int ty = h - 1 - sy;
        if (ty < 0 || ty >= h) return false;
        try
        {
            var px = new Color[1];
            depth.GetData(0, new Rectangle(sx, ty, 1, 1), px, 0, 1);
            float sceneNorm = Rendering.LinearDepth.Decode(px[0]);
            float alephNorm = Rendering.LinearDepth.Normalize(viewDepth,
                Rendering.LinearDepth.DefaultNear, Rendering.LinearDepth.DefaultFar);
            // Scene is nearer than the aleph → the funnel centre is occluded.
            return sceneNorm < alephNorm - 0.005f;
        }
        catch
        {
            return false;                                  // readback hiccup → treat as visible
        }
    }

    /// <summary>
    /// P547 — the linear-depth pass body: re-render the opaque scene geometry
    /// (ships, asteroids, stations — the exact models/matrices the color pass
    /// used this frame, gathered in the ship/asteroid/station loops) into the
    /// depth target with LinearDepth.fx, so the soft-particle smoke can fade at
    /// hull/rock/station intersections. No-op if the depth effect is missing.
    /// </summary>
    private void DrawSceneDepth(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        var depthEffect = _post?.LinearDepthEffect;
        if (depthEffect is null) return;
        const float near = Rendering.LinearDepth.DefaultNear;
        const float far = Rendering.LinearDepth.DefaultFar;
        foreach (var (model, world, scale) in _drawShipDepth)
            _shipRenderer.DrawDepth(model, world, view, projection, scale, depthEffect, near, far);
        foreach (var (geo, world, scale) in _drawNativeShipDepth)   // P566
            _nativeShipRenderer.DrawDepth(geo, world, view, projection, scale, depthEffect, near, far);
        foreach (var (world, variant, nativeWorld, nativeScale, modelStem) in _drawRookDepth)
        {
            // P567 — the depth pass draws a rock from its native .mdl mesh with the
            // same raw-winding culling as the color pass (CullCounterClockwiseFace),
            // falling back to the imported variant (then procedural) as the color
            // pass does, so the soft-particle depth silhouette always matches.
            var nativeGeo = _asteroidsNative ? _nativeAsteroidRenderer.GeometryFor(modelStem) : null;
            if (nativeGeo is not null)
                _nativeAsteroidRenderer.DrawDepth(nativeGeo, nativeWorld, view, projection, nativeScale, depthEffect, near, far);
            else
                _asteroidRenderers[variant].DrawDepth(world, view, projection, depthEffect, near, far);
        }
        foreach (var (world, nativeWorld, nativeScale, modelStem) in _drawStationDepth)
        {
            // P564 — the depth pass draws a station from its native .mdl mesh with the
            // same raw-winding culling as the colour pass, falling back to the
            // procedural placeholder as the colour pass does, so the soft-particle
            // depth silhouette always matches what the player sees.
            if (!string.IsNullOrEmpty(modelStem) && _nativeStationRenderer.MeshFor(modelStem) is { } ng)
                _nativeStationRenderer.DrawDepth(ng, nativeWorld, view, projection, nativeScale, depthEffect, near, far);
            else
                _stationRenderer.DrawDepth(world, view, projection, depthEffect, near, far);
        }
    }

    /// <summary>P628 — register a far-tier sprite livery texture once per hull
    /// look key so identical hulls share one batched draw call per type.</summary>
    private void RegisterLodTexture(string lookKey, Texture2D tex)
    {
        if (_lodRegisteredTextures.Add(lookKey))
            _shipLodSpriteRenderer?.RegisterTexture(lookKey, tex);
    }

    /// <summary>P628 — is this ship currently in the FAR tier (hysteresis-gated)?
    /// Used to suppress engine trails / motion ribbons and the model depth
    /// silhouette for distant sprite ships.</summary>
    private bool IsFarLod(short id)
        => _shipLodFar.TryGetValue(id, out var f) && f;

    /// <summary>
    /// P559 — gather the opaque scene geometry (ships, asteroids, stations) into
    /// the draw lists ONCE per frame, before the linear-depth pass. Running this
    /// before the color pass lets the depth pass render into its own target
    /// first and the color pass then draw into the (never-switched-away-from)
    /// color target, so ships/asteroids/stations survive the frame (P547's
    /// mid-frame target switch wiped them). Per-object spins advance exactly
    /// once here, so the linear-depth pass and the color pass share byte-
    /// identical world matrices (the color loops below reuse the same
    /// <see cref="_asteroidSpins"/> objects without re-advancing).
    /// </summary>
    private void PrepareSceneGeometry(short me, Vector3 cameraEye, float dtSec)
    {
        var world = _session.World;

        // Reset this frame's depth-gather lists (they were previously cleared
        // in the color pass header) so each frame re-collects exactly its own
        // geometry.
        _drawShipDepth.Clear();
        _drawRookDepth.Clear();
        _drawStationDepth.Clear();
        _drawNativeShipDepth.Clear();   // P566

        // Ships — mirror the color pass's skip-cockpit-own-hull rule; the color
        // loop re-derives the identical deterministic pose/world transform when
        // it draws. Null models (unknown hull) contribute no depth geometry, so
        // the depth silhouettes match exactly what the color pass can draw.
        foreach (var (id, pose) in _poses)
        {
            if ((_cameraMode == CameraMode.Cockpit && id == me) || (_cameraMode == CameraMode.DroneCockpit && id == _commandee?.Id)) continue;
            if (!ShipInDrawnSector(id, me)) continue;
            // P628 — far-tier ships draw as sprites, not models: no depth
            // silhouette (their tiny point must not occlude soft smoke oddly).
            if (IsFarLod(id)) continue;
            var xnaPos = PoseConverter.ToXna(pose.Position);
            var worldMat = PoseConverter.ToXnaWorldRelative(xnaPos, pose.Orientation, cameraEye);
            // P566 — native depth geometry (original .mdl at native scale); else the
            // Meshy content-model depth geometry, mirroring the color pass exactly.
            if (ShipsNativeFor(id)
                && _nativeShipRefById.TryGetValue(id, out var nref) && nref is not null
                && _nativeShipRenderer.MeshFor(nref.ModelStem!) is { } ngeo)
            {
                _drawNativeShipDepth.Add((ngeo, worldMat, nref.NativeScale));
            }
            else
            {
                var shipModel = _shipModelById.TryGetValue(id, out var m) ? m : null;
                if (shipModel is null) continue;
                float scale = _shipScaleById.TryGetValue(id, out var s) && s > 0f ? s : 1f;
                _drawShipDepth.Add((shipModel, worldMat, scale));
            }
        }

        // Asteroids — same sector-filter + one-time spin seeding as the color
        // pass, then advance each spin once and record the depth world matrix.
        _drawRooks.Clear();
        lock (_session.Gate)
        {
            if (TrySceneSector(world, me, out short mySector))
            {
                foreach (var (id, ast) in world.Asteroids)
                {
                    if (ast.ClusterId != mySector) continue;
                    var o = new Orientation();
                    o.Set(Vec(ast.Forward), Vec(ast.Up));
                    if (!_asteroidSpins.TryGetValue(id, out var spin))
                    {
                        spin = AsteroidSpin.FromWire(ast.Rotation);
                        _asteroidSpins[id] = spin;
                    }
                    int variant = AsteroidRenderer.MeshVariantFor(ast.AsteroidId);
                    var type = RockTextureFactory.Classify(ast.AabmCapabilities);
                    // P567 — carry the rock's OWN native model/texture names (its
                    // AsteroidDef, igc.h:1709-1720) so the native path renders each
                    // rock from its own mesh, falling back to the imported variants.
                    _drawRooks.Add((spin, Vec(ast.Position), o, ast.Radius, variant, type, ast.ModelName, ast.TextureName));
                }
            }
        }
        foreach (var (spin, rPos, rOri, rRadius, variant, _, modelStem, _) in _drawRooks)
        {
            spin.Advance(dtSec);
            var rXnaPos = PoseConverter.ToXna(rPos);
            var rRot = PoseConverter.ToXnaRotation(rOri);
            var spinAxis = new Vector3(spin.Axis.X, spin.Axis.Y, spin.Axis.Z);
            var spinRot = Matrix.CreateFromAxisAngle(spinAxis, spin.Angle);
            var rWorld = rRot * spinRot * Matrix.CreateScale(rRadius) * Matrix.CreateTranslation(rXnaPos - cameraEye);
            // P567 — the native path folds the radius into the native uniform scale
            // (radius/meshRadius), so its world carries NO CreateScale(rRadius) — the
            // mesh is scaled in local space by the native renderer before placement.
            var rWorldNoScale = rRot * spinRot * Matrix.CreateTranslation(rXnaPos - cameraEye);
            float meshRadius = _nativeAsteroidRenderer.MeshRadius(modelStem);
            float nativeScale = Rendering.NativeAsteroidResolver.ScaleForRadius(rRadius, meshRadius);
            _drawRookDepth.Add((rWorld, variant, rWorldNoScale, nativeScale, modelStem));
        }

        // Stations — same sector-filter as the color pass; record the depth world.
        // P570 — gathered through Rendering.StationSceneGather, which deduplicates
        // by StationId so each station is drawn exactly once in the colour pass.
        _drawStations.Clear();
        lock (_session.Gate)
        {
            if (TrySceneSector(world, me, out short myStSector))
            {
                _drawStations.AddRange(Rendering.StationSceneGather.ColourPass(world.Stations, myStSector, ResolveStationRadius, ResolveStationModel));
            }
        }
        foreach (var (sPos, sOri, _, sRadius, sModel, _) in _drawStations)
        {
            var sXnaPos = PoseConverter.ToXna(sPos);
            var sRot = PoseConverter.ToXnaRotation(sOri);
            var sWorld = sRot * Matrix.CreateScale(sRadius) * Matrix.CreateTranslation(sXnaPos - cameraEye);
            // P564 — native station: carry the unscaled native world + scale + stem so
            // the depth pass draws the same real .mdl the colour pass does (the
            // renderer folds the native RY(π) flip + scale). Fallback keeps sWorld.
            Matrix nativeWorld = sWorld;
            float nativeScale = 1f;
            string? stem = null;
            if (!string.IsNullOrEmpty(sModel) && _nativeStationRenderer.MeshFor(sModel) is { } ng)
            {
                nativeWorld = sRot * Matrix.CreateTranslation(sXnaPos - cameraEye);
                nativeScale = Rendering.NativeStationResolver.ScaleForRadius(sRadius, ng.MeshRadius);
                stem = sModel;
            }
            _drawStationDepth.Add((sWorld, nativeWorld, nativeScale, stem));
        }
    }

    /// <summary>
    /// HUD-21 — render the selected target's 3-D model preview into the shared
    /// render target for this frame. Caller invokes it right after
    /// <see cref="PrepareSceneGeometry"/> and BEFORE the frame's colour target
    /// is bound, so the RT excursion is lossless (nothing has been drawn to the
    /// colour target yet). Resolves the eligible target under the Gate, then
    /// draws its native mesh outside the lock with the scope-1 matrices
    /// (<see cref="TargetModelPreview"/>). Returns true when a model was
    /// rendered (so the caller can feed the RT to the HUD); false → empty
    /// fallback (the pane looks as it does today).
    /// <para><b>HUD-21b.</b> The gate takes its REAL inputs (own-source climb vs
    /// the target's source ship, same cluster, visible, resolved mesh —
    /// <c>WinTrek.cpp:8783-8818</c>); the camera pose is the ACTIVE camera's
    /// (<c>m_orientation</c>/<c>m_position</c>, <c>WinTrek.cpp:384,436-437</c>),
    /// not the ship's; the radius-1 scale uses native's max-vertex radius
    /// (<see cref="NativeShipRenderer.NativeShipGeometry.MaxVertexRadius"/>); and
    /// the resolved art is cached per <see cref="TargetPreviewKey"/> so the
    /// steady path performs no lookups that allocate. Exactly one rebind of the
    /// caller's colour target (<paramref name="restoreTo"/>).</para>
    /// </summary>
    /// <param name="me">The local ship id.</param>
    /// <param name="cameraEye">The frame's camera position (native m_position).</param>
    /// <param name="view">The frame's view matrix — a CreateLookAt view; its COLUMNS hold the
    /// camera basis (read by TargetModelPreview.CameraBasis).</param>
    /// <param name="restoreTo">The colour target the frame will draw into
    /// (null = backbuffer) — restored once by the preview renderer.</param>
    private bool RenderTargetModelPreview(short me, Vector3 cameraEye, in Matrix view, RenderTarget2D? restoreTo)
    {
        var world = _session.World;
        TargetPreviewKind kind;
        int targetId;
        int targetSourceId;
        int ownSourceId;
        int hullId = -1;
        string? modelStem = null;
        string? textureHint = null;
        bool sameCluster;
        bool visible;
        var targetPos = Vector3.Zero;
        var worldMat = Matrix.Identity;

        lock (_session.Gate)
        {
            if (_autoTarget is not { } t) return false;
            if (!world.Ships.TryGetValue(me, out var ownEnt) || ownEnt.Latest is not { } ownUpd) return false;
            if (ownEnt.SectorId < 0) return false;
            short ownSector = ownEnt.SectorId;
            // Native compares SOURCE ships (WinTrek.cpp:8796-8797): climb the
            // port's ParentId link (turret/drone/gunner → carrier) on BOTH sides.
            ownSourceId = SourceShipId(world, ownEnt);

            targetId = t.Id;
            switch (t.Kind)
            {
                case TargetKind.Ship:
                {
                    if (!world.Ships.TryGetValue(t.Id, out var ship)) return false;
                    var src = ship;
                    int guard = 0;
                    while (src.ParentId >= 0 && world.Ships.TryGetValue(src.ParentId, out var parent)
                        && parent.ShipId != src.ShipId && guard++ < 8)
                        src = parent;
                    if (src.Latest is not { } srcUp) return false;
                    targetSourceId = src.ShipId;
                    sameCluster = src.SectorId == ownSector;   // other cluster
                    visible = !src.IsNotVisible;                // cannot see
                    kind = TargetPreviewKind.Ship;
                    hullId = src.HullId;
                    targetPos = PoseConverter.ToXna(srcUp.Position);
                    worldMat = PoseConverter.ToXnaRotation(srcUp.Orientation);
                    break;
                }
                case TargetKind.Station:
                {
                    if (!world.Stations.TryGetValue(t.Id, out var st)) return false;
                    var (mStem, mTex) = ResolveStationModel(st);
                    if (string.IsNullOrEmpty(mStem)) return false;
                    targetSourceId = -1;                       // a station is no ship
                    sameCluster = st.ClusterId == ownSector;    // other cluster
                    visible = true;
                    kind = TargetPreviewKind.Station;
                    modelStem = mStem;
                    textureHint = mTex;
                    targetPos = PoseConverter.ToXna(Vec(st.Position));
                    // One reusable Orientation (HUD-21 allocated one per frame).
                    _previewOri.Set(Vec(st.Forward), Vec(st.Up));
                    worldMat = PoseConverter.ToXnaRotation(_previewOri);
                    break;
                }
                case TargetKind.Asteroid:
                {
                    if (!world.Asteroids.TryGetValue(t.Id, out var ast)) return false;
                    targetSourceId = -1;                        // an asteroid is no ship
                    sameCluster = ast.ClusterId == ownSector;    // other cluster
                    visible = true;
                    kind = TargetPreviewKind.Asteroid;
                    modelStem = ast.ModelName;
                    textureHint = ast.TextureName;
                    targetPos = PoseConverter.ToXna(Vec(ast.Position));
                    _previewOri.Set(Vec(ast.Forward), Vec(ast.Up));
                    var rot = PoseConverter.ToXnaRotation(_previewOri);
                    // The preview shows the target's CURRENT orientation, so the
                    // client-side spin (P252) is part of it.
                    if (_asteroidSpins.TryGetValue(t.Id, out var spin))
                        rot *= Matrix.CreateFromAxisAngle(new Vector3(spin.Axis.X, spin.Axis.Y, spin.Axis.Z), spin.Angle);
                    worldMat = rot;
                    break;
                }
                default:
                    return false;   // warp/probe/mine/treasure → empty fallback
            }
        }

        // Key → cache → gate (HUD-21b; Claude review moved it into the
        // headless-testable TargetPreviewCache). The art resolve allocates
        // (NativeShipRef, the TextureStemFor closure + string concat), so it runs
        // only when the selected target OR its hull/model identity changes; a
        // target with NO native mesh is cached as a null resolution (resolved
        // once, not every frame) and the gate sees the real mesh-present state.
        var key = new TargetPreviewKey(kind, targetId, hullId, modelStem, textureHint);
        _previewCache ??= new TargetPreviewCache<PreviewArt>(ResolvePreviewArt);
        if (_previewCache.Step(key, targetSourceId, ownSourceId, sameCluster: sameCluster, visible: visible)
            is not { } art)
            return false;

        // The native camera pose (WinTrek.cpp:384,436-437): the CAMERA
        // orientation's up (m_orientation) and m_position for dp. Every flight
        // view is Matrix.CreateLookAt, whose basis is stored in the COLUMNS —
        // TargetModelPreview.CameraBasis reads them (HUD-21b read the rows, the
        // transposed basis). Deviation: native's TurnTo uses m_orientation
        // WITHOUT the head-look (viewOrientation = m_orientationHead ·
        // m_orientation, WinTrek.cpp:426 vs :438); the port's view includes the P624
        // head yaw. The yaw is about the ship's own up, so camUp is unaffected
        // and only the near-vertical ±forward fallback can differ.
        TargetModelPreview.CameraBasis(view, out var camUp, out var camFwd);
        var dp = targetPos - cameraEye;

        // GPU draw outside the gate.
        var previewView = TargetModelPreview.View(dp, camUp, camFwd);
        var proj = TargetModelPreview.Projection();
        var lighting = TargetModelPreview.Lighting(dp);
        switch (kind)
        {
            case TargetPreviewKind.Station:
                _targetModelPreviewRenderer.RenderStation(art.StationGeo!, art.Texture,
                    worldMat, previewView, proj, art.NativeScale, lighting, restoreTo);
                break;
            case TargetPreviewKind.Asteroid:
                _targetModelPreviewRenderer.RenderAsteroid(art.ShipGeo!, art.Texture,
                    worldMat, previewView, proj, art.NativeScale, lighting, restoreTo);
                break;
            default:
                _targetModelPreviewRenderer.RenderShip(art.ShipGeo!, art.Texture,
                    worldMat, previewView, proj, art.NativeScale, lighting, restoreTo);
                break;
        }
        return true;
    }

    /// <summary>HUD-21b — one target's resolved preview art (geometry, texture,
    /// radius-1 scale), cached by <see cref="TargetPreviewCache{TArt}"/>.</summary>
    private sealed class PreviewArt
    {
        public NativeShipRenderer.NativeShipGeometry? ShipGeo;
        public NativeStationRenderer.NativeStationGeometry? StationGeo;
        public Texture2D? Texture;
        public float NativeScale = 1f;
    }

    /// <summary>HUD-21b — the allocating art resolve, run by the preview cache
    /// only on a key change. Null = no native mesh (cached; the gate then shows
    /// the native empty fallback).</summary>
    private PreviewArt? ResolvePreviewArt(TargetPreviewKey key)
    {
        switch (key.Kind)
        {
            case TargetPreviewKind.Ship:
            {
                var nref = ResolveNativeShip((short)key.HullId);
                var geo = nref?.ModelStem is null ? null : _nativeShipRenderer.MeshFor(nref.ModelStem);
                if (geo is null) return null;
                return new PreviewArt
                {
                    ShipGeo = geo,
                    Texture = nref!.TextureStem is null ? null : _nativeShipRenderer.TextureFor(nref.TextureStem),
                    NativeScale = TargetModelPreview.ScaleForMaxVertexRadius(geo.MaxVertexRadius),
                };
            }
            case TargetPreviewKind.Station:
            {
                if (key.ModelStem is null) return null;
                var geo = _nativeStationRenderer.MeshFor(key.ModelStem);
                if (geo is null) return null;
                var tStem = _nativeStationRenderer.TextureStemFor(key.ModelStem, key.TextureHint);
                return new PreviewArt
                {
                    StationGeo = geo,
                    Texture = tStem is null ? null : _nativeStationRenderer.TextureFor(tStem),
                    NativeScale = TargetModelPreview.ScaleForMaxVertexRadius(geo.MaxVertexRadius),
                };
            }
            case TargetPreviewKind.Asteroid:
            {
                if (key.ModelStem is null) return null;
                var geo = _nativeAsteroidRenderer.GeometryFor(key.ModelStem);
                if (geo is null) return null;
                var tStem = _nativeAsteroidRenderer.TextureStemFor(key.ModelStem, key.TextureHint);
                return new PreviewArt
                {
                    ShipGeo = geo,
                    Texture = tStem is null ? null : _nativeAsteroidRenderer.TextureFor(tStem),
                    NativeScale = TargetModelPreview.ScaleForMaxVertexRadius(geo.MaxVertexRadius),
                };
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// HUD-21b — native <c>GetSourceShip()</c>: climb the port's <c>ParentId</c>
    /// link (a turret/drone/gunner's carrier) to the root source ship
    /// (<c>WinTrek.cpp:8796-8797</c>). Cycles are guarded (8 hops) so a bad wire
    /// parent can never hang the frame.
    /// </summary>
    internal static short SourceShipId(ClientWorld world, ShipEntity ship)
    {
        int guard = 0;
        while (ship.ParentId >= 0 && world.Ships.TryGetValue(ship.ParentId, out var parent)
            && parent.ShipId != ship.ShipId && guard++ < 8)
            ship = parent;
        return ship.ShipId;
    }

    /// <summary>
    /// P562 — detect a ship's transit (sector change) for ANY known ship and
    /// play the native star swell on the ARRIVAL aleph. A warp's
    /// <c>DestinationId</c> is its PARTNER warp (see
    /// <see cref="Allegiance.Core.Model.WarpDestinationResolver"/>), so the
    /// arrival aleph is the warp in the NEW sector whose partner sits in the
    /// OLD sector the ship just left. The first sight of a ship only
    /// establishes its baseline sector (no swell — matching
    /// <see cref="Flight.WarpTransitFlash"/>'s once-per-transit edge). Must
    /// be called under <see cref="Session.ConnectionSession.Gate"/>.
    /// </summary>
    private void DetectTransitSwells(ClientWorld world)
    {
        foreach (var (shipId, ent) in world.Ships)
        {
            if (!TryTransit(_lastShipSector, shipId, ent, out short prev)) continue;
            short cur = ent.SectorId;

            // A real transit: swell the arrival aleph (in the new sector) whose
            // partner warp is in the old sector the ship left.
            foreach (var (wId, w) in world.Warps)
            {
                if (w.ClusterId != cur) continue;
                if (world.Warps.TryGetValue(w.DestinationId, out var partner)
                    && partner.ClusterId == prev)
                {
                    _warpRenderer.TriggerTransit(wId);
                    // P585 — the arrival-half of the opt-in transit pulse: the
                    // glimpse opens wide + lens-flare on this arrival aleph.
                    // Opt-in under --aleph-glimpse on (the native star swell above
                    // stays on regardless, as before P585). Coalesces with the own
                    // SET_CLUSTER signal in the drain, same warp, same frame.
                    if (_alephGlimpse)
                    {
                        _warpPulses.Trigger(wId, _nowSeconds);
                        Console.WriteLine($"[transit-pulse] warp #{wId} ARRIVAL pulse (sector change) @t={_nowSeconds:0.00}");
                    }
                }
            }
        }
    }

    /// <summary>P562/P775 review — the per-ship sector-change edge behind
    /// <see cref="DetectTransitSwells"/>: true (with the previous sector) only
    /// when a ship whose sector is KNOWN moved from one known sector to another.
    /// A ship known only from PLAYERINFO (P775: every enemy and drone, every
    /// sector) still holds the C# default <c>SectorId = 0</c> — a real cluster
    /// id — so it records no baseline until real sector telemetry lands
    /// (<see cref="ShipEntity.StatusSectorKnown"/>); otherwise its first status
    /// would read as a "transit" out of cluster 0. Allocation-free.</summary>
    internal static bool TryTransit(Dictionary<short, short> lastShipSector, short shipId, ShipEntity ent, out short prev)
    {
        prev = -1;
        if (!ent.StatusSectorKnown) return false;   // never-posed: no baseline yet
        short cur = ent.SectorId;
        if (!lastShipSector.TryGetValue(shipId, out prev))
        {
            lastShipSector[shipId] = cur;   // first sight: baseline, no swell
            return false;
        }
        if (cur == prev) return false;
        lastShipSector[shipId] = cur;
        return true;
    }

    /// <summary>
    /// P585 — drain ClientWorld's aleph-transit signals (a ship crossing an
    /// aleph in the viewer's sector) into the native star swell and, when the
    /// glimpse is enabled, the transit-pulse controller, then prune expired
    /// pulses. Runs once per frame under the world gate (the caller already
    /// holds it).
    /// </summary>
    private void DrainAlephTransitSignals(ClientWorld world)
    {
        DispatchAlephTransitSignals(
            _alephGlimpse,
            world.DrainAlephTransits(),
            _warpPulses,
            _warpRenderer.TriggerTransit,
            // P599 — log WHOSE transit pulsed (OWN / TEAM / ENEMY vs the
            // viewer's side): the objective evidence that the flare fires
            // for visible ENEMY ships, not only teammates. Only logged when
            // the glimpse pulse actually fires (flag on).
            sig => Console.WriteLine($"[transit-pulse] warp #{sig.WarpId} {TransitActorLabel(world, sig.ShipId)} transit signal (ship {sig.ShipId}) @t={_nowSeconds:0.00}"),
            _nowSeconds);
    }

    /// <summary>
    /// P607 — the dispatch rule for drained aleph-transit signals, extracted
    /// pure so it is unit-testable headless:
    /// <list type="bullet">
    /// <item>The <b>native star swell</b> (<paramref name="swell"/>, i.e.
    /// <c>WarpRenderer.TriggerTransit</c>) fires for EVERY legitimate visible
    /// transit (own, team, enemy, neutral; arrival and departure, per P599's
    /// rules — kills/docks never produce signals) <b>regardless of the glimpse
    /// flag</b>. This is what makes an enemy crossing the aleph visibly swell
    /// even with <c>--aleph-glimpse off</c>.</item>
    /// <item>The <b>glimpse pulse</b> (<paramref name="pulses"/> — funnel
    /// boost, destination glimpse open-wide, lens flare) fires only when
    /// <paramref name="alephGlimpse"/> is on, and only the pulse log
    /// (<paramref name="pulseLog"/>) is emitted then.</item>
    /// </list>
    /// Because <paramref name="swell"/> is idempotent (a same-warp, same-frame
    /// re-trigger just restarts one timeline) a single crossing observed by
    /// both <c>DetectTransitSwells</c> (sector change) and a drained signal
    /// still yields exactly ONE swell — P599's coalescing is preserved, never
    /// a double swell. Same for the pulse: <see cref="WarpPulseController"/>
    /// coalesces same-warp bursts within <see cref="WarpPulseController.MinRepeatGapSeconds"/>.
    /// </summary>
    internal static void DispatchAlephTransitSignals(
        bool alephGlimpse,
        IReadOnlyList<AlephTransitSignal> signals,
        WarpPulseController pulses,
        Action<short> swell,
        Action<AlephTransitSignal>? pulseLog,
        double now)
    {
        foreach (var sig in signals)
        {
            swell(sig.WarpId);                       // native star swell — always (P607 scope 2)
            if (alephGlimpse)
            {
                pulses.Trigger(sig.WarpId, now);     // glimpse funnel-boost/flare — flag-gated
                pulseLog?.Invoke(sig);
            }
        }
        pulses.Update(now);
    }

    /// <summary>
    /// P599 — classify the actor behind an aleph-transit signal relative to the
    /// viewer: OWN (the local ship), ENEMY (a different side), TEAM (same side),
    /// UNKNOWN (never seen — cannot happen for a real signal, but never crashes).
    /// Used to label the <c>[transit-pulse]</c> log so the live proof shows the
    /// flare firing for ENEMY ships, not only teammates.
    /// </summary>
    private static string TransitActorLabel(ClientWorld world, short shipId)
    {
        if (world.MyShipId == shipId) return "OWN";
        if (world.Ships.TryGetValue(shipId, out var e))
        {
            if (world.MySideId is { } my && e.SideId != my) return "ENEMY";
            return "TEAM";
        }
        return "UNKNOWN";
    }

    /// <summary>
    /// P759 — draw the runtime LOD slider overlay (native TK_ToggleLODSlider /
    /// m_pwrapImageLOD). A compact horizontal bar whose thumb position is the
    /// pure <see cref="Rendering.LodControl.Value"/> (0..100); the label is the
    /// applied bias <see cref="Rendering.LodControl.Bias"/> as a percentage.
    /// </summary>
    private void DrawLodSlider()
    {
        var vp = GraphicsDevice.Viewport;
        int barW = 220;
        int barH = 14;
        int x = 24;
        // P759b — h−84 put the bar across the bottom-left console/chat line
        // (e.g. "Ceto secured") and h−70 across the nav-target distance readout;
        // h−120 sits in the clear band between the WEAPONS/BOOST status line and
        // the nav readout so the slider never covers a chat/HUD line.
        int y = vp.Height - 120;
        var track = new Rectangle(x, y, barW, barH);
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            _spriteBatch.Draw(_whitePixel, track, new Color(8, 18, 26));
            int thumb = (int)MathF.Round(barW * _lodControl.Value / (float)Rendering.LodControl.MaxValue);
            _spriteBatch.Draw(_whitePixel, new Rectangle(x + Math.Clamp(thumb - 2, 0, barW - 4), y + 2, 4, barH - 4), new Color(0.40f, 0.88f, 0.84f));
            _spriteBatch.DrawString(_hudSmall, $"LOD {(int)MathF.Round(_lodControl.Bias * 100f)}%",
                new Vector2(x + barW + 8, y - 2), new Color(0.88f, 0.96f, 0.95f));
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>
    /// Build a <see cref="HudFrame"/> from the local ship's most recent real
    /// <c>ServerShipUpdate</c> and hand it to the <see cref="HudRenderer"/>. The
    /// frame is lifted from <see cref="ClientWorld.Ships"/>'s <c>Latest</c> —
    /// the same per-tick wire payload already driving rendering — so every HUD
    /// value is real (no placeholders), with zero extra wire work.
    /// </summary>
    private HudModel DrawHud()
    {
        if (LocalSpectating) return HudModelAdapter.Create(null, _cameraMode, hidden: true);   // SP-5: no pilot HUD while spectating
        var world = _session.World;
        HudFrame? frame = null;
        HudInventoryPane? inventoryPane = null;
        bool hudDocked = _dockedActive, hudDead = false;
        // P799 — native IsGhost for the local player via ClientWorld.OwnIsGhost:
        // mission deaths > iLives (shipIGC.h:2465-2469); see the helper's cite
        // block for the two wire paths the readings come from. The unlimited
        // sentinel can never be exceeded, so unlimited missions never ghost
        // (fLimitedLives, fsmission.cpp:4086).
        bool hudGhost = world.OwnIsGhost;
        lock (_session.Gate)
        {
            // P366 — resolve the leader-only bot dock/launch command for THIS
            // frame (null for a non-leader or when the selected target is not a
            // same-side bot). Cleared every frame here so a stale selection /
            // lost-leadership edge never leaves a phantom prompt.
            _leaderBotCommand = ResolveLeaderBotCommand(world);
            if (RenderShipId(world) is { } me
                && world.Ships.TryGetValue(me, out var entity)
                && entity.Latest is { } up)
            {
                hudDocked = entity.StationId >= 0;
                hudDead = entity.IsDead;
                // HUD-6b — resolve the native data sources once per frame from
                // the same client state the wire already carries (parent/source
                // ship for flag + signature, echoed CoastButton for vector lock).
                bool firing = (up.StateM & (Allegiance.Core.Sim.ShipControlState.AllWeapons | Allegiance.Core.Sim.ShipControlState.OneWeapon)) != 0;
                bool afterburnerActive = (up.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0;
                bool cloakActive = (up.StateM & Allegiance.Core.Sim.ShipControlState.CloakActive) != 0;
                // HUD-12 — the continuous native GetCloaking() fraction from the
                // owner-only OwnShipCloakUpdate(220). Strength is native's
                // 100·(1−Cloaking) (consoledata.cpp:480-489); IsCloaked is
                // Cloaking < 1 (consoledata.cpp:563-575), so the icon persists
                // through the decloak ramp.
                float? cloakStrength = world.MyCloaking is { } cloaking
                    ? Allegiance.Core.Wire.OwnShipCloakUpdate.StrengthPercent(cloaking)
                    : null;
                bool cloaked = world.MyCloaking is { } cv
                    ? Allegiance.Core.Wire.OwnShipCloakUpdate.IsCloaked(cv)
                    : cloakActive;
                var ownStatus = OwnStatusData.FromClientState(entity, world.Ships, _autoPilotEngaged, up.StateM);
                // P228 — feed the local ship's hull AND shield fractions into the
                // damage-flash tracker and read whether the HUD should be flashing
                // NOW. P463 added the shield fraction: a shield-absorbed hit (hull
                // unchanged) must flash too (native arms on any flAmount>0,
                // trekigc.cpp:4281). Only this frame path touches the tracker, and
                // only with the LOCAL ship's fractions, so the "damage to another
                // ship never flashes" guarantee holds by construction (native's
                // pShip==GetShip() gate, trekigc.cpp:4281). Monotonic clock =
                // _nowSeconds (float cast is ample precision for 0.25s flash halves).
                // HUD-10 — arm from the real per-hit VICTIM_DAMAGED(217) event
                // when one has arrived (native DamageShipEvent, trekigc.cpp:4270-4283),
                // then let Update's sampled hull/shield decrease catch damage
                // sources that have no per-hit wire event (collisions/mines).
                long hitVersion = world.VictimDamageVersion;
                if (hitVersion != _lastFlashHitVersion)
                {
                    _lastFlashHitVersion = hitVersion;
                    _flashTracker.Arm((float)_nowSeconds);
                }
                bool flashing = _flashTracker.Update((float)_nowSeconds, up.HullFraction, up.ShieldFraction);

                // P245 — feed the local ship's sector into the warp-transit flash
                // tracker; it arms exactly once when the sector actually changes
                // (the server's one-shot SET_CLUSTER per transit). Read the current
                // pulse opacity (0 outside the brief window) into the HUD frame so
                // the renderer blacks out the whole frame at the transit moment.
                // Log the single arm so the live proof can pin the exact frame.
                float warpFlash = _warpFlash.Update((float)_nowSeconds, entity.SectorId);
                if (_warpFlash.JustTransited)
                {
                    Console.WriteLine($"[monogame] warp transit flash ARMED — sector -> {entity.SectorId}");
                    // P296 — buffer the transit-instant ship state so the 3D
                    // teleport-spark burst (drawn on the next frame's scene
                    // pass) reproduces native AddTeleportParticles' velocity-
                    // derived spray axes from the ship's REAL velocity at the
                    // transit moment. Same once-per-transit edge as the flash.
                    _pendingTeleportBurst = (
                        up.Position,
                        up.Velocity,
                        up.Orientation.GetForward(),
                        up.Orientation.GetRight());
                }

                // P562 — detect a sector change for ANY known ship (own and
                // remote) and play the arrival aleph's native star swell.
                DetectTransitSwells(world);
                // P585 — drain the opt-in transit-pulse signals (departures via
                // SHIP_DELETE, own arrivals via SET_CLUSTER, re-sighting arrivals)
                // into the pulse controller + star swell.
                DrainAlephTransitSignals(world);

                // P362 — the weapon/systems status list: gather the own ship's
                // mounted weapon/missile systems (identity + ammo semantics from
                // the Loadout a BUY_LOADOUT_ACK granted, resolved to names/types
                // via the shop catalogue) and derive each system's
                // READY/RELOAD/DEPLETED status from the live ammo fraction via
                // the P361 auto-reload behaviour (WeaponReloadTracker). Reset
                // the tracker when the local ship changes (respawn). Empty
                // until the own ship actually HAS a loadout (a stock auto-connect
                // flight never buys one) — the HUD panel then draws nothing.
                if (_weaponFrameShipId != me) { _weaponReloadTracker.Reset(); _weaponFrameShipId = me; }
                var weaponRows = BuildWeaponStatusRows(entity, up.AmmoFraction);

                // HUD-7 — the native flight equipment/inventory pane (F4). Built
                // only while the pane is actually shown (native rebuilds on
                // loadout/cluster change, inventoryimage.cpp:356-393 — never
                // every frame); the model's InventoryVisible flag gates the draw.
                if (HudInventoryPaneBuilder.ShouldBuild(_consolePanes.Inventory, entity.StationId >= 0, entity.IsDead))
                    inventoryPane = BuildInventoryPane(entity, up);

                // P377 — is the LOCAL ship an actively-drifting Lifepod whose
                // oxygen is genuinely burning? (lifepod capability from the own
                // hull id resolved via the loaded core + still in space — the
                // same gate that keeps the server's oxygen draining; a
                // docked/rescued pod reads full/inactive). While adrift the
                // own-ship update's FUEL slot carries the server-computed
                // oxygen FRACTION (a pod has no real fuel), which we surface as
                // an OXYGEN countdown.
                bool adriftLifepod = Rendering.LifepodHud.IsAdriftLifepod(_shopCatalog, entity.HullId, entity.StationId);
                int oxygenSeconds = adriftLifepod
                    ? Rendering.LifepodHud.OxygenSeconds(up.FuelFraction, _shopCatalog)
                    : 0;

                frame = new HudFrame(
                    HullFraction: up.HullFraction,
                    ShieldFraction: up.ShieldFraction,
                    // P578 — while in an eject pod the ammo/energy gauges read
                    // zero (native IsNotEjected zeroes them in a pod,
                    // dialog.mdl:208,435,470,507). The FUEL slot keeps carrying
                    // the oxygen countdown (P377), so it is intentionally NOT
                    // zeroed — it shows the pod's endurance instead.
                    AmmoFraction: adriftLifepod ? 0f : up.AmmoFraction,
                    FuelFraction: up.FuelFraction,
                    EnergyFraction: adriftLifepod ? 0f : up.EnergyFraction,
                    Power: up.Power,
                    // P508 — the HUD speed readout uses the local SIMULATION velocity
                    // when the own-ship sim is rendering (so the gauge tracks the
                    // same-frame ship the player flies); server-truth velocity otherwise
                    // (the --noPrediction debug toggle). All the OTHER HUD values
                    // (hull/shield/fuel/ammo/power) stay server truth.
                    Speed: (_ownShipPrediction && _ownSim.HaveHull
                        ? _ownSim.Velocity.Length()
                        : up.Velocity.Length()),
                    // P445 — the HUD Thrust gauge now reads the client-owned
                    // PERSISTENT throttle (the setting the pilot steps with = / -
                    // and the wheel), not the server-echoed control value. They
                    // normally coincide, but the persistent value is exact and
                    // lag-free, and during autopilot it shows the cruise speed
                    // the ship will return to on disengage (autopilot sends its
                    // own throttle). The gauge's fraction mapping is
                    // 0.5*(1+throttle) = % of max cruise speed.
                    Throttle: _throttle,
                    AfterburnerActive: afterburnerActive,
                    // P221 — firing readout: true while StateM carries a fire
                    // bit. Group-fire ORs AllWeapons; single-weapon mode (P225)
                    // ORs OneWeapon instead, so both are detected (the sim sets
                    // exactly one when firing — Weapon.cs:363-375).
                    Firing: firing,
                    // P225 — the pilot's fire-mode selection (client-local,
                    // not from the wire: group-fire vs the selected fixed mount).
                    // Drives the WEAPONS readout next to the firing indicator.
                    GroupFire: _groupFire,
                    SelectedMount: _selectedMount,
                    Flashing: flashing,
                    WarpFlash: warpFlash,
                    // P255 — true while the server says our own ship is actively
                    // mining (the owner-only 206 stream; id != NA).
                    Mining: world.MiningAsteroidId != -1,
                    // P270/HUD-12 — true while OUR OWN ship is cloaked. Native
                    // IsCloaked = GetCloaking() < 1, so this is true through the
                    // decloak ramp too (consoledata.cpp:563-575); the state bit
                    // remains the fallback before the first 220 message.
                    Cloaked: cloaked,
                    // P773 — the local player's own Kill Bonus, DERIVED
                    // client-side from the own ship's experience (seeded by
                    // PLAYERINFO.fExperience, updated by KILL_SHIP —
                    // console.cpp:824-828, appmsg.cpp:2280,2769,2807-2811).
                    // The port-only per-tick kill-bonus stream (fmid 212) is retired.
                    KillBonusPercent: world.KillBonusPercent,
                    // P298 — the ship's real current roll angle about its forward
                    // axis, derived from the SAME orientation this client already
                    // tracks for the chase/cockpit cameras (up.Orientation is the
                    // compact-quaternion attitude the server broadcasts each tick).
                    RollDegrees: up.Orientation.GetRollDegrees(),
                    WeaponSystems: weaponRows,
                    IsAdriftLifepod: adriftLifepod,
                    OxygenSecondsRemaining: oxygenSeconds,
                    // P442 — the local player's live wallet from the money sync
                    // (SET_MONEY/MONEY_CHANGE/AUTODONATE/PAYDAY). Null until the
                    // first SET_MONEY — the HUD then renders nothing.
                    Money: world.MyMoney,
                    // P506 — the virtual-joystick HUD indicator, present only
                    // while mouse flight is ON: the raw integrated deflection
                    // (stick position), the active deadzone, and the curve mode,
                    // so the pilot can SEE when the stick is centred inside the
                    // deadzone (and hold it there) — the steadiness fix.
                    MouseStick: _mouseFlightEnabled
                        ? new MouseStickView(
                            _mouseFlight.Current.Yaw, _mouseFlight.Current.Pitch,
                            _mouseFlight.Deadzone, _mouseFlight.LinearControls)
                        : null,
                    // P618 — the live sim time scale as a percent (100 = full
                    // speed). Hidden by the renderer at full speed; shown as
                    // "TIME DILATION nn%" when the server slows the game clock.
                    TimeDilationPercent: 100f * (float)_session.World.TimeScale,
                    WarningMask: world.ShipWarnings.Mask,
                    ControlState: up.StateM,
                    // HUD-6 — own-ship centre-HUD status telemetry.
                    SignaturePercent: OwnSignaturePercent(world, ownStatus.SourceShip,
                        firing, _groupFire, _selectedMount, afterburnerActive, cloakActive),
                    Detected: entity.Detected,
                    AutopilotEngaged: ownStatus.AutopilotEngaged,
                    VectorLockEngaged: ownStatus.VectorLockEngaged,
                    CarriedFlag: ownStatus.CarriedFlag,
                    CarriedArtifact: ownStatus.CarriedArtifact,
                    // HUD-8 — the live turn control vector this frame actually sent
                    // (jsValues[c_axisYaw]/[c_axisPitch] equivalent).
                    TurnYaw: _lastSentControls.Yaw,
                    TurnPitch: _lastSentControls.Pitch,
                    // HUD-12 — continuous cloak strength (native
                    // 100·(1−GetCloaking()), consoledata.cpp:480-489).
                    CloakStrengthPercent: cloakStrength,
                    // HUD-12 — own source-ship type name for the command-view
                    // summary (native GetModelTypeDesc(Me), dialog.mdl:4717-4733).
                    // HUD-12b — the turret suffix comes from the value-keyed
                    // cache instead of concatenating a new string every frame.
                    ShipTypeName: (_hullById.TryGetValue(ownStatus.SourceShip.HullId, out var ownHullType)
                        ? ownHullType.Name : null) is { Length: > 0 } typeName
                            ? (IsTurretGunner() ? HudStringCache.TurretShipType(typeName) : typeName)
                            : null);
            }
        }
        var model = HudModelAdapter.Create(frame, _cameraMode, _lockBracket, _leadIndicator,
            _captureSpec is not null ? "OVERVIEW" : null, _targetSelectionBracket, _targetBearingScreenDir,
            velocityMarker: _velocityMarker, chaseAim: _chaseAim, orderArrows: _orderArrows,
            leaderCommand: _leaderBotCommand, ripcord: RipcordStatusLabel(),
            commandee: CommandeeLabel(), turret: IsTurretGunner(),
            targetPane: ComputeTargetPane(),
            inventorySlide: _paneSlides.Progress(SlidingPane.Inventory, _nowSeconds),
            diagnostics: Environment.GetEnvironmentVariable("ALLEGIANCE_LOD_OVERLAY") == "1"
                ? $"LOD full:{_lodFullCount} far:{_lodFarCount}" : null,
            hidden: _hideHud || _directorActive || _hudHidden,
            docked: hudDocked, dead: hudDead, commandView: CommandCameraActive, ghost: hudGhost,
            notices: BuildTipNotices(),
            contacts: _drawRadarContacts,
            radarPulse: (MathF.Sin((float)_nowSeconds * 5f) + 1f) * 0.5f, radarMode: _radarMode,
            chat: BuildChatModel(),
            cargoLine: BuildCargoHudText(), flightPartsLine: BuildFlightPartsHudText(),
            commandLines: BuildCommandHudLines(), inventoryVisible: _consolePanes.Inventory,
            inventoryPane: inventoryPane,
            lastBaseObjectives: _lastBaseHud, hitConfirmation: _nowSeconds < _hudHitUntil,
            console: BuildConsoleText(),
            podReturnInstruction: $"{KeyOf("TargetFriendlyBaseNearest").Label} / {KeyOf("ToggleAutopilot").Label} AUTOPILOT TO BASE",
            roundRadar: _hudSettings.RoundRadar,
            showJoystickIndicator: _hudSettings.ShowJoystickIndicator,
            buffers: _hudSnapshotBuffers);
        if (_hudSettings.Style == HudStyle.Classic) _radarRenderer.Draw(model, GraphicsDevice.Viewport);
        if (model.FlightVisible && model.Context.Contacts.Count > 0)
            _shipTagRenderer.Draw(_hudSettings.Style == HudStyle.Modern && _hudSettings.ColorBlindSafe
                ? _drawShipTags.Select(t => t with { Colour = HudPalette.Side(t.Colour) }).ToArray()
                : _drawShipTags);
        // HUD-21 — feed this frame's target-model preview (or null fallback) to
        // the active HUD renderer so DrawTargetPane composites it under the oval.
        ActiveHud.TargetModelPreview = _hudTargetModelPreview;
        ActiveHud.Draw(model, GraphicsDevice.Viewport);
        if (_hudSettings.Style == HudStyle.Classic) DrawEjectDeathOverlays(model);
        DrawTargetNoMatchFeedback();
        return model;
    }

    /// <summary>HUD-10 — build the chat snapshot with the native compose state
    /// (console.cpp:524-529) and the per-line sender side colours
    /// (trekigc.cpp:4736-4741). P784 — all five compose states are live
    /// (ComposeMachine), the recipient label resolves the actual recipient name
    /// (UpdateComposedChat, console.cpp:1299-1338) and history lines carry the
    /// native leader flag (trekigc.cpp:4498).</summary>
    private HudChat BuildChatModel()
    {
        var (lines, colors, leaders) = BuildChatLinesAndColors();
        NativeComposeState state = _composeVisualState;
        float composeProgress = ChatComposeProgress;
        Rendering.HudColor? side = null;
        string recipientLabel;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own)
                && world.TryGetSideColor(own.SideId, out float r, out float g, out float b))
                side = new Rendering.HudColor(r * 0.85f, g * 0.85f, b * 0.85f, 1f);

            // P784 — native UpdateComposedChat fills the compose line's
            // recipient box with the RESOLVED recipient (side name / wing name /
            // cluster name / ship name, console.cpp:1299-1338); resolve the same
            // names from the world, falling back to the plain channel tag.
            string? sideName = null, wingName = null, sectorName = null, shipName = null;
            if (_chatTarget == ChatTarget.Team)
                sideName = world.SideRoster?.Sides.FirstOrDefault(s => s.SideId == world.MySideId).Name;
            else if (_chatTarget == ChatTarget.Wing && world.MyShipId is { } meWing
                     && world.Ships.TryGetValue(meWing, out var wingOwn))
                wingName = Allegiance.ClientCore.State.WingNames.For(wingOwn.WingId);
            else if (_chatTarget == ChatTarget.FriendlySector && world.MyShipId is { } meSector
                     && world.Ships.TryGetValue(meSector, out var sectorOwn))
                sectorName = world.Clusters.TryGetValue(sectorOwn.SectorId, out var cluster) ? cluster.Name : null;
            else if (_chatTarget == ChatTarget.Individual && world.MyShipId is { } meSelf
                     && world.Ships.TryGetValue(meSelf, out var self))
                shipName = self.CharacterName;
            recipientLabel = ChatTargetLogic.ResolvedLabel(_chatTarget, sideName, wingName, sectorName, shipName);
        }
        return new HudChat(lines, _chatPane.Offset,
            _chatActive ? _chatBuffer.ToString() : string.Empty,
            state != NativeComposeState.NotComposing, _nowSeconds,
            recipientLabel, ChatPaneFilterLabel(),
            state, composeProgress, side, colors,
            leaders,
            Rendering.ChatPaneLayout.ConfiguredLines(_hudSettings.ChatLines, _dockedActive));
    }

    /// <summary>HUD-6 — native signature percentage = 100·signature / side
    /// c_gaSignature (WinTrek/console.cpp:865-866), evaluated on the SOURCE ship
    /// (a turret gunner reads the carrier's signature, console.cpp:845) with
    /// the native per-part gating in <see cref="OwnStatusData.SignaturePercent"/>.
    /// Unknown hull/side data leaves the readout absent rather than inventing a
    /// value.</summary>
    private float? OwnSignaturePercent(ClientWorld world, ShipEntity entity,
        bool firing, bool groupFire, int selectedMount, bool afterburnerActive, bool cloakActive)
    {
        short sideId = entity.SideId;
        if (_shopCatalog is null) return null;
        // HUD-6b — the per-frame LINQ hull scan is replaced by the catalogue
        // dictionary built once in Game1.Lifecycle (same cache HUD-4b uses).
        if (!_hullById.TryGetValue(entity.HullId, out var ownHull)) return null;
        float resistance = world.GlobalAttributesFor(sideId).Get(Allegiance.Core.Model.GlobalAttributeSet.Signature);
        return OwnStatusData.SignaturePercent(ownHull.Signature, entity.Loadout, ResolveFlightPart,
            firing, groupFire, selectedMount, afterburnerActive, cloakActive, resistance);
    }

    /// <summary>P578/HUD-9 — flat tip view for the shared snapshot: the three
    /// retained ordinary slots plus the up-to-four critical slots (opacity 1;
    /// native has no fade — expiry alone clears criticals, console.cpp:913-931).</summary>
    private HudNotice[] BuildTipNotices()
    {
        var list = new List<HudNotice>();
        foreach (var text in _ordinaryTips)
            if (!string.IsNullOrEmpty(text)) list.Add(new(text, false, 1));
        foreach (var (text, expiresAt) in _criticalTips)
            if (_nowSeconds < expiresAt) list.Add(new(text, true, 1));
        return list.ToArray();
    }

    /// <summary>HUD-9 — resolve the console text and banner frame from the live
    /// world under the gate lock. Sector follows the flight/docked context
    /// (console.cpp:870; dialog.mdl:1430-1456), carrier/board state follow the
    /// parent ship (console.cpp:797-804), orders use the verb+target wording
    /// (console.cpp:875-908), and the vote banner reads the active ballot
    /// (console.cpp:935-937). Mission lives/kills stay null (not on the wire).</summary>
    private HudConsoleText BuildConsoleText()
    {
        var world = _session.World;
        string? sector = null, carrier = null, accepted = null, queued = null, vote = null;
        string? viewedSector = null;
        int boardState = 0;
        int? lives = null, missionKills = null;
        bool deathmatch = false;
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own))
            {
                short sectorId = own.SectorId;
                if (own.StationId >= 0 && world.Stations.TryGetValue(own.StationId, out var station))
                    sectorId = station.ClusterId;
                if (world.Clusters.TryGetValue(sectorId, out var cluster) && cluster.Name is { Length: > 0 })
                    sector = cluster.Name;
                // HUD-20 — docked command view shows the VIEWED sector: native
                // GetCluster() returns the view cluster when one is set, else the
                // ship's (clintlib.h:996); the port's docking rule is
                // ViewedClusterId ?? station cluster (ClientWorld.Commanding.cs:10).
                // "Currently viewing:" is drawn only in ofInStationCommand
                // (dialog.mdl:5421-5422), so resolve it only while docked.
                if (own.StationId >= 0)
                {
                    short viewedId = HudConsoleFormat.ViewedSectorId(world.ViewedClusterId, sectorId);
                    if (world.Clusters.TryGetValue(viewedId, out var viewedCluster)
                        && viewedCluster.Name is { Length: > 0 } viewedName)
                        viewedSector = viewedName;
                }

                if (own.ParentId != -1)
                {
                    // Native BoardState: parented non-turret = observer, parented
                    // turret = gunner (console.cpp:797-804).
                    boardState = own.State == ShipStatusMessage.StateTurret ? 2 : 1;
                    if (world.Ships.TryGetValue(own.ParentId, out var parent)
                        && parent.CharacterName is { Length: > 0 } name)
                        carrier = name;
                }
            }
            accepted = ResolveOrderText(world, world.MyAcceptedCommand);
            queued = ResolveOrderText(world, world.MyQueuedCommand);
            if (world.ActiveBallot is { } ballot && ballot.BallotText is { Length: > 0 } ballotText)
                vote = ballotText;
            // HUD-12 — native console lives/kills (console.cpp:812-821):
            // iLives − MissionDeaths() and MissionKills(), plus the deathmatch
            // gate for the kills badge. All three readings are real client state:
            // MISSIONDEF's iLives, and the own PlayerInfo counters seeded by
            // PLAYERINFO and maintained from KILL_SHIP (native appmsg.cpp).
            if (world.TryGetOwnMissionStats(out short kills, out short deaths))
            {
                lives = HudConsoleFormat.RemainingLives(world.MissionLives, deaths);
                missionKills = kills;
            }
            deathmatch = world.MissionBriefing?.Deathmatch == true;
        }
        HudColor? sideColor = null;
        lock (_session.Gate)
            if (world.MySideId is { } mySide && world.TryGetSideColor(mySide, out var sr, out var sg, out var sb))
                sideColor = new HudColor(sr, sg, sb);
        return new HudConsoleText
        {
            SectorName = sector,
            ViewedSectorName = viewedSector,
            CarrierName = carrier,
            BoardState = boardState,
            AcceptedOrder = accepted,
            QueuedOrder = queued,
            VoteText = vote,
            OrdinaryTips = _ordinaryTips.Where(t => !string.IsNullOrEmpty(t)).ToArray(),
            CriticalTips = _criticalTips.Where(t => _nowSeconds < t.ExpiresAt)
                .Select(t => new HudNotice(t.Text, true, 1)).ToArray(),
            Lives = lives,
            MissionKills = missionKills,
            Deathmatch = deathmatch,
            SideColor = sideColor
        };
    }

    /// <summary>Resolve the native accepted/queued order target name and defer the
    /// verb + target formatting to the tested
    /// <see cref="HudConsoleFormat.OrderText"/> (console.cpp:875-908).</summary>
    private static string? ResolveOrderText(ClientWorld world, QueuedCommandState? command)
    {
        if (command is not { } c || c.CommandId == OrderChange.CidNone) return null;
        string? name = world.TryCommandTarget(c, out _, out var targetName) && targetName.Length > 0
            ? targetName : null;
        return HudConsoleFormat.OrderText(c.CommandId, name);
    }

    /// <summary>P425 — the HUD commandee readout label, or null when the pilot is
    /// not currently commanding anyone (nothing to draw). Names the same-side
    /// ship/drone selected as <see cref="_commandee"/> plus the issue-order key,
    /// so the pilot always sees who they are about to order and how to send it.
    /// Pure string formatting of the already-selected commandee (no world read —
    /// the selection is authoritative client state).</summary>
    private string? CommandeeLabel()
    {
        if (_commandee is not { } cmd) return null;
        return $"CMD {cmd.Name} (#{cmd.Id})  [Insert]";
    }

    /// <summary>
    /// P781 — emit the lead-up teleport sparks for this frame from the emitters
    /// the gate-locked pose pass gathered (<see cref="_teleportEmitters"/>).
    /// Native calls <c>AddTeleportParticles</c> once per rendered frame per ship
    /// (thinggeo.cpp:503-528), which scales the spray density with the frame
    /// rate; here a fixed <see cref="Rendering.WarpTransitParticles.EmissionClockSeconds"/>
    /// (60 Hz) accumulator gates the emission to at most one native call's worth
    /// of sparks per 1/60 s — a deliberate deviation (see WarpTransitParticles
    /// remarks and the P781 report). Runs on the render thread; the emitters are
    /// a plain snapshot (no gate needed).
    /// </summary>
    private void EmitTeleportLeadup(float frameSeconds)
    {
        const float clock = Rendering.WarpTransitParticles.EmissionClockSeconds;
        _teleportEmitAccumulator += frameSeconds;
        // One native call's worth per elapsed 1/60 s: a 144 Hz client emits on
        // ~every 2nd-3rd frame, a 30 Hz client twice per frame, so the spray
        // density is the native 60 fps density at any frame rate. A hitch is
        // capped at MaxEmissionsPerFrame (the excess is dropped, never a burst).
        int ticks = 0;
        while (_teleportEmitAccumulator >= clock && ticks < MaxTeleportEmissionsPerFrame)
        {
            _teleportEmitAccumulator -= clock;
            ticks++;
        }
        if (_teleportEmitAccumulator >= clock) _teleportEmitAccumulator %= clock;
        if (ticks == 0) return;

        int count = _teleportEmitterCount;
        for (int tick = 0; tick < ticks; tick++)
            for (int i = 0; i < count; i++)
            {
                ref readonly var e = ref _teleportEmitters[i];
                _warpParticles.EmitTeleport(e.Position, e.FrameVelocity, e.Forward, e.Right,
                    e.Radius, e.TimeUntilTeleport, clock, _trailRandom);
            }
    }

    /// <summary>P781 — the most 60 Hz emission ticks one rendered frame may
    /// catch up (a 15 fps frame); a longer hitch drops the excess.</summary>
    private const int MaxTeleportEmissionsPerFrame = 4;

    /// <summary>P414/HUD-9 — the native ripcord countdown label, "Ripcord in: n",
    /// resolved from the live world ripcord state under the gate lock. Native
    /// <c>GetRipcordTimeLeft</c> adds 0.5 s and <c>NumberString</c> adds another
    /// 0.5 s before truncating (consoledata.cpp:521-532; modeler.cpp:2008), so
    /// the displayed whole second is <c>(int)(left + 1.0)</c>. Null when no
    /// ripcord is active.
    /// P781 — the label disappears once the countdown reaches 0 (native's
    /// <c>m_dtRipcordCountdown = 0</c> pin), but the ripcord STATE survives:
    /// native keeps the frozen model until the server moves the ship
    /// (shipIGC.cpp:286-294), and the port's server teleport is silent toward
    /// the owner on the wire, so the state is expired only after the port-only
    /// 2 s safety grace (<see cref="Allegiance.ClientCore.Flight.RipcordLeadup.SafetyGraceSeconds"/>).
    /// Through the 0 … −2 s window the ship keeps flying the frozen model and
    /// sending SHIP_UPDATE, and the ripcord key still cancels (native
    /// fRipcordActive is still true there).</summary>
    private string? RipcordStatusLabel()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            // Without a start time the countdown cannot be computed; native
            // IsRipcording(Me) gates the whole image, so a null start time must
            // show nothing rather than a fabricated "Ripcord in: 1"
            // (consoledata.cpp:521; dialog.mdl:5139-5154).
            if (!world.RipcordActive || world.RipcordActiveSinceLocalMs is null) return null;

            float dur = RipcordDurationSeconds(world);
            float left = _ripcordCountdownSound.TimeLeft(world.RipcordActive,
                world.RipcordActiveSinceLocalMs, ClientNowMilliseconds, dur, world.TimeScale);
            if (dur > 0f && left <= 0f)
            {
                // P781 — the label is gone at 0; the state itself is expired
                // only once the countdown has overrun by the port-only safety
                // grace (native pins at 0 until the server's move). The pure
                // RipcordLeadup math is the same TiDi rule the audio cue
                // integrates (shared, not forked — see RipcordLeadup remarks).
                if (world.RipcordActiveSinceLocalMs is { } start
                    && Allegiance.ClientCore.Flight.RipcordLeadup.OverrunSeconds(start, ClientNowMilliseconds, dur, world.TimeScale)
                        <= -Allegiance.ClientCore.Flight.RipcordLeadup.SafetyGraceSeconds)
                {
                    world.ClearRipcord();
                }
                return null;
            }
            // Native double-0.5 rounding is in HudConsoleFormat.RipcordLabel
            // (consoledata.cpp:521; modeler.cpp:2008), which also refuses to
            // fabricate a label when the start time is unknown.
            return HudConsoleFormat.RipcordLabel(world.RipcordActive, world.RipcordActiveSinceLocalMs, left);
        }
    }

    /// <summary>P414 + P479 — the own hull's ripcord countdown duration (seconds),
    /// from the loaded static core via <see cref="_shopCatalog"/> (0 = unknown,
    /// when the core is unavailable — the caller then fails open and keeps the
    /// tag until an abort/other clear). The server counts down exactly this
    /// value (<c>Ship.ResetRipcordTimeLeft</c> → <c>RipcordSpeed</c>), so the
    /// client expiry matches the real teleport timing. P479: the duration is
    /// divided by the owning side's <c>c_gaRipcordTime</c> (learned from the
    /// SIDE_ATTRIBUTE_CHANGE(76) broadcast into
    /// <see cref="ClientWorld.GlobalAttributesFor"/>), the exact server-side
    /// computation — the hull's <c>RipcordSpeed</c> is <c>ripcordSpeed /
    /// GA[c_gaRipcordTime]</c> (<c>shipIGC.cpp:4665</c>).</summary>
    private float RipcordDurationSeconds(ClientWorld world)
    {
        if (RenderShipId(world) is not { } me || !world.Ships.TryGetValue(me, out var ent)) return 0f;
        return RipcordDurationSecondsFor(world, ent);
    }

    /// <summary>HUD-12 — a ship's ripcord countdown duration in seconds, the
    /// native <c>GetRipcordSpeed()</c>: the hull's <c>RipcordSpeed</c> divided by
    /// the owning side's <c>c_gaRipcordTime</c> (<c>Igc/shipIGC.cpp:4665</c>).
    /// Native's remote-ripcord handler resets each observing client's copy of the
    /// countdown with exactly this value (<c>clintlib/appmsg.cpp:966-969</c>), so
    /// the radar's per-ship seconds match the native source. Returns 0 when the
    /// hull or catalogue is unknown (the caller then omits the seconds).</summary>
    private float RipcordDurationSecondsFor(ClientWorld world, ShipEntity ent)
    {
        if (_shopCatalog is not { } catalog) return 0f;
        float ripcordSpeed = 0f;
        foreach (var h in catalog.Hulls)
            if (h.HullId == ent.HullId) { ripcordSpeed = h.RipcordSpeed; break; }
        if (ripcordSpeed <= 0f) return 0f;
        float ga = world.GlobalAttributesFor(ent.SideId)
            .Get(Allegiance.Core.Model.GlobalAttributeSet.RipcordTime);
        return ga > 0f ? ripcordSpeed / ga : ripcordSpeed;
    }

    /// <summary>
    /// P249+P360 — gather the whole-mission cluster graph (positions from the
    /// real EXPORT OT_cluster data, edges from the CLUSTERINFO(200) graph)
    /// under the Gate lock and draw the starmap. Called EVERY flight frame so
    /// the persistent corner mini-map (P360) is always on screen without a
    /// toggle key; when Tab has expanded the view it draws the full centred
    /// pane instead. The current sector comes from the local ship's SectorId,
    /// so the highlight tracks where the pilot actually is (and updates live
    /// as they transit sectors). Positions are the AUTHORED ScreenX/ScreenY
    /// layout coordinates native's sectormap uses; the renderer fits them into
    /// the panel via <see cref="StarmapLayout"/>. No world/wire change — this
    /// data already exists and is already correct; this stage only renders it.
    /// </summary>
    private void DrawStarmap(GameTime gameTime)
    {
        var world = _session.World;
        // P374 — the threat-icon / highlight blink phase, computed from wall
        // time (frame-independent): ~2.5 Hz on-off. Native toggles its
        // m_bFlashFrame every 2 pane events (sectormap.cpp:729-740); the exact
        // event cadence is unspecified, so the port uses a fixed ~400 ms phase.
        bool flashOn = (((long)gameTime.TotalGameTime.TotalMilliseconds / 400) & 1) == 0;
        var warningSnapshot = new Dictionary<short, short>();
        var highlights = new HashSet<short>();
        var sites = new List<StarmapLayout.ClusterSite>();
        IReadOnlyList<ClusterInfo.WarpEdge>? edges = null;
        short? current = null;
        // P762b — native's chat cluster (GetChatCluster(), clintlib.h:998-1009):
        // the viewed cluster when set, else the ship's own cluster. This drives
        // sectorhighlightbmp (sectormap.cpp:1095), separately from the
        // commander's HIGHLIGHT_CLUSTER set in `highlights`.
        short? chatCluster = null;
        SectorInfoPaneLayout.Data? pane = null;
        // P460 — per-cluster ownership (sector id -> owners) and the side-colour
        // lookup for the owner/secondary rings.
        var owners = new Dictionary<short, SectorMapQueries.ClusterOwners>();
        var sideColors = new Dictionary<short, Color>();
        IReadOnlyDictionary<short, int>? knownEnemySectors = null;
        lock (_session.Gate)
        {
            highlights.UnionWith(world.HighlightedClusters);
            if (world.Graph is { } g)
            {
                edges = g.Warps;
                // P493 — the sectors the client knows enemies are in right now,
                // from the cross-side SHIP_STATUS(90) stream (probe/scout eyes).
                // Computed under the gate lock because it reads world.Ships /
                // world.MySideId, which the receive thread mutates.
                knownEnemySectors = SectorMapQueries.KnownEnemySectors(
                    world.Ships.Values.Select(e => (e.SectorId, e.SideId, e.HasRealIdentity, e.Unknown)),
                    world.MySideId);
                foreach (var ce in g.Clusters)
                {
                    // A graph cluster always has a matching EXPORT OT_cluster
                    // (both are built from the mission's sectors), but guard
                    // anyway so a missing position can never crash the draw.
                    if (world.Clusters.TryGetValue(ce.Id, out var dc))
                        sites.Add(new StarmapLayout.ClusterSite(ce.Id, dc.Name, dc.ScreenX, dc.ScreenY));
                    // P374 — snapshot this sector's current threat level under the
                    // gate lock (ClusterWarningFor reads a dict the receive thread
                    // mutates, so it must not be read outside the lock).
                    warningSnapshot[ce.Id] = world.ClusterWarningFor(ce.Id);

                    // P460 — who owns this sector? Count the client's stations in
                    // the cluster, excluding pedestal types (native
                    // GetClusterOwners, sectormap.cpp:11-50), and remember the
                    // two owners' side colours for the rings.
                    var stations = world.Stations.Values
                        .Where(s => s.ClusterId == ce.Id)
                        .Select(s => (s.SideId, s.StationTypeId));
                    if (SectorMapQueries.GetClusterOwners(stations, IsPedestalType) is { } o)
                    {
                        owners[ce.Id] = o;
                        RememberSideColor(o.Owner);
                        if (o.Secondary is { } sec) RememberSideColor(sec);
                    }
                }
                if (RenderShipId(world) is { } me && world.Ships.TryGetValue(me, out var own))
                    current = own.SectorId;
                chatCluster = world.ViewedClusterId ?? current;

                // P272 — the info pane for the selected cluster: gather that
                // cluster's asteroids from the client's own decoded EXPORT
                // data (world.Asteroids is keyed by AsteroidId and carries
                // AabmCapabilities + Ore + ClusterId) and build the pure pane
                // content. Real data — nothing fabricated. Only gathered in the
                // EXPANDED view (the compact mini-map draws no info pane).
                //
                // P752 — the pane follows the POINTER like native's
                // SectorMapPane::MouseMove (sectormap.cpp:1413-1461): a hovered
                // cluster is both the selection and the mouse-over cluster, and
                // the pane draws the hovered cluster's warning text
                // (sectormap.cpp:517-534). The helium figure is the SIDE-KNOWN
                // ore (live ASTEROIDS_UPDATE/ASTEROID_MINED fraction x OreMax,
                // native GetOreSeenBySide), never the static EXPORT value.
                short? paneSector = _starmapHoverSector ?? _selectedSectorId;
                if (_minimapExpanded && paneSector is { } sid)
                {
                    string selName = world.Clusters.TryGetValue(sid, out var sdc) ? sdc.Name : "";
                    // P752 — the sample build (side-known ore) lives in the pure,
                    // unit-tested SamplesForCluster so the draw path and the
                    // regression test exercise the same code.
                    var samples = SectorInfoPaneLayout.SamplesForCluster(world.Asteroids, world.AsteroidLive, sid);

                    // P460 — the own-probe count for the selected sector, with
                    // native's capability filter (probe type with no capabilities,
                    // or >= c_eabmRescue). Capabilities resolved from the core's
                    // probe-type table by each probe's ProbetypeId.
                    int probeCount = 0;
                    if (world.MySideId is { } mySide && _shopCatalog?.ProbeTypes is { } ptypes)
                    {
                        var probeRows = world.Probes.Values
                            .Where(p => p.ClusterId == sid)
                            .Select(p => (
                                p.ClusterId, p.SideId,
                                EabmCapabilities: ptypes.TryGetValue(p.ProbetypeId, out var pt) ? pt.EabmCapabilities : (short)-1));
                        probeCount = SectorMapQueries.CountOwnProbes(probeRows, sid, mySide);
                    }
                    // P752 — the hovered cluster's warning text (native draws it
                    // at the pane origin while the pointer is over the sector,
                    // sectormap.cpp:517-534).
                    string? warnText = _starmapHoverSector is { } hov
                        && warningSnapshot.TryGetValue(hov, out var hw)
                        ? StarmapThreatPresentation.WarningText(hw)
                        : null;
                    if (string.IsNullOrEmpty(warnText)) warnText = null;
                    pane = SectorInfoPaneLayout.Build(selName, samples, probeCount,
                        expanded: _sectorInfoExpanded, warningText: warnText);
                }
            }
        }

        // Local funcs that must run under the gate lock above.
        void RememberSideColor(short sideId)
        {
            if (!sideColors.ContainsKey(sideId) && world.TryGetSideColor(sideId, out float r, out float g, out float b))
                sideColors[sideId] = new Color(r, g, b);
        }
        bool IsPedestalType(short typeId)
            => _shopCatalog?.StationTypes is { } st && st.TryGetValue(typeId, out var t) && t.HasCapability(Allegiance.Core.DataStationTypeIGC.SabmPedestal);
        Color? SideColor(short sideId) => sideColors.TryGetValue(sideId, out var c) ? c : null;

        if (edges is null || sites.Count == 0)
            return;

        var vp = GraphicsDevice.Viewport;
        if (_minimapExpanded)
        {
            // Expanded: the full centred starmap pane (P249) + the info pane.
            float panelW = MathF.Min(vp.Width * 0.80f, 1040f);
            float panelH = MathF.Min(vp.Height * 0.66f, 640f);
            float panelX = (vp.Width - panelW) / 2f;
            float panelY = (vp.Height - panelH) / 2f;
            _starmapRenderer.Draw(sites, edges, current, _selectedSectorId,
                id => warningSnapshot.TryGetValue(id, out var w) ? w : (short)0,
                flashOn, vp, panelW, panelH, owners, SideColor, knownEnemySectors, highlights, chatCluster);
            if (pane is { } p)
            {
                _sectorInfoPaneRenderer.Draw(p, vp, panelX, panelY, panelW, panelH,
                    world.MySideId is { } mySide && sideColors.TryGetValue(mySide, out var mc) ? mc : (Color?)null,
                    expanded: _sectorInfoExpanded);
                // P752 — the renderer records its own drawn box; the expand
                // button is hit-tested against that exact rect (native checks
                // the button before the map, sectormap.cpp:186-201).
                _sectorInfoPaneBox = _sectorInfoPaneRenderer.LastBox;
            }
            else
            {
                _sectorInfoPaneBox = null;
            }
        }
        else
        {
            // Collapsed (the P360 default): the persistent compact corner
            // mini-map — current sector + warp topology at a glance, always on.
            _starmapRenderer.DrawMini(sites, edges, current,
                id => warningSnapshot.TryGetValue(id, out var w) ? w : (short)0,
                flashOn, vp, owners, SideColor, knownEnemySectors, highlights, chatCluster);
        }
    }

    /// <summary>
    /// P250 — gather the nearby objects in the local player's CURRENT sector
    /// (ships, stations, warps) under the Gate lock, project each to screen via
    /// <see cref="RadarProjection"/>, apply the native LOD filters and draw them
    /// through <see cref="RadarBracketRenderer"/>. HUD-4 adds the own-ship marker,
    /// missile/probe/mine/treasure/tech contacts, ordinary asteroids and the
    /// native per-kind LOD presence rules. Native has no distance fade
    /// (radarimage.cpp:689-700 is commented out), so contacts are full opacity.
    /// No new wire/data plumbing: this reuses the exact per-sector filtering the
    /// draw loops already do (<c>ClusterId/SectorId == mySector</c>).
    /// </summary>
    private static bool IsExternalFlightCamera(CameraMode mode) =>
        mode is CameraMode.Chase or CameraMode.External
            or CameraMode.ExternalTurret or CameraMode.ExternalMissile
            or CameraMode.ExternalFlyBy or CameraMode.ExternalFlyByTarget
            or CameraMode.ExternalOrbit or CameraMode.ExternalTarget
            or CameraMode.ExternalBase or CameraMode.ExternalStation
            or CameraMode.ExternalOverride;

    private void DrawRadar(Matrix view, Matrix proj, Vector3 cameraEye, double timeSeconds)
    {
        var world = _session.World;
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width, h = vp.Height;
        const float inset = 14f;
        // Native has no distance-alpha fade (radarimage.cpp:689-700 is commented
        // out), so every contact is full opacity; pinned by the parity test.
        const float op = RadarBracketLayout.NativeContactOpacity;

        // P495 — resolve this frame's investment-pane model + the hovered
        // station-type bucket's build mask (native SetInvestAsteroid). Builds
        // the model here (before the pane draw reuses it) so the radar's
        // build-spot highlight and the pane list always agree this frame.
        RefreshInvestState();

        // P436 — the selected-target highlight. Resolve the player's current
        // _autoTarget to its radar-contact type.
        var sel = _autoTarget;
        var selType = sel?.Kind switch
        {
            TargetKind.Ship => RadarBracketRenderer.RadarContactType.Ship,
            TargetKind.Station => RadarBracketRenderer.RadarContactType.Station,
            TargetKind.Asteroid => RadarBracketRenderer.RadarContactType.Asteroid,
            TargetKind.Warp => RadarBracketRenderer.RadarContactType.Warp,
            TargetKind.Probe => RadarBracketRenderer.RadarContactType.Probe,
            TargetKind.Treasure => RadarBracketRenderer.RadarContactType.Treasure,
            _ => (RadarBracketRenderer.RadarContactType?)null,
        };

        _drawRadarContacts.Clear();
        _drawShipTags.Clear();   // P372 — per-ship in-world side tags
        lock (_session.Gate)
        {
            if (RenderShipId(world) is not { } me) return;
            if (!world.Ships.TryGetValue(me, out var ownEnt)) return;
            short mySector = RenderSector(ownEnt.SectorId);
            // P624 — radar mode filter (All/Default/Target/Minimal) + my side for
            // the enemy/friendly test; snapshot under the gate with everything else.
            var radarMode = _radarMode;
            short mySide = world.MySideId ?? -1;
            // Native bRangeLabels = !CommandCamera (radarimage.cpp:523-524). It
            // suppresses the range text AND is the gate for the nearest-enemy
            // lookup (radarimage.cpp:582-583) — HUD-5b no longer resolves a
            // nearest enemy in the command camera / every mode.
            bool commandCamera = CommandCameraActive;
            bool rangeLabels = RadarLabelModel.RangeLabelsEnabled(commandCamera);
            bool rangeZero = !rangeLabels;   // native asteroid LOD reuses bRangeLabels
            var accepted = world.MyAcceptedCommand;
            var queued = world.MyQueuedCommand;
            float ownRadius = HullLength(ownEnt.HullId) * 0.5f;
            // HUD-12 — native c_maskSubject source: the console's chat-subject
            // list, non-null only while a chat is being composed
            // (console.cpp:2327-2334). The port's composer state drives the same
            // gate; the subject set itself is RadarOverlayRules.IsSubject.
            bool subjectComposing = _chatActive || _qcMenuOpen;
            var subjectTarget = _chatTarget;
            short myWingId = ownEnt.WingId;
            long radarNowMs = (long)(_nowSeconds * 1000);

            Color? SideColor(short side)
                => world.TryGetSideColor(side, out float r, out float g, out float b) ? new Color(r, g, b) : null;
            HudColor? SideHud(short side)
                => world.TryGetSideColor(side, out float r, out float g, out float b) ? new HudColor(r, g, b) : null;
            bool OwnOrAllied(short side) => side == mySide || world.AreAllied(mySide, side);
            bool IsPedestalType(short typeId)
                => _shopCatalog?.StationTypes is { } stationTypes
                   && stationTypes.TryGetValue(typeId, out var stationType)
                   && stationType.HasCapability(Allegiance.Core.DataStationTypeIGC.SabmPedestal);
            // Native LOD predicates use the adjusted surface separation
            // (radarimage.cpp:626-636); range labels use the centre distance.
            float LodSeparation(float centre, float objectRadius) => MathF.Max(0f, centre - ownRadius - objectRadius);

            // HUD-13 — native per-contact bracket-sizing inputs
            // (radarimage.cpp:307-319): GetScreenRadius() from the SAME
            // projection the selected-target / missile-lock bracket uses
            // (ProjectedRadius, Game1.Camera.cs:215) and GetDistanceToEdge()
            // from the placed (possibly edge-clamped) point. Pure value math -
            // no allocation. An off-screen contact takes native's fixed
            // CmdViewMaxIconScreenSize radius (WinTrek.cpp:7876-7878), so only
            // on-screen contacts pay for the projection.
            RadarScreenRadius.ContactBracket Bracketing(
                RadarBracketLayout.RadarSite site, float objectRadius, Vector3 worldPos)
                => RadarScreenRadius.ForContact(
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen,
                    objectRadius, (worldPos - cameraEye).Length(), w, h, proj.M22,
                    site.Point.X, site.Point.Y);

            // HUD-16 — the native radar icon surface for each contact
            // (pmodel->GetIcon(), radarimage.cpp:591-593), resolved from the
            // core's per-type iconName through the once-built cache, exactly
            // like native's LoadRadarIcon(name) (trekigc.cpp:2731-2744).
            var iconNames = _radarIconNames;

            // P527 — the radar EYE uses the own-ship RENDER pose (P508 local
            // sim), not the raw 20 Hz ServerShipUpdate pose, so every contact's
            // projection shares the frame's own position. Falls back to the raw
            // update only when the own ship wasn't rendered this frame.
            SimVec ownSimPos;
            if (!ResolveRadarContactAnchor(_poses, world.Ships, me, out ownSimPos))
            {
                if (ownEnt.Latest is not { } ownUp) return;
                ownSimPos = ownUp.Position;
            }

            // HUD-5 — native nearest-enemy bracket: FindTarget(pshipSource,
            // c_ttShip | c_ttEnemy | c_ttNearest) over the current sector
            // (radarimage.cpp:582-583; Igc/common.cpp:470-630). Native only
            // resolves it while bRangeLabels is on, so the command camera never
            // pays for the scan and never draws the bracket.
            short? nearestEnemyId = null;
            if (rangeLabels)
            {
                _nearestEnemyScratch.Clear();
                foreach (var (id, ship) in world.Ships)
                {
                    if (id == me || ship.IsNotVisible || ship.Unknown || ship.ParentId >= 0) continue;
                    if (ship.SectorId != mySector || ship.Latest is not { }) continue;
                    if (!ResolveRadarContactAnchor(_poses, world.Ships, id, out var enemyPos)) continue;
                    float distance = (ownSimPos - enemyPos).Length();
                    _nearestEnemyScratch.Add(new(id,
                        Enemy: ship.SideId != mySide && !world.AreAllied(mySide, ship.SideId),
                        DistanceSquared: distance * distance));
                }
                nearestEnemyId = RadarLabelModel.NearestEnemy(_nearestEnemyScratch);
            }

            bool MatchesCommand(QueuedCommandState? command, IgcObjectType type, short id)
                => command is { } c && c.TargetType == type && c.TargetId == id;
            (int? CommandId, bool CommandAccepted) CommandFor(IgcObjectType type, short id)
            {
                if (MatchesCommand(accepted, type, id)) return (accepted!.Value.CommandId, true);
                if (MatchesCommand(queued, type, id)) return (queued!.Value.CommandId, false);
                return (null, false);
            }

            // Own ship — native c_maskMe marker (radarimage.cpp:733-735). The
            // marker only makes sense when the own hull is actually drawn:
            // external/chase cameras, and only when the own ship projects in
            // front (cockpit would otherwise edge-clamp it to bottom-centre).
            if (IsExternalFlightCamera(_cameraMode))
            {
                var ownXna = PoseConverter.ToXna(ownSimPos);
                var ownSite = RadarProjection.Project(
                    ownXna, cameraEye, view, proj, w, h, inset);
                if (ownSite.Placement == RadarBracketLayout.RadarPlacement.OnScreen)
                {
                    var ownBracket = Bracketing(ownSite, ownRadius, ownXna);
                    _drawRadarContacts.Add(new HudContact(
                        HudContactKind.Ship,
                        new HudPoint(ownSite.Point.X, ownSite.Point.Y),
                        true, op, SideHud(mySide), false, false,
                        // P783 — the native ship All tier labels the own ship too
                        // (bLabel = bStats = true, radarimage.cpp:740-745), so it
                        // carries its real name and its own hull/shield readings;
                        // every other LOD suppresses them again.
                        Name: string.IsNullOrEmpty(ownEnt.CharacterName) ? null : ownEnt.CharacterName,
                        Shield: ownEnt.Latest?.ShieldFraction,
                        Hull: ownEnt.Latest?.HullFraction,
                        IsSelf: true, IsSameSide: true,
                        Range: rangeLabels ? 0 : null,
                        ClipSeparation: LodSeparation(0f, ownRadius),
                        ScreenRadius: ownBracket.ScreenRadius,
                        DistanceToEdge: ownBracket.DistanceToEdge,
                        IconStem: iconNames?.Ship(ownEnt.HullId)));
                }
            }

            // Ships — other, living craft in my sector (skip corpses + unknown +
            // parented gunners; a departed ship's stale SectorId would otherwise
            // still match mySector and show on the radar frozen).
            foreach (var (id, ship) in world.Ships)
            {
                if (id == me || ship.IsNotVisible || ship.Unknown || ship.ParentId >= 0) continue;
                if (ship.SectorId != mySector) continue;
                if (ship.Latest is not { }) continue;                     // keep the Latest gate
                if (!ResolveRadarContactAnchor(_poses, world.Ships, id, out var shipPos)) continue;
                bool isEnemy = ship.SideId != mySide && !world.AreAllied(mySide, ship.SideId);
                // P436 — is this ship the player's own selected target? Pure
                // (RadarTargetSelection) so the flagging is unit-testable.
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Ship, id, selType, sel?.Id);
                // P624 — native per-kind LOD (friendly ships leave Target/Minimal).
                if (!RadarLod.ShowShip(radarMode, OwnOrAllied(ship.SideId), isSelf: false, isSel)) continue;
                var shipXna = PoseConverter.ToXna(shipPos);
                var site = RadarProjection.Project(
                    shipXna, cameraEye, view, proj, w, h, inset);
                // HUD-13 — this contact's native bracket-sizing inputs
                // (GetScreenRadius/GetDistanceToEdge, radarimage.cpp:307-319).
                var shipBracket = Bracketing(site, HullLength(ship.HullId) * 0.5f, shipXna);
                // P372 — resolve the contact's REAL side colour from the side
                // roster (native pside->GetColor(), radarimage.cpp:682-685);
                // null keeps the type-based teal fallback only while no roster
                // colour is known yet (TryGetSideColor returns false).
                Color? sideCol = SideColor(ship.SideId);
                var shipCommand = CommandFor(IgcObjectType.Ship, id);
                bool carriedFlag = RadarOverlayRules.CarriedFlag(ship.CarriedFlagSideId);
                bool carriedArtifact = RadarOverlayRules.CarriedArtifact(ship.CarriedFlagSideId);
                float separation = (ownSimPos - shipPos).Length();
                // HUD-12 — native radar overlays that had no data source before:
                // command-chat subjects (radarimage.cpp:572-574,651), threat
                // damage buckets (radarimage.cpp:660-671), drone ripcord
                // (radarimage.cpp:999-1005) and friendly ripcord seconds
                // (radarimage.cpp:1015-1030).
                bool sameSide = ship.SideId == mySide;
                bool subject = RadarOverlayRules.SubjectsApply(commandCamera, subjectComposing)
                    && RadarOverlayRules.IsSubject(
                        subjectTarget, subjectComposing, sameSide, sameSector: true,
                        ship.WingId, myWingId);
                bool threat = _hostileTracker.Threat(id, radarNowMs);
                bool threatFlash = threat && _hostileTracker.Flash(id, radarNowMs);
                byte pilotType = RadarOverlayRules.PilotTypeOf(ship.Player);
                bool droneRip = RadarOverlayRules.DroneRipcord(
                    ship.Latest?.StateM ?? 0, sameSide, pilotType);
                // P783 — native ship ore fill is gated on the pilot being a MINER
                // of the exact own side (radarimage.cpp:992-997), not merely on
                // the contact being friendly.
                bool minerFill = sameSide && pilotType == Allegiance.Core.Sim.Ship.PilotTypeMiner;
                // Remote ripcord start times are stamped with the world's wall
                // clock (ClientWorld.NowSeconds); the HUD's _nowSeconds is a
                // different base, so read the ripcord countdown on the world
                // clock (native measures GetRipcordTimeLeft, not a HUD frame).
                long worldNowMs = (long)(world.NowSeconds() * 1000);
                int? ripSeconds = world.TryGetRemoteRipcord(id, out long ripStart)
                    ? RadarOverlayRules.FriendlyRipcordSeconds(
                        isSelf: false, sameSide, active: true, ripStart, worldNowMs,
                        RipcordDurationSecondsFor(world, ship))
                    : null;
                _drawRadarContacts.Add(new HudContact(
                    HudContactKind.Ship,
                    new HudPoint(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op, SideHud(ship.SideId), isSel, false,
                    Name: string.IsNullOrEmpty(ship.CharacterName) ? null : ship.CharacterName,
                    Range: rangeLabels ? (int)(separation + 0.5f) : null,
                    Hull: ship.Latest?.HullFraction,
                    Shield: ship.Latest?.ShieldFraction,
                    Fill: minerFill ? ship.Latest?.OreFraction : null,
                    IsEnemy: isEnemy, IsSameSide: sameSide,
                    IsAllied: !sameSide && world.AreAllied(mySide, ship.SideId),
                    AcceptedOrder: shipCommand.CommandAccepted, QueuedOrder: shipCommand.CommandId is not null && !shipCommand.CommandAccepted,
                    Threat: threat, ThreatFlash: threatFlash,
                    Subject: subject,
                    CarriedFlag: carriedFlag, CarriedArtifact: carriedArtifact,
                    Ripcord: droneRip, RipcordSeconds: ripSeconds,
                    NearestEnemy: nearestEnemyId == id,
                    CommandId: shipCommand.CommandId, CommandAccepted: shipCommand.CommandAccepted,
                    // HUD-17 — native friendly detection eye: another SAME-SIDE
                    // PLAYER-piloted ship whose own-side ShipStatus carries the
                    // raw Detected bit (radarimage.cpp:1006-1019). The server now
                    // sends that bit for same-side player ships (the ship's own
                    // side's copy), so `ship.Detected` is real data, not a
                    // constant. The gate itself is RadarLabelModel's pure native
                    // port (ShowDetectionEyeFor), unit-pinned.
                    DetectionEye: RadarLabelModel.ShowDetectionEyeFor(ship, mySide, isSelf: false),
                    // P783 — the native clip runs on the surface separation, not
                    // the printed centre distance (radarimage.cpp:626-636).
                    ClipSeparation: LodSeparation(separation, HullLength(ship.HullId) * 0.5f),
                    ScreenRadius: shipBracket.ScreenRadius,
                    DistanceToEdge: shipBracket.DistanceToEdge,
                    IconStem: iconNames?.Ship(ship.HullId)));
                // P372 — the in-world hull "team tag": drawn only for ON-SCREEN
                // ships (an off-screen ship's edge-clamped bracket already
                // carries its side colour), always in the contact's real side
                // colour, so the 3D-view hull itself carries the side indicator
                // without recolouring the real per-civ art.
                if (site.Placement == RadarBracketLayout.RadarPlacement.OnScreen
                    && sideCol is { } tagCol)
                    _drawShipTags.Add(new Rendering.ShipSideTagRenderer.ShipTag(
                        new NumericsVec2(site.Point.X, site.Point.Y), tagCol));
            }

            // Stations in my sector.
            foreach (var (sid, st) in world.Stations)
            {
                if (st.ClusterId != mySector) continue;
                var sp = Vec(st.Position);
                float centre = (ownSimPos - sp).Length();
                float objectRadius = _shopCatalog?.StationTypes is { } stationTypes
                    && stationTypes.TryGetValue(st.StationTypeId, out var stationType) ? stationType.Radius : 0f;
                bool pedestal = IsPedestalType(st.StationTypeId);
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Station, sid, selType, sel?.Id);
                var stationXna = PoseConverter.ToXna(sp);
                var site = RadarProjection.Project(
                    stationXna, cameraEye, view, proj, w, h, inset);
                bool onScreen = site.Placement == RadarBracketLayout.RadarPlacement.OnScreen;
                if (!RadarLod.ShowStation(radarMode, pedestal, OwnOrAllied(st.SideId), onScreen,
                    LodSeparation(centre, objectRadius), isSel)) continue;
                var stationCommand = CommandFor(IgcObjectType.Station, sid);
                var stationBracket = Bracketing(site, objectRadius, stationXna);
                _drawRadarContacts.Add(new HudContact(
                    HudContactKind.Station,
                    new HudPoint(site.Point.X, site.Point.Y),
                    onScreen, op, SideHud(st.SideId), isSel, false,
                    Name: string.IsNullOrEmpty(st.Name) ? null : st.Name,
                    Range: rangeLabels ? (int)(centre + 0.5f) : null,
                    Hull: world.StationLive.TryGetValue(sid, out var live) ? live.HullFraction : null,
                    Shield: world.StationLive.TryGetValue(sid, out var liveShield) ? liveShield.ShieldFraction : null,
                    IsEnemy: st.SideId != mySide && !world.AreAllied(mySide, st.SideId),
                    IsSameSide: st.SideId == mySide,
                    IsAllied: st.SideId != mySide && world.AreAllied(mySide, st.SideId),
                    AcceptedOrder: stationCommand.CommandAccepted, QueuedOrder: stationCommand.CommandId is not null && !stationCommand.CommandAccepted,
                    CommandId: stationCommand.CommandId, CommandAccepted: stationCommand.CommandAccepted,
                    // P783 — the pedestal capability chooses the fixed 1000 m label
                    // tier (radarimage.cpp:784-791) and the clip uses the surface
                    // separation (:626-636).
                    IsPedestalStation: pedestal,
                    ClipSeparation: LodSeparation(centre, objectRadius),
                    ScreenRadius: stationBracket.ScreenRadius,
                    DistanceToEdge: stationBracket.DistanceToEdge,
                    IconStem: iconNames?.Station(st.StationTypeId)));
            }

            // Warps in my sector.
            foreach (var (wid, wp) in world.Warps)
            {
                if (wp.ClusterId != mySector) continue;
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Warp, wid, selType, sel?.Id);
                if (!RadarLod.ShowWarp(radarMode, isSel)) continue;
                var wPos = Vec(wp.Position);
                var warpXna = PoseConverter.ToXna(wPos);
                var site = RadarProjection.Project(
                    warpXna, cameraEye, view, proj, w, h, inset);
                float separation = (ownSimPos - wPos).Length();
                var warpCommand = CommandFor(IgcObjectType.Warp, wid);
                var warpBracket = Bracketing(site, wp.Radius, warpXna);
                _drawRadarContacts.Add(new HudContact(
                    HudContactKind.Warp,
                    new HudPoint(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op, null, isSel, false,
                    Name: string.IsNullOrEmpty(wp.Name) ? null : wp.Name,
                    Range: rangeLabels ? (int)(separation + 0.5f) : null,
                    AcceptedOrder: warpCommand.CommandAccepted, QueuedOrder: warpCommand.CommandId is not null && !warpCommand.CommandAccepted,
                    CommandId: warpCommand.CommandId, CommandAccepted: warpCommand.CommandAccepted,
                    // P783 — the Target-mode range clip (radarimage.cpp:955-956)
                    // compares the surface separation (:626-636).
                    ClipSeparation: LodSeparation(separation, wp.Radius),
                    ScreenRadius: warpBracket.ScreenRadius,
                    DistanceToEdge: warpBracket.DistanceToEdge,
                    // Native warp icon = the map's warpDef.iconName, which the
                    // wire's DataWarpIGC carries (warpIGC.cpp:34).
                    IconStem: iconNames?.Warp(wp.IconName)));
            }

            // Missiles — outside All only missiles aimed at the player's own side
            // are shown (radarimage.cpp:819-832).
            foreach (var (mid, ms) in world.Missiles)
            {
                if (ms.ClusterId != mySector) continue;
                bool aimsAtOwnSide = ms.TargetType switch
                {
                    IgcObjectType.Ship when world.Ships.TryGetValue(ms.TargetId, out var tgtShip) => tgtShip.SideId == mySide,
                    IgcObjectType.Station when world.Stations.TryGetValue(ms.TargetId, out var tgtStation) => tgtStation.SideId == mySide,
                    _ => false,
                };
                short launcherSide = ms.LauncherId >= 0 && world.Ships.TryGetValue(ms.LauncherId, out var launchShip)
                    ? launchShip.SideId : (short)-1;
                Color? sideCol = launcherSide >= 0 ? SideColor(launcherSide) : null;
                if (!RadarLod.ShowMissile(radarMode, aimsAtOwnSide, isSelected: false)) continue;
                float centre = (ownSimPos - ms.Position).Length();
                var missileXna = PoseConverter.ToXna(ms.Position);
                var site = RadarProjection.Project(
                    missileXna, cameraEye, view, proj, w, h, inset);
                // HUD-13 — the client wire carries no missile model radius, so
                // this takes native's minimum icon radius (unused anyway: a
                // missile carries no overlay bracket).
                var missileBracket = Bracketing(site, 0f, missileXna);
                _drawRadarContacts.Add(new HudContact(
                    HudContactKind.Missile,
                    new HudPoint(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    sideCol is { } c ? new HudColor(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f) : null,
                    false, false,
                    // P783 — the missile tier (radarimage.cpp:819-832) is a range
                    // line only: native keeps it in All and, outside All, only for
                    // a missile aimed at the exact own side (GetSide() == mine).
                    Range: rangeLabels ? (int)(centre + 0.5f) : null,
                    MissileTargetsOwnSide: aimsAtOwnSide,
                    ClipSeparation: LodSeparation(centre, 0f),
                    ScreenRadius: missileBracket.ScreenRadius,
                    DistanceToEdge: missileBracket.DistanceToEdge,
                    // Native missile icon = dataMissileType->iconName
                    // (missileigc.cpp:50,60), keyed by the wire's type id.
                    IconStem: iconNames?.Missile(ms.TypeId)));
            }

            // Probes in my sector.
            foreach (var (pid, pb) in world.Probes)
            {
                if (pb.ClusterId != mySector) continue;
                float centre = (ownSimPos - pb.Position).Length();
                float objectRadius = _shopCatalog?.ProbeTypes is { } probeTypes
                    && probeTypes.TryGetValue(pb.ProbetypeId, out var probeType)
                        ? probeType.Radius : 0f;
                string? probeTypeName = _shopCatalog?.ProbeTypes is { } probeTypesNamed
                    && probeTypesNamed.TryGetValue(pb.ProbetypeId, out var probeTypeNamed)
                        ? probeTypeNamed.ModelName : null;
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Probe, pid, selType, sel?.Id);
                if (!RadarLod.ShowProbeOrMine(radarMode, OwnOrAllied(pb.SideId),
                    LodSeparation(centre, objectRadius), isSel)) continue;
                var probeXna = PoseConverter.ToXna(pb.Position);
                var site = RadarProjection.Project(
                    probeXna, cameraEye, view, proj, w, h, inset);
                var probeBracket = Bracketing(site, objectRadius, probeXna);
                bool probeSameSide = pb.SideId == mySide;
                _drawRadarContacts.Add(new HudContact(
                    HudContactKind.Probe,
                    new HudPoint(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    SideHud(pb.SideId), isSel, false,
                    // P783 — native probe/mine tier (radarimage.cpp:834-875): the
                    // model name is the All-mode enemy name line, hull is the real
                    // probe health (GetFraction, :1080). The native fill bar is
                    // GetTimeFraction (:1079), which needs the probe's m_time0 that
                    // the export does not carry - left absent, see the report.
                    Name: string.IsNullOrEmpty(probeTypeName) ? null : probeTypeName,
                    Range: rangeLabels ? (int)(centre + 0.5f) : null,
                    Hull: pb.Fraction,
                    IsEnemy: !probeSameSide && !world.AreAllied(mySide, pb.SideId),
                    IsSameSide: probeSameSide,
                    IsAllied: !probeSameSide && world.AreAllied(mySide, pb.SideId),
                    ClipSeparation: LodSeparation(centre, objectRadius),
                    ScreenRadius: probeBracket.ScreenRadius,
                    DistanceToEdge: probeBracket.DistanceToEdge,
                    IconStem: iconNames?.Probe(pb.ProbetypeId)));
            }

            // Mines in my sector.
            foreach (var (mineId, mine) in world.Mines)
            {
                if (mine.ClusterId != mySector) continue;
                float centre = (ownSimPos - mine.Position).Length();
                float objectRadius = Rendering.MineModelSource.ResolveRadius(mine.MinetypeId, _shopCatalog?.MineTypes);
                string? mineTypeName = _shopCatalog?.MineTypes is { } mineTypes
                    && mineTypes.TryGetValue(mine.MinetypeId, out var mineType)
                        ? mineType.ModelName : null;
                if (!RadarLod.ShowProbeOrMine(radarMode, OwnOrAllied(mine.SideId),
                    LodSeparation(centre, objectRadius), isSelected: false)) continue;
                var mineXna = PoseConverter.ToXna(mine.Position);
                var site = RadarProjection.Project(
                    mineXna, cameraEye, view, proj, w, h, inset);
                var mineBracket = Bracketing(site, objectRadius, mineXna);
                bool mineSameSide = mine.SideId == mySide;
                _drawRadarContacts.Add(new HudContact(
                    HudContactKind.Mine,
                    new HudPoint(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    SideHud(mine.SideId), false, false,
                    // P783 — native mine tier (radarimage.cpp:834-875): name for an
                    // on-screen enemy in All, and the fill bar is the mine's
                    // GetTimeFraction (:1073), which needs m_time0 from the
                    // export - absent, so no bar is shown (see the report).
                    Name: string.IsNullOrEmpty(mineTypeName) ? null : mineTypeName,
                    Range: rangeLabels ? (int)(centre + 0.5f) : null,
                    IsEnemy: !mineSameSide && !world.AreAllied(mySide, mine.SideId),
                    IsSameSide: mineSameSide,
                    IsAllied: !mineSameSide && world.AreAllied(mySide, mine.SideId),
                    ClipSeparation: LodSeparation(centre, objectRadius),
                    ScreenRadius: mineBracket.ScreenRadius,
                    DistanceToEdge: mineBracket.DistanceToEdge,
                    IconStem: iconNames?.Mine(mine.MinetypeId)));
            }

            // HUD-4b — resolve the own loadout/hull once per frame so every
            // treasure reuses the same pack-usefulness and launcher-mountability
            // answers instead of rescanning the catalogue and passing constant
            // trues (radarimage.cpp:480-508).
            DataHullTypeIGC? ownHull = _hullById.TryGetValue(ownEnt.HullId, out var ownHullType) ? ownHullType : null;
            bool ownHasAfterburner = false, ownHasAmmoWeapon = false;
            if (ownEnt.Loadout is { Count: > 0 } ownLoadout)
            {
                foreach (var p in ownLoadout)
                {
                    if (ResolveFlightPart(p.PartId) is not { } mountedPart) continue;
                    if (mountedPart.Type is DataPartTypeIGC.Afterburner) ownHasAfterburner = true;
                    else if (mountedPart.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: not 0 }) ownHasAmmoWeapon = true;
                }
            }

            // Treasures in my sector — useful part vs tech vs not-useful
            // (radarimage.cpp:437-511).
            // P783 — the native treasure NAME the All-mode label prints
            // (Igc/treasureIGC.cpp:121-167): the buyable's model name for a part
            // or development, "Flag"/"Artifact" for a flag (SetName), and the
            // literal "acs14" model name for cash/powerup.
            string? TreasureName(TreasureEntity te)
            {
                switch (te.Code)
                {
                    case TreasureCode.Flag:
                        return te.PayloadId == Allegiance.Core.Model.Side.ArtifactSentinelFlag
                            ? "Artifact" : "Flag";
                    case TreasureCode.Part:
                        return ResolveFlightPart(te.PayloadId)?.ModelName;
                    case TreasureCode.Development:
                        return world.MissionCore?.Developments.TryGetValue(te.PayloadId, out var dev) == true
                            ? dev.ModelName : null;
                    default:
                        return "acs14";
                }
            }
            foreach (var (tid, te) in world.Treasures)
            {
                if (te.ClusterId != mySector) continue;
                float centre = (ownSimPos - te.Position).Length();
                var treasurePart = ResolveFlightPart(te.PayloadId);
                EquipmentType? partEquipmentType = treasurePart?.EquipmentType;
                bool packUseful = true;
                bool launcherMountable = false;
                if (treasurePart is { } tp)
                {
                    if (tp.EquipmentType == EquipmentType.Pack && tp.Type is DataPartTypeIGC.Pack pack)
                        packUseful = pack.PackType == 1 ? ownHasAfterburner : ownHasAmmoWeapon;   // c_packFuel=1, c_packAmmo=0
                    if (RadarTreasure.IsLauncherType(tp.EquipmentType) && ownHull is { } hull)
                        launcherMountable = InFlightPartRules.CanMount(hull, tp, 0);
                }
                // The native tech-vs-part discrimination still needs the side's
                // live development-tech mask; a default (unobserved) mask has no
                // usable bits, so partEffectsSatisfied stays true (documented
                // deferral). Pack usefulness + launcher mountability ARE live.
                var use = RadarTreasure.Usefulness(te.Code, partEquipmentType,
                    partEffectsSatisfied: true, packUseful, launcherMountable);
                bool useful = use != RadarTreasureUse.NotUseful;
                bool isTech = use == RadarTreasureUse.UsefulTech;
                bool isSel = sel is { Kind: TargetKind.Treasure } treasureSel && treasureSel.Id == tid;
                if (!RadarLod.ShowTreasure(radarMode, useful, LodSeparation(centre, 5f), isSel)) continue;
                var treasureXna = PoseConverter.ToXna(te.Position);
                var site = RadarProjection.Project(
                    treasureXna, cameraEye, view, proj, w, h, inset);
                // HUD-13 — the wire carries no treasure model radius; native's
                // minimum icon radius applies (a treasure carries only the
                // flag highlight, which has no art in this checkout).
                var treasureBracket = Bracketing(site, 0f, treasureXna);
                _drawRadarContacts.Add(new HudContact(
                    isTech ? HudContactKind.Tech : HudContactKind.Treasure,
                    new HudPoint(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    null, isSel, false,
                    // P783 — the treasure tier (radarimage.cpp:877-899): a useful
                    // treasure/tech prints its name in All only, its range in
                    // every LOD except the Target clip, and never any stat bar.
                    Name: TreasureName(te),
                    Range: rangeLabels ? (int)(centre + 0.5f) : null,
                    IsSelf: false, IsTech: isTech, TreasureUseful: useful,
                    // Native SetRadius(5.0f) for a treasure (treasureIGC.cpp:193).
                    ClipSeparation: LodSeparation(centre, 5f),
                    ScreenRadius: treasureBracket.ScreenRadius,
                    DistanceToEdge: treasureBracket.DistanceToEdge,
                    // Native treasure icon = the treasure model's own icon, with
                    // the useful-tech override to icontechbmp (radarimage.cpp:719,890).
                    IconStem: iconNames?.Treasure(te.Code, te.PayloadId, isTech)));
            }

            // Asteroids — native ordinary-rock LOD (radarimage.cpp:903-930) plus
            // the P495 build-spot highlight for a hovered station-type investment.
            var investAabm = _investAsteroidAabm;
            foreach (var (aid, ast) in world.Asteroids)
            {
                if (ast.ClusterId != mySector) continue;
                var astPos = Vec(ast.Position);
                float centre = (ownSimPos - astPos).Length();
                float separation = LodSeparation(centre, ast.Radius);
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Asteroid, aid, selType, sel?.Id);
                bool namedSpecial = !string.IsNullOrEmpty(ast.Name) && ast.Name[0] != 'A';
                bool buildSpot = investAabm is { } activeAabm && RadarInvestAsteroidSelection.IsBuildSpot(activeAabm, ast.AabmCapabilities);
                // Project only asteroids the radar will actually draw (HUD-4b —
                // avoid a per-frame matrix projection for every rock in sector).
                if (!buildSpot && !RadarLod.ShowAsteroid(radarMode, namedSpecial, rangeZero, separation, isSel)) continue;
                var asteroidXna = PoseConverter.ToXna(astPos);
                var aSite = RadarProjection.Project(
                    asteroidXna, cameraEye, view, proj, w, h, inset);
                world.AsteroidLive.TryGetValue(aid, out var asteroidLive);
                var asteroidCommand = CommandFor(IgcObjectType.Asteroid, aid);
                var asteroidBracket = Bracketing(aSite, ast.Radius, asteroidXna);
                _drawRadarContacts.Add(new HudContact(
                    HudContactKind.Asteroid,
                    new HudPoint(aSite.Point.X, aSite.Point.Y),
                    aSite.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    null, isSel, buildSpot,
                    Name: string.IsNullOrEmpty(ast.Name) ? null : ast.Name,
                    Range: rangeLabels ? (int)(centre + 0.5f) : null,
                    Hull: world.AsteroidLive.TryGetValue(aid, out var liveRock) ? liveRock.Fraction : null,
                    Fill: world.AsteroidLive.TryGetValue(aid, out var liveOre) ? liveOre.OreFraction : null,
                    AcceptedOrder: asteroidCommand.CommandAccepted, QueuedOrder: asteroidCommand.CommandId is not null && !asteroidCommand.CommandAccepted,
                    CommandId: asteroidCommand.CommandId, CommandAccepted: asteroidCommand.CommandAccepted,
                    // P783 — the asteroid tier clips on the surface separation
                    // (radarimage.cpp:626-636,909,926,939).
                    ClipSeparation: separation,
                    ScreenRadius: asteroidBracket.ScreenRadius,
                    DistanceToEdge: asteroidBracket.DistanceToEdge,
                    // Native asteroid icon = AsteroidDef.iconName, carried by the
                    // wire's DataAsteroidIGC.IconName (asteroidigc.cpp:35).
                    IconStem: iconNames?.Asteroid(ast.IconName)));
            }

            // HUD-5b — opt-in evidence line for the frame (see _radarProofEnabled).
            if (_radarProofEnabled)
            {
                int ships = 0, enemies = 0;
                var proofLabels = new RadarLabelCache();
                var enemyProof = new System.Text.StringBuilder();
                for (int i = 0; i < _drawRadarContacts.Count; i++)
                {
                    var c = _drawRadarContacts[i];
                    if (c.Kind != HudContactKind.Ship || c.IsSelf) continue;
                    ships++;
                    if (!c.IsEnemy) continue;
                    enemies++;
                    var proofLabel = proofLabels.Resolve(i, c, radarMode, commandCamera);
                    if (enemyProof.Length > 0) enemyProof.Append("; ");
                    enemyProof.Append($"{c.Name ?? "?"}@{c.Position.X:0},{c.Position.Y:0}")
                        .Append(c.OnScreen ? " on" : " off")
                        .Append(c.NearestEnemy ? " nearest" : "")
                        .Append($" name='{proofLabel.NameLine ?? "-"}' range='{proofLabel.RangeLine ?? "-"}'")
                        // HUD-13 — the native bracket-sizing inputs this frame.
                        .Append($" screenRadius={c.ScreenRadius:0.##} distanceToEdge={c.DistanceToEdge:0.##}");
                }
                _radarProofLine = $"contacts={_drawRadarContacts.Count} ships={ships} enemies={enemies}"
                    + $" nearestEnemy={(nearestEnemyId is { } nid ? nid.ToString() : "none")}"
                    + $" rangeLabels={rangeLabels} enemy[{enemyProof}]";
            }
        }

    }
    /// <summary>
    /// P578 — draw the eject/death feedback overlays straight onto the HUD,
    /// called from <see cref="DrawHud"/> after the fight widgets: the critical +
    /// non-critical tip lines (native <c>PostText</c> →
    /// <c>SetCriticalTipText</c>/<c>SetTipText</c>, trekigc.cpp:3067-3083; the
    /// critical <c>CriticalTipText0</c> centred red, the tips in the accent
    /// colour, both expiring after native's 7.5 s, console.cpp:913), the
    /// eject-pod panel with the endurance gauge (native <c>EjectPodImage</c>,
    /// dialog.mdl:1926-1960 — real <c>conejectpoddisplaybmp.mdl</c> when the
    /// artwork is present), and the death banner when the local ship is a ghost
    /// (native <c>DeathBannerPane = SwitchPane(IsGhost, deathbannerbmp)</c>,
    /// dialog.mdl:5438-5439). Text-only ASCII, so it renders in the spritefont.
    /// </summary>
    private void DrawEjectDeathOverlays(HudModel model)
    {
        var vp = GraphicsDevice.Viewport;

        if (!model.Visible) return;
        bool inPod = model.PodVisible, ghost = model.DeathVisible;
        float oxyFraction = model.Telemetry?.FuelFraction ?? 0f;
        int oxySec = model.Telemetry?.OxygenSecondsRemaining ?? 0;
        if (!inPod && !ghost) return;

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            // ── 1. Eject-pod identity panel + oxygen endurance bar ─────────
            // Native DefaultEjectPodDisplayOn: conejectpoddisplaybmp (172x44)
            // with softgaugebmp (52x4) at (66,10), EjectPodImage at
            // SideCenter(-86,-87) (dialog.mdl:1922-1996,5436). The art is drawn
            // at its natural size, not scaled to the viewport.
            if (inPod)
            {
                var (px, py) = LifepodPanelLayout.PanelTopLeft(vp.Width, vp.Height);
                var panelTex = _feedbackTextures?.EjectPodPanel;
                if (panelTex is not null)
                    _spriteBatch.Draw(panelTex, new Rectangle(px, py,
                        LifepodPanelLayout.PanelWidth, LifepodPanelLayout.PanelHeight), Color.White);
                else
                {
                    FillP578Rect(px, py, LifepodPanelLayout.PanelWidth, LifepodPanelLayout.PanelHeight,
                        new Color(0.020f, 0.055f, 0.060f, 0.72f));
                    FillP578Rect(px, py, LifepodPanelLayout.PanelWidth, 1f, new Color(0.14f, 0.34f, 0.32f));
                    _spriteBatch.DrawString(_hudSmall, "YOU HAVE EJECTED.",
                        new Vector2(px + 6f, py + 6f), new Color(0.88f, 0.96f, 0.95f));
                    _spriteBatch.DrawString(_hudSmall, $"O : {P578Countdown(oxySec)}",
                        new Vector2(px + 6f, py + LifepodPanelLayout.PanelHeight - _hudSmall.LineSpacing),
                        new Color(0.6f, 0.6f, 0.6f));
                }

                // Native GaugeImage(JustifyLeft, softgaugebmp, GetEndurance):
                // an integer-width left-to-right fill of the 52x4 bar
                // (dialog.mdl:1927-1935; engine/image.cpp:492-529).
                var (gx, gy) = LifepodPanelLayout.GaugeTopLeft(vp.Width, vp.Height);
                var gaugeTex = _hudNativeTextures?.SoftGauge;
                if (gaugeTex is not null)
                {
                    int fill = (int)(LifepodPanelLayout.GaugeWidth * Math.Clamp(oxyFraction, 0f, 1f));
                    if (fill > 0)
                        _spriteBatch.Draw(gaugeTex,
                            new Rectangle(gx, gy, fill, LifepodPanelLayout.GaugeHeight),
                            new Rectangle(0, 0, fill, LifepodPanelLayout.GaugeHeight), Color.White);
                }
                else
                {
                    FillP578Rect(gx, gy, LifepodPanelLayout.GaugeWidth, LifepodPanelLayout.GaugeHeight,
                        new Color(0.08f, 0.20f, 0.18f));
                    FillP578Rect(gx, gy, LifepodPanelLayout.GaugeWidth * Math.Clamp(oxyFraction, 0f, 1f),
                        LifepodPanelLayout.GaugeHeight, new Color(0.07f, 0.95f, 0.84f));
                }
            }

            // ── 3. Death banner (ghost) ────────────────────────────────────
            // Native DeathBannerPane = unscaled deathbannerbmp at
            // SideCenter(-105,15) (dialog.mdl:1998-2003,5439).
            if (ghost)
            {
                var (bx, by) = DeathBannerLayout.TopLeft(vp.Width, vp.Height);
                var bannerTex = _feedbackTextures?.DeathBanner;
                if (bannerTex is not null)
                    _spriteBatch.Draw(bannerTex, new Rectangle(bx, by,
                        DeathBannerLayout.Width, DeathBannerLayout.Height), Color.White);
                else
                {
                    string b = "YOU WERE KILLED";
                    _spriteBatch.DrawString(_hudSmall, b, new Vector2(bx, by), new Color(0.95f, 0.30f, 0.22f));
                }
            }
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>P578 — mm:ss countdown string for the pod's endurance readout.</summary>
    private static string P578Countdown(int seconds)
    {
        seconds = Math.Max(0, seconds);
        int m = seconds / 60, s = seconds % 60;
        return $"{m:D2}:{s:D2}";
    }

    /// <summary>P578 — a filled rectangle for the procedural pod-panel fallback
    /// (same white-pixel trick the HUD renderer uses).</summary>
    private void FillP578Rect(float x, float y, float w, float h, Color c)
    {
        _spriteBatch.Draw(_whitePixel, new Rectangle((int)x, (int)y, (int)w, (int)h), c);
    }

    /// <summary>
    /// P436 — the on-screen counterpart to P415's console-only "no &lt;noun&gt; in
    /// sector" line: when a U/Z filtered-target key finds no matching candidate
    /// in the sector, show a brief centred HUD line (existing HUD-small font,
    /// fading out over the final moments of the display window) so the pilot
    /// SEES the failed lookup while flying instead of only reading it in the
    /// console. Matches the port's existing HUD-text convention — a short
    /// <c>SpriteBatch.DrawString</c> line in <c>_hudSmall</c>; deliberately no
    /// new toast/notification system.
    /// </summary>
    private void DrawTargetNoMatchFeedback()
    {
        if (_targetNoMatchNoun is not { } noun || _hideHud || _directorActive || _hudHidden) return;
        double remaining = _targetNoMatchUntil - _nowSeconds;
        if (remaining <= 0d)
        {
            _targetNoMatchNoun = null;
            return;
        }
        string text = $"NO {noun.ToUpperInvariant()} IN SECTOR";
        var vp = GraphicsDevice.Viewport;
        var sz = _hudSmall.MeasureString(text);
        var pos = new Vector2((vp.Width - sz.X) / 2f, vp.Height * 0.40f);
        // Fade out over the final 0.6 s of the display window (the line stays
        // fully visible for the bulk of it, then dissolves rather than popping).
        float fade = (float)Math.Clamp(remaining / 0.6, 0.0, 1.0);
        var colour = new Color(1f, 1f, 1f) * fade;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            ActiveHud.DrawOverlayText(text, pos, colour);
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>
    /// The number of distinct fixed-mount ids the pilot can cycle the weapon
    /// selection among. The wire never transmits the local hull's hardpoint
    /// list, and a stock auto-connect flight never populates the own ship's
    /// loadout (the own-ship EXPORT is the NA(-1) dummy, ClientWorld.cs:215;
    /// BUY_LOADOUT_ACK only reaches a buyer) — so there is no authoritative
    /// count without a server/wire change (out of scope for P225). When a
    /// mounted loadout IS known we use its distinct non-negative mount ids;
    /// otherwise we fall back to the full 2-bit selection range
    /// (SelectedWeaponMask+1 = 4) — the largest set of distinct mount ids the
    /// sim's selection mask can express. Selecting an id that has no weapon
    /// mounted simply fires nothing (Weapon.IsWeaponSelected), which is
    /// harmless; group-fire does not consult the selection at all.
    /// </summary>
    private int FixedMountCount()
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } id && _session.World.Ships.TryGetValue(id, out var ship)
                && _shopCatalog is { } catalog)
                foreach (var hull in catalog.Hulls)
                    if (hull.HullId == ship.HullId) return hull.MaxFixedWeapons;
        }
        return 0;
    }

    /// <summary>Adopt the sim's <c>float[3]</c> wire position as a <see cref="SimVec"/>.</summary>
    private static SimVec Vec(float[] a) => new(a[0], a[1], a[2]);

    /// <summary>
    /// P286 — the shared per-resource-type diffuse for asteroids, built lazily
    /// from <see cref="RockTextureFactory.GeneratePixels"/> (deterministic,
    /// seeded) and cached so the draw loop only pays for the handful of types
    /// a sector actually shows, never one texture per rock.
    /// P421 — now prefers the REAL original-game surface bitmap for the type
    /// (decoded in <see cref="RockTextures.Load"/>) when one exists; the
    /// procedural generator remains the documented fallback for gold (which has
    /// no native rock row) and any missing/undecodable asset.
    /// </summary>
    private Texture2D EnsureAsteroidTexture(RockTextureFactory.AsteroidResourceType type)
    {
        if (_asteroidTextures.TryGetValue(type, out var tex))
            return tex;

        tex = _rockTextures.For(type);               // P421 real original surface
        if (tex is null)
        {
            var px = RockTextureFactory.GeneratePixels(
                RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSeed, type);
            // P529 — mipmapped fallback too (same anti-alias treatment as the
            // real surfaces), so a procedural rock minifies smoothly as well.
            tex = TextureMipmapBuilder.CreateMipmapped(GraphicsDevice, px, RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSize);
        }
        _asteroidTextures[type] = tex;
        return tex;
    }

    /// <summary>
    /// P333 — the real <c>HullType.Length</c> for a hull id, from the cached
    /// shop-catalogue lookup. 0 when unknown (empty catalogue / hull not found)
    /// so the caller's scale degrades to 1 (no scaling) rather than breaking.
    /// </summary>
    private float HullLength(short hullId) =>
        _hullLengthById.TryGetValue(hullId, out var len) ? len : 0f;

    /// <summary>
    /// P448b — the <see cref="ClientWorld.RemoteShipHullResolver"/> body: effective
    /// hull stats for a REMOTE ship from the loaded shop-catalogue hull record × that
    /// ship's side's global-attribute factors (identity when the side is unknown —
    /// exactly how the server treats a sideless ship, per <c>ShipDeadReckoner.HullStats.FromHull</c>),
    /// plus the afterburner max thrust. Registered on ClientWorld at construction so
    /// the dead-reckoner can (re-)seed each remote ship from real hull data.
    /// Returns null when the hull record isn't in the (possibly absent) catalogue yet
    /// — the ship is then rendered on its raw pose until a later update resolves it.
    /// Afterburner is treated as ABSENT (0) for remote ships: the client has no
    /// reliable per-ship part/loadout table for arbitrary ships (P448a §5), and a null
    /// afterburner is behaviourally identical to native's no-afterburner branch —
    /// documented, harmless over ≤1 tick of extrapolation.
    /// </summary>
    private (Allegiance.ClientCore.Flight.ShipDeadReckoner.HullStats Stats, float AfterburnerMaxThrust)?
        ResolveRemoteHullStats(short sideId, short hullId)
    {
        var cat = _shopCatalog;
        if (cat is null) return null;
        DataHullTypeIGC? hull = null;
        foreach (var h in cat.Hulls)
            if (h.HullId == hullId) { hull = h; break; }
        if (hull is null) return null;

        var gas = _session.World.GlobalAttributesFor(sideId);
        var stats = Allegiance.ClientCore.Flight.ShipDeadReckoner.HullStats.FromHull(
            hull.Value,
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.Thrust),
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.MaxSpeed),
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.TurnRate),
            gas.Get(Allegiance.Core.Model.GlobalAttributeSet.TurnTorque));
        return (stats, 0f);   // afterburner absent for remote ships (see doc above)
    }

    /// <summary>
    /// P288 — resolve the real per-faction/class ship content model for a ship
    /// (its side id + hull id), via the civ+class lookup. The civ key comes from
    /// the ship's real side resolved through the client's real side roster
    /// (<c>SideRoster</c>, which carries each side's CivilizationId); the class
    /// comes from the hull's real <c>ModelName</c>. Returns null when the
    /// civ/hull can't be resolved → caller uses the bios_scout fallback. Called
    /// under the world Gate lock (with the pose-gather pass), so the Draw loop
    /// reads the result lock-free.
    /// </summary>
    private XnaModel? ResolveShipModel(short sideId, short hullId)
    {
        var asset = ResolveShipAsset(sideId, hullId);
        return asset is not null && _shipModels.TryGetValue(asset, out var m) ? m : null;
    }

    /// <summary>
    /// P337 — the real Meshy baseColor texture for the same asset
    /// <see cref="ResolveShipModel"/> resolved (identical civ+hull derivation,
    /// keyed by the same asset name), or null when unresolvable → the caller
    /// falls back to <c>_fallbackTexture</c>. Kept as a sibling lookup so the
    /// texture always pairs with the model that was resolved for a ship.
    /// </summary>
    private Texture2D? ResolveShipTexture(short sideId, short hullId)
    {
        var asset = ResolveShipAsset(sideId, hullId);
        return asset is not null && _shipTextures.TryGetValue(asset, out var t) ? t : null;
    }

    /// <summary>The civ+class P287 asset name for a ship's (side, hull), shared
    /// by the model and texture resolvers so they always agree.</summary>
    private string? ResolveShipAsset(short sideId, short hullId)
    {
        if (_hullResolver is null || _shopCatalog is null) return null;
        if (hullId == 0) return null;                              // no SHIP_STATUS/EXPORT yet
        // P581b — a "catalogue:<id>" override draws that catalogue model (repaired preferred)
        // instead of the hull's own civ+class Meshy asset. The resolved key is returned
        // directly; when it can't load, ShipsNativeFor already chose the native fallback.
        if (_hullById is { } hulls && hulls.TryGetValue(hullId, out var hull)
            && _shipArtOverride.TryGetCatalogue(hull.ModelName?.Trim(), hullId) is { } catId)
        {
            var catKey = _shipCatalogue.ResolveKey(catId, _shipModelKeys);
            if (catKey is not null) return catKey;
        }
        var civKey = CivKeyForShipSide(_session.World, sideId);
        if (civKey is null) return null;
        return _hullResolver.ResolveAsset(civKey, hullId, _shopCatalog);
    }

    /// <summary>
    /// P569 — per-hull cache of the shield-flare cap's native inputs: the
    /// struck ship's shield ellipsoid (native-mesh per-axis extents) and its
    /// mesh-bounding radius (native m_radiusMesh). Keyed by hull id so a burst
    /// of hits on one hull never re-scans the native mesh.
    private readonly Dictionary<short, (SimVec Ellipse, float MeshRadius)> _shieldEllipseById = new();

    /// <summary>
    /// P569 — resolve the struck ship's shield ellipsoid + mesh radius for the
    /// shield-flare cap: the native-mesh per-axis half-extents (the client's
    /// proxy for native's hull hit-test <c>GetEllipseEquation()</c>,
    /// <c>shieldIGC.cpp:171</c>) and its merged-bounding radius
    /// (<c>m_radiusMesh</c>, thinggeo.cpp:1053). Falls back to the
    /// <c>0.5·Length</c> sphere (<c>fallbackRadius</c> on all axes + as the mesh
    /// radius) when the hull's native mesh is unknown/undecodable — preserving
    /// the formula with a spherical shield (the P545 approximation).
    /// </summary>
    private (SimVec Ellipse, float MeshRadius) ResolveShieldEllipse(short hullId, float fallbackRadius)
    {
        if (fallbackRadius <= 0f) fallbackRadius = Rendering.ShieldFlareParticles.ShipRadiusDefault;
        var fallback = (new SimVec(fallbackRadius, fallbackRadius, fallbackRadius), fallbackRadius);

        if (hullId < 0) return fallback;
        if (_shieldEllipseById.TryGetValue(hullId, out var cached)) return cached;

        var result = fallback;
        if (_hullById is { } hulls && hulls.TryGetValue(hullId, out var hull)
            && !string.IsNullOrEmpty(hull.ModelName)
            && _nativeShipRenderer is { } nsr
            && nsr.ShieldEllipse(hull.ModelName.Trim()) is { } ext
            && ext.X > 0f && ext.Y > 0f && ext.Z > 0f)
        {
            float mr = nsr.MeshRadius(hull.ModelName.Trim());
            result = (new SimVec(ext.X, ext.Y, ext.Z), mr > 0f ? mr : fallbackRadius);
        }

        _shieldEllipseById[hullId] = result;
        return result;
    }

    /// <summary>
    /// P607 — startup audit of NATIVE ship art resolution across the whole
    /// loaded core (mirroring P593b's projectile/missile audits): every hull's
    /// <c>ModelName</c> <c>.mdl</c> must resolve through the native ship
    /// renderer (exists + decodes to geometry). P800 extends it beyond "mesh
    /// non-null" with the degeneracy checks that can still leave a hull
    /// invisible (empty mesh, non-finite/non-positive radius → bad scale,
    /// non-positive Length → bad preview framing, unresolvable texture stem —
    /// see <see cref="Rendering.ShipNativeAudit.CollectDefects"/>).
    /// Reports N hulls and the unresolved/defect list — 0 means every hull
    /// renders native; any hull that fails is logged once by name and falls
    /// back to the procedural stand-in at draw time (never a crash, never
    /// invisible).
    /// </summary>
    private void AuditNativeShipResolution()
    {
        if (_hullById is null || _nativeShipRenderer is null) return;
        var unresolved = Rendering.ShipNativeAudit.CollectUnresolved(
            _hullById.Select(kv => (kv.Key, kv.Value.Name, (string?)kv.Value.ModelName)),
            stem => _nativeShipRenderer.MeshFor(stem!) is not null);
        int n = _hullById.Count;
        foreach (var u in unresolved)
            Console.WriteLine($"[client] P607 native ship resolution FAILED: hull {u.HullId} '{u.Name}' (ModelName '{u.Stem}') — will render the procedural stand-in");
        // P800 — degeneracy audit over the SAME parsed meshes (cheap: the meshes
        // are already compiled by the resolution pass above; the texture-stem
        // check is an asset-existence probe, not a decode).
        var defects = Rendering.ShipNativeAudit.CollectDefects(_hullById.Values.Select(h =>
        {
            var stem = h.ModelName?.Trim();
            var geo = stem is { Length: > 0 } s ? _nativeShipRenderer.MeshFor(s) : null;
            return new Rendering.ShipNativeAudit.HullHealth(
                h.HullId, h.Name, h.ModelName,
                MeshResolved: geo is not null,
                TriangleCount: geo?.TriangleCount ?? 0,
                MeshRadius: geo?.MeshRadius ?? 0f,
                MaxVertexRadius: geo?.MaxVertexRadius ?? 0f,
                HullLength: h.Length,
                TextureStemResolved: stem is { Length: > 0 } st
                    && _nativeShipRenderer.TextureStemFor(st, h.TextureName) is not null);
        }));
        foreach (var d in defects)
            Console.WriteLine($"[client] P800 native ship health FAILED: {d}");
        Console.WriteLine(unresolved.Count == 0 && defects.Count == 0
            ? $"[monogame] P607 ship-model audit: {n} hull(s), 0 unresolved native, 0 degenerate (P800 health ok)"
            : unresolved.Count == 0
                ? $"[monogame] P607 ship-model audit: {n} hull(s), 0 unresolved native, {defects.Count} degenerate (P800)"
                : $"[monogame] P607 ship-model audit: {n} hull(s), {unresolved.Count} unresolved native -> procedural stand-in");
    }

    /// <summary>
    /// P566 — resolve a ship's NATIVE art (original <c>.mdl</c> mesh + native
    /// texture) from its real hull record: <c>ModelName</c> → the native mesh stem,
    /// <c>TextureName</c>/model-import → the native texture stem
    /// (<see cref="NativeShipRenderer.TextureStemFor"/>), and the native uniform
    /// scale <c>m_scale = (0.5·Length)/meshRadius</c>. Returns null when the hull is
    /// unknown or its native mesh fails to decode — the caller then falls back to
    /// the Meshy content path (then the placeholder), exactly the required chain.
    /// </summary>
    private NativeShipRef? ResolveNativeShip(short hullId)
    {
        if (_hullById is null || !_hullById.TryGetValue(hullId, out var hull)) return null;
        var modelStem = hull.ModelName?.Trim();
        if (string.IsNullOrEmpty(modelStem)) return null;
        var geo = _nativeShipRenderer.MeshFor(modelStem);
        if (geo is null) return null;                       // native art failed → Meshy fallback
        var texStem = _nativeShipRenderer.TextureStemFor(modelStem, hull.TextureName);
        float scale = NativeShipResolver.NativeScaleForLength(hull.Length, geo.MeshRadius);
        return new NativeShipRef(modelStem, texStem, scale);
    }

    /// <summary>
    /// P565 — the effective native/meshy decision for ONE ship, resolving the committed
    /// per-hull art override (<see cref="_shipArtOverride"/>) over the global <c>--ships</c>
    /// flag. Resolution order (flag &gt; override &gt; native default): a GlobalForce flag wins;
    /// else a per-hull override (by model name or hull id) wins; else the global <c>_shipsNative</c>
    /// default. A Meshy override whose asset is missing falls back to native (logged once) —
    /// never a broken render.
    /// </summary>
    private bool ShipsNativeFor(short id)
    {
        // P601 — when the optional Meshy pack is absent, every hull renders
        // native; explicitly requested Meshy options log once then fall back
        // (never a broken or invisible ship).
        if (!_meshyPackAvailable)
        {
            var model = _hullById is { } known && known.TryGetValue(id, out var entry)
                ? entry.ModelName?.Trim() : null;
            if (!_shipsNative || _shipArtOverride?.Resolve(model, id) == ShipArtOverride.ArtChoice.Meshy)
                LogMeshyUnavailableOnce();
            return true;
        }
        if (_shipArtOverride is null || _hullById is not { } hulls || !hulls.TryGetValue(id, out var hull))
            return _shipsNative;
        var modelName = hull.ModelName?.Trim();
        // P581b — a "catalogue:<id>" override: use the meshy path ONLY when the catalogue
        // model actually loads; if it's missing it falls back to native (logged once),
        // exactly like a missing Meshy asset.
        if (_shipArtOverride.TryGetCatalogue(modelName, id) is { } catId)
        {
            var key = _shipCatalogue.ResolveKey(catId, _shipModelKeys);
            if (key is not null) return false;                       // draw the catalogue model
            if (modelName is not null && _missingMeshyLogged.Add(modelName))
                Console.WriteLine($"[client] ship-art override: catalogue model '{catId}' missing — falling back to native");
            return true;
        }
        var choice = _shipArtOverride.Resolve(modelName, id);
        if (choice == Rendering.ShipArtOverride.ArtChoice.Native) return true;
        if (choice == Rendering.ShipArtOverride.ArtChoice.Meshy)
        {
            if (MeshyAssetLoaded(hull)) return false;
            if (modelName is not null && _missingMeshyLogged.Add(modelName))
                Console.WriteLine($"[client] ship-art override: Meshy asset for '{modelName}' missing — falling back to native");
            return true;
        }
        return _shipsNative;
    }

    /// <summary>P565 — true when a real loaded Meshy model exists for this hull under at
    /// least one civilization's art vocabulary (so a forced-Meshy override renders a real
    /// model, not the placeholder).</summary>
    private bool MeshyAssetLoaded(DataHullTypeIGC hull)
    {
        foreach (var civ in Allegiance.Core.Content.HullModelResolver.CivKeyById.Values.Distinct())
        {
            var asset = _hullResolver.ResolveAsset(civ, hull);
            if (asset is not null && _shipModels.ContainsKey(asset)) return true;
        }
        return false;
    }

    /// <summary>P566 — the world-space aft-engine-nozzle anchor for a native ship,
    /// from its real <c>.mdl</c> <c>FrameData</c> (rocket/thrust/smoke frame,
    /// <see cref="NativeShipResolver.AftNozzleLocalOffset"/>) transformed through
    /// the native RY(π) flip + uniform scale + the ship's orientation. Returns null
    /// when the model has no such frame (or no native art) → the caller uses the
    /// pre-P566 guessed aft-centre anchor.</summary>
    private Allegiance.Core.Collision.Vector3? NativeAftWorldOffset(NativeShipRef nref, Allegiance.Core.Sim.Orientation orient)
    {
        var frames = _nativeShipRenderer.FramesFor(nref.ModelStem!);
        if (frames is null) return null;
        var local = NativeShipResolver.AftNozzleLocalOffset(frames, 0f);
        if (local == Vector3.Zero) return null;                 // no thrust frame authored
        // Native RY(π) flip (thinggeo.cpp:1501-1503) negates X and Z; then uniform scale.
        var flipped = new Vector3(-local.X, local.Y, -local.Z) * nref.NativeScale;
        var x = Vector3.Transform(flipped, PoseConverter.ToXnaRotation(orient));
        return new Allegiance.Core.Collision.Vector3(x.X, x.Y, x.Z);
    }

    /// <summary>Resolve a ship's side id to a manifest civ key via the real
    /// side roster, or null when unknown (no roster, or a civ with no art).</summary>
    private static string? CivKeyForShipSide(Allegiance.ClientCore.State.ClientWorld world, short sideId)
    {
        if (world.SideRoster is not { } roster) return null;
        foreach (var s in roster.Sides)
            if (s.SideId == sideId)
                return HullModelResolver.CivKeyByIdShort(s.CivId);
        return null;
    }

    /// <summary>
    /// P519/P526 — render the selected hull's REAL in-game model spinning behind
    /// the docked HULLS/PARTS panes, using native loadout-screen motion
    /// (WinTrek/load.cpp:877-887): a level spin about the ship's UP (Y) at
    /// 1 rad/s with a fixed π/8 tilt about X, applied after the spin — the port
    /// replaces the in-flight <c>cmExternalOrbit</c> tumble (WinTrek.cpp:7186-7191)
    /// with <see cref="Rendering.DockedHullOrbit"/>'s single spin angle. The model
    /// is resolved through <see cref="ResolveShipModel"/> exactly as in flight (via
    /// <see cref="HullModelResolver"/>, including any per-model orientation fix),
    /// so the preview is the same asset the player flies. A hull with no model
    /// asset uses the in-flight fallback (same as when flying it). The model is
    /// rotated in place by the accumulated spin angle under a fixed camera, the
    /// visual equivalent of native's loadout geo spinning the static ship. Called
    /// once per docked frame, before the translucent panes are overlaid so the UI
    /// stays readable over the moving preview. No space scene — one model + one
    /// light.
    /// </summary>
    private void DrawDockedHullPreview(GameTime gameTime, Rendering.ShopModel shop)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _hullPreviewSpin = Rendering.DockedHullOrbit.Advance(_hullPreviewSpin, dt);

        short side = _session.World.MySideId ?? -1;
        short hullId = shop.EffectiveHullId;

        // Native gives the loadout geo its OWN z-buffered pane with no world
        // scene (load.cpp:860-910, "Give this guy a zbuffer"); the docked world
        // (station) drawn earlier this frame must not show through the preview.
        GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
        // The HUD SpriteBatch passes drawn before this leave DepthStencilState
        // None (the exact SpriteBatch state leak P339 fixed for the flight
        // pass, above): with ships CullNone the back faces overdraw the front
        // faces and the mesh reads full of holes. Re-enable opaque depth here.
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;

        // P566 — NATIVE loadout preview (default): show the same original .mdl the
        // player flies, framed by native's loadout eye = 2.4 × the model's rendered
        // radius (0.5·Length). Falls back to the Meshy preview below when the native
        // art can't be resolved.
        // P565 — honour the per-hull art override in the hangar preview too (ShipsNativeFor).
        if (ShipsNativeFor(hullId))
        {
            var nref = ResolveNativeShip(hullId);
            if (nref is not null && _nativeShipRenderer.MeshFor(nref.ModelStem!) is { } ngeo)
            {
                var ntex = nref.TextureStem is not null ? _nativeShipRenderer.TextureFor(nref.TextureStem) : null;
                float nr = nref.NativeScale * ngeo.MeshRadius;   // native rendered radius = 0.5·Length
                if (nr > 0f)
                {
                    var nworld = Rendering.DockedHullOrbit.World(_hullPreviewSpin);
                    var neye = new Vector3(0f, 0f, Rendering.DockedHullPreviewFraming.EyeDistance(nr));
                    var nview = Matrix.CreateLookAt(neye, Vector3.Zero, Vector3.Up);
                    var nproj = Matrix.CreatePerspectiveFieldOfView(
                        Rendering.DockedHullPreviewFraming.FovRadians,
                        GraphicsDevice.Viewport.AspectRatio,
                        Rendering.DockedHullPreviewFraming.NativeZClipNear,
                        Rendering.DockedHullPreviewFraming.NativeZClipFar);
                    _nativeShipRenderer.Draw(ngeo, ntex, nworld, nview, nproj, 1f, nref.NativeScale, null, nref.ModelStem, nref.TextureStem);
                    return;
                }
            }
        }

        var model = ResolveShipModel(side, hullId) ?? _fallbackModel ?? _shipStandInModel;
        var tex = ResolveShipTexture(side, hullId) ?? _fallbackTexture;

        // P525 — frame the hull the way native's loadout geo pane does
        // (load.cpp:860-934): normalise the model to its own radius (not the
        // hull length — 1/length put the camera INSIDE small hulls) and sit the
        // eye at 2.4 x that normalised radius with a 50 degree FOV. See
        // Rendering/DockedHullPreviewFraming.cs for the pure math + citations.
        float radius = Rendering.ShipRenderScale.RenderRadius(model);
        float scale = Rendering.DockedHullPreviewFraming.NormaliseScale(radius);

        var world = Rendering.DockedHullOrbit.World(_hullPreviewSpin);
        var eye = new Vector3(0f, 0f, Rendering.DockedHullPreviewFraming.EyeDistance(1f));
        var view = Matrix.CreateLookAt(eye, Vector3.Zero, Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(
            Rendering.DockedHullPreviewFraming.FovRadians,
            GraphicsDevice.Viewport.AspectRatio, 0.1f, 40f);

        _shipRenderer.Draw(model, world, view, proj, 1f, scale, tex);
    }

    /// <summary>P544 — the cached per-type native visual (model / directional /
    /// billboard / box) resolved from the loaded static core; an unknown type
    /// or missing core resolves to the box.</summary>
    private ProbeRenderer? ProbeRendererFor(short typeId)
    {
        if (_probeTypes.TryGetValue(typeId, out var cached)) return cached;
        ProbeRenderer? renderer = null;
        if (_shopCatalog?.ProbeTypes?.TryGetValue(typeId, out var type) == true)
        {
            Texture2D? texture = null;
            try
            {
                var mdl = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, type.ModelName + ".mdl"));
                string? stem = ProbeModelSource.ResolveTextureStemFor(mdl, type.TextureName, type.ModelName,
                    name => _assets.Exists(name + ".mdl"));
                if (stem is not null)
                {
                    var decoded = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, stem + ".mdl"));
                    texture = new Texture2D(GraphicsDevice, decoded.Width, decoded.Height);
                    texture.SetData(MdlTextureDecoder.ToPremultipliedRgba(decoded));
                }
                renderer = new ProbeRenderer(GraphicsDevice, mdl.Meshes, texture, _hdMaterials, _assets, type.ModelName, stem);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
            {
                texture?.Dispose();
                Console.WriteLine($"[hd] probe type {typeId} unavailable ({e.Message}); using canonical probe");
            }
        }
        _probeTypes[typeId] = renderer;
        return renderer;
    }

    private ProjectileVisual ResolveProjectileVisual(short typeId)
    {
        if (_projectileVisuals.TryGetValue(typeId, out var v)) return v;
        var visual = _projectileTypes is { } types && types.TryGetValue(typeId, out var p)
            ? ProjectileVisual.Resolve(p)
            : ProjectileVisual.Box();
        _projectileVisuals[typeId] = visual;
        return visual;
    }

    /// <summary>P544 — the cross-section size fed to the near-camera cull: the
    /// largest lateral extent of the round's rendered geometry (billboard/model
    /// = its sprite radius; directional/decal = its width) so a huge round near
    /// the eye is culled conservatively.</summary>
    private static float ProjectileCullWidth(ProjectileVisual v) => v.Kind switch
    {
        ProjectileVisualKind.Billboard => Math.Max(v.EffectiveWidth, 2f * v.Radius),
        ProjectileVisualKind.Model => Math.Max(v.EffectiveWidth, 2f * v.Radius),
        ProjectileVisualKind.Directional => v.EffectiveWidth,
        _ => v.EffectiveWidth,
    };

    /// <summary>P288 — the imported content missile model for a fired missile's
    /// real type, else null (the caller then uses the native-.mdl demo
    /// renderer). Uses the static core's missile table the same way the server
    /// maps type→ModelName.</summary>
    private XnaModel? PickMissileModel(short typeId)
    {
        if (_shopCatalog is null) return null;
        var asset = Rendering.MissileModelSource.ResolveImportedAsset(typeId, _shopCatalog.MissileTypes);
        if (asset is null) return null;
        return _missileModels.TryGetValue(asset, out var m) ? m : null;
    }

    /// <summary>Draw an imported content missile model (same FBX/cull-none
    /// conventions as the ship models, with the ship nose-alignment so the
    /// nose points along the world transform's forward). P336 — folds in the
    /// per-model scale (<see cref="_missileScaleByModel"/>) so the content
    /// model renders at its real native-.mdl size, exactly as
    /// <see cref="ShipRenderer.Draw"/>'s <c>worldScale</c> does for ships.
    /// P337 — binds the real Meshy baseColor (<paramref name="texture"/>, may
    /// be null for an untextured fallback). P530 — <paramref name="lighting"/>
    /// is the current sector's native bidirectional lighting; when supplied the
    /// shared <see cref="SectorLightingEffect"/> replaces the stock rig (key +
    /// blue back-light + 0.25 ambient, specular zero, third light off), which
    /// also drops the P337 ambient lift (native has neither headlight nor lift).
    /// When null the legacy P337 stock rig is kept (back-compat / non-scene
    /// callers).</summary>
    private void DrawMissileContentModel(XnaModel model, Matrix world, Matrix view, Matrix projection,
        Texture2D? texture, SectorLighting? lighting = null)
    {
        float scale = _missileScaleByModel.TryGetValue(model, out var s) && s > 0f ? s : 1f;
        var localScale = Matrix.CreateScale(scale);
        var gd = GraphicsDevice;
        // P530 — explicit render state for the content-missile pass (the P529
        // discipline): pin blend/depth/rasterizer/sampler so it never inherits
        // a SpriteBatch HUD leftover. CullNone because the FBX winding is
        // back-facing to MonoGame's default CCW-front (like the ship models).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullNone;
        gd.SamplerStates[0] = SamplerState.LinearClamp;
        foreach (var mesh in model.Meshes)
        {
            foreach (var effect in mesh.Effects)
            {
                if (effect is not BasicEffect basic) continue;
                basic.World = mesh.ParentBone.Transform * Rendering.ShipRenderer.SharedNoseAlignment * localScale * world;
                basic.View = view;
                basic.Projection = projection;
                if (lighting is { } lp)
                {
                    SectorLightingEffect.Apply(basic, lp);   // P530 — native sector lighting
                    // P530 readability floor for the textured-Meshy content
                    // missiles, same as the ship path (see ShipAmbientFloor).
                    basic.AmbientLightColor = Vector3.Max(basic.AmbientLightColor, Rendering.ShipRenderer.ShipAmbientFloor);
                }
                else
                {
                    basic.EnableDefaultLighting();                               // P337 legacy
                    basic.AmbientLightColor = Rendering.ShipRenderer.ShipAmbient;
                }
                basic.Texture = texture;                                         // P337
                basic.TextureEnabled = texture is not null;
            }
            mesh.Draw();
        }
    }
}

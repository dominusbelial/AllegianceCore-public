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
            if (world.MyShipId is not { } me) return false;
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
            _capFlyWallMs = Environment.TickCount64;
        }
        bool capThis = captureArmed && Environment.TickCount64 - _capFlyWallMs >= CapSettleGraceMs;
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

        _lockBracket = null;   // P230 — recomputed only when a lock target projects into view this frame
        _leadIndicator = null; // P266 — recomputed only when a lead point projects into view this frame
        _targetSelectionBracket = null; // P345 — recomputed only when a T-selected target projects into view this frame
        _targetBearingScreenDir = null; // P359 — recomputed whenever a T-selected target is resolvable this frame

        if (_state == ClientState.Lobby)
        {
            DrawLobby();
        }
        else if (_state == ClientState.Connecting)
        {
            DrawConnecting();
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
            var world = _session.World;
            if (world.MyShipId is { } me && _poses.TryGetValue(me, out var myPose))
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
                _sectorLighting = world.Ships.TryGetValue(me, out var ownLite)
                    && world.Clusters.TryGetValue(ownLite.SectorId, out var dcLite)
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
                if (_captureSpec is { } cap && !cap.Chase)
                {
                    ComputeCaptureCamera(aspect, out view, out proj, out cameraEye);
                }
                else
                {
                    (view, proj, cameraEye) = ComputeFlightCamera(
                        myPos, myRot, myPose.Orientation, aspect, chaseDt);
                }
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
                bool bloom = _bloomEnabled && _post is { BloomReady: true };
                bool soft = _softEnabled && _post is { SoftReady: true };

                // P559 — gather the opaque scene geometry ONCE (ships, asteroids,
                // stations — the same per-object world transforms, spins advanced
                // exactly once) so the linear-depth pass and the color pass below
                // share identical geometry. Depth-gather lists are reset here so
                // each frame re-collects exactly its own geometry.
                PrepareSceneGeometry(me, cameraEye, dtSec);

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
                    GraphicsDevice.SetRenderTarget(bloom ? _post.SceneRT : (capThis ? _capTarget : (RenderTarget2D?)null));
                    GraphicsDevice.BlendState = BlendState.Opaque;
                    GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                }
                else
                {
                    _softContext = null;
                }

                if (bloom)
                {
                    GraphicsDevice.SetRenderTarget(_post!.SceneRT);
                    GraphicsDevice.Clear(Color.Black);
                }

                _starFar.Draw(view, proj, cameraEye);
                _starMid.Draw(view, proj, cameraEye);
                _starNear.Draw(view, proj, cameraEye);

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
                    (float)_nowSeconds);
                float debrisFocus = proj.M22;
                _drawDustViews.Clear();
                foreach (var pt in _debrisGeo.Points)
                {
                    float age = (float)_nowSeconds - pt.StartTime;
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

                foreach (var (id, pose) in _poses)
                {
                    // In cockpit mode the local hull is not drawn — you are inside
                    // it; every OTHER known ship still renders normally.
                    if (_cameraMode == CameraMode.Cockpit && id == me)
                        continue;

                    var xnaPos = PoseConverter.ToXna(pose.Position);
                    var worldMat = PoseConverter.ToXnaWorldRelative(xnaPos, pose.Orientation, cameraEye);
                    // P270 — alpha-blend cloaked ships (subtle shimmer over
                    // elapsed wall-clock); fully solid otherwise. The server
                    // already withheld undetectable enemies, so anything here is
                    // legitimately visible to this client's side.
                    float shipOpacity = _cloakedShips.Contains(id)
                        ? Flight.CloakVisual.Opacity(gameTime.TotalGameTime.TotalSeconds)
                        : 1f;
                    var shipModel = _shipModelById.TryGetValue(id, out var m) ? m : null;
                    // P566 — NATIVE ship path (default): draw the original .mdl mesh +
                    // native texture at native scale/orientation under pure native sector
                    // lighting. When the hull's native art failed to decode (ref null /
                    // geometry null) it falls through to the Meshy content path below.
                    if (ShipsNativeFor(id)
                        && _nativeShipRefById.TryGetValue(id, out var nref) && nref is not null
                        && _nativeShipRenderer.MeshFor(nref.ModelStem!) is { } ngeo)
                    {
                        var ntex = nref.TextureStem is not null ? _nativeShipRenderer.TextureFor(nref.TextureStem) : null;
                        _nativeShipRenderer.Draw(ngeo, ntex, worldMat, view, proj, shipOpacity, nref.NativeScale, _sectorLighting);
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
                        _shipRenderer.Draw(shipModel ?? _fallbackModel, worldMat, view, proj, shipOpacity, scale, shipTex, _sectorLighting);
                    }
                }

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
                        _nativeAsteroidRenderer.Draw(nativeGeo, nativeTex, rWorldNoScale, view, proj, nativeScale, _sectorLighting);
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
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var ownW))
                    {
                        short myWarpSector = ownW.SectorId;
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
                foreach (var (wId, wPos, wOri, wRadius, wTex) in _drawWarps)
                {
                    var wXnaPos = PoseConverter.ToXna(wPos);
                    var wRot = PoseConverter.ToXnaRotation(wOri);
                    // P562 — the whole-mesh rigid spin is gone; the vortex now
                    // flows from the native animated UVs inside WarpRenderer.
                    var wWorld = wRot * Matrix.CreateScale(wRadius * Rendering.WarpRenderer.VisualScaleMultiplier)
                                 * Matrix.CreateTranslation(wXnaPos - cameraEye);
                    _warpRenderer.Draw(wWorld, view, proj, dt,
                        wRadius * Rendering.WarpRenderer.VisualScaleMultiplier, wTex, wId);
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
                        _nativeStationRenderer.Draw(ngeo, ntex, sWorldNS, view, proj, nscale, _sectorLighting);
                    }
                    else
                    {
                        var sWorld = sRot * Matrix.CreateScale(sRadius) * Matrix.CreateTranslation(sXnaPos - cameraEye);
                        var sTint = (world.MySideId ?? -1) == sSide
                            ? Rendering.StationRenderer.FriendlyColor
                            : Rendering.StationRenderer.EnemyColor;
                        _stationRenderer.Draw(sWorld, view, proj, sTint, _sectorLighting);
                    }

                    // P367 — this visible station's navigation beacon strobes too
                    // (native LightsGeo is confirmed present in 422 shipped .mdl
                    // files including stations — this is a common ship+station
                    // effect, not ship-only). Anchored at the structure's real
                    // radius and blinked by the same real pulse envelope.
                    if (sRadius > 0f)
                        AddNavStrobeAnchors(Rendering.NavStrobeLayout.StationAnchors(sPos, sOri, sRadius), sRadius);
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
                    long buildNow = Environment.TickCount64;
                    world.PruneExpiredBuildingEffects(buildNow);
                    if (world.Ships.TryGetValue(me, out var ownBe))
                    {
                        short myBeSector = ownBe.SectorId;
                        foreach (var (_, be) in world.BuildingEffects)
                        {
                            if (be.ClusterId != myBeSector) continue;
                            float p = be.ProgressFraction(buildNow);
                            float radius = be.RadiusStation * (0.2f + 0.8f * p);
                            float alpha = Rendering.BuildingEffectRenderer.MinAlpha
                                          + (Rendering.BuildingEffectRenderer.MaxAlpha
                                             - Rendering.BuildingEffectRenderer.MinAlpha) * p;
                            _drawBuilds.Add((PoseConverter.ToXna(be.PositionStop), radius, alpha));
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
                    long projNow = Environment.TickCount64;
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
                                _projectileModelRenderer.Draw(geo, tex, bXna - cameraEye, bXnaDir, bSpin, view, proj);
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
                    long mNow = Environment.TickCount64;
                    world.PruneExpiredMissiles(mNow);
                    if (world.Ships.TryGetValue(me, out var ownMs))
                    {
                        short myMissileSector = ownMs.SectorId;
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
                            _drawMissiles.Add((ms.Position, mo, PickMissileModel(ms.TypeId)));
                            _drawTrailMissiles.Add(new TrailMissile(
                                ms.MissileId, ms.Position, ms.Forward, _trailNowSec));
                        }
                    }
                }
                foreach (var (mPos, mOri, mModel) in _drawMissiles)
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
                        _missileRenderer.Draw(mWorld, view, proj, _sectorLighting);
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
                    if (world.Ships.TryGetValue(me, out var ownPb))
                    {
                        short myProbeSector = ownPb.SectorId;
                        foreach (var (_, pe) in world.Probes)
                        {
                            if (pe.ClusterId != myProbeSector) continue;
                            _drawProbes.Add((PoseConverter.ToXna(pe.Position), pe.SideId));
                        }
                    }
                }
                foreach (var (pbPos, pbSide) in _drawProbes)
                {
                    var pbWorld = Matrix.CreateTranslation(pbPos - cameraEye);
                    var pbTint = (world.MySideId ?? -1) == pbSide
                        ? Rendering.StationRenderer.FriendlyColor
                        : Rendering.StationRenderer.EnemyColor;
                    _probeRenderer.Draw(pbWorld, view, proj, pbTint, _sectorLighting);
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
                    if (world.Ships.TryGetValue(me, out var ownMine))
                    {
                        short myMineSector = ownMine.SectorId;
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
                        long chaffNow = Environment.TickCount64;
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
                // mySector. A treasure is static (its P0 is final), so each is
                // drawn at that real position as the shared procedural gold-gem
                // placeholder, scaled by the native pickup radius 5.0
                // (TreasureRenderer.DefaultRadius — the only size a treasure
                // carries; there is no ModelName to load).
                _drawTreasures.Clear();
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var ownTr))
                    {
                        short myTreasureSector = ownTr.SectorId;
                        foreach (var (_, te) in world.Treasures)
                        {
                            if (te.ClusterId != myTreasureSector) continue;
                            _drawTreasures.Add(PoseConverter.ToXna(te.Position));
                        }
                    }
                }
                foreach (var tp in _drawTreasures)
                {
                    var tworld = Matrix.CreateScale(Rendering.TreasureRenderer.DefaultRadius)
                                 * Matrix.CreateTranslation(tp - cameraEye);
                    _treasureRenderer.Draw(tworld, view, proj, dt);
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
                        (float)dt, _trailRandom);
                }
                _warpParticles.Update((float)dt);
                _drawSparkViews.Clear();
                _drawSparkViews.AddRange(_warpParticles.Enumerate());
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
                _damageDebris.Update((float)dt);
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
                    var et = kv.Value;
                    _engineTrail.Emit(new Rendering.EngineTrailShip(
                        kv.Key, et.Position, et.Velocity, et.Orientation, et.Radius,
                        _shipThrustPower.GetValueOrDefault(kv.Key)), (float)dt, _trailRandom);
                }
                _engineTrail.Update((float)dt);
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
                // and every visible station's beacons, each sized by the REAL
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
                _motionTrail.Advance((float)dt);
                _drawMotionTrailStrips.Clear();
                _drawMotionTrailStrips.AddRange(_motionTrail.Enumerate());
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
                    nowLocalMs = Environment.TickCount64;
                    world.PruneExpiredExplosions(nowLocalMs);
                    if (world.MyShipId is { } meX && world.Ships.TryGetValue(meX, out var ownX))
                    {
                        ownSectorId = ownX.SectorId;
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
                            (float)_nowSeconds, _softContext ?? Rendering.SoftParticleContext.Off);
                    else if (_effectsQuality == EffectsQuality.Volumetric
                        && _volumetricTech == VolumetricTechnique.DisplacedSphereB && _displacedRenderer is { } disp)
                        disp.Draw(_drawExplosions, ownSectorId, cameraEye, view, proj,
                            (float)_nowSeconds, _softContext ?? Rendering.SoftParticleContext.Off);
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
                _hullHitParticles.Update((float)dt);
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
                _shieldFlareParticles.Update((float)dt);
                _drawShieldFlares.Clear();
                _drawShieldFlares.AddRange(_shieldFlareParticles.Enumerate());
                _shieldFlareRenderer.Draw(_drawShieldFlares, cameraEye, view, proj);

                // P547 — bloom composite: bright-pass → blur → additive over the
                // FINAL target (backbuffer or the capture target), then the
                // radar + HUD + SpriteBatch overlays below draw on top of it so
                // they never bloom. When bloom is off nothing here runs.
                if (bloom && _post is { BloomReady: true })
                {
                    GraphicsDevice.SetRenderTarget(capThis ? _capTarget : (RenderTarget2D?)null);
                    _post.Bloom!.Render(_post.SceneRT!);
                }

                // P250 — local current-sector tactical radar. Drawn here (inside
                // the camera block, so view/proj/cameraEye are live) over the 3D
                // scene; the P211 HUD gauges render on top of it afterwards. It
                // only ever appears when the player has a ship + camera, which is
                // exactly when a local tactical picture is wanted.
                // P580 — suppressed while the director is recording (clean b-roll).
                if (!_directorActive)
                    DrawRadar(view, proj, cameraEye, gameTime.TotalGameTime.TotalSeconds);
            }

            // 2D HUD overlay on top of the 3D scene (P211).
            DrawHud();

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
        if (!_directorActive)
            DrawChat();

            // P217 — the quick-chat voice-macro menu, on top of the chat log
            // (itself above the HUD). Only drawn while a menu level is open.
            if (_qcMenuOpen && _qcMenuNode is { } qn && _qcMenuPath.Count > 0)
                _quickChatRenderer.Draw(qn, _qcMenuPath, GraphicsDevice.Viewport);
        }

        // P265 — B scoreboard overlay: the real in-mission roster (name / side /
        // kills / deaths) from ClientWorld.ScoreRoster. Drawn as a compact panel
        // in the flying state (it is mission data); independent flag from the
        // starmap/help flags, so closing it restores the prior view.
        if (_state == ClientState.Flying && _scoreboardVisible)
        {
            var rows = Rendering.ScoreboardLayout.Build(
                _session.World.ScoreRoster, _session.World.IsSideActive);
            string? self = _session.World.Ships.TryGetValue(
                _session.World.MyShipId ?? -1, out var se) ? se.CharacterName : null;
            _scoreboardRenderer.Draw(GraphicsDevice.Viewport, rows, self);
        }

        // P275 — P Pilot Info overlay: the local player's OWN live stats (kills,
        // deaths, combat rating, rank, score) extracted from the same periodic
        // ScoreRosterUpdate(209) snapshot as the scoreboard. Independent flag from
        // the scoreboard, so both can be open at once; drawn only while flying.
        if (_state == ClientState.Flying && _pilotInfoVisible)
        {
            var info = Rendering.PilotInfoLayout.Build(_session.World.ScoreRoster,
                _session.World.Ships.TryGetValue(_session.World.MyShipId ?? -1, out var pe) ? pe.CharacterName : null);
            _pilotInfoRenderer.Draw(GraphicsDevice.Viewport, info);
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
        if (_state == ClientState.Flying && IsBriefingVisible(out int? brRemaining))
        {
            string briefText = _session.World.BriefingText;
            _briefingRenderer.Draw(GraphicsDevice.Viewport, briefText, briefText, brRemaining);
        }

        // P257 — H keybinding-reference overlay, drawn TOP-MOST over every
        // other overlay (HUD, starmap, chat) so it is fully legible as the
        // reference panel the player consults briefly; it covers in both the
        // lobby and flying states. Because it is an independent flag, closing
        // it reveals the previous overlays exactly as they were.
        if (_helpVisible)
            _helpOverlayRenderer.Draw(GraphicsDevice.Viewport);

        // P268/P273 — the docked/hangar screen, drawn top-most as the docked
        // replacement view (a fullscreen dark panel covers the stationary ship's
        // cockpit scene). Only present while the local ship is actually docked.
        // Its P273 sub-view is the teleport pane (board a teammate's turret /
        // teleport to a station); otherwise the P268 hangar loadout shows.
        if (_dockedActive && _investActive && _investDefs is not null)
        {
            // P487 — the investment pane sub-view (native PurchasesPaneImpl),
            // drawn in the same docked-panel style as the teleport/hangar views.
            // P495 — reuses the model RefreshInvestState built this frame (so the
            // radar build-spot highlight and the list agree), with a fallback
            // build in case DrawRadar's camera block didn't run.
            var invModel = _investModel ?? BuildInvestmentModel();
            var invRows = Rendering.InvestmentLayout.Build(invModel);
            _dockedScreenRenderer.Draw(GraphicsDevice.Viewport, invRows, money: _session.World.MyMoney,
                backdropAlpha: Rendering.DockedScreenRenderer.InvestBackdropAlpha);
        }
        else if (_dockedActive && _teleportActive && _teleportModel is { } telPane)
        {
            var telRows = Rendering.TeleportLayout.Build(telPane);
            _dockedScreenRenderer.Draw(GraphicsDevice.Viewport, telRows, money: _session.World.MyMoney);
        }
        else if (_dockedActive && _shopModel is { } dockShop)
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
                backdropAlpha: 0.62f);
        }

        // P262 — game-over result screen, drawn above EVERYTHING (even the help
        // keymap) so it is unmissable: the "why did everything stop" answer.
        // Shows only once the real GAME_OVER(62) wire message has arrived
        // (world.GameOverResult), resolving its real winner/reason via
        // GameOverPanelInfo. The local player's own side (MySideId) decides the
        // victorious/defeated/draw title; all text is real wire data.
        if (_state == ClientState.Flying
            && _session.World.GameOverResult is { } go)
        {
            var info = Rendering.GameOverPanelInfo.From(go, _session.World.MySideId);
            _gameOverScreenRenderer.Draw(GraphicsDevice.Viewport, info, _session.World.GameOverPlayersList);
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

        base.Draw(gameTime);
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
            if (_cameraMode == CameraMode.Cockpit && id == me) continue;
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
            if (world.Ships.TryGetValue(me, out var own))
            {
                short mySector = own.SectorId;
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
            if (world.Ships.TryGetValue(me, out var ownSt))
            {
                short myStSector = ownSt.SectorId;
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
            short cur = ent.SectorId;
            if (!_lastShipSector.TryGetValue(shipId, out short prev))
            {
                _lastShipSector[shipId] = cur;   // first sight: baseline, no swell
                continue;
            }
            if (cur == prev) continue;
            _lastShipSector[shipId] = cur;

            // A real transit: swell the arrival aleph (in the new sector) whose
            // partner warp is in the old sector the ship left.
            foreach (var (wId, w) in world.Warps)
            {
                if (w.ClusterId != cur) continue;
                if (world.Warps.TryGetValue(w.DestinationId, out var partner)
                    && partner.ClusterId == prev)
                    _warpRenderer.TriggerTransit(wId);
            }
        }
    }

    /// <summary>
    /// Build a <see cref="HudFrame"/> from the local ship's most recent real
    /// <c>ServerShipUpdate</c> and hand it to the <see cref="HudRenderer"/>. The
    /// frame is lifted from <see cref="ClientWorld.Ships"/>'s <c>Latest</c> —
    /// the same per-tick wire payload already driving rendering — so every HUD
    /// value is real (no placeholders), with zero extra wire work.
    /// </summary>
    private void DrawHud()
    {
        var world = _session.World;
        HudFrame? frame = null;
        lock (_session.Gate)
        {
            // P366 — resolve the leader-only bot dock/launch command for THIS
            // frame (null for a non-leader or when the selected target is not a
            // same-side bot). Cleared every frame here so a stale selection /
            // lost-leadership edge never leaves a phantom prompt.
            _leaderBotCommand = ResolveLeaderBotCommand(world);
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var entity)
                && entity.Latest is { } up)
            {
                // P228 — feed the local ship's hull AND shield fractions into the
                // damage-flash tracker and read whether the HUD should be flashing
                // NOW. P463 added the shield fraction: a shield-absorbed hit (hull
                // unchanged) must flash too (native arms on any flAmount>0,
                // trekigc.cpp:4281). Only this frame path touches the tracker, and
                // only with the LOCAL ship's fractions, so the "damage to another
                // ship never flashes" guarantee holds by construction (native's
                // pShip==GetShip() gate, trekigc.cpp:4281). Monotonic clock =
                // _nowSeconds (float cast is ample precision for 0.25s flash halves).
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
                    AfterburnerActive: (up.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                    // P221 — firing readout: true while StateM carries a fire
                    // bit. Group-fire ORs AllWeapons; single-weapon mode (P225)
                    // ORs OneWeapon instead, so both are detected (the sim sets
                    // exactly one when firing — Weapon.cs:363-375).
                    Firing: (up.StateM & (Allegiance.Core.Sim.ShipControlState.AllWeapons | Allegiance.Core.Sim.ShipControlState.OneWeapon)) != 0,
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
                    // P270 — true while OUR OWN ship is cloaked (the CloakActive
                    // bit on the own-ship update) — drives the CLOAKED HUD tag.
                    Cloaked: (up.StateM & Allegiance.Core.Sim.ShipControlState.CloakActive) != 0,
                    // P298 — the local ship's live Kill Bonus (streamed owner-only
                    // on OwnShipKillBonusUpdate(212); the renderer-only client has
                    // no server-side Ship object to read it from directly).
                    KillBonusPercent: world.KillBonusPercent,
                    KillBonusStreak: world.KillBonusStreak,
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
                        : null);
            }
        }
        if (!_hideHud && !_directorActive)   // P572 --hide-hud; P580 the director always hides the HUD
            _hudRenderer.Draw(frame, _cameraMode, GraphicsDevice.Viewport, _lockBracket, _leadIndicator,
                _captureSpec is not null ? "OVERVIEW" : null, _targetSelectionBracket, _targetBearingScreenDir,
                leaderCommand: _leaderBotCommand, ripcordStatus: RipcordStatusLabel(),
                commandeeLabel: CommandeeLabel(), turretView: IsTurretGunner());

        // P578 — the eject/death feedback overlays drawn straight onto the HUD:
        // the critical/tip lines (native SetCriticalTipText/SetTipText), the
        // eject-pod panel with the endurance gauge, and the death banner when a
        // ghost. Skips the fight HUD only when it's hidden or the director is up.
        if (!_hideHud && !_directorActive)
            DrawEjectDeathOverlays();
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

    /// <summary>P414 — the HUD "RIPCORDING TO &lt;sector&gt;" label while the local
    /// player's own ship has a ripcord countdown active, resolved from the
    /// live world ripcord state under the gate lock (the sector id → the
    /// cluster's real name from the EXPORT OT_cluster table, same source the
    /// starmap pane uses). Null when no ripcord is active — the HUD draws no
    /// tag. ASCII-safe (names are ASCII), so it renders in the spritefont.
    /// Also self-expires the tag once the countdown's real duration (the own
    /// hull's <c>RipcordSpeed</c>, the same value the server counts down) has
    /// elapsed — the server's teleport is silent on the wire, so without this
    /// the tag would linger after the ship has actually arrived.</summary>
    private string? RipcordStatusLabel()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            if (!world.RipcordActive) return null;

            // Self-expire when RipcordSpeed seconds have elapsed since the
            // activation was decoded (abort clears it sooner via RIPCORD_ABORTED).
            if (world.RipcordActiveSinceLocalMs is { } start)
            {
                float dur = RipcordDurationSeconds(world);
                if (dur > 0f && (Environment.TickCount64 - start) / 1000.0 >= dur)
                {
                    world.ClearRipcord();
                    return null;
                }
            }

            if (world.RipcordTargetSectorId is { } sid)
            {
                if (world.Clusters.TryGetValue(sid, out var dc) && dc.Name is { Length: > 0 })
                    return $"RIPCORDING TO {dc.Name}";
                return $"RIPCORDING TO SECTOR {sid}";
            }
            return "RIPCORDING";
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
        if (_shopCatalog is not { } catalog) return 0f;
        if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var ent))
        {
            float ripcordSpeed = 0f;
            foreach (var h in catalog.Hulls)
                if (h.HullId == ent.HullId) { ripcordSpeed = h.RipcordSpeed; break; }
            if (ripcordSpeed > 0f && world.MySideId is { } sideId)
            {
                float ga = world.GlobalAttributesFor(sideId).Get(Allegiance.Core.Model.GlobalAttributeSet.RipcordTime);
                if (ga > 0f) ripcordSpeed /= ga;   // shipIGC.cpp:4665 — c_gaRipcordTime divides GetRipcordSpeed()
            }
            return ripcordSpeed;
        }
        return 0f;
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
        var sites = new List<StarmapLayout.ClusterSite>();
        IReadOnlyList<ClusterInfo.WarpEdge>? edges = null;
        short? current = null;
        SectorInfoPaneLayout.Data? pane = null;
        // P460 — per-cluster ownership (sector id -> owners) and the side-colour
        // lookup for the owner/secondary rings.
        var owners = new Dictionary<short, SectorMapQueries.ClusterOwners>();
        var sideColors = new Dictionary<short, Color>();
        IReadOnlyDictionary<short, int>? knownEnemySectors = null;
        lock (_session.Gate)
        {
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
                if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own))
                    current = own.SectorId;

                // P272 — the info pane for the selected cluster: gather that
                // cluster's asteroids from the client's own decoded EXPORT
                // data (world.Asteroids is keyed by AsteroidId and carries
                // AabmCapabilities + Ore + ClusterId) and build the pure pane
                // content. Real data — nothing fabricated. Only gathered in the
                // EXPANDED view (the compact mini-map draws no info pane).
                if (_minimapExpanded && _selectedSectorId is { } sid)
                {
                    string selName = world.Clusters.TryGetValue(sid, out var sdc) ? sdc.Name : "";
                    var samples = new List<SectorInfoPaneLayout.AsteroidSample>();
                    foreach (var ast in world.Asteroids.Values)
                        if (ast.ClusterId == sid)
                            samples.Add(new SectorInfoPaneLayout.AsteroidSample(ast.AabmCapabilities, ast.Ore));

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
                    pane = SectorInfoPaneLayout.Build(selName, samples, probeCount);
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
                flashOn, vp, panelW, panelH, owners, SideColor, knownEnemySectors);
            if (pane is { } p)
                _sectorInfoPaneRenderer.Draw(p, vp, panelX, panelY, panelW, panelH,
                    world.MySideId is { } mySide && sideColors.TryGetValue(mySide, out var mc) ? mc : (Color?)null);
        }
        else
        {
            // Collapsed (the P360 default): the persistent compact corner
            // mini-map — current sector + warp topology at a glance, always on.
            _starmapRenderer.DrawMini(sites, edges, current,
                id => warningSnapshot.TryGetValue(id, out var w) ? w : (short)0,
                flashOn, vp, owners, SideColor, knownEnemySectors);
        }
    }

    /// <summary>
    /// P250 — gather the nearby objects in the local player's CURRENT sector
    /// (ships, stations, warps) under the Gate lock, project each to screen via
    /// <see cref="RadarProjection"/>, apply the distance fade, and draw them
    /// through <see cref="RadarBracketRenderer"/>. Missiles/mines/asteroids are
    /// named follow-ups, not contacts this stage (the brief's explicit scope
    /// boundary). No new wire/data plumbing: this reuses the exact per-sector
    /// filtering the draw loops already do (<c>ClusterId/SectorId == mySector</c>,
    /// e.g. the P218 asteroid / P227 warp / P234 missile loops). Always-on, like
    /// native's radar (part of the standard HUD, no toggle — the P211 cockpit
    /// gauges occupy the corners/centre while these small icons sit at object
    /// positions throughout the viewport, so they don't structurally collide).
    /// </summary>
    private void DrawRadar(Matrix view, Matrix proj, Vector3 cameraEye, double timeSeconds)
    {
        var world = _session.World;
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width, h = vp.Height;
        const float inset = 14f;

        // P495 — resolve this frame's investment-pane model + the hovered
        // station-type bucket's build mask (native SetInvestAsteroid). Builds
        // the model here (before the pane draw reuses it) so the radar's
        // build-spot highlight and the pane list always agree this frame.
        RefreshInvestState();

        // P436 — the selected-target highlight. Resolve the player's current
        // _autoTarget to its radar-contact type (Asteroid has no radar blip →
        // null) and a frame-independent 0..1 pulse phase for the highlight.
        var sel = _autoTarget;
        var selType = sel?.Kind switch
        {
            TargetKind.Ship => RadarBracketRenderer.RadarContactType.Ship,
            TargetKind.Station => RadarBracketRenderer.RadarContactType.Station,
            TargetKind.Warp => RadarBracketRenderer.RadarContactType.Warp,
            _ => (RadarBracketRenderer.RadarContactType?)null,
        };
        float highlightPulse = (MathF.Sin((float)timeSeconds * 5f) + 1f) * 0.5f;

        _drawRadarContacts.Clear();
        _drawShipTags.Clear();   // P372 — per-ship in-world side tags
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return;
            if (!world.Ships.TryGetValue(me, out var ownEnt)) return;
            short mySector = ownEnt.SectorId;
            // P527 — the radar EYE uses the own-ship RENDER pose (P508 local
            // sim), not the raw 20 Hz ServerShipUpdate pose, so every contact's
            // projection and distance-fade share the frame's own position (the
            // same shared accessor the P523 anchors use). Falls back to the raw
            // update only when the own ship wasn't rendered this frame.
            SimVec ownSimPos;
            if (!ResolveRadarContactAnchor(_poses, world.Ships, me, out ownSimPos))
            {
                if (ownEnt.Latest is not { } ownUp) return;
                ownSimPos = ownUp.Position;
            }
            var ownPos = PoseConverter.ToXna(ownSimPos);

            // P326 — the former local function Add(...) is inlined below (it
            // captured per-frame state — ownPos/view/proj/w/h/inset and the
            // contact buffer — so it could not be a cached delegate; inlining
            // removes the per-frame closure allocation the review flagged).

            // Ships — other, living craft in my sector (skip self + corpses +
            // out-of-view vessels; a departed ship's stale SectorId would
            // otherwise still match mySector and show on the radar frozen).
            foreach (var (id, ship) in world.Ships)
            {
                if (id == me || ship.IsNotVisible) continue;
                if (ship.SectorId != mySector) continue;
                if (ship.Latest is not { }) continue;                     // keep the Latest gate
                // P527 — anchor to the pose the renderer drew THIS frame (P448b
                // dead-reckoned for remotes), NOT the raw 20 Hz ServerShipUpdate
                // pose, else the bracket + in-world side tag TRAIL a moving hull
                // (live playtest 2026-10-04: the HUD icon sat visibly behind a
                // steady miner). Fallback to raw only when not rendered.
                if (!ResolveRadarContactAnchor(_poses, world.Ships, id, out var shipPos)) continue;
                var site = RadarProjection.Project(
                    PoseConverter.ToXna(shipPos), ownPos, view, proj, w, h, inset);
                float op = RadarBracketLayout.Opacity((ownSimPos - shipPos).Length(),
                    RadarBracketLayout.CloseRange, RadarBracketLayout.FarRange);
                // P372 — resolve the contact's REAL side colour from the side
                // roster (native pside->GetColor(), radarimage.cpp:682-685);
                // null keeps the type-based teal fallback only while no roster
                // colour is known yet (TryGetSideColor returns false).
                Color? sideCol = null;
                if (world.TryGetSideColor(ship.SideId, out float sr, out float sg, out float sb))
                    sideCol = new Color(sr, sg, sb);
                // P436 — is this ship the player's own selected target? Pure
                // (RadarTargetSelection) so the flagging is unit-testable.
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Ship, id, selType, sel?.Id);
                _drawRadarContacts.Add(new RadarBracketRenderer.RadarContact(
                    RadarBracketRenderer.RadarContactType.Ship,
                    new NumericsVec2(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    sideCol, isSel));
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
                var site = RadarProjection.Project(
                    PoseConverter.ToXna(sp), ownPos, view, proj, w, h, inset);
                float op = RadarBracketLayout.Opacity((ownSimPos - sp).Length(),
                    RadarBracketLayout.CloseRange, RadarBracketLayout.FarRange);
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Station, sid, selType, sel?.Id);
                _drawRadarContacts.Add(new RadarBracketRenderer.RadarContact(
                    RadarBracketRenderer.RadarContactType.Station,
                    new NumericsVec2(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    null, isSel));
            }

            // Warps in my sector.
            foreach (var (wid, wp) in world.Warps)
            {
                if (wp.ClusterId != mySector) continue;
                var wPos = Vec(wp.Position);
                var site = RadarProjection.Project(
                    PoseConverter.ToXna(wPos), ownPos, view, proj, w, h, inset);
                float op = RadarBracketLayout.Opacity((ownSimPos - wPos).Length(),
                    RadarBracketLayout.CloseRange, RadarBracketLayout.FarRange);
                bool isSel = RadarTargetSelection.IsSelected(
                    RadarBracketRenderer.RadarContactType.Warp, wid, selType, sel?.Id);
                _drawRadarContacts.Add(new RadarBracketRenderer.RadarContact(
                    RadarBracketRenderer.RadarContactType.Warp,
                    new NumericsVec2(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op,
                    null, isSel));
            }

            // P495 — asteroid build spots for the hovered station-type investment
            // (native SetInvestAsteroid, loadout.cpp:1396-1409). Only the rocks in
            // my sector whose capability matches the station type's AabmBuild are
            // added, as asteroid contacts carrying the build-spot highlight; a
            // non-matching rock gets no blip (asteroids otherwise have no radar
            // blip, P436), so the hint shows exactly where the selected station
            // type may be built.
            if (_investAsteroidAabm is { } investAabm)
            {
                foreach (var (_, ast) in world.Asteroids)
                {
                    if (ast.ClusterId != mySector) continue;
                    if (!RadarInvestAsteroidSelection.IsBuildSpot(investAabm, ast.AabmCapabilities)) continue;
                    var aPos = PoseConverter.ToXna(Vec(ast.Position));
                    var aSite = RadarProjection.Project(aPos, ownPos, view, proj, w, h, inset);
                    float aOp = RadarBracketLayout.Opacity((ownSimPos - Vec(ast.Position)).Length(),
                        RadarBracketLayout.CloseRange, RadarBracketLayout.FarRange);
                    _drawRadarContacts.Add(new RadarBracketRenderer.RadarContact(
                        RadarBracketRenderer.RadarContactType.Asteroid,
                        new NumericsVec2(aSite.Point.X, aSite.Point.Y),
                        aSite.Placement == RadarBracketLayout.RadarPlacement.OnScreen, aOp,
                        null, false, IsInvestBuildSpot: true));
                }
            }
        }

        if (_drawRadarContacts.Count > 0)
        {
            _radarRenderer.Draw(_drawRadarContacts, vp, highlightPulse);
            // P372 — the per-ship in-world side-colour team tags, overlaid on the
            // 3D scene after the brackets (own SpriteBatch pass).
            _shipTagRenderer.Draw(_drawShipTags);
        }
        // P436 — the on-screen "no <candidate> in sector" feedback for the U/Z
        // filtered-target keys (drawn even when there are no radar contacts).
        DrawTargetNoMatchFeedback();
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
    private void DrawEjectDeathOverlays()
    {
        var vp = GraphicsDevice.Viewport;

        // Own-ship pod/ghost state under the gate (same readings DrawHud's
        // frame uses: the lifepod hull + NA-station gate, and the true-death
        // flag for the ghost banner).
        bool inPod = false, ghost = false;
        int oxySec = 0;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own) && own.Latest is { } up)
            {
                inPod = Rendering.LifepodHud.IsAdriftLifepod(_shopCatalog, own.HullId, own.StationId);
                oxySec = inPod ? Rendering.LifepodHud.OxygenSeconds(up.FuelFraction, _shopCatalog) : 0;
                // P578 — "ghost" = a true death (KILL_SHIP with bDeathCredit for
                // me) while not docked — native's IsGhost predicate for the
                // death banner. The frozen-dead own ship never docks, so the
                // StationId < 0 guard is belt-and-suspenders.
                ghost = own.IsDead && own.StationId < 0;
            }
        }

        if (_tipLines.Count == 0 && !inPod && !ghost) return;

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            // ── 1. Tip lines ────────────────────────────────────────────────
            int critIdx = 0, tipIdx = 0;
            foreach (var (text, critical, expiresAt) in _tipLines)
            {
                double remaining = expiresAt - _nowSeconds;
                float fade = (float)Math.Clamp(remaining / 0.8, 0.0, 1.0);   // dissolve over the last 0.8 s
                var sz = _hudSmall.MeasureString(text);
                if (critical)
                {
                    float y = vp.Height * 0.205f + critIdx * _hudSmall.LineSpacing;
                    var col = new Color(1f, 0.20f, 0.15f) * fade;   // native red CriticalTipText0
                    _spriteBatch.DrawString(_hudSmall, text, new Vector2((vp.Width - sz.X) / 2f, y), col);
                    critIdx++;
                }
                else
                {
                    float y = vp.Height * 0.30f + tipIdx * _hudSmall.LineSpacing;
                    var col = new Color(0.07f, 0.95f, 0.84f) * fade;   // side accent colour
                    _spriteBatch.DrawString(_hudSmall, text, new Vector2((vp.Width - sz.X) / 2f, y), col);
                    tipIdx++;
                }
            }

            // ── 2. Eject-pod panel ──────────────────────────────────────────
            if (inPod)
            {
                float panelW = vp.Width * 0.30f, panelH = vp.Height * 0.075f;
                float px = (vp.Width - panelW) / 2f, py = vp.Height * 0.855f;
                // The real conejectpoddisplaybmp when available (native
                // EjectPodImage, dialog.mdl:1926-1960); else a procedural panel.
                var panelTex = _feedbackTextures?.EjectPodPanel;
                if (panelTex is not null)
                {
                    float aspect = (float)panelTex.Width / panelTex.Height;
                    float h = panelH, w = h * aspect;
                    var dst = new Rectangle((int)(vp.Width / 2f - w / 2f), (int)py, (int)w, (int)h);
                    _spriteBatch.Draw(panelTex, dst, Color.White);
                    _spriteBatch.DrawString(_hudBig, "YOU HAVE EJECTED.",
                        new Vector2(vp.Width / 2f - _hudBig.MeasureString("YOU HAVE EJECTED.").X / 2f, py + panelH * 0.12f),
                        new Color(0.88f, 0.96f, 0.95f));
                    // Endurance gauge: "O : mm:ss" + a bar of the fraction left
                    // (native softgaugebmp of GetEndurance(Me)).
                    string od = $"O : {P578Countdown(oxySec)}";
                    _spriteBatch.DrawString(_hudSmall, od,
                        new Vector2(vp.Width / 2f + w / 2f + 12f, py + panelH * 0.55f),
                        new Color(0.07f, 0.95f, 0.84f));
                }
                else
                {
                    FillP578Rect(px, py, panelW, panelH, new Color(0.020f, 0.055f, 0.060f, 0.72f));
                    FillP578Rect(px, py, panelW, 1f, new Color(0.14f, 0.34f, 0.32f));
                    _spriteBatch.DrawString(_hudBig, "YOU HAVE EJECTED.",
                        new Vector2(vp.Width / 2f - _hudBig.MeasureString("YOU HAVE EJECTED.").X / 2f, py + 6f),
                        new Color(0.88f, 0.96f, 0.95f));
                    string od = $"OXYGEN O : {P578Countdown(oxySec)}";
                    _spriteBatch.DrawString(_hudSmall, od,
                        new Vector2(vp.Width / 2f - _hudSmall.MeasureString(od).X / 2f, py + panelH - _hudSmall.LineSpacing),
                        new Color(0.07f, 0.95f, 0.84f));
                }
            }

            // ── 3. Death banner (ghost) ────────────────────────────────────
            if (ghost)
            {
                var bannerTex = _feedbackTextures?.DeathBanner;
                if (bannerTex is not null)
                {
                    float bw = vp.Width * 0.46f;
                    float bh = bw * bannerTex.Height / (float)bannerTex.Width;
                    _spriteBatch.Draw(bannerTex,
                        new Rectangle((int)(vp.Width / 2f - bw / 2f), (int)(vp.Height * 0.085f), (int)bw, (int)bh),
                        Color.White);
                }
                else
                {
                    string b = "YOU WERE KILLED";
                    var bs = _hudBig.MeasureString(b);
                    _spriteBatch.DrawString(_hudBig, b,
                        new Vector2((vp.Width - bs.X) / 2f, vp.Height * 0.095f), new Color(0.95f, 0.30f, 0.22f));
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
        if (_targetNoMatchNoun is not { } noun) return;
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
            _spriteBatch.DrawString(_hudSmall, text, pos, colour);
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
        var world = _session.World;
        if (world.MyShipId is { } me
            && world.Ships.TryGetValue(me, out var ent)
            && ent.Loadout is { Count: > 0 } lo)
        {
            int mounts = lo.Select(p => p.MountId).Where(m => m >= 0).Distinct().Count();
            if (mounts > 0)
                return Math.Min(mounts, ShipControlState.SelectedWeaponMask + 1);
        }
        return ShipControlState.SelectedWeaponMask + 1;
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
        if (_shipArtOverride is null || _hullById is not { } hulls || !hulls.TryGetValue(id, out var hull))
            return _shipsNative;
        var modelName = hull.ModelName?.Trim();
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
                        GraphicsDevice.Viewport.AspectRatio, 0.1f, 40f);
                    _nativeShipRenderer.Draw(ngeo, ntex, nworld, nview, nproj, 1f, nref.NativeScale, null);
                    return;
                }
            }
        }

        var model = ResolveShipModel(side, hullId) ?? _fallbackModel;
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

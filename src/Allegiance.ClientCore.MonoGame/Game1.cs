using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The playable MonoGame client. Connects to a real <see cref="Allegiance.Server"/>
/// over the real <see cref="ConnectionSession"/> wire path, then every frame:
/// samples the keyboard → translates to a <see cref="FlightCommand"/> →
/// <see cref="ConnectionSession.CommandControls"/>; drives a
/// <see cref="RemoteShipPredictor"/> per remote (teammate) ship from the real
/// update stream; and draws a static <see cref="Rendering.Starfield"/> plus
/// every known ship (own via raw position, others via their predicted pose)
/// from a chase camera behind the local ship.
/// </summary>
public sealed class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly ConnectionSession _session;
    private readonly string _playerName;

    private XnaModel _model = null!;
    private ShipRenderer _shipRenderer = null!;
    private ChaseCamera _chaseCamera = null!;
    private CockpitCamera _cockpitCamera = null!;
    private Starfield _starNear = null!;
    private Starfield _starMid = null!;
    private Starfield _starFar = null!;

    // 2D HUD overlay (P211). Drawn with SpriteBatch on top of the 3D scene each
    // frame; the reticle + camera-mode tag are camera-independent, the telemetry
    // widgets read the local ship's live ServerShipUpdate (see HudFrame).
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _whitePixel = null!;
    private SpriteFont _hudSmall = null!;
    private SpriteFont _hudBig = null!;
    private HudRenderer _hudRenderer = null!;

    // P213 — real audio. Null until LoadContent runs; inert (silent, non-fatal)
    // if the host has no usable audio device (see SoundManager remarks).
    private Audio.SoundManager? _sound;

    // First-person cockpit toggle (V, "View"). Defaults to Chase so anyone not
    // pressing V sees exactly the pre-P211 third-person behaviour; Cockpit hides
    // the local hull and rolls the horizon with the ship (see CockpitCamera).
    private CameraMode _cameraMode = CameraMode.Chase;
    private bool _vKeyWasDown; // edge-detect the V toggle so holding it doesn't re-fire

    // Server tick interval: 1 / ServerConfig.UpdatesPerSecond (default 20).
    private const double UpdateIntervalSeconds = 1.0 / 20.0;

    // Monotonic render clock feeding the predictors.
    private double _nowSeconds;
    private readonly Dictionary<short, RemoteShipPredictor> _remotes = new();
    private readonly Dictionary<short, uint> _remoteLastTick = new();
    private readonly Dictionary<short, Flight.PredictedPose> _poses = new();

    // Minimal auto-start so the POC can actually fly (see Update): ready-up and
    // launch are one-shot, sent once the prerequisites are met.
    private bool _autoReadySent;
    private bool _autoLaunchSent;

    // Virtual-joystick mouse flight (P210). Enabled/disabled with M, mirroring
    // the original's TK_ToggleMouse: hiding the OS cursor while active, showing
    // it again when off, and resetting the stored deflection on toggle-off (the
    // original's SetEnabled(false) zeroes m_ppnumber[0..2]). While active the
    // controller's (yaw, pitch) REPLACES arrow-key yaw/pitch; roll/throttle stay
    // purely keyboard, untouched.
    private readonly MouseFlightController _mouseFlight = new();
    private bool _mouseFlightEnabled;
    private bool _mKeyWasDown; // edge-detect the M toggle so holding it doesn't re-fire

    // P212 — player autopilot ("click-and-go"). T selects the candidate nearest
    // the crosshair; G toggles autopilot on/off toward the selected target; ANY
    // real manual flight input auto-disengages (native WinTrek SenseJoystick
    // behaviour). _autoTarget is null when nothing is selected; engaged is
    // orthogonal — you can hold a target while autopilot is off.
    private AutopilotTarget? _autoTarget;
    private bool _autoPilotEngaged;
    private bool _tKeyWasDown; // edge-detect T so holding it doesn't re-fire
    private bool _gKeyWasDown; // edge-detect G so holding it doesn't re-fire
    private readonly string _windowTitleBase;

    public Game1(ushort port, string playerName)
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        _session = new ConnectionSession();
        _playerName = playerName;
        _port = port;
        IsMouseVisible = true;
        _windowTitleBase = $"Allegiance ClientCore MonoGame ({playerName})";
        Window.Title = _windowTitleBase;
        Content.RootDirectory = "Content";

        // The console may be redirected/absent on some hosts; keep failures loud.
        try { Window.AllowUserResizing = true; } catch { /* headless/unsupported */ }
    }

    private readonly ushort _port;

    protected override void Initialize()
    {
        base.Initialize();

        Console.WriteLine($"[monogame] connecting to 127.0.0.1:{_port} as '{_playerName}' ...");
        try
        {
            _session.Connect(_port, _playerName);
            _session.SendLogonRequest(Request(_playerName));
            var ack = _session.WaitForAck(5000);
            Console.WriteLine($"[monogame] logged on: serverTime={ack.TimeServer}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] FAILED to connect/log on: {ex.Message}");
            Exit();
        }
    }

    protected override void LoadContent()
    {
        base.LoadContent();
        try
        {
            _model = Content.Load<XnaModel>("Models/bios_scout_upscaled");
            _shipRenderer = new ShipRenderer(_model);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] FAILED to load ship model: {ex.Message}");
            Exit();
            return;
        }
        // near/far: with the camera-relative fix (ChaseCamera.Matrices), the
        // view-space distances involved are small (a nearby ship, not the
        // sim's absolute world coordinates), so a modest far plane is enough
        // — keeps the near:far ratio (and therefore depth-buffer precision)
        // healthy. distanceBehind/heightAbove are tuned so the whole ship
        // (its true rendered size after the FBX's baked ParentBone.Transform
        // scale — see Content.mgcb's Scale=1, not 20; the bone transform
        // already supplies the needed ~100x) fits in frame. farPlane bumped
        // from the original 3000 to 6000 to give the Starfield below more
        // visible range (near:far ratio 600:1 with near=10 — still healthy,
        // well short of the ~8000:1 ratio P207 found actually risky).
        // fieldOfViewDegrees/turnSmoothingTauSeconds: live playtest feedback
        // was that flying "felt very 2D" even with roll/pitch/yaw genuinely
        // working — the default 45° FOV plus a perfectly-locked camera gave
        // no sense of depth or turning. See ChaseCamera's constructor remarks.
        _chaseCamera = new ChaseCamera(distanceBehind: 350f, heightAbove: 90f, nearPlane: 10f, farPlane: 6000f,
            fieldOfViewDegrees: 75f, turnSmoothingTauSeconds: 0.18f);

        // First-person cockpit camera (P211) — same FOV/near/far family as the
        // chase camera so switching views keeps perceived scale; a small forward
        // offset keeps the near clip clear of the (un-rendered) cockpit interior.
        _cockpitCamera = new CockpitCamera(nearPlane: 1f, farPlane: 6000f,
            fieldOfViewDegrees: 75f, forwardOffset: 14f);

        // 2D HUD plumbing (P211): a SpriteBatch, a 1x1 white pixel for
        // procedurally-tinted fills/lines, and the two baked spritefonts.
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _whitePixel = new Texture2D(GraphicsDevice, 1, 1);
        _whitePixel.SetData(new[] { Color.White });
        _hudSmall = Content.Load<SpriteFont>("HUD");
        _hudBig = Content.Load<SpriteFont>("HUDLarge");
        _hudRenderer = new HudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // Three static point-starfield layers at different distance bands so
        // forward motion has real parallax depth, not just one flat backdrop
        // — see Starfield's own remarks for why this was needed (the chase
        // camera alone gives no visual reference for straight-line travel;
        // a single distance band gave only weak, uniform parallax). Centered
        // on the origin with bands wide enough to cover this mission's
        // typical spawn coordinates (observed in the hundreds to low
        // thousands) without needing to wait for the real ship position,
        // which LoadContent runs before receiving anyway. Nearer layers are
        // brighter and sparser (fast, sharp parallax); farther layers are
        // dimmer and denser (slow, backdrop-like) — real depth cues on top
        // of the parallax-rate difference itself.
        _starNear = new Starfield(GraphicsDevice, center: Vector3.Zero, innerRadius: 1200f, outerRadius: 2200f,
            count: 500, seed: 111, minBrightness: 220, maxBrightness: 255);
        _starMid = new Starfield(GraphicsDevice, center: Vector3.Zero, innerRadius: 2200f, outerRadius: 3800f,
            count: 1200, seed: 222, minBrightness: 160, maxBrightness: 220);
        _starFar = new Starfield(GraphicsDevice, center: Vector3.Zero, innerRadius: 3800f, outerRadius: 6000f,
            count: 2500, seed: 333, minBrightness: 90, maxBrightness: 160);

        // P213 — real audio. Loaded after the render content so a sound failure
        // (no audio device) never blocks the visuals; SoundManager degrades
        // gracefully to silent rather than throwing.
        _sound = new Audio.SoundManager(Content);
        _sound.Load();
    }

    protected override void Update(GameTime gameTime)
    {
        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed
            || Keyboard.GetState().IsKeyDown(XnaKeys.Escape))
            Exit();

        _nowSeconds += gameTime.ElapsedGameTime.TotalSeconds;

        // Sample the keyboard once; the M toggle needs the sampled state and the
        // per-frame flight input builds off the same snapshot.
        var ks = Keyboard.GetState();

        // Virtual-joystick mouse-flight toggle (M), edge-triggered to mirror the
        // original's TK_ToggleMouse key (not a held-until-repeat behaviour).
        bool mMouse = ks.IsKeyDown(XnaKeys.M);
        if (mMouse && !_mKeyWasDown)
        {
            _mouseFlightEnabled = !_mouseFlightEnabled;
            IsMouseVisible = !_mouseFlightEnabled;
            if (_mouseFlightEnabled)
                _mouseFlight.Enable();      // arm the just-enabled skip
            else
                _mouseFlight.Reset();       // zero the stored deflection (SetEnabled(false))
            Console.WriteLine($"[monogame] mouse flight {( _mouseFlightEnabled ? "ON" : "OFF" )}");
        }
        _mKeyWasDown = mMouse;

        // First-person cockpit toggle (V), edge-triggered like the M toggle —
        // press to flip between the third-person chase camera and the cockpit
        // view; holding V does not re-fire. 'V' is unused by FlightInput (WASD,
        // arrows, Q/E, A/D, Escape and M are all taken), and "View" is a mnemonic.
        bool vCam = ks.IsKeyDown(XnaKeys.V);
        if (vCam && !_vKeyWasDown)
        {
            _cameraMode = _cameraMode == CameraMode.Chase ? CameraMode.Cockpit : CameraMode.Chase;
            Console.WriteLine($"[monogame] camera: {_cameraMode}");
        }
        _vKeyWasDown = vCam;

        var input = new FlightInputState(
            ThrottleForward: ks.IsKeyDown(XnaKeys.W),
            ThrottleBack:    ks.IsKeyDown(XnaKeys.S),
            StrafeLeft:      ks.IsKeyDown(XnaKeys.Q),
            StrafeRight:     ks.IsKeyDown(XnaKeys.E),
            PitchUp:         ks.IsKeyDown(XnaKeys.Up),
            PitchDown:       ks.IsKeyDown(XnaKeys.Down),
            YawLeft:         ks.IsKeyDown(XnaKeys.Left),
            YawRight:        ks.IsKeyDown(XnaKeys.Right),
            RollLeft:        ks.IsKeyDown(XnaKeys.A),
            RollRight:       ks.IsKeyDown(XnaKeys.D));
        var cmd = FlightInput.Translate(input);

        // Raw mouse delta this frame, captured for BOTH the virtual-joystick
        // deflection below and the autopilot's manual-input disengage check
        // (a physical mouse movement past deadzone = manual flight input).
        int mouseDx = 0, mouseDy = 0;
        if (_mouseFlightEnabled)
        {
            var ms = Mouse.GetState();
            int centerX = GraphicsDevice.Viewport.Width / 2;
            int centerY = GraphicsDevice.Viewport.Height / 2;
            mouseDx = ms.X - centerX;
            mouseDy = ms.Y - centerY;

            var defl = _mouseFlight.Update(mouseDx, mouseDy);
            cmd = cmd with
            {
                Controls = cmd.Controls with { Yaw = defl.Yaw, Pitch = defl.Pitch },
            };

            Mouse.SetPosition(centerX, centerY);
        }

        var world = _session.World;

        // P212 — player autopilot ("click-and-go"), edge-triggered like the
        // M/V toggles above so holding a key never re-fires.
        //   T  → select the candidate nearest the crosshair (ships except self/
        //        dead, stations, asteroids).
        //   G  → toggle autopilot on/off toward the selected target.
        bool tPress = ks.IsKeyDown(XnaKeys.T);
        if (tPress && !_tKeyWasDown)
        {
            if (TryOwnPose(out var oPos, out var oOrient))
            {
                var picked = PickNearestTarget(oPos, oOrient);
                _autoTarget = picked;
                Console.WriteLine(picked is { } pt
                    ? $"[monogame] target: {pt.Name} ({pt.Kind}#{pt.Id})"
                    : "[monogame] no target in view");
            }
        }
        _tKeyWasDown = tPress;

        bool gPress = ks.IsKeyDown(XnaKeys.G);
        if (gPress && !_gKeyWasDown)
        {
            if (_autoPilotEngaged)
            {
                DisengageAutopilot("toggle-off");
            }
            else
            {
                // Implicit nearest pick if none selected yet, then engage.
                if (_autoTarget is not { } tgt && TryOwnPose(out var pPos, out var pOrient))
                    _autoTarget = PickNearestTarget(pPos, pOrient);
                if (_autoTarget is { } at)
                {
                    _autoPilotEngaged = true;
                    Console.WriteLine($"[monogame] autopilot ON → {at.Name} ({at.Kind}#{at.Id})");
                    _sound?.PlayAutopilotEngage();
                    RefreshWindowTitle();
                }
            }
        }
        _gKeyWasDown = gPress;

        // Native behaviour (WinTrek.cpp's SenseJoystick): ANY real manual
        // deflection past the deadzone disengages autopilot before that frame's
        // input is applied. Detect it from the raw key/mouse state — not the
        // computed cmd, which the autopilot would otherwise be masking anyway.
        bool manualFlightInput =
            input.ThrottleForward || input.ThrottleBack || input.StrafeLeft || input.StrafeRight ||
            input.PitchUp || input.PitchDown || input.YawLeft || input.YawRight ||
            input.RollLeft || input.RollRight ||
            (_mouseFlightEnabled && (mouseDx != 0 || mouseDy != 0));
        if (_autoPilotEngaged && manualFlightInput)
            DisengageAutopilot("manual input");

        // Send controls: while autopilot drives, its steering REPLACES the
        // manual command and the movement buttons stay neutral (stateM=0) — it
        // only uses the analog throttle axis (AutopilotSteering.Throttle), so
        // it does NOT go through FlightInput's keyboard idle-throttle (-1)
        // convention. If the target can no longer be resolved (died/vanished)
        // disengage and fall back to the manual command.
        if (_autoPilotEngaged && TryGetAutopilotControls(out var apControls))
        {
            _session.CommandControls(apControls, 0);
        }
        else
        {
            if (_autoPilotEngaged) DisengageAutopilot("target lost");
            _session.CommandControls(cmd.Controls, cmd.StateM);
        }

        // Recompute predicted poses for every remote (non-self) ship, feeding
        // each newly-arrived real update into its predictor.
        _poses.Clear();
        if (world.MyShipId is { } me)
        {
            // Own ship renders at its raw last-known position (no smoothing),
            // and drives the P213 engine/ambience mix from its real telemetry.
            if (world.Ships.TryGetValue(me, out var mine) && mine.Latest is { } mineUp)
            {
                _poses[me] = new Flight.PredictedPose(mineUp.Position, mineUp.Orientation);
                _sound?.Update(
                    throttle: mineUp.Controls.Throttle,
                    speed: mineUp.Velocity.Length(),
                    cockpitMode: _cameraMode == CameraMode.Cockpit);
            }
        }
        foreach (var (id, ship) in world.Ships)
        {
            if (id == world.MyShipId || ship.Latest is not { } up) continue;

            if (!_remotes.TryGetValue(id, out var pred))
            {
                pred = new RemoteShipPredictor(UpdateIntervalSeconds);
                _remotes[id] = pred;
            }
            if (!_remoteLastTick.TryGetValue(id, out uint lastTick) || lastTick != ship.LastUpdateTick)
            {
                pred.Observe(up, _nowSeconds);
                _remoteLastTick[id] = ship.LastUpdateTick;
            }
            if (pred.Predict(_nowSeconds) is { } pose)
                _poses[id] = pose;
        }

        // Minimal auto-start so the POC is actually playable. The server only
        // begins a mission once every OCCUPIED side is aggregate-ready (P180
        // TryAutoStart), and the local ship only flies once it has undocked
        // into a sector (the server's PerformUndock, gated on
        // FM_C_DOCKED stationID=NA). Both are things a real client does
        // through lobby/hangar buttons this POC deliberately omits, so we do
        // the minimum to reach flight: ready-up once we have a ship, then
        // undock once the mission is STAGE_STARTED. One-shot each (guard
        // flags above).
        if (world.MyShipId is { } pid)
        {
            if (!_autoReadySent)
            {
                _session.SendPlayerReady(true, pid);
                _autoReadySent = true;
                Console.WriteLine("[monogame] auto-ready sent");
            }
            if (!_autoLaunchSent
                && world.Mission is { } mi && mi.Stage == (int)MissionStage.Started)
            {
                _session.SendDocked();
                _autoLaunchSent = true;
                Console.WriteLine("[monogame] auto-undock sent");
            }
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);

        var world = _session.World;
        if (world.MyShipId is { } me && _poses.TryGetValue(me, out var myPose))
        {
            var myPos = PoseConverter.ToXna(myPose.Position);
            var myRot = PoseConverter.ToXnaRotation(myPose.Orientation);
            var aspect = GraphicsDevice.Viewport.AspectRatio;

            // Camera selection (P211): chase (third-person, roll-free) or
            // cockpit (first-person, full orientation with roll). Both honour
            // the same camera-relative convention and both supply a cameraEye
            // that every draw subtracts from object positions.
            var (view, proj) = _cameraMode == CameraMode.Cockpit
                ? _cockpitCamera.Matrices(myPos, myRot, aspect, out var cockpitEye)
                : _chaseCamera.Matrices(myPos, myRot, aspect, (float)gameTime.ElapsedGameTime.TotalSeconds, out cockpitEye);
            var cameraEye = cockpitEye;

            _starFar.Draw(view, proj, cameraEye);
            _starMid.Draw(view, proj, cameraEye);
            _starNear.Draw(view, proj, cameraEye);

            foreach (var (id, pose) in _poses)
            {
                // In cockpit mode the local hull is not drawn — you are inside
                // it; every OTHER known ship still renders normally.
                if (_cameraMode == CameraMode.Cockpit && id == me)
                    continue;

                var xnaPos = PoseConverter.ToXna(pose.Position);
                var worldMat = PoseConverter.ToXnaWorldRelative(xnaPos, pose.Orientation, cameraEye);
                _shipRenderer.Draw(worldMat, view, proj);
            }
        }

        // 2D HUD overlay on top of the 3D scene (P211).
        DrawHud();

        base.Draw(gameTime);
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
        if (world.MyShipId is { } me
            && world.Ships.TryGetValue(me, out var entity)
            && entity.Latest is { } up)
        {
            frame = new HudFrame(
                HullFraction: up.HullFraction,
                ShieldFraction: up.ShieldFraction,
                AmmoFraction: up.AmmoFraction,
                FuelFraction: up.FuelFraction,
                EnergyFraction: up.EnergyFraction,
                Power: up.Power,
                Speed: up.Velocity.Length(),
                Throttle: up.Controls.Throttle,
                AfterburnerActive: (up.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0);
        }
        _hudRenderer.Draw(frame, _cameraMode, GraphicsDevice.Viewport);
    }

    // ── P212 player-autopilot helpers ─────────────────────────────────────────

    /// <summary>The kind of object an autopilot target can be.</summary>
    private enum TargetKind { Ship, Station, Asteroid }

    /// <summary>A selected autopilot target: enough to re-resolve its live
    /// position each frame from <see cref="ClientWorld"/> (ships may move),
    /// plus a display name for the console / window-title readout.</summary>
    private readonly record struct AutopilotTarget(TargetKind Kind, short Id, string Name);

    /// <summary>True once the local ship has a usable real position/orientation
    /// (assigned a ship id AND received at least one update).</summary>
    private bool TryOwnPose(out SimVec position, out Orientation orientation)
    {
        position = default;
        orientation = Orientation.Identity;
        if (_session.World.MyShipId is not { } me) return false;
        if (!_session.World.Ships.TryGetValue(me, out var mine) || mine.Latest is not { } up) return false;
        position = up.Position;
        orientation = up.Orientation;
        return true;
    }

    /// <summary>
    /// Soft-lock target selection (P212): among every known non-self, non-dead
    /// ship, station and asteroid, pick the one whose projected screen position
    /// lies nearest the crosshair (screen centre). Candidates are projected
    /// through the same view/projection matrices <see cref="Draw"/> builds for
    /// the active camera (the chase camera is sampled with a zero delta so its
    /// turn-smoothing state is not disturbed by the extra call); anything
    /// behind the camera or outside the view frustum is discarded, then the
    /// smallest screen-space distance from centre wins. No true 3D ray-pick —
    /// this cone/nearest-to-crosshair approximation is the intended POC scope.
    /// </summary>
    private AutopilotTarget? PickNearestTarget(SimVec simPos, Orientation simOrient)
    {
        var world = _session.World;
        var xnaPos = PoseConverter.ToXna(simPos);
        var rot = PoseConverter.ToXnaRotation(simOrient);
        float aspect = GraphicsDevice.Viewport.AspectRatio;

        Matrix view, projection;
        Vector3 eye;
        if (_cameraMode == CameraMode.Cockpit)
            (view, projection) = _cockpitCamera.Matrices(xnaPos, rot, aspect, out eye);
        else
            (view, projection) = _chaseCamera.Matrices(xnaPos, rot, aspect, 0f, out eye);
        var viewProj = view * projection;

        AutopilotTarget? best = null;
        float bestDistSq = float.MaxValue;

        void Consider(SimVec p, TargetKind kind, short id, string name)
        {
            var rel = PoseConverter.ToXna(p) - eye;
            var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
            if (clip.W <= 1e-6f) return;                          // behind the camera
            float sx = clip.X / clip.W;
            float sy = clip.Y / clip.W;
            if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return; // outside the FOV frustum
            float d = sx * sx + sy * sy;
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = new AutopilotTarget(kind, id, name);
            }
        }

        if (world.MyShipId is { } me)
        {
            foreach (var (id, ship) in world.Ships)
            {
                if (id == me || ship.IsDead || ship.Latest is not { } up) continue;
                string nm = string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName;
                Consider(up.Position, TargetKind.Ship, id, nm);
            }
        }
        foreach (var (id, st) in world.Stations)
            Consider(Vec(st.Position), TargetKind.Station, id, st.Name);
        foreach (var (id, ast) in world.Asteroids)
            Consider(Vec(ast.Position), TargetKind.Asteroid, id, ast.Name);

        return best;
    }

    /// <summary>Compute the autopilot's per-frame steering toward the selected
    /// target (re-read every frame, so a moving target is followed), or return
    /// false if there is no resolvable target / own pose.</summary>
    private bool TryGetAutopilotControls(out ControlData controls)
    {
        controls = ControlData.Neutral;
        if (_autoTarget is not { } tgt) return false;
        if (!ResolveTargetPosition(tgt, out var targetPos)) return false;
        if (!TryOwnPose(out var pos, out var orient)) return false;
        controls = AutopilotSteering.SteerToward(pos, orient, targetPos);
        return true;
    }

    /// <summary>Re-resolve a stored target's live world position. False when the
    /// object is gone, or the ship has died (you cannot steer at a corpse).</summary>
    private bool ResolveTargetPosition(AutopilotTarget tgt, out SimVec pos)
    {
        var world = _session.World;
        switch (tgt.Kind)
        {
            case TargetKind.Ship:
                if (world.Ships.TryGetValue(tgt.Id, out var ship) && ship.Latest is { } up && !ship.IsDead)
                {
                    pos = up.Position;
                    return true;
                }
                break;
            case TargetKind.Station:
                if (world.Stations.TryGetValue(tgt.Id, out var st))
                {
                    pos = Vec(st.Position);
                    return true;
                }
                break;
            case TargetKind.Asteroid:
                if (world.Asteroids.TryGetValue(tgt.Id, out var ast))
                {
                    pos = Vec(ast.Position);
                    return true;
                }
                break;
        }
        pos = default;
        return false;
    }

    /// <summary>Clear the autopilot (engaged flag + stored target) with a
    /// console note. Called on G toggle-off, on ANY manual flight input (native
    /// SenseJoystick behaviour), and when the target becomes unresolvable.</summary>
    private void DisengageAutopilot(string reason)
    {
        if (!_autoPilotEngaged && _autoTarget is null) return;
        bool wasEngaged = _autoPilotEngaged;
        _autoPilotEngaged = false;
        _autoTarget = null;
        Console.WriteLine($"[monogame] autopilot OFF ({reason})");
        if (wasEngaged) _sound?.PlayAutopilotDisengage();
        RefreshWindowTitle();
    }

    /// <summary>Mirror the autopilot/target state in the window title — a cheap
    /// on-screen indication that avoids touching P211's HUD surface.</summary>
    private void RefreshWindowTitle()
    {
        Window.Title = _autoPilotEngaged && _autoTarget is { } t
            ? $"{_windowTitleBase} [AUTOPILOT: {t.Name}]"
            : _windowTitleBase;
    }

    /// <summary>Adopt the sim's <c>float[3]</c> wire position as a <see cref="SimVec"/>.</summary>
    private static SimVec Vec(float[] a) => new(a[0], a[1], a[2]);

    protected override void UnloadContent()
    {
        _sound?.Dispose();
        _session.Dispose();
        base.UnloadContent();
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name,
        ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 1234u,
        DwCookie = 0x33Fu,
        CrcFileList = -1,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 70000000000000000ul,
        DrmHash = "monogame",
    };
}

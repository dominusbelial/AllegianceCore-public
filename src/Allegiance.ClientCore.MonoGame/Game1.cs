using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

    // P216 — mission announcer-cue bookkeeping. _countdownStartCuePlayed gates
    // the single countdown-start callout; _countdownLastSecond mirrors native's
    // m_nLastCountdown change-detection (WinTrek.cpp:11150) so each second's
    // per-second tick plays exactly once, not every frame it happens to still be
    // true; _gameOverCuePlayed fires the game-over callout exactly once on the
    // real Stage → Over transition.
    private bool _countdownStartCuePlayed;
    private int _countdownLastSecond;
    private bool _gameOverCuePlayed;

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

    /// <summary>Everything needed to boot <see cref="Game1"/> — which mode it
    /// starts in (lobby vs direct connect) and the config each mode needs.
    /// Built by <c>Program.cs</c> from the CLI args.</summary>
    public sealed class LaunchOptions
    {
        public string PlayerName = "monogame";

        /// <summary>Non-null selects DIRECT connect (skip the lobby), connecting
        /// straight to <see cref="DirectPort"/> on loopback — the unchanged
        /// <c>[host] [port] [name]</c> dev/testing shortcut.</summary>
        public string? DirectHost;

        public ushort DirectPort;

        /// <summary>Base URL of the lobby directory whose <c>/api/servers</c>
        /// the server-browser state polls. Default documented in
        /// <see cref="ServerListing.LobbyUrlDefault"/>.</summary>
        public string LobbyUrl = ServerListing.LobbyUrlDefault;
    }

    /// <summary>The two top-level states of the client. Lobby is the default
    /// entry (server browser); Connecting is transient while the wire handshake
    /// runs; Flying is the joined-in-flight mode (existing behaviour).</summary>
    private enum ClientState { Lobby, Connecting, Flying }

    private ClientState _state;

    private readonly string _lobbyUrl;
    private string? _connectHost;
    private ushort _connectPort;
    private bool _returnToLobbyOnFailure;   // join-from-lobby failed → revert, don't exit

    // Server-browser (lobby) state.
    private readonly HttpClient _http;
    private readonly List<ServerListing> _servers = new();
    private int _selectedIndex;
    private string? _lobbyError;
    private double _lastLobbyFetch = double.NegativeInfinity;
    private volatile bool _lobbyFetchInFlight;   // read on the main thread, set on the fetch continuation
    private volatile bool _lobbyResultPending;   // a fetch finished; data/error staged for the main thread
    private List<ServerListing>? _lobbyResultData;
    private string? _lobbyResultError;

    // Chat (P214): a text-input line composed from Window.TextInput characters
    // and sent via ConnectionSession.SendChat; a log overlay drawn from the
    // already-populated ClientWorld.Events.Chats.
    private bool _chatActive;
    private readonly StringBuilder _chatBuffer = new();
    private const int MaxChatLen = 880;
    private ChatRenderer _chatRenderer = null!;

    // Rising-edge detectors so a held key neither re-fires nor (Escape) both
    // cancels chat and then quits the game across consecutive frames.
    private bool _escWasDown, _enterWasDown, _backWasDown, _upWasDown, _downWasDown, _rWasDown;

    public Game1(LaunchOptions opts)
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        _session = new ConnectionSession();
        _playerName = opts.PlayerName;
        IsMouseVisible = true;
        _windowTitleBase = $"Allegiance ClientCore MonoGame ({opts.PlayerName})";
        Window.Title = _windowTitleBase;
        Content.RootDirectory = "Content";

        // The console may be redirected/absent on some hosts; keep failures loud.
        try { Window.AllowUserResizing = true; } catch { /* headless/unsupported */ }

        _lobbyUrl = opts.LobbyUrl;
        _http = new HttpClient
        {
            BaseAddress = new Uri(opts.LobbyUrl.EndsWith('/') ? opts.LobbyUrl : opts.LobbyUrl + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5),
        };

        if (opts.DirectHost is not null)
        {
            _connectHost = opts.DirectHost;
            _connectPort = opts.DirectPort;
            _state = ClientState.Connecting;   // kicked off in Initialize
        }
        else
        {
            _state = ClientState.Lobby;        // server browser
        }
    }

    protected override void Initialize()
    {
        base.Initialize();

        // Real text input for the chat line (MonoGame Window.TextInput — NOT a
        // manual Keyboard.GetState() scan, which misses shift/caps/punctuation).
        // The handler filters everything itself, so subscribing is unconditional.
        Window.TextInput += OnTextInput;

        if (_state == ClientState.Connecting)
            BeginConnect(_connectHost!, _connectPort);
        // else Lobby: _lastLobbyFetch stays NegativeInfinity so Update's very
        // first lobby tick fires an immediate fetch.
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

        // P214 — in-flight chat overlay (same SpriteBatch/font plumbing as the HUD).
        _chatRenderer = new ChatRenderer(_spriteBatch, _whitePixel, _hudSmall);

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
        _nowSeconds += gameTime.ElapsedGameTime.TotalSeconds;
        var ks = Keyboard.GetState();

        // Escape: while composing chat it cancels the input line (never quits);
        // in any other state it quits the game (as before, along with the
        // gamepad Back button). Edge-detected so a single held Escape can never
        // both cancel chat and then quit across consecutive frames.
        bool esc = ks.IsKeyDown(XnaKeys.Escape);
        bool backBtn = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed;
        if ((esc && !_escWasDown) || backBtn)
        {
            if (_chatActive)
            {
                _chatActive = false;
                _chatBuffer.Clear();
            }
            else
            {
                Exit();
            }
        }
        _escWasDown = esc;

        // Top-level state dispatch: the server browser (default entry) and the
        // transient connecting state don't run any flight logic; only Flying does.
        if (_state != ClientState.Flying)
        {
            if (_state == ClientState.Lobby) UpdateLobby(ks);
            // Connecting is a transient pause while the wire handshake runs;
            // BeginConnect (in Initialize) flips us to Flying on success.
            base.Update(gameTime);
            return;
        }

        UpdateFlying(ks, gameTime);
        base.Update(gameTime);
    }

    /// <summary>
    /// The in-flight state (previously the whole of <see cref="Update"/>): chat
    /// composition, flight controls, autopilot, remote prediction and the
    /// minimal auto-start. All keyboard flight input is gated off while a chat
    /// line is being composed, so typing a word can never also thrust/turn the
    /// ship; the autopilot (once engaged) may keep steering, since it is not a
    /// keystroke-derived input.
    /// </summary>
    private void UpdateFlying(KeyboardState ks, GameTime gameTime)
    {
        // Chat: Enter opens the input line; while composing, Enter sends the
        // line via SendChat and Backspace deletes the previous character. Text
        // itself arrives through Window.TextInput (OnTextInput).
        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        if (enter && !_enterWasDown)
        {
            if (_chatActive) SendChatLine();
            else { _chatActive = true; _chatBuffer.Clear(); }
        }
        _enterWasDown = enter;

        if (_chatActive)
        {
            bool back = ks.IsKeyDown(XnaKeys.Back);
            if (back && !_backWasDown && _chatBuffer.Length > 0)
                _chatBuffer.Length -= 1;
            _backWasDown = back;
        }

        bool composing = _chatActive;

        // Virtual-joystick mouse-flight toggle (M), edge-triggered to mirror the
        // original's TK_ToggleMouse key (not a held-until-repeat behaviour).
        bool mMouse = ks.IsKeyDown(XnaKeys.M);
        if (!composing && mMouse && !_mKeyWasDown)
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
        if (!composing && vCam && !_vKeyWasDown)
        {
            _cameraMode = _cameraMode == CameraMode.Chase ? CameraMode.Cockpit : CameraMode.Chase;
            Console.WriteLine($"[monogame] camera: {_cameraMode}");
        }
        _vKeyWasDown = vCam;

        // While composing chat, keyboard flight input is fully neutral — typing a
        // word must never also thrust/pitch/yaw the ship.
        var input = composing
            ? FlightInputState.None
            : new FlightInputState(
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
        if (_mouseFlightEnabled && !composing)
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
        // M/V toggles above so holding a key never re-fires. Skipped while chat
        // is composed so typing can't re-target or toggle the autopilot.
        //   T  → select the candidate nearest the crosshair (ships except self/
        //        dead, stations, asteroids).
        //   G  → toggle autopilot on/off toward the selected target.
        if (!composing)
        {
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
        }

        // Native behaviour (WinTrek.cpp's SenseJoystick): ANY real manual
        // deflection past the deadzone disengages autopilot before that frame's
        // input is applied. Detect it from the raw key/mouse state — not the
        // computed cmd, which the autopilot would otherwise be masking anyway.
        bool manualFlightInput =
            !composing && (
            input.ThrottleForward || input.ThrottleBack || input.StrafeLeft || input.StrafeRight ||
            input.PitchUp || input.PitchDown || input.YawLeft || input.YawRight ||
            input.RollLeft || input.RollRight ||
            (_mouseFlightEnabled && (mouseDx != 0 || mouseDy != 0)));
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

        // P216 — mission announcer cues, from the same real per-frame mission
        // read. Countdown: the server's SET_START_TIME(14) broadcast is what
        // announces the countdown — the client's Mission.Stage is pinned to
        // StageStarted by the join burst (Server.cs:2138), so it cannot
        // distinguish the brief STAGE_STARTING window; the real "countdown
        // began" signal is that SET_START_TIME receipt, anchored on the world as
        // CountdownStartLocalMs (the same timeStart field native's
        // CheckCountdownSound anchors on, WinTrek.cpp:11144). Fire the
        // countdownstart callout once at the opening, then each per-second tick
        // (countdown10..countdown1) exactly once per distinct seconds-remaining,
        // mirroring native's m_nLastCountdown change-detection (WinTrek.cpp:11150).
        if (world.CountdownStartLocalMs is { } cdStart)
        {
            if (!_countdownStartCuePlayed)
            {
                _sound?.PlayCountdownStart();
                _countdownStartCuePlayed = true;
                _countdownLastSecond = 0;
            }

            double elapsed = (Environment.TickCount64 - cdStart) / 1000.0;
            int remaining = (int)Math.Ceiling(Audio.CountdownSound.BriefingCountdownSeconds - elapsed);
            if (remaining != _countdownLastSecond)
            {
                _countdownLastSecond = remaining;
                if (Audio.CountdownSound.TickAssetFor(remaining) is { })
                    _sound?.PlayCountdownSecond(remaining);
            }
        }

        // Game-over: fire once on the real transition of world.Mission.Stage
        // into Over — driven by the server's GAME_OVER(62) broadcast →
        // ClientWorld.Apply(GameOver) — reusing the exact same world.Mission /
        // stage read as the auto-undock above (no second notion of "ended").
        if (world.Mission is { } gmi && gmi.Stage == (int)MissionStage.Over && !_gameOverCuePlayed)
        {
            _gameOverCuePlayed = true;
            _sound?.PlayGameOver();
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);

        if (_state == ClientState.Lobby)
        {
            DrawLobby();
        }
        else if (_state == ClientState.Connecting)
        {
            DrawConnecting();
        }
        else // Flying
        {
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

            // P214 — real chat overlay (log + composing line) on top of the HUD.
            DrawChat();
        }

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

    // ── P214 — lobby / server browser ────────────────────────────────────────

    /// <summary>How often the lobby list is re-polled (a few seconds, not every
    /// frame — the directory is a slowly-changing remote source).</summary>
    private const double LobbyRefreshSeconds = 3.0;

    /// <summary>One lobby update tick: consume any completed async fetch, then
    /// (re)arm the next fetch on the cadence, and handle navigation/join keys.
    /// Runs on the main thread only.</summary>
    private void UpdateLobby(KeyboardState ks)
    {
        // Pick up a completed background fetch (result staged on the worker
        // thread; consumed here on the main thread so the list/selection is
        // mutated on the single rendering thread).
        if (_lobbyResultPending)
        {
            _lobbyResultPending = false;
            _lobbyError = _lobbyResultError;
            if (_lobbyResultData is { } data)
            {
                _servers.Clear();
                _servers.AddRange(data);
                if (_servers.Count > 0)
                    _selectedIndex = Math.Clamp(_selectedIndex, 0, _servers.Count - 1);
                else
                    _selectedIndex = 0;
            }
            _lobbyResultData = null;
            _lobbyResultError = null;
        }

        // Periodic fetch (fire-and-forget; _lobbyFetchInFlight prevents overlap).
        if (!_lobbyFetchInFlight && _nowSeconds - _lastLobbyFetch >= LobbyRefreshSeconds)
        {
            _lastLobbyFetch = _nowSeconds;
            _lobbyFetchInFlight = true;
            _ = FetchServersAsync();
        }

        // Navigation: arrows (also free pre-flight — no flight keys collide).
        bool up = ks.IsKeyDown(XnaKeys.Up);
        if (up && !_upWasDown && _servers.Count > 0)
            _selectedIndex = (_selectedIndex - 1 + _servers.Count) % _servers.Count;
        _upWasDown = up;

        bool down = ks.IsKeyDown(XnaKeys.Down);
        if (down && !_downWasDown && _servers.Count > 0)
            _selectedIndex = (_selectedIndex + 1) % _servers.Count;
        _downWasDown = down;

        // Manual retry: R forces an immediate refetch (helps an unreachable lobby).
        bool rRetry = ks.IsKeyDown(XnaKeys.R);
        if (rRetry && !_rWasDown)
        {
            _lastLobbyFetch = double.NegativeInfinity;
            _lobbyError = null;
        }
        _rWasDown = rRetry;

        // Enter joins the highlighted server. The advertised address is parsed
        // for display and handed to BeginConnect; ConnectionSession pins the
        // transport to loopback, so a LAN-advertised address still reaches a
        // server on this host. On join failure we revert to the lobby with a
        // message rather than exiting.
        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        if (enter && !_enterWasDown && _servers.Count > 0)
        {
            var s = _servers[Math.Min(_selectedIndex, _servers.Count - 1)];
            if (s.Port > 0)
            {
                _returnToLobbyOnFailure = true;
                Console.WriteLine($"[monogame] joining {s.Name} @ {s.Address}:{s.Port} ...");
                BeginConnect(s.Address, (ushort)s.Port);
            }
            else
            {
                _lobbyError = $"Cannot join '{s.Name}' - it advertises no valid port.";
            }
        }
        _enterWasDown = enter;
    }

    /// <summary>HTTP GET the lobby's <c>/api/servers</c> and stage the result for
    /// the main thread (see <see cref="UpdateLobby"/>). Standard
    /// <see cref="HttpClient"/> — no MonoGame networking involved. Never throws;
    /// failures become an on-screen error and the cadence just retries.</summary>
    private async Task FetchServersAsync()
    {
        List<ServerListing>? data = null;
        string? error = null;
        try
        {
            data = await _http.GetFromJsonAsync<List<ServerListing>>(
                "/api/servers", ServerListing.JsonOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = $"Lobby unreachable ({_lobbyUrl}): {ex.Message} - retrying...";
        }

        // Volatile write AFTER filling the data so the main thread's volatile
        // read of _lobbyResultPending pairs correctly.
        _lobbyResultData = data;
        _lobbyResultError = error;
        _lobbyResultPending = true;
        _lobbyFetchInFlight = false;
    }

    /// <summary>Draw the server-browser screen: title, the fetched listings as a
    /// selectable list, the currently-highlighted row, an error/empty banner,
    /// and a footer of the controls. Self-contained SpriteBatch session.</summary>
    private void DrawLobby()
    {
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width;
        float h = vp.Height;

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            var titleCol = new Color(HudColor.TealAccent.R, HudColor.TealAccent.G, HudColor.TealAccent.B);
            var dimCol = new Color(0.48f, 0.64f, 0.62f);
            var textCol = new Color(0.88f, 0.96f, 0.95f);

            var title = "SERVER BROWSER";
            var tsz = _hudBig.MeasureString(title);
            _spriteBatch.DrawString(_hudBig, title, new Vector2((w - tsz.X) / 2f, 28f), titleCol);

            var sub = $"Polling {_lobbyUrl}/api/servers";
            var ssz = _hudSmall.MeasureString(sub);
            _spriteBatch.DrawString(_hudSmall, sub, new Vector2((w - ssz.X) / 2f, 82f), dimCol);

            if (_lobbyError is { } err)
            {
                _spriteBatch.DrawString(_hudSmall, err, new Vector2(60f, 130f),
                    new Color(1f, 0.45f, 0.35f));
                _spriteBatch.DrawString(_hudSmall, "Press R to retry now.", new Vector2(60f, 156f), dimCol);
            }
            else if (_servers.Count == 0)
            {
                _spriteBatch.DrawString(_hudSmall, "No servers found yet - waiting for registrations...",
                    new Vector2(60f, 130f), dimCol);
            }

            const float rowH = 30f;
            const float listTop = 170f;
            for (int i = 0; i < _servers.Count; i++)
            {
                float y = listTop + i * rowH;
                bool sel = i == _selectedIndex;
                // Highlight bar behind the selected row.
                _spriteBatch.Draw(_whitePixel, new Rectangle(50, (int)y, (int)(w * 0.7f), (int)(rowH - 6f)),
                    sel ? new Color(0.08f, 0.35f, 0.33f, 0.55f) : new Color(0f, 0f, 0f, 0f));

                var line = _servers[i].FormatRow();
                _spriteBatch.DrawString(_hudSmall, line, new Vector2(56f, y + 2f), sel ? titleCol : textCol);
            }

            var footer = "Up/Down select   ENTER join   R refresh   ESC quit";
            var fsz = _hudSmall.MeasureString(footer);
            _spriteBatch.DrawString(_hudSmall, footer, new Vector2((w - fsz.X) / 2f, h - 40f), dimCol);
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>A brief "connecting…" screen while the wire handshake completes
    /// (it is synchronous, so this is a single rendered frame — a nicety, not
    /// a spinner).</summary>
    private void DrawConnecting()
    {
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width;
        float h = vp.Height;

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            string host = string.IsNullOrEmpty(_connectHost) ? "127.0.0.1" : _connectHost;
            string msg = $"Connecting to {host}:{_connectPort} as '{_playerName}'...";
            var sz = _hudBig.MeasureString(msg);
            _spriteBatch.DrawString(_hudBig, msg, new Vector2((w - sz.X) / 2f, (h - sz.Y) / 2f),
                new Color(0.88f, 0.96f, 0.95f));
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    /// <summary>Establish the real wire connection + logon handshake for the
    /// chosen server (the exact logic that used to run unconditionally in
    /// <see cref="Initialize"/>). On success flips to <see cref="ClientState.Flying"/>;
    /// on failure either reverts to the lobby (when joined from there) or exits
    /// (the unchanged direct-connect path).</summary>
    private void BeginConnect(string host, ushort port)
    {
        _state = ClientState.Connecting;
        Console.WriteLine($"[monogame] connecting to {host}:{port} as '{_playerName}' ...");
        try
        {
            _session.Connect(port, _playerName);
            _session.SendLogonRequest(Request(_playerName));
            var ack = _session.WaitForAck(5000);
            Console.WriteLine($"[monogame] logged on: serverTime={ack.TimeServer}");
            _state = ClientState.Flying;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] FAILED to connect/log on: {ex.Message}");
            if (_returnToLobbyOnFailure)
            {
                _returnToLobbyOnFailure = false;
                _state = ClientState.Lobby;
                _lobbyError = $"Could not join {host}:{port} - {ex.Message}";
            }
            else
            {
                Exit();
            }
        }
    }

    // ── P214 — chat ──────────────────────────────────────────────────────────

    /// <summary>Sends the composed line as Everyone chat and closes the input.</summary>
    private void SendChatLine()
    {
        string text = _chatBuffer.ToString().Trim();
        _chatActive = false;
        _chatBuffer.Clear();
        if (text.Length == 0)
            return;
        Console.WriteLine($"[monogame] chat >> {text}");
        // Plain Everyone/Team broadcast — SendChatBuoy and command parsing are
        // out of scope; the server relays the sender's own echo back, which is
        // what actually displays the line (it is NOT echoed locally).
        _session.SendChat(text, ChatTarget.Everyone);
    }

    /// <summary>MonoGame's real text-input handler (<c>Window.TextInput</c>):
    /// appends printable characters to the line buffer while composing. Control
    /// characters (Enter/Backspace/Escape) are handled by the key-edge logic in
    /// <see cref="UpdateFlying"/>, so they are filtered here to avoid double
    /// handling.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_state != ClientState.Flying || !_chatActive)
            return;
        char c = e.Character;
        if (c == '\r' || c == '\n' || c == '\b' || c == (char)27) // Enter / Back / Esc
            return;
        if (c < 32 || c == 127)
            return;
        if (_chatBuffer.Length < MaxChatLen)
            _chatBuffer.Append(c);
    }

    /// <summary>Build the display strings for the in-flight chat log from the
    /// real, already-populated <c>ClientWorld.Events.Chats</c> (arrival-ordered),
    /// resolving each sender's character name from the world roster where known.</summary>
    private List<string> BuildChatLines()
    {
        var world = _session.World;
        var chats = world.Events.Chats;
        var lines = new List<string>(Math.Min(chats.Count, ChatRenderer.MaxVisible));
        int start = Math.Max(0, chats.Count - ChatRenderer.MaxVisible);
        for (int i = start; i < chats.Count; i++)
        {
            var m = chats[i];
            string sender = "";
            if (m.SidSender >= 0 && world.Ships.TryGetValue(m.SidSender, out var ent) && !string.IsNullOrEmpty(ent.CharacterName))
                sender = ent.CharacterName!;
            else if (m.SidSender >= 0)
                sender = $"sid{m.SidSender}";
            lines.Add(ChatLogFormat.Format(sender, m.Message, (ChatTarget)m.ChatTarget));
        }
        return lines;
    }

    /// <summary>Render the in-flight chat overlay (log panel + active composing
    /// line) on top of the HUD.</summary>
    private void DrawChat()
    {
        _chatRenderer.Draw(
            BuildChatLines(),
            _chatActive ? _chatBuffer.ToString() : string.Empty,
            _chatActive,
            _nowSeconds,
            GraphicsDevice.Viewport);
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
        _http.Dispose();
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

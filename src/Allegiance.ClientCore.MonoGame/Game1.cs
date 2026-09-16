using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
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
/// <see cref="ConnectionSession.CommandControls"/>; and draws a static
/// <see cref="Rendering.Starfield"/> plus every known ship — own and remote
/// alike — at its raw last-known <see cref="Flight.PredictedPose"/> from the
/// real update stream, no smoothing (see that type's remarks for why), from a
/// chase camera behind the local ship.
/// </summary>
public sealed class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly ConnectionSession _session;
    private readonly string _playerName;

    private XnaModel _model = null!;
    private ShipRenderer _shipRenderer = null!;
    private AsteroidRenderer _asteroidRenderer = null!;
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

    // Monotonic render clock (lobby refresh timing, chat timestamps, etc).
    private double _nowSeconds;
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
    private bool _kKeyWasDown; // edge-detect K (suicide) so holding it doesn't re-fire
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

    // P217 — real quick-chat (SonicChat) voice-macro system. The menu tree +
    // voice table are parsed at runtime from the shipped Content/*.mdl files
    // (_quickChatDb). R (free in flight) opens the quick-chat menu; selecting a
    // leaf sends a real CHATMESSAGE with the command's voiceOver index; the
    // receive path plays the matching voice for incoming quick-chats (which
    // includes the sender's own server relay echo — see
    // ProcessIncomingQuickChatVoices).
    private QuickChatDatabase? _quickChatDb;
    private QuickChatMenuRenderer _quickChatRenderer = null!;
    private bool _qcMenuOpen;
    private QuickChatMenu? _qcMenuNode;
    private List<QuickChatMenu> _qcMenuPath = new();
    private readonly HashSet<Keys> _qcKeysPrev = new();  // last-frame nav keys (rising-edge detection)
    private bool _qcRWasDown;                            // edge-detect the R open toggle
    private ChatMessage? _lastVoiceProcessed;            // receive-side voice watermark

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

        // P217 — the quick-chat voice-macro data, parsed once at runtime from
        // verbatim copies of the original's real .mdl files shipped as Content
        // (see QuickChatMdlParser for why runtime parsing beats a build-time
        // generated table). Failure degrades gracefully: quick-chat is simply
        // unavailable, the rest of the client unaffected.
        try
        {
            string qcPath = Path.Combine(AppContext.BaseDirectory, "Content", "quickchat.mdl");
            string sdPath = Path.Combine(AppContext.BaseDirectory, "Content", "sounddef.mdl");
            if (File.Exists(qcPath) && File.Exists(sdPath))
            {
                _quickChatDb = QuickChatMdlParser.Parse(
                    File.ReadAllText(qcPath), File.ReadAllText(sdPath));
                Console.WriteLine($"[monogame] quick-chat: {_quickChatDb.EntriesByIndex.Count} voices, " +
                    $"{_quickChatDb.MenusByName.Count} menus, mainMenu {(_quickChatDb.MainMenu is null ? "MISSING" : "ok")}");
            }
            else
            {
                Console.WriteLine("[monogame] quick-chat: Content .mdl files missing; quick-chat disabled");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] FAILED to parse quick-chat data: {ex.Message}");
            _quickChatDb = null;
        }
        _quickChatRenderer = new QuickChatMenuRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

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

        // P218 — the shared procedural asteroid rock mesh. Generated once here
        // (no content-pipeline asset, like Starfield's stars) and re-used for
        // every asteroid instance, scaled per-instance by its real Radius in
        // Draw. See AsteroidRenderer's remarks for why no ShipRenderer-style
        // FBX winding special cases are needed.
        _asteroidRenderer = new AsteroidRenderer(GraphicsDevice);

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
            else if (_qcMenuOpen)
            {
                BackQuickChat();   // back one level, or close if at the top
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
        // itself arrives through Window.TextInput (OnTextInput). Enter is
        // ignored while the quick-chat menu is open (its keys are navigation).
        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        if (!_qcMenuOpen && enter && !_enterWasDown)
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

        // P217 — quick-chat voice macros. R (free in flight) opens the menu;
        // while it is open every shortcut key navigates instead of flying.
        bool qcKey = ks.IsKeyDown(XnaKeys.R);
        if (!_chatActive && qcKey && !_qcRWasDown && !_qcMenuOpen)
            OpenQuickChat();
        _qcRWasDown = qcKey;

        if (_qcMenuOpen)
            NavigateQuickChat(ks);

        // A modal UI consumes the keyboard: chat composition OR the quick-chat
        // menu both neutralise flight input / toggles / autopilot-steering, so
        // navigating or typing never also thrusts/turns the ship.
        bool composing = _chatActive || _qcMenuOpen;

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

        // P222 — suicide (TK_Suicide): edge-triggered like the M/V toggles above
        // (press once per key-down, never per-frame while held). Gated behind the
        // same composing check so typing in chat / navigating the quick-chat menu
        // never triggers it. The server's HandleSuicide re-runs the exact combat-
        // kill path (KillShipEvent, launcher NULL), so a pressed key produces the
        // same KILL_SHIP death a combat death does — no confirmation dialog, no
        // hold-to-fire, no cooldown (native's own TK_Suicide is a plain instant
        // command). Bound to K — free in flight (W/S/Q/E/Arrows/A/D/Shift/R/M/V/
        // T/G/Enter/Back/Escape/Space all taken) and the natural mnemonic for
        // "kill".
        bool kSuicide = ks.IsKeyDown(XnaKeys.K);
        if (!composing && kSuicide && !_kKeyWasDown)
        {
            _session.SendSuicide();
            Console.WriteLine("[monogame] suicide");
        }
        _kKeyWasDown = kSuicide;

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
            RollRight:       ks.IsKeyDown(XnaKeys.D),
            // P220 — afterburner (TK_FireBooster): held, continuous (not
            // edge-triggered), reading either Shift key. Mirrors native's
            // IsTrekKeyDown(TK_FireBooster) sense (WinTrek.cpp:8270-8271);
            // Shift is the recollection default since the native binding
            // table isn't in this checkout (see KEYBINDING_AUDIT_P220.md).
            Afterburner:     ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift),
            // P221 — fire (TK_FireWeapon): held, continuous (not edge-triggered)
            // like afterburner, so a held Space keeps firing (native
            // IsTrekKeyDown sense). Bound to Space — unclaimed by every other
            // binding (W/S/Q/E/Up/Down/Left/Right/A/D/Shift/R/M/V/T/G/Enter/
            // Back/Escape all taken). Absent a confirmed native default VK
            // table in this checkout (see KEYBINDING_AUDIT_P220.md), Space is
            // this port's own reasonable choice — the same justification P220
            // used for Shift→afterburner.
            Fire:            ks.IsKeyDown(XnaKeys.Space));
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
        //   T  → cycle to the next in-view candidate, nearest-to-crosshair first
        //        (ships except self/dead take priority; stations/asteroids only
        //        when no ship is in view). Repeated presses step through every
        //        candidate rather than re-picking the same nearest one.
        //   G  → toggle autopilot on/off toward the selected target.
        if (!composing)
        {
            bool tPress = ks.IsKeyDown(XnaKeys.T);
            if (tPress && !_tKeyWasDown)
            {
                if (TryOwnPose(out var oPos, out var oOrient))
                {
                    var picked = CycleTarget(oPos, oOrient);
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

        // Recompute every ship's render pose from its raw last-known
        // ServerShipUpdate — no smoothing/prediction (see PredictedPose's
        // remarks). Locked against the receive thread's concurrent upserts
        // into world.Ships — see ConnectionSession.Gate's doc comment.
        _poses.Clear();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me)
            {
                // Own ship also drives the P213 engine/ambience mix from its
                // real telemetry.
                if (world.Ships.TryGetValue(me, out var mine) && mine.Latest is { } mineUp)
                {
                    _poses[me] = new Flight.PredictedPose(mineUp.Position, mineUp.Orientation);
                    _sound?.Update(
                        throttle: mineUp.Controls.Throttle,
                        speed: mineUp.Velocity.Length(),
                        cockpitMode: _cameraMode == CameraMode.Cockpit,
                        afterburnerActive: (mineUp.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                        deltaSeconds: (float)gameTime.ElapsedGameTime.TotalSeconds);
                }
            }
            foreach (var (id, ship) in world.Ships)
            {
                if (id == world.MyShipId || ship.Latest is not { } up) continue;
                _poses[id] = new Flight.PredictedPose(up.Position, up.Orientation);
            }
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
                Console.WriteLine("[monogame] audio: countdown-start");
            }

            double elapsed = (Environment.TickCount64 - cdStart) / 1000.0;
            int remaining = (int)Math.Ceiling(Audio.CountdownSound.BriefingCountdownSeconds - elapsed);
            if (remaining != _countdownLastSecond)
            {
                _countdownLastSecond = remaining;
                if (Audio.CountdownSound.TickAssetFor(remaining) is { })
                {
                    _sound?.PlayCountdownSecond(remaining);
                    Console.WriteLine($"[monogame] audio: countdown tick {remaining}");
                }
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
            Console.WriteLine("[monogame] audio: game-over");
        }

        // P217 — receive-side quick-chat voices: play the real voice for any
        // newly-arrived quick-chat (which includes the sender's own server
        // relay echo, so a locally-sent macro is voiced exactly once by this
        // path — never doubled locally at the send site).
        ProcessIncomingQuickChatVoices();

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

                // P218 — asteroids. ClientWorld.Asteroids is NOT sector-filtered
                // (fine for the targeting code, which frustum-projects every
                // object anyway; wrong for a draw loop, which would otherwise try
                // to render rocks from all 13 sectors of the "Combat Demo" map at
                // once — 134 total, per vizfight's own [boot] log). So filter to
                // the local player's OWN sector: ast.ClusterId == the own ship's
                // SectorId (the same cluster concept set from SET_CLUSTER/
                // SHIP_STATUS — ClientWorld.cs:164,180). World access rides the
                // same Gate lock as every other read; the actual draws happen
                // after the lock is released. Asteroids are static (exported
                // once at join, never re-sent), so their pose/radius are real
                // wire data, and each rock is scaled by its true Radius so
                // bigger asteroids actually look bigger.
                var rooks = new List<(SimVec Pos, Orientation Ori, float Radius)>();
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var own))
                    {
                        short mySector = own.SectorId;
                        foreach (var (_, ast) in world.Asteroids)
                        {
                            if (ast.ClusterId != mySector) continue;
                            var o = new Orientation();
                            o.Set(Vec(ast.Forward), Vec(ast.Up));
                            rooks.Add((Vec(ast.Position), o, ast.Radius));
                        }
                    }
                }
                foreach (var (rPos, rOri, rRadius) in rooks)
                {
                    var rXnaPos = PoseConverter.ToXna(rPos);
                    var rRot = PoseConverter.ToXnaRotation(rOri);
                    var rWorld = rRot * Matrix.CreateScale(rRadius) * Matrix.CreateTranslation(rXnaPos - cameraEye);
                    _asteroidRenderer.Draw(rWorld, view, proj);
                }
            }

            // 2D HUD overlay on top of the 3D scene (P211).
            DrawHud();

            // P214 — real chat overlay (log + composing line) on top of the HUD.
            DrawChat();

            // P217 — the quick-chat voice-macro menu, on top of the chat log
            // (itself above the HUD). Only drawn while a menu level is open.
            if (_qcMenuOpen && _qcMenuNode is { } qn && _qcMenuPath.Count > 0)
                _quickChatRenderer.Draw(qn, _qcMenuPath, GraphicsDevice.Viewport);
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
        lock (_session.Gate)
        {
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
                    AfterburnerActive: (up.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                    // P221 — firing readout: true while StateM carries the
                    // group-fire AllWeapons bit (the same bit FlightInput.Translate
                    // ORs in when Space is held). Drives the reticle/FIRING tag.
                    Firing: (up.StateM & Allegiance.Core.Sim.ShipControlState.AllWeapons) != 0);
            }
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
            _session.Connect(port, _playerName, host);
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
        lock (_session.Gate)
        {
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

    // ── P217 — quick-chat voice macros ───────────────────────────────────────

    /// <summary>Open the quick-chat menu at its root (<c>mainMenu</c>). No-op if
    /// the data failed to load.</summary>
    private void OpenQuickChat()
    {
        if (_quickChatDb?.MainMenu is not { } root)
            return;
        _qcMenuOpen = true;
        _qcMenuPath = new List<QuickChatMenu> { root };
        _qcMenuNode = root;
        // Prime the rising-edge set so the R that just opened the menu is not
        // also consumed as a selection on this same frame.
        _qcKeysPrev.Clear();
        foreach (var c in root.Choices)
            if (CharToXnaKey(c.Key) is { } k)
                _qcKeysPrev.Add(k);
        Console.WriteLine("[monogame] quick-chat menu (R closes via ESC; select by shortcut key)");
    }

    /// <summary>Handle one frame of quick-chat menu navigation: shortcut keys
    /// descend into submenus or send leaf commands (rising-edge detected via
    /// <see cref="_qcKeysPrev"/>). Escape is handled in <see cref="Update"/>.</summary>
    private void NavigateQuickChat(KeyboardState ks)
    {
        if (_qcMenuNode is not { } node)
            return;

        foreach (var choice in node.Choices)
        {
            if (CharToXnaKey(choice.Key) is not { } key)
                continue;
            bool down = ks.IsKeyDown(key);
            if (down && !_qcKeysPrev.Contains(key))
            {
                if (choice.Submenu is { } sub)
                {
                    DescendQuickChat(sub);
                }
                else if (choice.Send is { } sa)
                {
                    SendQuickChatLeaf(sa);
                }
                break;   // one selection per frame
            }
        }

        // Remember this frame's down-state for next frame's rising-edge test,
        // computed against the (possibly descended) current menu.
        _qcKeysPrev.Clear();
        if (_qcMenuNode is { } cur)
            foreach (var c in cur.Choices)
                if (CharToXnaKey(c.Key) is { } k && ks.IsKeyDown(k))
                    _qcKeysPrev.Add(k);
    }

    /// <summary>Send a leaf quick-chat command: a real <c>CHATMESSAGE</c> whose
    /// <c>voiceOver</c> is the command's sonic-chat index and whose text is the
    /// spoken caption (the same text the receiving side displays). The voice is
    /// NOT played locally here — the server relays the sender's own echo back
    /// into <see cref="Events.Chats"/>, and
    /// <see cref="ProcessIncomingQuickChatVoices"/> voices that single copy
    /// (this port sends the caption as text, so a quick-chat is distinguishable
    /// from a typed chat on receive by index + caption match). Closes the menu.</summary>
    private void SendQuickChatLeaf(QuickChatSendAction send)
    {
        CloseQuickChat();
        _session.SendChat(send.Entry.Caption, send.Target, voiceOver: (short)send.Entry.Index);
        Console.WriteLine($"[monogame] quick-chat >> #{send.Entry.Index} \"{send.Entry.Caption}\" ({send.Target})");
    }

    private void DescendQuickChat(QuickChatMenu sub)
    {
        _qcMenuPath.Add(sub);
        _qcMenuNode = sub;
    }

    /// <summary>Back out one menu level, or close the menu entirely when already
    /// at the top — never quits the game (same care P214 took with chat's
    /// Escape).</summary>
    private void BackQuickChat()
    {
        if (_qcMenuPath.Count <= 1)
            CloseQuickChat();
        else
        {
            _qcMenuPath.RemoveAt(_qcMenuPath.Count - 1);
            _qcMenuNode = _qcMenuPath[^1];
        }
    }

    private void CloseQuickChat()
    {
        _qcMenuOpen = false;
        _qcMenuNode = null;
        _qcMenuPath.Clear();
        _qcKeysPrev.Clear();
    }

    /// <summary>Map a <c>QuickChatMenuItem.char</c> shortcut (a digit or ASCII
    /// letter, as in the real data) to a MonoGame <c>Keys</c> value. Returns
    /// null for anything unusual (not a digit/letter).</summary>
    private static Keys? CharToXnaKey(char c)
    {
        if (c >= '0' && c <= '9') return Keys.D0 + (c - '0');
        if (c >= 'A' && c <= 'Z') return Keys.A + (c - 'A');
        if (c >= 'a' && c <= 'z') return Keys.A + (c - 'a');   // physical key, not shifted
        return null;
    }

    /// <summary>
    /// P217 receive path — play the real voice for each newly-arrived quick-chat.
    /// Walks <see cref="Events.Chats"/> forward from the last processed message
    /// (a watermark reference; if that message has been evicted from the bounded
    /// 500-entry log, it re-anchors on the newest to avoid a re-play burst).
    /// A chat is voiced only when its <c>voiceOver</c> index resolves to a real
    /// entry whose caption matches the message text — which is what
    /// distinguishes an actual quick-chat (index + spoken caption) from a typed
    /// chat (index 0, arbitrary text that never equals a caption). This same
    /// path voices the sender's own server-echoed macro exactly once.
    /// </summary>
    private void ProcessIncomingQuickChatVoices()
    {
        if (_quickChatDb is null || _sound is null)
            return;
        var chats = _session.World.Events.Chats;
        if (chats.Count == 0)
            return;

        int start = 0;
        if (_lastVoiceProcessed is not null)
        {
            bool found = false;
            for (int i = 0; i < chats.Count; i++)
            {
                if (ReferenceEquals(chats[i], _lastVoiceProcessed))
                {
                    start = i + 1;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // Evicted from the window: anchor on the newest only.
                _lastVoiceProcessed = chats[^1];
                return;
            }
        }

        for (int i = start; i < chats.Count; i++)
        {
            var m = chats[i];
            var e = _quickChatDb.ByVoiceOver(m.VoiceOver);
            if (e is not null && e.SoundStem.Length > 0
                && string.Equals(m.Message, e.Caption, StringComparison.Ordinal))
            {
                _sound.PlayVoice(e.SoundStem);
                Console.WriteLine($"[monogame] audio: quick-chat voice \"{e.SoundStem}\" (\"{m.Message}\")");
            }
            _lastVoiceProcessed = m;
        }
    }

    // ── P212 player-autopilot helpers ─────────────────────────────────────────

    /// <summary>The kind of object an autopilot target can be.</summary>
    private enum TargetKind { Ship, Station, Asteroid, Warp }

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
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is not { } me) return false;
            if (!_session.World.Ships.TryGetValue(me, out var mine) || mine.Latest is not { } up) return false;
            position = up.Position;
            orientation = up.Orientation;
            return true;
        }
    }

    /// <summary>
    /// Soft-lock target candidates (P212, cycling added live 2026-09-14): every
    /// known non-self, non-dead ship, station and asteroid whose projected
    /// screen position lands inside the view frustum, sorted nearest-to-crosshair
    /// first. Candidates are projected through the same view/projection matrices
    /// <see cref="Draw"/> builds for the active camera (the chase camera is
    /// sampled with a zero delta so its turn-smoothing state is not disturbed by
    /// the extra call). No true 3D ray-pick — this cone/nearest-to-crosshair
    /// approximation is the intended POC scope.
    ///
    /// Ships take priority over static scenery: with ~134 asteroids per map, one
    /// is almost always more centered on the crosshair than a moving ship, so a
    /// plain "nearest to crosshair among everything" contest made ship-targeting
    /// nearly unreachable whenever any asteroid shared the view — confirmed live
    /// (a real ship was in-frustum and an asteroid still won). Stations/asteroids
    /// are only included as a fallback when no ship is in view at all.
    /// </summary>
    private List<AutopilotTarget> CollectTargetCandidates(SimVec simPos, Orientation simOrient)
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

        var candidates = new List<(AutopilotTarget Target, float DistSq)>();
        int shipsSeen = 0, shipsInFrustum = 0, stationsSeen = 0, astSeen = 0, warpsSeen = 0;

        void Consider(SimVec p, TargetKind kind, short id, string name)
        {
            var rel = PoseConverter.ToXna(p) - eye;
            var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
            if (clip.W <= 1e-6f) return;                          // behind the camera
            float sx = clip.X / clip.W;
            float sy = clip.Y / clip.W;
            if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return; // outside the FOV frustum
            if (kind == TargetKind.Ship) shipsInFrustum++;
            candidates.Add((new AutopilotTarget(kind, id, name), sx * sx + sy * sy));
        }

        lock (_session.Gate)
        {
            if (world.MyShipId is { } me)
            {
                foreach (var (id, ship) in world.Ships)
                {
                    if (id == me || ship.IsDead || ship.Latest is not { } up) continue;
                    shipsSeen++;
                    string nm = string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName;
                    Consider(up.Position, TargetKind.Ship, id, nm);
                }
            }
            if (shipsInFrustum == 0)
            {
                foreach (var (id, st) in world.Stations)
                {
                    stationsSeen++;
                    Consider(Vec(st.Position), TargetKind.Station, id, st.Name);
                }
                foreach (var (id, ast) in world.Asteroids)
                {
                    astSeen++;
                    Consider(Vec(ast.Position), TargetKind.Asteroid, id, ast.Name);
                }
                foreach (var (id, wp) in world.Warps)
                {
                    warpsSeen++;
                    Consider(Vec(wp.Position), TargetKind.Warp, id, wp.Name);
                }
            }
        }
        Console.WriteLine($"[monogame] target-pick: ships {shipsInFrustum}/{shipsSeen} in view, " +
                          $"{stationsSeen} station(s), {astSeen} asteroid(s), {warpsSeen} warp(s) scanned, mode={_cameraMode}");

        candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        return candidates.ConvertAll(c => c.Target);
    }

    /// <summary>Nearest in-view candidate (see <see cref="CollectTargetCandidates"/>),
    /// or null if nothing qualifies — used for the G-key implicit-pick fallback,
    /// which always wants the closest thing, not a cycle step.</summary>
    private AutopilotTarget? PickNearestTarget(SimVec simPos, Orientation simOrient)
    {
        var candidates = CollectTargetCandidates(simPos, simOrient);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>T-key target selection: steps to the NEXT in-view candidate after
    /// whichever is currently selected (nearest-to-crosshair first, wrapping
    /// around), rather than always re-picking the single nearest one. Without
    /// this, repeated T presses looked "stuck" on the same ship whenever the
    /// player wasn't actively moving the crosshair between presses — confirmed
    /// live. Falls back to the nearest candidate when nothing is currently
    /// selected, or the current selection no longer appears in view.</summary>
    private AutopilotTarget? CycleTarget(SimVec simPos, Orientation simOrient)
    {
        var candidates = CollectTargetCandidates(simPos, simOrient);
        if (candidates.Count == 0) return null;
        if (_autoTarget is { } current)
        {
            int idx = candidates.FindIndex(c => c.Kind == current.Kind && c.Id == current.Id);
            if (idx >= 0)
                return candidates[(idx + 1) % candidates.Count];
        }
        return candidates[0];
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
        lock (_session.Gate)
        {
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
                case TargetKind.Warp:
                    if (world.Warps.TryGetValue(tgt.Id, out var wp))
                    {
                        pos = Vec(wp.Position);
                        return true;
                    }
                    break;
            }
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
        _asteroidRenderer?.Dispose();
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

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
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
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

    private XnaModel _fallbackModel = null!;
    private readonly Dictionary<string, XnaModel> _shipModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XnaModel> _missileModels = new(StringComparer.Ordinal);
    private Rendering.HullModelResolver _hullResolver = null!;
    private ShipRenderer _shipRenderer = null!;
    private AsteroidRenderer[] _asteroidRenderers = null!;
    private Dictionary<RockTextureFactory.AsteroidResourceType, Texture2D> _asteroidTextures = null!;
    // P252 — per-asteroid client-side spin state, keyed by AsteroidId so each
    // rock rotates about its own wire axis at its own wire rate (real
    // m2xspiderw data gives every rock a distinct rate — never lockstep).
    private readonly Dictionary<short, AsteroidSpin> _asteroidSpins = new();
    private WarpRenderer _warpRenderer = null!;
    // P289 — the shared procedural station mesh (the P218/P227 pattern, hub+
    // ring silhouette). Built once here (no content-pipeline asset) and drawn
    // per-instance, scaled by each real station's DataStationTypeIGC.Radius and
    // tinted by side. See StationRenderer.
    private StationRenderer _stationRenderer = null!;
    // P294 — the translucent under-construction visual reusing the station mesh.
    private BuildingEffectRenderer _buildingEffectRenderer = null!;
    private ProjectileRenderer _projectileRenderer = null!;
    private MissileRenderer _missileRenderer = null!;
    // P290 — the shared REAL probe model mesh (the same runtime native-.mdl
    // story as the missile renderer; see ProbeModelSource). Built once here
    // from a real probe .mdl parsed at runtime and drawn per deployed probe at
    // its real position. Unlike the missile mesh, the probe mesh winds for
    // MonoGame's DEFAULT back-face culling (the P290 empirical finding — see
    // ProbeRenderer), so no special flip is applied.
    private ProbeRenderer _probeRenderer = null!;
    private MineRenderer _mineRenderer = null!;
    private TreasureRenderer _treasureRenderer = null!;
    private ChaffRenderer _chaffRenderer = null!;

    // P235 — missile exhaust trail. The pure particle pool (no MonoGame on its
    // surface) plus the camera-facing billboard renderer. _trailNowSec is the
    // pool's monotonic clock, advanced by real elapsed time each frame in Draw
    // (feeds both the per-missile velocity estimate and particle aging).
    private readonly MissileTrailParticles _trailParticles = new();
    private readonly ITrailRandom _trailRandom = new RandomTrailSource();
    private MissileTrailRenderer _trailRenderer = null!;
    private double _trailNowSec;

    // P296 — warp-transit teleport spark spray (native AddTeleportParticles).
    // The pure particle pool + camera-facing billboard renderer; the burst fires
    // once per transit reusing WarpTransitFlash's one-shot edge (see DrawHud).
    private readonly Rendering.WarpTransitParticles _warpParticles = new();
    private Rendering.WarpTransitParticleRenderer _warpParticleRenderer = null!;
    // Transit-instant ship state (position/velocity/axes) buffered in DrawHud
    // when WarpTransitFlash.JustTransited arms, consumed on the next frame's 3D
    // scene render (DrawHud runs after the 3D pass, so the burst is one frame
    // late — imperceptible, and structurally exactly-once per transit).
    private (Allegiance.Core.Collision.Vector3 Position, Allegiance.Core.Collision.Vector3 Velocity,
        Allegiance.Core.Collision.Vector3 Forward, Allegiance.Core.Collision.Vector3 Right)? _pendingTeleportBurst;

    // P297 — the continuous damage-debris smoke stream (native AddDamageParticles,
    // thinggeo.cpp:699-755). The pure shared pool + camera-facing billboard
    // renderer; each frame's 3D pass emits one puff per damage vent per damaged
    // visible ship (driven by each ship's real hull fraction via
    // Flight.DamageDebrisTracker), then ages + draws them. See the emit block
    // in Draw for how the per-ship emission inputs are gathered under the Gate.
    private readonly Rendering.DamageDebrisParticles _damageDebris = new();
    private Rendering.DamageDebrisRenderer _damageDebrisRenderer = null!;
    // Per-frame emission inputs for every currently-damaged visible ship,
    // rebuilt under the Gate lock in the same pass that gathers _poses.
    private readonly Dictionary<short, DamageEmitter> _damageEmitters = new();

    // P263 — the real explosion VFX: decoded sprite artwork + the billboard
    // renderer that plays each blast with native size/duration/fade.
    private ExplosionAssets _explosionAssets = null!;
    private ExplosionRenderer _explosionRenderer = null!;

    // P231 — projectile bolt sizing/tint. A projectile carries no Width and no
    // per-type colour on the wire (and the client has no static core), so these
    // are the port's defaults: a fixed thin width, a length that grows with
    // speed (faster shots read as longer streaks, clamped to stay sane), and a
    // single amber tracer colour standing in for every projectile type (real
    // per-type DataProjectileTypeIGC.Color is unreachable client-side this
    // stage — see the P231 summary). Visibility radius ≈ the camera far plane,
    // the "sector filter" analog for a wire that carries no projectile sector.
    private const float ProjectileWidth = 0.35f;
    // P248 (2026-09-17): shrunk from 0.12/32/480 — Domingo live, after the
    // sideways-orientation fix above: "the line looks as long as a missile,
    // make it shorter... just like the missiles". A real missile's measured
    // extent (mis05.mdl, P233) is 1.73x1.73x11.35 units; these three constants
    // are retuned so a typical ~600u/s Gatling shot (the reference figure
    // cited in P239/P243) lands at 12 units — close to that missile length —
    // instead of the old 72u.
    private const float BoltLengthPerUnitSeconds = 0.02f;
    private const float MinBoltLength = 4f;
    private const float MaxBoltLength = 16f;
    private const float ProjectileVisibilityRadiusSqr = 5900f * 5900f;
    // P292 — chaff visibility radius ≈ the camera far plane, the "sector filter"
    // analog for a wire (CREATE_CHAFF) that carries no chaff cluster id (same
    // reasoning and value as the projectile filter above). A chaff further than
    // this from the local ship is culled.
    private const float ChaffVisibilityRadiusSqr = 5900f * 5900f;
    // P239 (clearance) + P243 (width). Near-camera cull gap: a bolt whose solid
    // box is inside (or within this distance of) the active camera eye is
    // skipped (ProjectileCameraCull). P239 set this LARGE (60) because the bolt
    // was then 8 units wide: a bolt whose 8-wide face got within ~3 units of the
    // eye subtended ~100° of the 75° FOV and flooded the view (observed live).
    // P243 thinned the bolt to 0.35 units (ship-relative: a fighter's half-extent
    // is ~1.94, so the old bolt was wider than the ship firing it), which
    // collapses that flood risk — a 0.35-wide face 3 units ahead now subtends
    // only ~6.7°, and at the 15-unit gap below ~1.3°. So the clearance shrinks
    // proportionally to keep the guarantee (bolt never renders with its surface
    // uncomfortably close to / coincident with the eye) while letting own-forward
    // shots become visible sooner. It is deliberately NOT removed entirely: a
    // projectile spawns at the ship's centre (no muzzle offset, Weapon.cs:230),
    // which can momentarily coincide with the 14-unit-forward cockpit eye.
    private const float ProjectileNearCamClearance = 15f;
    private static readonly Vector3 ProjectileTint = new(1.0f, 0.84f, 0.32f);

    // P234 — missile surface tint. The client has no static core to read
    // per-type DataMissileTypeIGC colouring, and the missile renders solid/un-
    // textured (texture-mapping is out of scope), so a single neutral ordnance
    // grey stands in for every missile type — the same single-default convention
    // ProjectileTint above uses.
    private static readonly Vector3 MissileDiffuseColor = new(0.55f, 0.55f, 0.58f);
    private ChaseCamera _chaseCamera = null!;
    private CockpitCamera _cockpitCamera = null!;
    // P295 — the external-missile view camera (TrackCamera port).
    private MissileCamera _missileCamera = null!;
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
    private StarmapRenderer _starmapRenderer = null!;
    private RadarBracketRenderer _radarRenderer = null!;
    private Rendering.HelpOverlayRenderer _helpOverlayRenderer = null!;
    private Rendering.GameOverScreenRenderer _gameOverScreenRenderer = null!;

    // P213 — real audio. Null until LoadContent runs; inert (silent, non-fatal)
    // if the host has no usable audio device (see SoundManager remarks).
    private Audio.SoundManager? _sound;

    // P304 — background music. The MonoGame wrapper owns the two looping beds
    // (flightmusic / stationmusic) and applies the pure MusicPlanner's
    // directives; the planner holds the toggle/volume/track state and is what
    // the tests + live harness drive. Null until LoadContent; inert when the
    // host has no audio device (see Music remarks). The three edge-trackers
    // give J / + / - clean edge-triggered (never repeat-while-held) bindings.
    private Audio.Music? _music;
    private bool _musicToggleWasDown, _musicVolUpWasDown, _musicVolDownWasDown;

    // P232 — weapon-fire + projectile in-flight ambient sound. The planner is
    // pure (no MonoGame) and computes per-frame commands from the live projectiles
    // for the local listener; Game1 replays its commands against _sound each
    // frame (see UpdateProjectileSounds).
    private readonly Audio.ProjectileSoundPlanner _projectileSoundPlanner = new();
    // P236 — the three missile sound cues (launch / in-flight ambient / explosion),
    // decided by the same pure-planner pattern as P232 (see UpdateMissileSounds).
    private readonly Audio.MissileSoundPlanner _missileSoundPlanner = new();

    // P264 — the incoming-missile warning loop's edge-tracking pure planner (see
    // UpdateMissileWarning).
    private readonly Audio.MissileWarningController _missileWarningController = new();
    // P237 — warp ambient loop (alephloop), decided by the same pure-planner
    // pattern as P232 (see UpdateWarpSounds).
    private readonly Audio.WarpSoundPlanner _warpSoundPlanner = new();
    // P260 — the two probe sound cues (deploy one-shot + own-side scanning
    // ambient loop), decided by the same pure-planner pattern as P236 (see
    // UpdateProbeSounds).
    private readonly Audio.ProbeSoundPlanner _probeSoundPlanner = new();
    // P267 — station exterior ambient loop (shipout), decided by the same
    // pure-planner pattern as the warp ambient (see UpdateStationSounds).
    private readonly Audio.StationSoundPlanner _stationSoundPlanner = new();

    // First-person camera mode (V, "View"). Defaults to Cockpit — native's
    // own WinTrekClient constructor starts in m_cm(cmCockpit)
    // (WinTrek/WinTrek.cpp:2704), so the cockpit is the faithful primary view,
    // not an alternate. V cycles through the modes available to the pilot's
    // current state (CameraCycler): Cockpit → Chase → ExternalTurret →
    // ExternalMissile, with the external-turret view offered only while gunning.
    // Cockpit hides the local hull and rolls the horizon with the ship (see
    // CockpitCamera); the P295 external views are added in Draw below.
    private CameraMode _cameraMode = CameraMode.Cockpit;
    private bool _vKeyWasDown; // edge-detect the V toggle so holding it doesn't re-fire

    // Monotonic render clock (lobby refresh timing, chat timestamps, etc).
    private double _nowSeconds;
    private readonly Dictionary<short, Flight.PredictedPose> _poses = new();
    // P270 — ship ids rendered cloaked this frame (subset of _poses), collected
    // in the same locked pass that builds _poses so the Draw loop can read the
    // cloaked state lock-free and alpha-blend those ships' models.
    private readonly HashSet<short> _cloakedShips = new();
    // P288 — per-ship resolved content model this frame, collected under the
    // same Gate lock as _poses (a different faction/class mesh per ship, or null
    // → bios_scout fallback), so the Draw loop picks models lock-free.
    private readonly Dictionary<short, XnaModel?> _shipModelById = new();

    // P282 — the READY-CHECK screen replaces the old unconditional auto-ready
    // hack: the player toggles their own ready with Y (the server holds the
    // mission at NotStarted until every occupied side is aggregate-ready), the
    // screen shows the live same-side roster of ready states + the per-side
    // TEAM_READY aggregate, and the mission owner can force their side ready
    // with O. _ownReady is the local toggle; the *_WasDown latches edge-detect
    // the toggle / force keys.
    private Rendering.ReadyCheckOverlayRenderer _readyCheckRenderer = null!;
    private bool _ownReady;
    private bool _rcYWasDown, _rcOWasDown;

    // P268 — docked/hangar loadout shop state. When the local ship is docked
    // (ShipEntity.StationId != -1) the client shows this screen instead of
    // flight; launching sends a real BUY_LOADOUT(fLaunch=true) with the chosen
    // hull+parts, and a fallback timer auto-launches so an unattended dock
    // never strands the player.
    private Rendering.ShopCatalog? _shopCatalog;
    private Rendering.ShopModel? _shopModel;
    private Rendering.DockedScreenRenderer _dockedScreenRenderer = null!;
    private bool _dockedActive;
    private bool _launchSent;
    private long _dockSinceMs;
    private bool _dockUpWasDown, _dockDownWasDown, _dockSpaceWasDown, _dockLWasDown, _dockUWasDown;
    // P285 — Builder-target chooser ','/'.' latches (edge-triggered like the
    // other shop keys so a held ','/'.' doesn't repeatedly cycle the target).
    private bool _btPrevWasDown, _btNextWasDown;
    private const long DockFallbackMs = 30_000;

    // P273 — the docked teleport pane (native TeleportPane), a sub-view of the
    // docked screen. T opens it (board a teammate's turret / teleport to a
    // friendly station), B returns to the hangar. It reads the live world to
    // rebuild the destination list each time it is opened.
    private Rendering.TeleportPaneModel? _teleportModel;
    private bool _teleportActive;
    private bool _telUpWasDown, _telDownWasDown, _telSpaceWasDown, _telTWasDown, _telBWasDown;

    // P216 — mission announcer-cue bookkeeping. _countdownStartCuePlayed gates
    // the single countdown-start callout; _countdownLastSecond mirrors native's
    // m_nLastCountdown change-detection (WinTrek.cpp:11150) so each second's
    // per-second tick plays exactly once, not every frame it happens to still be
    // true; _gameOverCuePlayed fires the game-over callout exactly once on the
    // real Stage → Over transition.
    private bool _countdownStartCuePlayed;
    private int _countdownLastSecond;
    private bool _gameOverCuePlayed;

    // P271 — mission-briefing popup. _briefingRenderer draws the dismissible
    // pre-launch briefing panel; _briefingDismissed is the one-shot latch for
    // a manual Enter/Esc dismissal. The popup otherwise auto-closes when the
    // briefing countdown expires (mission goes live), mirroring native's
    // MissionBriefPopup closing as the stage leaves STAGE_STARTING.
    private Rendering.MissionBriefingRenderer _briefingRenderer = null!;
    private bool _briefingDismissed;

    // P281 — the post-connect SIDE-SELECT screen. The server defers a joining
    // peer's spawn to a side-select step (SIDE_ROSTER(211), then it waits for
    // FM_C_POSITIONREQ(39)); until our ship is seated (SET_CLUSTER sets
    // World.MyShipId) the client shows this picker instead of flying.
    // _sideSelectIndex is the highlighted roster row; the *_WasDown latches
    // edge-detect the navigate/confirm keys.
    private Rendering.SideSelectOverlayRenderer _sideSelectRenderer = null!;
    private int _sideSelectIndex;
    private bool _ssUpWasDown, _ssDownWasDown, _ssEnterWasDown, _ssSpaceWasDown, _ssRWasDown;

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
    // P230 — the per-frame lock-target box descriptor computed in Draw (projected
    // from the world's lock target) and consumed by DrawHud; null when there is no
    // lock target or it is out of view.
    private LockBracketView? _lockBracket;
    // P266 — the per-frame lead-indicator descriptor computed in Draw (projected
    // from the world's lead point + target) and consumed by DrawHud; null when
    // there is no lead granted or it is out of view.
    private LeadIndicatorView? _leadIndicator;
    private bool _tKeyWasDown; // edge-detect T so holding it doesn't re-fire
    private bool _gKeyWasDown; // edge-detect G so holding it doesn't re-fire
    private bool _kKeyWasDown; // edge-detect K (suicide) so holding it doesn't re-fire
    private bool _cKeyWasDown; // edge-detect C (chaff drop) so holding it doesn't re-fire
    private bool _nKeyWasDown; // edge-detect N (mine/probe drop) so holding it doesn't re-fire

    // P249 — whole-mission starmap overlay. Toggled with Tab (free in flight —
    // see the UpdateFlying toggle for the full key-claim accounting). While
    // visible, DrawStarmap() renders the sector-map pane over the 3D scene; the
    // game keeps simulating underneath (an integrated HUD pane like native's,
    // not a paused full-screen mode).
    private bool _starmapVisible;
    private bool _tabKeyWasDown; // edge-detect Tab so holding it doesn't re-fire
    // P272 — starmap cluster selection. While the starmap is open, ',' / '.'
    // cycle the selected cluster (info pane) back/forward; keyboard is chosen
    // over mouse hover because there is no map-cursor picking wired into the
    // MonoGame flight controls (the mouse is the M virtual-joystick steering
    // axis, not a picker) — see the UpdateFlying block for the key-choice
    // rationale. _selectedSectorId is null until the pilot picks one.
    private short? _selectedSectorId;
    private bool _selPrevKeyWasDown; // edge-detect ',' so holding it doesn't re-fire
    private bool _selNextKeyWasDown; // edge-detect '.' so holding it doesn't re-fire
    private Rendering.SectorInfoPaneRenderer _sectorInfoPaneRenderer = null!;

    // P257 — H keybinding-reference overlay. Toggled with H (rebound from F1
    // at P258.1 — F1 is intercepted by firmware/OS shortcuts on some
    // keyboards before it reaches the game), independent of every other
    // overlay flag so closing it resumes the prior view (starmap / HUD /
    // chat) exactly as it was.
    private bool _helpVisible;
    private bool _helpKeyWasDown; // edge-detect H so holding it doesn't re-fire
    private Rendering.ScoreboardOverlayRenderer _scoreboardRenderer = null!;
    private bool _scoreboardVisible;
    private bool _scoreboardKeyWasDown; // edge-detect B so holding it doesn't re-fire
    // P275 — the Pilot Info overlay (your own live stats; P toggles). Same
    // independent-flag / edge-detect discipline as the scoreboard/help toggles.
    private Rendering.PilotInfoOverlayRenderer _pilotInfoRenderer = null!;
    private bool _pilotInfoVisible;
    private bool _pilotInfoKeyWasDown; // edge-detect P so holding it doesn't re-fire
    private long _lastMissileSendMs = 0; // P226 throttle: re-arm so the first hold fires immediately.
    // Was long.MinValue, but Environment.TickCount64 - long.MinValue overflows a
    // long (unchecked arithmetic wraps to an unpredictable/negative value), so the
    // >= 250 throttle check silently failed on every frame -- held Left Ctrl never
    // actually fired a missile from the real client (found by P236 instrumentation,
    // docs/MISSILE_AUDIO_P236.md). TickCount64 is always a large positive value
    // (ms since system start), so nowMs - 0 safely exceeds 250 on the first check.

    // P225 — single-weapon selection (client-local, the same pattern as
    // _cameraMode / _mouseFlightEnabled: never sent to the server directly,
    // folded into the fire stateM each frame). _groupFire=true is the default,
    // reproducing P221's all-weapons behaviour so nothing regresses; turning it
    // off (TK_ToggleGroupFire, this port's ` key) makes Fire OR
    // OneWeapon | the selected fixed mount instead. _selectedMount is the
    // fixed-mount id used in OneWeapon mode, stepped with `[`/`]`
    // (TK_PrevWeapon / TK_NextWeapon) through FixedMountCount().
    private bool _groupFire = true;
    private int _selectedMount;
    private bool _gfireKeyWasDown; // edge-detect the ` toggle so holding it doesn't re-fire
    private bool _prevWKeyWasDown; // edge-detect [ so holding it doesn't re-fire
    private bool _nextWKeyWasDown; // edge-detect ] so holding it doesn't re-fire

    // P228 — combat damage flash. Pure per-frame detector + timer (no MonoGame on
    // the surface) fed only the LOCAL ship's hull fraction; tracks the previous
    // frame's value internally so it can detect "just took damage" as a hull
    // decrease, and returns whether the HUD should flash now. Gated to the local
    // ship by DrawHud (world.MyShipId), matching native's pShip == GetShip()
    // gate (trekigc.cpp:4281) — damage to any other ship never reaches it.
    private readonly Flight.DamageFlashTracker _flashTracker = new();

    // P245 — warp-transit flash tracker: fires the brief blackscreen pulse when
    // the local ship's SectorId changes (the one-shot SET_CLUSTER the server
    // sends per transit). Fed and read in DrawHud, gated to MyShipId like the
    // damage tracker above.
    private readonly Flight.WarpTransitFlash _warpFlash = new();

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

        /// <summary>P306 — when non-null, this launch is a HEADLESS video
        /// capture: connect to the given host:port, render the whole fleet
        /// from an automatic overview camera into an offscreen
        /// <see cref="RenderTarget2D"/>, save a real PNG frame sequence
        /// (P207 offscreen-render recipe), log real render FPS, and exit
        /// after <see cref="CaptureSpec.DurationSeconds"/>. Skips the
        /// side-select / ready-check screens automatically.</summary>
        public CaptureSpec? Capture;
    }

    /// <summary>P306 capture-launch parameters (see
    /// <see cref="LaunchOptions.Capture"/>). <c>MaxFps</c> bounds the PNG
    /// write rate so disk IO does not perturb the real render-FPS
    /// measurement; when the renderer drops below it every frame is still
    /// captured (an honest slideshow), and each written frame's wall
    /// timestamp is recorded in <c>frames.tsv</c> so the encoder can
    /// reproduce the real cadence.</summary>
    public sealed class CaptureSpec
    {
        public string Host = "127.0.0.1";
        public ushort Port;
        public string PlayerName = "capture";
        public double DurationSeconds = 60;
        public string OutDir = ".";
        public double MaxFps = 30;
        public int MaxFrames = 20000;
    }

    /// <summary>The top-level states of the client. Lobby is the default
    /// entry (server browser); Connecting is transient while the wire handshake
    /// runs; SideSelect (P281) is the post-connect side-choice screen between
    /// logging on and being seated; ReadyCheck (P282) is the pre-flight ready
    /// screen between seating and the mission actually starting; Flying is the
    /// joined-in-flight mode (existing behaviour).</summary>
    private enum ClientState { Lobby, Connecting, SideSelect, ReadyCheck, Flying }

    private ClientState _state;

    private readonly string _lobbyUrl;
    private string? _connectHost;
    private ushort _connectPort;
    private bool _returnToLobbyOnFailure;   // join-from-lobby failed → revert, don't exit

    // Server-browser (lobby) state.
    private readonly HttpClient _http;
    private readonly List<ServerListing> _servers = new();

    // P274 — lobby text filter / find (native FilterDialogPopup's game-name
    // substring edit box, gamescreen.cpp:371/510, + FindDialogPopup's jump-to-
    // name). The visible list is the _servers positions that pass the filter;
    // _selectedIndex is an index INTO _visibleRows, so Enter joins the real
    // listing that sits under the highlighted filtered row. F toggles the mode
    // (mnemonic: Find, the native FindDialogPopup); typing narrows live; Esc
    // clears it (see Update's Escape chain); Backspace deletes the last char.
    private bool _filterActive;
    private readonly StringBuilder _filterBuffer = new();
    private readonly List<int> _visibleRows = new();   // indices into _servers
    private const int MaxFilterLen = 64;
    private bool _fWasDown;                            // edge-detect the F toggle

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

    // P306 — headless video-capture state (see LaunchOptions.Capture).
    private const int CapSettleGraceMs = 2500;   // wait after entering Flying so every bot is known before recording
    private readonly CaptureSpec? _captureSpec;
    private RenderTarget2D? _capTarget;
    private bool _capActive;
    private bool _capStarted;
    private System.Diagnostics.Stopwatch _capClock = new();
    private double _capLastWrite = -1e9;
    private int _capFrame;            // PNG frames written
    private int _capRenderFrames;     // render frames counted (loop throughput)
    private readonly List<double> _capWriteTimes = new();
    private string _capDir = "";
    private bool _capSideRequested;   // capture: auto side-0 request sent
    private bool _capReadySent;       // capture: auto PLAYER_READY sent
    private long _capReadyWallMs;     // capture: wall-clock when we auto-readied (grace to Flying)
    private bool _capFlyForced;       // capture: forced the ReadyCheck→Flying handoff
    private bool _capFlyMarked;       // capture: first Flying-draw marked for the settle grace
    private long _capFlyWallMs;       // capture: wall-clock of that first Flying draw
    private CapturePngSaver? _capSaver;   // background PNG writer (keeps encode off the render thread)
    private Color[]? _capPixels;          // reusable GPU-readback scratch buffer

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
        _captureSpec = opts.Capture;
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
            // P288 — the per-ship/per-civ model catalog. The fallback is the
            // original single Bios Scout model; every P287 ship asset is loaded
            // alongside it so the resolver can pick real per-faction geometry.
            _fallbackModel = Content.Load<XnaModel>("Models/" + Rendering.HullModelResolver.FallbackAsset);
            foreach (var asset in Rendering.HullModelResolver.ShipAssets)
            {
                try
                {
                    _shipModels[asset] = Content.Load<XnaModel>("Models/" + asset);
                }
                catch (Exception inner)
                {
                    Console.WriteLine($"[monogame] ship model '{asset}' unavailable: {inner.Message}");
                }
            }
            _hullResolver = new Rendering.HullModelResolver();
            _shipRenderer = new ShipRenderer(GraphicsDevice);

            // P306 — headless capture: an offscreen target the WHOLE frame is
            // rendered into (the P207 offscreen-render recipe — never the
            // on-screen backbuffer, which is unreliable/black on Wayland),
            // same 1280x720 as the backbuffer so the viewport/aspect maths are
            // identical whether drawing on-screen or into the target.
            if (_captureSpec is { } cap)
            {
                _capDir = cap.OutDir;
                Directory.CreateDirectory(_capDir);
                _capTarget = new RenderTarget2D(GraphicsDevice, 1280, 720,
                    false, SurfaceFormat.Color, DepthFormat.Depth24);
                _capSaver = new CapturePngSaver(1280, 720);
                _capPixels = new Color[1280 * 720];
                Console.WriteLine($"[capture] offscreen capture target ready -> {_capDir}");
            }
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

        // P295 — the external-missile view camera. Same FOV/near/far family as
        // the chase camera so switching views keeps perceived scale; the framing
        // factor backs the eye off proportionally to the missile↔ship separation
        // so a freshly-fired missile and the ship it left stay in frame together.
        _missileCamera = new MissileCamera(nearPlane: 10f, farPlane: 6000f,
            fieldOfViewDegrees: 75f, framingFactor: 0.35f);

        // 2D HUD plumbing (P211): a SpriteBatch, a 1x1 white pixel for
        // procedurally-tinted fills/lines, and the two baked spritefonts.
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _whitePixel = new Texture2D(GraphicsDevice, 1, 1);
        _whitePixel.SetData(new[] { Color.White });
        _hudSmall = Content.Load<SpriteFont>("HUD");
        _hudBig = Content.Load<SpriteFont>("HUDLarge");
        // P241 — decode the six real original-game *Gaugebmp.mdl gauges from the
        // artwork tree at startup (same runtime .mdl convention as the missile
        // model). Graceful: any missing gauge falls back to procedural bars.
        var gauges = GaugeTextures.Load(GraphicsDevice, GaugeTextureSource.ResolveArtworkDir());
        _hudRenderer = new HudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, gauges);

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

        // P249 — whole-mission starmap overlay pane (same SpriteBatch/font
        // plumbing as the HUD and chat overlays).
        _starmapRenderer = new StarmapRenderer(_spriteBatch, _whitePixel, _hudSmall);
        // P272 — its per-cluster info pane (mirrors native's separate
        // SectorInfoPane class, sectormap.cpp:139).
        _sectorInfoPaneRenderer = new SectorInfoPaneRenderer(_spriteBatch, _whitePixel, _hudSmall);

        // P257 — H keybinding-reference overlay panel.
        _helpOverlayRenderer = new Rendering.HelpOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        _scoreboardRenderer = new Rendering.ScoreboardOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        // P275 — Pilot Info overlay (your own live stats), same panel conventions.
        _pilotInfoRenderer = new Rendering.PilotInfoOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P268 — the docked/hangar loadout shop. Loads the real static core the
        // server boots (PCore001.igc under the artwork dir) purely as the shop's
        // display catalogue (hull/part names, prices, categories); the client
        // otherwise has no static core (MissileModelSource). Graceful: a missing
        // core yields an empty catalogue, so the docked screen still renders the
        // current ship and launch/undock stay usable.
        _shopCatalog = Rendering.ShopCatalog.TryLoad(Rendering.GaugeTextureSource.ResolveArtworkDir());
        Console.WriteLine($"[monogame] shop catalogue: {(_shopCatalog is null ? "UNAVAILABLE (core not found)" : $"{_shopCatalog.Hulls.Count} hulls, {_shopCatalog.Parts.Count} parts")}");
        _dockedScreenRenderer = new Rendering.DockedScreenRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P262 — game-over result screen: the large centred panel announced at
        // mission end (winner + real wire reason). Built only on demand in Draw;
        // nothing here depends on mission state.
        _gameOverScreenRenderer = new Rendering.GameOverScreenRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P271 — mission-briefing popup (pre-launch). Same translucent-panel
        // conventions as the other overlays (own SpriteBatch session, teal
        // border, pure-ASCII strings via ScoreboardLayout.AsciiSafe).
        _briefingRenderer = new Rendering.MissionBriefingRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P281 — the post-connect side-select screen (between connecting and
        // flying). Same translucent-panel conventions as the other overlays.
        _sideSelectRenderer = new Rendering.SideSelectOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        _readyCheckRenderer = new Rendering.ReadyCheckOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P250 — local current-sector tactical radar: nearby ships/stations/
        // warps as on-screen or edge-clamped icons (same SpriteBatch plumbing).
        _radarRenderer = new RadarBracketRenderer(_spriteBatch, _whitePixel);

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

        // P218 — the asteroid rock meshes. P286 upgraded this from one fully
        // procedural mesh to FOUR shared variants: the three Meshy-imported
        // asteroid meshes (drgrock/rrocket/tacrock, loaded through the content
        // pipeline exactly like the ship model and normalized to a RockMesh by
        // ImportedRockMeshLoader) plus the original procedural RockMeshFactory
        // rock as a fallback. Each is built once here and re-used for every
        // asteroid instance, scaled per-instance by its real Radius in Draw.
        // A per-type diffuse cache (built lazily in Draw) tints each asteroid
        // by its real AabmCapabilities, so rocks vary by both shape and colour.
        _asteroidRenderers = new AsteroidRenderer[4];
        string[] meshAssets = ["Models/drgrock", "Models/rrocket", "Models/tacrock"];
        for (int i = 0; i < meshAssets.Length; i++)
        {
            RockMesh imported;
            try
            {
                imported = ImportedRockMeshLoader.FromModel(Content.Load<XnaModel>(meshAssets[i]));
                Console.WriteLine($"[monogame] asteroid mesh '{meshAssets[i]}': {imported.VertexCount} verts, {imported.TriangleCount} tris");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] FAILED to import asteroid mesh '{meshAssets[i]}': {ex.Message}; using procedural fallback");
                imported = RockMeshFactory.Generate();
            }
            _asteroidRenderers[i] = new AsteroidRenderer(GraphicsDevice, imported);
        }
        _asteroidRenderers[3] = new AsteroidRenderer(GraphicsDevice);   // procedural fallback variant
        _asteroidTextures = new Dictionary<RockTextureFactory.AsteroidResourceType, Texture2D>();

        // P227 — the shared procedural warp funnel mesh (the P218 pattern,
        // different shape). Generated once here (no content-pipeline asset)
        // and drawn per-instance, scaled by each warp's real Radius and
        // constantly swirling around its own forward axis. See WarpRenderer.
        _warpRenderer = new WarpRenderer(GraphicsDevice);

        // P289 — the shared procedural station mesh (the P218/P227 pattern,
        // hub + equatorial ring silhouette). Generated once here (no
        // content-pipeline asset) and drawn per-instance, scaled by each real
        // station's DataStationTypeIGC.Radius and tinted by side. See
        // StationRenderer.
        _stationRenderer = new StationRenderer(GraphicsDevice);

        // P294 — the shared under-construction visual, reusing the same
        // procedural station mesh StationRenderer draws, rendered translucent +
        // progress-scaled so it reads as a build-in-progress (see
        // BuildingEffectRenderer).
        _buildingEffectRenderer = new BuildingEffectRenderer(GraphicsDevice);

        // P231 — the shared procedural projectile "bolt" mesh (the P218/P227
        // pattern). Generated once here (no content-pipeline asset) and drawn
        // per-instance: rotated to the projectile's travel direction, scaled to
        // its streak length/width, positioned at the real extrapolated position.
        _projectileRenderer = new ProjectileRenderer(GraphicsDevice);

        // P234 — the shared REAL missile model mesh (the P218/P227 "generated or
        // loaded once, drawn per instance" pattern, but the geometry comes from
        // P233's parsed .mdl rather than a procedural generator). Loaded once
        // here from the artwork path; see MissileModelSource for the single-model
        // scope (this stage renders one missile type's real mesh). The renderer
        // applies the P234 winding fix internally (CullClockwiseFace for the
        // DirectX-authored mesh — verified: 0 interior holes vs 20 with the
        // default cull, see docs/MISSILE_RENDERING_P234.md).
        _missileRenderer = new MissileRenderer(
            GraphicsDevice, MissileModelSource.LoadDemoModel().Meshes, MissileDiffuseColor);

        // P290 — the shared REAL probe model mesh (the P234/P288 pattern: the
        // geometry comes from a real probe .mdl parsed at runtime, NOT a
        // content-pipeline asset — P287 imported only ships/missiles, no probe
        // art). Loaded once here from the artwork path; see ProbeModelSource
        // for the single-model census scope (PCore001 has 17 distinct probe
        // ModelNames, all real .mdl files present; the renderer uses the
        // canonical utl02 demo shared by every deployed probe, the graceful
        // single-fallback convention). Unlike the missile mesh, the probe mesh
        // winds for MonoGame's DEFAULT back-face culling (the P290 empirical
        // finding), so the renderer applies no winding flip.
        _probeRenderer = new ProbeRenderer(
            GraphicsDevice, ProbeModelSource.LoadDemoModel().Meshes);

        // P291 — the shared REAL mine sprite + decal-field renderer (the P290
        // runtime-native-asset pattern, applied to the mine's real TEXTURE
        // representation). The mine census disproved a probe-style mesh: no mine
        // type in the corpus has a ModelName, and native renders mines as a
        // MineFieldGeo decal cloud of the fxmine texture (see MineModelSource /
        // MineFieldGeometry). Loaded once here from the artwork path.
        _mineRenderer = new MineRenderer(GraphicsDevice, Rendering.MineModelSource.LoadTexture());

        // P293 — the shared procedural gold-gem treasure placeholder (no art
        // asset to load: DataTreasureIGC carries no ModelName and native derives
        // a treasure's visual from its code + a runtime-decided model we lack —
        // see TreasureMeshFactory). Generated here once and shared by every
        // treasure, scaled by the native pickup radius 5.0 in the draw loop.
        _treasureRenderer = new TreasureRenderer(GraphicsDevice);

        // P292 — the shared REAL chaff sprites + billboard renderer (the P291
        // runtime-native-asset pattern, applied to the chaff's single-decal
        // representation). The chaff census disproved a mesh: no chaff type in
        // the corpus has a ModelName, and native CchaffIGC loads ONE LoadDecal of
        // the chaff type's texture (chaffigc.cpp:42-46) — a flat camera-facing
        // sprite, not a mine-style decal cloud. The two real textures are
        // fxminebmp / cc_cmtexbmp; both are preloaded here and picked per chaff
        // by the type's TextureName (see ChaffModelSource / ChaffRenderer).
        var chaffTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        foreach (var stem in Rendering.ChaffModelSource.RealStems)
        {
            try
            {
                var mdlt = Rendering.ChaffModelSource.LoadTexture(stem);
                var tx = new Texture2D(GraphicsDevice, mdlt.Width, mdlt.Height);
                tx.SetData(MdlTextureDecoder.ToPremultipliedRgba(mdlt));
                chaffTextures[stem] = tx;
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[monogame] chaff texture '{stem}' unavailable: {inner.Message}");
            }
        }
        _chaffRenderer = new ChaffRenderer(GraphicsDevice, chaffTextures);

        // P288 — the three P287-imported missile content models (mis05/mis400/
        // artifact). Loaded alongside the native-.mdl demo renderer above; the
        // per-type lookup picks one of these when the fired missile's type maps
        // to an imported model, else falls back to the native demo renderer.
        foreach (var asset in Rendering.HullModelResolver.MissileAssets)
        {
            try
            {
                _missileModels[asset] = Content.Load<XnaModel>("Models/" + asset);
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[monogame] missile model '{asset}' unavailable: {inner.Message}");
            }
        }

        // P235 — the missile exhaust-trail billboard renderer (shared generated
        // soft-sprite + additive effect; per-frame vertex data is rebuilt in
        // Draw, so no persistent buffers are held here).
        _trailRenderer = new MissileTrailRenderer(GraphicsDevice);

        // P296 — the warp-transit teleport-spark billboard renderer (native
        // hot-blue additive glow; same generated-soft-sprite convention).
        _warpParticleRenderer = new WarpTransitParticleRenderer(GraphicsDevice);
        _damageDebrisRenderer = new DamageDebrisRenderer(GraphicsDevice);

        // P263 — the real explosion sprite artwork (decoded from the .mdl
        // strips at runtime) + the explosion billboard renderer that plays each
        // blast with native size/duration/fade.
        _explosionAssets = Rendering.ExplosionAssets.Load(GraphicsDevice, Rendering.ExplosionAssets.ResolveArtworkDir());
        _explosionRenderer = new ExplosionRenderer(GraphicsDevice, _explosionAssets);

        // P213 — real audio. Loaded after the render content so a sound failure
        // (no audio device) never blocks the visuals; SoundManager degrades
        // gracefully to silent rather than throwing.
        _sound = new Audio.SoundManager(Content);
        _sound.Load();

        // P304 — background music beds. Loaded after the render content like the
        // SFX manager, and equally graceful: no audio device leaves it inert
        // (silent) rather than blocking the visuals. Tracks are real-asset-first,
        // placeholder-generated-fallback (see Audio.Music remarks).
        _music = new Audio.Music(Content);
        _music.Load();
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
            if (IsBriefingVisible(out _))
            {
                DismissBriefing();   // P271 — Esc closes the briefing popup, not the game
            }
            else if (_chatActive)
            {
                _chatActive = false;
                _chatBuffer.Clear();
            }
            else if (_qcMenuOpen)
            {
                BackQuickChat();   // back one level, or close if at the top
            }
            else if (_state == ClientState.Lobby && _filterActive)
            {
                ClearFilter();     // P274 — Esc clears the filter, never quits while filtering
            }
            else
            {
                Exit();
            }
        }
        _escWasDown = esc;

        // P257 — H keybinding-reference overlay. Edge-triggered like the other
        // toggles (holding it never re-fires), available in every state (lobby
        // and flight — the panel lists both flight and lobby bindings), and
        // gated behind chat/menu composition so typing never flips it. It is an
        // independent flag: toggling it on/off never disturbs the starmap, HUD
        // or chat-log state, which all resume unchanged when it closes.
        // Rebound from F1 (P258.1, live 2026-09-18): F1 is intercepted by
        // firmware/OS shortcuts on some keyboards before it ever reaches the
        // game — confirmed live, Domingo's own hardware never saw the keydown
        // at all. H (mnemonic: Help) is unclaimed and a normal key every
        // keyboard delivers.
        bool helpKey = ks.IsKeyDown(XnaKeys.H);
        if (helpKey && !_helpKeyWasDown && !_chatActive && !_qcMenuOpen)
        {
            _helpVisible = !_helpVisible;
            Console.WriteLine($"[monogame] help {( _helpVisible ? "ON" : "OFF" )}");
        }
        _helpKeyWasDown = helpKey;

        // P265 — B scoreboard toggle (mnemonic: BoaRd / scoreBoaRd). B is an
        // unclaimed single letter (grep-verified, P257 KeyBindingTable: the only
        // letters in use are A C D E G H K M N Q R S T V W + modifiers). Edge-
        // triggered and composing-gated like the H help toggle, independent of
        // the starmap/help flags so the prior view resumes on close.
        bool scoreKey = ks.IsKeyDown(XnaKeys.B);
        if (scoreKey && !_scoreboardKeyWasDown && !_chatActive && !_qcMenuOpen)
        {
            _scoreboardVisible = !_scoreboardVisible;
            Console.WriteLine($"[monogame] scoreboard {( _scoreboardVisible ? "ON" : "OFF" )}");
        }
        _scoreboardKeyWasDown = scoreKey;

        // P275 — P Pilot Info toggle (mnemonic: Pilot). P is an unclaimed single
        // letter (grep-verified, P257 KeyBindingTable: no P binding anywhere in
        // Game1; in-use singles are A C D E F G H K L M N O Q R S T U V W + B).
        // Edge-triggered and composing-gated like the H/B toggles, independent of
        // the scoreboard/starmap/help flags so the prior view resumes on close.
        bool pilotKey = ks.IsKeyDown(XnaKeys.P);
        if (pilotKey && !_pilotInfoKeyWasDown && !_chatActive && !_qcMenuOpen)
        {
            _pilotInfoVisible = !_pilotInfoVisible;
            Console.WriteLine($"[monogame] pilotinfo {( _pilotInfoVisible ? "ON" : "OFF" )}");
        }
        _pilotInfoKeyWasDown = pilotKey;

        // Top-level state dispatch: the server browser (default entry) and the
        // transient connecting state don't run any flight logic; only Flying does.
        if (_state != ClientState.Flying)
        {
            if (_state == ClientState.Lobby) UpdateLobby(ks);
            // Connecting is a transient pause while the wire handshake runs;
            // BeginConnect (in Initialize) flips us to SideSelect on success.
            else if (_state == ClientState.SideSelect) UpdateSideSelect(ks);
            // P282 — pre-flight ready screen once seated, before the mission starts.
            else if (_state == ClientState.ReadyCheck) UpdateReadyCheck(ks);
            base.Update(gameTime);
            return;
        }

        UpdateFlying(ks, gameTime);

        // P304 — background music. Runs every flying frame AFTER UpdateFlying
        // (whose docked screen early-returns), so the music subsystem keeps
        // being driven in both the flying and docked states — exactly where the
        // flight↔station switch must happen. Edge-triggered, composing-gated
        // keybindings (J toggle, + / - volume) land here too.
        UpdateMusic(ks, gameTime);

        base.Update(gameTime);
    }

    /// <summary>
    /// P304 — the per-frame music drive (see the call site in <see cref="Update"/>).
    /// Handles the three edge-triggered keybindings (J toggle music, +/− volume)
    /// — gated off while composing chat so typing never flips the music — then
    /// feeds the live docked state into the music subsystem so it switches
    /// between the flight and station beds. Docked state is read with the exact
    /// real expression the rest of the client uses
    /// (<c>ShipEntity.StationId != -1</c>, see <c>HandleDockedScreen</c>), not a
    /// re-derived copy.
    /// </summary>
    private void UpdateMusic(KeyboardState ks, GameTime gameTime)
    {
        if (_music is null) return;

        bool musicKey = ks.IsKeyDown(XnaKeys.J);           // P304 — toggle music (J: Jam)
        if (musicKey && !_musicToggleWasDown && !_chatActive && !_qcMenuOpen)
            _music.Toggle();
        _musicToggleWasDown = musicKey;

        bool volUp = ks.IsKeyDown(XnaKeys.OemPlus);        // +/= — music volume up
        if (volUp && !_musicVolUpWasDown && !_chatActive && !_qcMenuOpen)
            _music.VolumeUp();
        _musicVolUpWasDown = volUp;

        bool volDown = ks.IsKeyDown(XnaKeys.OemMinus);     // -/_ — music volume down
        if (volDown && !_musicVolDownWasDown && !_chatActive && !_qcMenuOpen)
            _music.VolumeDown();
        _musicVolDownWasDown = volDown;

        // Dock state from the real client world, under the same receive-thread
        // lock as the rest of the per-frame world read.
        var world = _session.World;
        bool docked = false;
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me &&
                world.Ships.TryGetValue(me, out var ent) &&
                ent.StationId != -1)
                docked = true;
        }

        _music.Update(docked);
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
            if (IsBriefingVisible(out _))
                DismissBriefing();   // P271 — Enter closes the briefing popup, doesn't open chat
            else if (_chatActive) SendChatLine();
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

        // P268 — docked/hangar loadout screen. When the local ship is docked
        // (ShipEntity.StationId != -1 — the server's real DockedNotify/SHIP_STATUS
        // latch), this replaces flight (and the old automatic undock): the player
        // shops the loadout and launches. Running first and returning means no
        // flight control / weapon / toggles apply while docked.
        if (HandleDockedScreen(ks, composing))
            return;

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

        // P249 — whole-mission starmap overlay toggle (Tab), edge-triggered like
        // the M/V toggles above and gated behind the same composing check so
        // typing in chat / navigating the quick-chat menu never flips it. Tab is
        // unclaimed by every other binding (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/
        // Enter/Back/Escape/Space/K/C/N/OemTilde/[ /]/LeftCtrl all taken) and is
        // the natural native-adjacent overlay key (the audit blessed Tab or M for
        // an overview toggle; M is already TK_ToggleMouse, so Tab it is).
        bool tab = ks.IsKeyDown(XnaKeys.Tab);
        if (!composing && tab && !_tabKeyWasDown)
        {
            _starmapVisible = !_starmapVisible;
            Console.WriteLine($"[monogame] starmap {( _starmapVisible ? "ON" : "OFF" )}");
        }
        _tabKeyWasDown = tab;

        // P272 — starmap cluster selection (info pane). ',' / '.' cycle the
        // selected sector back/forward WHILE the starmap is open, edge-triggered
        // like every other toggle and gated behind the same composing check so
        // typing in chat never cycles it. Keyboard cycling is chosen over mouse
        // hover: there is no map-cursor picking wired into the MonoGame flight
        // controls (the mouse is the M virtual-joystick steering axis, not a
        // picker), so a dedicated key pair is the established starmap-navigation
        // idiom here (Tab toggles the map). ',' and '.' are the one free,
        // natural prev/next pair — every other key is claimed (arrows are yaw,
        // brackets are weapon cycle, OemTilde is group fire, M/V/K/C/N/T/G all
        // taken). First press with no selection starts at the player's sector.
        bool selPrev = ks.IsKeyDown(XnaKeys.OemComma);
        bool selNext = ks.IsKeyDown(XnaKeys.OemPeriod);
        if (!composing && _starmapVisible)
        {
            if (selPrev && !_selPrevKeyWasDown)
                CycleSelectedSector(delta: -1);
            if (selNext && !_selNextKeyWasDown)
                CycleSelectedSector(delta: +1);
        }
        _selPrevKeyWasDown = selPrev;
        _selNextKeyWasDown = selNext;

        // Camera mode cycle (V), edge-triggered like the M toggle — press to
        // advance through the modes available to the pilot's current state
        // (Cockpit → Chase → ExternalTurret → ExternalMissile, P295); holding V
        // does not re-fire. The external-turret view is offered only while
        // actually gunning (CameraCycler skips it otherwise). 'V' is unused by
        // FlightInput (WASD, arrows, Q/E, A/D, Escape and M are all taken), and
        // "View" is a mnemonic.
        bool vCam = ks.IsKeyDown(XnaKeys.V);
        if (!composing && vCam && !_vKeyWasDown)
        {
            _cameraMode = Rendering.CameraCycler.Next(_cameraMode, IsTurretGunner());
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

        // P223 — drop chaff (TK_DropChaff): edge-triggered like the M/V/K toggles
        // above (press once per key-down, never per-frame while held), gated behind
        // the same composing check so typing in chat / navigating the quick-chat
        // menu never drops chaff. The server's HandleFireExpendable (Server.cs:6658)
        // resolves the mounted chaff launcher at mount 0, decrements its ammo, and
        // broadcasts FE(87)+CREATE_CHAFF(109)[+MISSILE_SPOOFED]+END_SPOOFING to the
        // sector — a plain instant command like native's TK_DropChaff. Bound to C —
        // free in flight (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/Enter/Back/Escape/Space/
        // K all taken) and the natural mnemonic for "chaff".
        bool cChaff = ks.IsKeyDown(XnaKeys.C);
        if (!composing && cChaff && !_cKeyWasDown)
        {
            _session.SendFireExpendable(EquipmentType.ChaffLauncher);
            Console.WriteLine("[monogame] drop chaff");
        }
        _cKeyWasDown = cChaff;

        // P224 — mine/probe drop (TK_DropMine, native's "Drop Mine or Probe",
        // trekinput.cpp:1424): edge-triggered like the C chaff key above and gated
        // behind the same composing check. It sends FIRE_EXPENDABLE(82) with
        // EquipmentType.Dispenser — the single mount value native's mine AND probe
        // dispensers share (CmineTypeIGC/CprobeTypeIGC both report ET_Dispenser,
        // minetypeigc.h:62-64 / probetypeigc.h:92-94) — and the server deploys
        // whichever of the two is mounted at mount 0 (HandleFireExpendable's subtype
        // dispatch, Server.cs:6698). Bound to N — free in flight (W/S/Q/E/Arrows/A/D/
        // Shift/R/M/V/T/G/Enter/Back/Escape/Space/K/C/OemTilde/[ /] all taken) and the
        // natural mnemonic for "miNe".
        bool nMine = ks.IsKeyDown(XnaKeys.N);
        if (!composing && nMine && !_nKeyWasDown)
        {
            _session.SendFireExpendable(EquipmentType.Dispenser);
            Console.WriteLine("[monogame] drop mine/probe");
        }
        _nKeyWasDown = nMine;

        // P226 — fire missile (TK_FireMissile): HELD/continuous like afterburner
        // (Shift) and weapon fire (Space) — not edge-triggered — so holding the key
        // keeps requesting launches. It sends the FM_CS_FIRE_MISSILE(70) launch
        // request (ConnectionSession.SendFireMissile): native declares FIRE_MISSILE
        // a bidirectional CS message whose high-word missileFireIGC state bit can't
        // cross the 16-bit wire stateM (igc.h:829,2457-2465), so the wire request is
        // the player-launch path and the server fires authoritatively against its own
        // CommandTarget lock. A ~250 ms throttle bounds chatter while held; the
        // server's lock-rebuild is the real fire-rate limiter. Bound to Left Ctrl —
        // unclaimed by every other binding (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/
        // Enter/Back/Escape/Space/K/C/N/OemTilde/[ /] all taken) and the common
        // flight-sim missile-fire convention (the brief's own suggestion).
        bool missileCtrl = ks.IsKeyDown(XnaKeys.LeftControl);
        if (!composing && missileCtrl)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastMissileSendMs >= 250)
            {
                _lastMissileSendMs = nowMs;
                _session.SendFireMissile();
                Console.WriteLine("[monogame] fire missile (Left Ctrl held) -> FIRE_MISSILE(70)");
            }
        }
        else
        {
            // Re-arm the throttle promptly on release so a fresh hold starts
            // immediately; keep the edge cheap.
            _lastMissileSendMs = Environment.TickCount64 - 250;
        }

        // P225 — single-weapon selection. Edge-triggered like the M/V/K/C
        // toggles above (press once per key-down, never per-frame while held),
        // gated behind the same composing check so typing in chat / navigating
        // the quick-chat menu never re-targets the fire mode.
        //   ` (backtick) → TK_ToggleGroupFire: flip between group-fire (all
        //     weapons, the P221 default) and single-weapon (OneWeapon — the
        //     selected fixed mount). The backtick is a punctuation key distinct
        //     from the [ / ] cycle keys, unclaimed by every other binding
        //     (W/S/Q/E/Up/Down/Left/Right/A/D/Shift/R/M/V/T/G/Enter/Back/
        //     Escape/Space/K/C all taken), and reads naturally as a "fire-mode"
        //     toggle.
        //   [ / ] → TK_PrevWeapon / TK_NextWeapon: step _selectedMount backward
        //     / forward through the fixed-mount selection range (wrapping at
        //     FixedMountCount()). The bracket pair is the task-blessed mnemonic
        //     for "cycle the selection". The selection is advanced even while
        //     group-fire is ON so it is already in place the moment group-fire
        //     is toggled off (it simply has no effect until then).
        bool gFire = ks.IsKeyDown(XnaKeys.OemTilde);
        if (!composing && gFire && !_gfireKeyWasDown)
        {
            _groupFire = !_groupFire;
            Console.WriteLine(_groupFire
                ? "[monogame] weapons: GROUP FIRE (all)"
                : $"[monogame] weapons: SINGLE (mount {_selectedMount})");
        }
        _gfireKeyWasDown = gFire;

        bool prevW = ks.IsKeyDown(XnaKeys.OemOpenBrackets);
        if (!composing && prevW && !_prevWKeyWasDown)
        {
            _selectedMount = FlightInput.PrevMount(_selectedMount, FixedMountCount());
            Console.WriteLine($"[monogame] selected mount {_selectedMount}");
        }
        _prevWKeyWasDown = prevW;

        bool nextW = ks.IsKeyDown(XnaKeys.OemCloseBrackets);
        if (!composing && nextW && !_nextWKeyWasDown)
        {
            _selectedMount = FlightInput.NextMount(_selectedMount, FixedMountCount());
            Console.WriteLine($"[monogame] selected mount {_selectedMount}");
        }
        _nextWKeyWasDown = nextW;

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
            Fire:            ks.IsKeyDown(XnaKeys.Space),
            // P225 — the client-local weapon-selection state carried in each
            // frame: group-fire vs the selected fixed mount (see the ` / [ / ]
            // handlers above). Translate folds these into the fire stateM
            // (AllWeapons when group-firing, OneWeapon|mount when not).
            GroupFire:       _groupFire,
            SelectedMount:   _selectedMount);
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
        _damageEmitters.Clear();
        _cloakedShips.Clear();
        _shipModelById.Clear();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me)
            {
                // Own ship also drives the P213 engine/ambience mix from its
                // real telemetry.
                if (world.Ships.TryGetValue(me, out var mine) && mine.Latest is { } mineUp)
                {
                    // P255 — while the local ship is actively mining a rock, its
                    // rendered facing is overridden to aim at the rock (the
                    // per-tick MiningAsteroidId the server streams), mirroring
                    // native's mining autopilot turn-to-face — plus a gentle
                    // roll "for effect" (native shipIGC.cpp:2251-2253). Applies
                    // in every camera mode via the same pose the camera and hull
                    // draw from; when mining stops (or the rock is gone) the
                    // server-authoritative orientation is used untouched.
                    Orientation poseOrientation = mineUp.Orientation;
                    var rockId = world.MiningAsteroidId;
                    if (rockId != -1 && world.Asteroids.TryGetValue(rockId, out var rockAst))
                        poseOrientation = MiningFacing(mineUp.Orientation, mineUp.Position, rockAst.Position,
                            (float)gameTime.ElapsedGameTime.TotalSeconds);
                    _poses[me] = new Flight.PredictedPose(mineUp.Position, poseOrientation);
                    // P297 — the damage-debris smoke stream (native
                    // AddDamageParticles). Like native (WinTrek.cpp:6616-6621,
                    // which hides the local ship's damage display in the
                    // internal/cockpit view and shows it in external views), the
                    // local ship only vents in non-cockpit camera modes.
                    if (_cameraMode != CameraMode.Cockpit)
                        GatherDamageEmitter(me, mineUp);
                    _sound?.Update(
                        throttle: mineUp.Controls.Throttle,
                        speed: mineUp.Velocity.Length(),
                        cockpitMode: _cameraMode == CameraMode.Cockpit,
                        afterburnerActive: (mineUp.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                        deltaSeconds: (float)gameTime.ElapsedGameTime.TotalSeconds,
                        missileLocked: world.MissileLock.IsFullLock);   // P230 — lock beep at full lock

                    // P267 — the hull's maneuvering-thruster (turn) loop, driven
                    // from the local ship's real commanded turn axes (the same
                    // ControlData the engine mix reads), and the hull exterior
                    // loop audible in the chase/exterior view.
                    _sound?.UpdateTurnThruster(
                        Audio.TurnThrusterSound.LevelFor(
                            mineUp.Controls.Yaw, mineUp.Controls.Pitch, mineUp.Controls.Roll));
                    _sound?.UpdateShipExterior(_cameraMode != CameraMode.Cockpit);
                }
            }
            foreach (var (id, ship) in world.Ships)
            {
                if (id == world.MyShipId || ship.Latest is not { } up) continue;
                _poses[id] = new Flight.PredictedPose(up.Position, up.Orientation);
                // P297 — any other visible damaged ship vents too (native's
                // per-ship AddDamageParticles scope — not just the local hull).
                GatherDamageEmitter(id, up);
                // P270 — remember which of these render poses is a cloaked ship
                // (the CloakActive stateM bit) so Draw alpha-blends it. The
                // server already excluded any undetectable enemy, so anything
                // here is a ship this client's side legitimately sees (a
                // teammate, or an enemy close enough to detect it).
                if (Flight.CloakVisual.IsCloaked(up.StateM))
                    _cloakedShips.Add(id);
                // P288 — resolve this ship's per-faction/class content model
                // under the same lock (civ from side roster, class from hull).
                _shipModelById[id] = ResolveShipModel(ship.SideId, ship.HullId);
            }
        }

        // P232 — weapon-fire + projectile in-flight ambient sound, driven from
        // the local ship's perspective this frame (see the method for details).
        UpdateProjectileSounds();

        // P236 — the three missile sound cues (launch one-shot, in-flight ambient
        // loop, tiered explosion one-shot), driven from the live in-sector missile
        // set this frame (see the method for details).
        UpdateMissileSounds();

        // P264 — the incoming-missile warning loop, driven from the server-streamed
        // "an enemy missile is tracking YOU" state this frame (see the method).
        UpdateMissileWarning();

        // P260 — the two probe sound cues (deploy one-shot on the local ship's own
        // drop, and the scanning ambient loop while an own-side probe is active in
        // the current sector), driven from the live in-sector probe set this frame
        // (see the method for details).
        UpdateProbeSounds();

        // P237 — the warp ambient loop (alephloop), driven from the live in-sector
        // warps within audible range this frame (see the method for details).
        UpdateWarpSounds();

        // P267 — the station exterior ambient loop (shipout), driven from the
        // live in-sector stations within audible range this frame (see the method).
        UpdateStationSounds();

        // P267 — the station destroyed/captured one-shots and the tech-completion
        // chime, drained from the wire events the world observed this frame.
        UpdateSoundEvents();

        // P282 — ready-up is NO LONGER automatic here. The old hack sent
        // SendPlayerReady(true) unconditionally the instant the ship existed,
        // which lied to the server's P180 auto-start gate. Today the player
        // toggles ready on the pre-flight READY-CHECK screen (UpdateReadyCheck);
        // once every occupied side is aggregate-ready the server starts the
        // countdown and UpdateReadyCheck hands off to this Flying state. Launch
        // (undock) is likewise not automatic — when docked the hangar/loadout
        // screen (P268) takes over and the player launches via BUY_LOADOUT
        // (fLaunch=true) or the fallback timer.

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

    /// <summary>
    /// P268 — the docked/hangar loadout screen. Returns true (meaning "consume
    /// this frame: show the hangar, no flight input") while the local ship is
    /// docked (its <see cref="State.ShipEntity.StationId"/> is latched non-NA by
    /// the real DockedNotify / SHIP_STATUS wire traffic). Behaviour:
    ///   UP/DOWN     move the shop cursor (hulls then parts);
    ///   SPACE       select — pick the hull, or toggle a part in/out of the loadout;
    ///   L           LAUNCH — send a real BUY_LOADOUT(fLaunch=true) with the chosen
    ///               hull + toggled parts (HangarScreen::OnButtonLaunch →
    ///               BuyLoadout(ship,true), hangarscreen.cpp:145-160);
    ///   U           UNDOCK without buying — send the plain FM_C_DOCKED(NA);
    ///   (fallback)  after <see cref="DockFallbackMs"/> with no interaction,
    ///               auto-launch the current loadout so an unattended dock never
    ///               strands the POC player.
    /// The hangar exits itself once the server undocks (StationId reverts to NA).
    /// </summary>
    private bool HandleDockedScreen(KeyboardState ks, bool composing)
    {
        var world = _session.World;
        short me = world.MyShipId ?? -1;
        bool myDocked = world.Ships.TryGetValue(me, out _) && world.Ships[me].StationId != -1;

        if (myDocked)
        {
            var mySe = world.Ships[me]; // non-null (myDocked guaranteed membership)
            // Enter the hangar once per dock: build a fresh shop model from the
            // current ship + catalogue (never mutate the current ship itself).
            if (!_dockedActive)
            {
                _dockedActive = true;
                _launchSent = false;
                _dockSinceMs = Environment.TickCount64;
                // P284 — gate the offered hulls/parts by the docked station's
                // tech and the side's researched developments: an item shows iff
                // TtbmRequired ⊆ (stationLocal | mySideDev), the exact P283
                // containment. The docked station resolves through the world's
                // station table → its StationTypeIGC (TtbmLocal); the side's
                // live DevelopmentTechs comes from the wire (CREATE_BUCKETS at
                // join, SIDE_TECH_CHANGE on research). If either can't be
                // resolved, FilteredFor degrades to the full catalogue.
                var offerCat = _shopCatalog ?? Rendering.ShopCatalog.Empty;
                byte[]? builderStationLocal = null;
                Allegiance.Core.Model.TechTreeBitMask? builderSideDev = null;
                if (_shopCatalog is { StationTypes: not null } scat
                    && world.Stations.TryGetValue(mySe.StationId, out var dockStation)
                    && scat.StationTypes.TryGetValue(dockStation.StationTypeId, out var stype)
                    && world.DevelopmentTechsFor(world.MySideId ?? -1) is { } myDev)
                {
                    offerCat = _shopCatalog.FilteredFor(stype.TtbmLocal, myDev);
                    // P285 — keep the same tech terms so the Builder-target
                    // chooser is tech-gated the same way the hull/part offer is.
                    builderStationLocal = stype.TtbmLocal;
                    builderSideDev = myDev;
                }
                _shopModel = new Rendering.ShopModel(
                    offerCat,
                    mySe.HullId,
                    mySe.Loadout ?? Array.Empty<ExpandedPartData>(),
                    offerCat.BuilderTargetsFor(builderStationLocal, builderSideDev));
                // P273 — build the teleport destination list from the live world.
                _teleportModel = BuildTeleportModel(world, mySe);
                _teleportActive = false;
                Console.WriteLine($"[monogame] docked: hangar/loadout screen ({offerCat.Hulls.Count} hulls, {offerCat.Parts.Count} parts offered)");
            }

            // P273 — T toggles the docked sub-view between the hangar loadout
            // shop and the teleport pane (edge-triggered like every toggle).
            bool tKey = ks.IsKeyDown(XnaKeys.T);
            if (!composing && !_launchSent && tKey && !_telTWasDown)
            {
                _teleportActive = !_teleportActive;
                // Rebuild the destination list each time the pane opens so it
                // reflects the current world (a teammate may have docked/left).
                if (_teleportActive && world.Ships.TryGetValue(me, out var myNow))
                    _teleportModel = BuildTeleportModel(world, myNow);
                Console.WriteLine($"[monogame] teleport pane {( _teleportActive ? "OPEN" : "CLOSED" )}");
            }
            _telTWasDown = tKey;

            if (!composing && !_launchSent && _teleportActive && _teleportModel is { } tel)
            {
                // P273 — the teleport pane's own keys (sub-view of the docked screen).
                bool up = ks.IsKeyDown(XnaKeys.Up);
                bool down = ks.IsKeyDown(XnaKeys.Down);
                bool space = ks.IsKeyDown(XnaKeys.Space);
                bool bKey = ks.IsKeyDown(XnaKeys.B);

                if (up && !_telUpWasDown) { tel.MoveCursor(-1); Console.WriteLine($"[monogame] teleport cursor -> {tel.Cursor}"); }
                if (down && !_telDownWasDown) { tel.MoveCursor(+1); Console.WriteLine($"[monogame] teleport cursor -> {tel.Cursor}"); }
                if (space && !_telSpaceWasDown)
                {
                    ActivateTeleport(tel, mySe);
                }
                if (bKey && !_telBWasDown)
                {
                    _teleportActive = false;
                    Console.WriteLine("[monogame] teleport -> back to hangar");
                }

                _telUpWasDown = up; _telDownWasDown = down;
                _telSpaceWasDown = space; _telBWasDown = bKey;
            }
            // While composing (chat) the shop keys yield to the text line.
            else if (!composing && !_launchSent && _shopModel is { } shop)
            {
                bool up = ks.IsKeyDown(XnaKeys.Up);
                bool down = ks.IsKeyDown(XnaKeys.Down);
                bool space = ks.IsKeyDown(XnaKeys.Space);
                bool lKey = ks.IsKeyDown(XnaKeys.L);
                bool uKey = ks.IsKeyDown(XnaKeys.U);
                // P285 — Builder-target chooser: ','/'.' cycle the station type
                // a purchased Builder/Constructor hull should construct (the
                // same ','/'.' pair P272 uses on the starmap, free in the shop).
                bool btPrev = ks.IsKeyDown(XnaKeys.OemComma);
                bool btNext = ks.IsKeyDown(XnaKeys.OemPeriod);

                if (up && !_dockUpWasDown) { shop.MoveCursor(-1); Console.WriteLine($"[monogame] shop cursor -> {shop.Cursor}"); }
                if (down && !_dockDownWasDown) { shop.MoveCursor(+1); Console.WriteLine($"[monogame] shop cursor -> {shop.Cursor}"); }
                if (space && !_dockSpaceWasDown)
                {
                    shop.Activate();
                    Console.WriteLine($"[monogame] shop select: hull={shop.EffectiveHullId} parts={shop.ChosenParts.Count}");
                }
                if (btPrev && !_btPrevWasDown) { shop.CycleBuilderTarget(-1); Console.WriteLine($"[monogame] builder target -> {shop.CurrentBuilderTarget?.Name ?? "(none)"}"); }
                if (btNext && !_btNextWasDown) { shop.CycleBuilderTarget(+1); Console.WriteLine($"[monogame] builder target -> {shop.CurrentBuilderTarget?.Name ?? "(none)"}"); }
                if (lKey && !_dockLWasDown)
                {
                    SendBuilderTargetIfApplicable(shop);
                    _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
                    _launchSent = true;
                    Console.WriteLine($"[monogame] LAUNCH -> BUY_LOADOUT(fLaunch) hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
                }
                else if (uKey && !_dockUWasDown)
                {
                    _session.SendDocked();
                    _launchSent = true;
                    Console.WriteLine("[monogame] UNDOCK (no buy) -> FM_C_DOCKED(NA)");
                }

                _dockUpWasDown = up; _dockDownWasDown = down;
                _dockSpaceWasDown = space; _dockLWasDown = lKey; _dockUWasDown = uKey;
                _btPrevWasDown = btPrev; _btNextWasDown = btNext;

                // Fallback: an unattended dock must never stall the POC, so after
                // the grace period we auto-launch the current (possibly shopped)
                // loadout — the same BUY_LOADOUT(fLaunch) the L key sends.
                if (!_launchSent && Environment.TickCount64 - _dockSinceMs >= DockFallbackMs)
                {
                    SendBuilderTargetIfApplicable(shop);
                    _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
                    _launchSent = true;
                    Console.WriteLine("[monogame] dock fallback: auto-launch current loadout");
                }
            }
            else if (_launchSent)
            {
                // Launch requested; release key latches so a held key can't
                // re-fire if we somehow re-enter before undock completes.
                _dockUpWasDown = _dockDownWasDown = _dockSpaceWasDown = _dockLWasDown = _dockUWasDown = false;
            }

            return true; // docked → consume the frame, no flight input
        }

        if (_dockedActive) // the server undocked us → leave the hangar
        {
            _dockedActive = false;
            _shopModel = null;
            _teleportModel = null;
            _teleportActive = false;
            Console.WriteLine("[monogame] undocked: leaving hangar");
        }
        return false;
    }

    /// <summary>P285 — when the shop's effective hull is a Builder and the
    /// player has cycled to a Builder target station type, send the port-extension
    /// <c>SET_BUILDER_TARGET</c>(201) so the server sets the launched ship's
    /// <see cref="Allegiance.Core.Sim.Ship.BuilderStationType"/> to the choice
    /// (the purchase-time wiring the P268/P284 shop lacked). Sent just before
    /// the BUY_LOADOUT launch; the field is server-persisted on the ship, so
    /// ordering vs the launch is immaterial.</summary>
    private void SendBuilderTargetIfApplicable(Rendering.ShopModel shop)
    {
        if (shop.IsBuilderActive && shop.BuilderStationTypeId is { } stId)
        {
            _session.SendSetBuilderTarget(shop.EffectiveHullId, stId);
            Console.WriteLine($"[monogame] BUILDER TARGET -> SET_BUILDER_TARGET hull {shop.EffectiveHullId} stationType {stId}");
        }
    }

    /// <summary>
    /// P273 — build the docked teleport pane's destination list from the live
    /// world snapshot: boardable same-side docked ships with turret slots plus
    /// friendly restart-capable stations (excluding the one we're at). Hull and
    /// station-type lookups come from the shop catalogue's parsed core; a
    /// missing core degrades the list (ships need the hull's turret-slot count,
    /// so they are omitted when the core is absent, but stations still list by
    /// their wire name).
    /// </summary>
    private Rendering.TeleportPaneModel BuildTeleportModel(ClientWorld world, ShipEntity mySe)
    {
        var cat = _shopCatalog;
        var hulls = cat?.Hulls.ToDictionary(h => h.HullId) ?? new Dictionary<short, DataHullTypeIGC>();
        Func<short, DataHullTypeIGC?> hullLookup = id => hulls.TryGetValue(id, out var h) ? h : null;
        Func<short, DataStationTypeIGC?> stypeLookup =
            cat?.StationTypes is { } stypes
                ? id => stypes.TryGetValue(id, out var t) ? t : null
                : _ => null;

        short me;
        short mySide;
        var ships = new List<ShipView>();
        var stations = new List<StationView>();

        // P319 (finding H-3) — world.Ships / world.Stations are upserted by the
        // receive thread under _session.Gate (EXPORT(26), SHIP_STATUS, ExportStation /
        // StationCapture); enumerating them unlocked on dock entry threw
        // InvalidOperationException ("Collection was modified") — a crash at the
        // exact moment of docking. Same P306 precedent as elsewhere in Game1:
        // snapshot the two tables (and MyShipId/MySideId) into local lists under
        // the lock, then build the pane model outside it.
        lock (_session.Gate)
        {
            me = world.MyShipId ?? -1;
            mySide = world.MySideId ?? -1;

            foreach (var s in world.Ships.Values)
                if (s.ShipId != me)
                    ships.Add(new ShipView(s.ShipId, s.SideId, s.StationId, s.HullId));
            foreach (var s in world.Stations.Values)
                stations.Add(new StationView(s.StationId, s.SideId, s.StationTypeId, s.Name));
        }

        return Rendering.TeleportPaneModel.Build(
            me, mySide, mySe.StationId, ships, stations, hullLookup, stypeLookup);
    }

    /// <summary>
    /// P289 — resolve a station's real per-type Radius, the scale driver for the
    /// procedural station visual, from its StationTypeId via the shop
    /// catalogue's station-type table (loaded once at boot from the same
    /// PCore001.igc the server boots). Falls back to
    /// <see cref="StationRenderer.DefaultFallbackRadius"/> when the static core
    /// is absent or the type id is unknown, so a missing core never breaks
    /// station rendering — every real station still appears at a sensible
    /// size proportional to the same data the server uses.
    /// </summary>
    private float ResolveStationRadius(Allegiance.Core.DataStationIGC st)
    {
        if (_shopCatalog is { StationTypes: not null } cat
            && cat.StationTypes.TryGetValue(st.StationTypeId, out var type)
            && type.Radius > 0f)
            return type.Radius;
        return Rendering.StationRenderer.DefaultFallbackRadius;
    }

    /// <summary>
    /// P273 — act on the teleport pane's SELECT: translate the pure verdict into
    /// the real wire message(s), mirroring native <c>TeleportPane::OnButtonTeleport</c>
    /// (<c>teleportpane.cpp:296-361</c>):
    /// <list type="bullet">
    /// <item>board a ship — if already on a different ship, leave first (the
    /// native <c>DisembarkAndBoard</c>, :312-319) then <c>BOARD_SHIP</c>;</item>
    /// <item>teleport to a station — if on a ship, leave first (the native
    /// <c>DisembarkAndTeleport</c>, :328-338) then <c>DOCKED(stationID)</c>;</item>
    /// </list>
    /// After acting, close the pane and suppress the auto-launch fallback (the
    /// player did something intentional).
    /// </summary>
    private void ActivateTeleport(Rendering.TeleportPaneModel tel, ShipEntity mySe)
    {
        var action = tel.Activate();
        bool onboard = mySe.ParentId != -1;   // are we currently a gunner/observer on another ship?

        switch (action.Kind)
        {
            case Rendering.TeleportPaneModel.ActionKind.BoardShip:
                if (onboard) { _session.SendBoardShip(Rendering.TeleportPaneModel.Na); Console.WriteLine("[monogame] teleport: leave current ship (DisembarkAndBoard)"); }
                _session.SendBoardShip(action.TargetId);
                Console.WriteLine($"[monogame] teleport: board ship {action.TargetId} as gunner -> BOARD_SHIP");
                break;

            case Rendering.TeleportPaneModel.ActionKind.TeleportToStation:
                if (onboard) { _session.SendBoardShip(Rendering.TeleportPaneModel.Na); Console.WriteLine("[monogame] teleport: leave current ship (DisembarkAndTeleport)"); }
                _session.SendDocked(action.TargetId);
                Console.WriteLine($"[monogame] teleport: to station {action.TargetId} -> DOCKED(stationID)");
                break;

            default:
                Console.WriteLine("[monogame] teleport: nothing selected");
                return;
        }

        _teleportActive = false;
        _launchSent = true;   // suppress the unattended-dock auto-launch after an intentional action
    }

    // P297 — the per-ship emission inputs that drive one frame of the continuous
    // damage-debris smoke stream (native AddDamageParticles): a damaged visible
    // ship's real position/velocity/orientation + its native vent count (from
    // its real hull fraction via Flight.DamageDebrisTracker).
    private readonly record struct DamageEmitter(
        Allegiance.Core.Collision.Vector3 Position,
        Allegiance.Core.Collision.Vector3 Velocity,
        Allegiance.Core.Sim.Orientation Orientation,
        int VentCount);

    /// <summary>Record or clear a ship's damage-debris emitter for this frame:
    /// vents (and thus streams) only while the ship's real hull fraction is at
    /// or below native's 0.9 threshold; a healthy ship contributes nothing.
    /// Called under the Gate lock in the pose-gather pass.</summary>
    private void GatherDamageEmitter(short id, Allegiance.Core.Wire.ServerShipUpdate up)
    {
        int vents = Flight.DamageDebrisTracker.DamageFrameCount(up.HullFraction);
        if (vents > 0)
            _damageEmitters[id] = new DamageEmitter(up.Position, up.Velocity, up.Orientation, vents);
        else
            _damageEmitters.Remove(id);
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
        _lockBracket = null;   // P230 — recomputed only when a lock target projects into view this frame
        _leadIndicator = null; // P266 — recomputed only when a lead point projects into view this frame

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

                // Camera selection: P211's chase (third-person) / cockpit
                // (first-person, full orientation with roll), plus P295's two new
                // external views (ExternalTurret follows the carrier you're
                // gunning on; ExternalMissile watches the latest live missile in
                // your sector). All honour the same camera-relative convention
                // and all supply a cameraEye that every draw subtracts from
                // object positions.
                Matrix view; Matrix proj; Vector3 cameraEye;
                float chaseDt = (float)gameTime.ElapsedGameTime.TotalSeconds;
                // P306 — capture mode: a fixed tracking overview of the whole
                // fleet instead of a cockpit/chase follow-cam, so the video
                // shows N ships in frame at once (the exact smoothness scale
                // this stress test measures). Computed per frame from the live
                // _poses so the camera rides the drifting formation centroid.
                if (_captureSpec is not null)
                {
                    ComputeCaptureCamera(aspect, out view, out proj, out cameraEye);
                }
                else switch (_cameraMode)
                {
                    case CameraMode.Cockpit:
                        (view, proj) = _cockpitCamera.Matrices(myPos, myRot, aspect, out cameraEye);
                        break;
                    case CameraMode.ExternalTurret:
                        // Follow the ship whose turret we're gunning on (the
                        // carrier), reusing the existing turret-boarding state
                        // (ShipEntity.ParentId). Fall back to our own chase view
                        // if we're somehow not gunning or the carrier is unseen.
                        if (TryGetGunnerCarrierPose(out var carrierPos, out var carrierRot))
                            (view, proj) = _chaseCamera.Matrices(carrierPos, carrierRot, aspect, chaseDt, out cameraEye);
                        else
                            (view, proj) = _chaseCamera.Matrices(myPos, myRot, aspect, chaseDt, out cameraEye);
                        break;
                    case CameraMode.ExternalMissile:
                        // Watch the latest live missile in our sector (native's
                        // cmExternalMissile fallback frames the missile + the ship
                        // it was fired from; the client wire carries no missile
                        // target/shooter). Fall back to chase when none flying.
                        if (TryGetLatestMissilePos(out var missilePos))
                            (view, proj) = _missileCamera.Matrices(missilePos, myPos, aspect, out cameraEye);
                        else
                            (view, proj) = _chaseCamera.Matrices(myPos, myRot, aspect, chaseDt, out cameraEye);
                        break;
                    default: // Chase
                        (view, proj) = _chaseCamera.Matrices(myPos, myRot, aspect, chaseDt, out cameraEye);
                        break;
                }
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
                    // P270 — alpha-blend cloaked ships (subtle shimmer over
                    // elapsed wall-clock); fully solid otherwise. The server
                    // already withheld undetectable enemies, so anything here is
                    // legitimately visible to this client's side.
                    float shipOpacity = _cloakedShips.Contains(id)
                        ? Flight.CloakVisual.Opacity(gameTime.TotalGameTime.TotalSeconds)
                        : 1f;
                    var shipModel = _shipModelById.TryGetValue(id, out var m) ? m : null;
                    _shipRenderer.Draw(shipModel ?? _fallbackModel, worldMat, view, proj, shipOpacity);
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
                var rooks = new List<(AsteroidSpin Spin, SimVec Pos, Orientation Ori, float Radius, int Variant, RockTextureFactory.AsteroidResourceType Type)>();
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
                            // P252 — seed (once) a per-asteroid spin from the
                            // wire's real axis+rate, then reuse it every frame
                            // so each rock accumulates its own rotation.
                            if (!_asteroidSpins.TryGetValue(id, out var spin))
                            {
                                spin = AsteroidSpin.FromWire(ast.Rotation);
                                _asteroidSpins[id] = spin;
                            }
                            // P286 — pick the imported mesh variant from the
                            // asteroid's stable AsteroidId (silhouette variety)
                            // and its resource type from the real
                            // AabmCapabilities (colour variety).
                            int variant = AsteroidRenderer.MeshVariantFor(ast.AsteroidId);
                            var type = RockTextureFactory.Classify(ast.AabmCapabilities);
                            rooks.Add((spin, Vec(ast.Position), o, ast.Radius, variant, type));
                        }
                    }
                }
                foreach (var (spin, rPos, rOri, rRadius, variant, type) in rooks)
                {
                    spin.Advance(dtSec);
                    var rXnaPos = PoseConverter.ToXna(rPos);
                    var rRot = PoseConverter.ToXnaRotation(rOri);
                    // P252 — apply the accumulated spin about the rock's own
                    // (body-local) axis as a post-multiply of the base
                    // orientation, exactly native's o.PostRotate(axis, angle*dt)
                    // composed over time.
                    var spinAxis = new Vector3(spin.Axis.X, spin.Axis.Y, spin.Axis.Z);
                    var spinRot = Matrix.CreateFromAxisAngle(spinAxis, spin.Angle);
                    var rWorld = rRot * spinRot * Matrix.CreateScale(rRadius) * Matrix.CreateTranslation(rXnaPos - cameraEye);
                    // P286 — draw the asteroid's chosen imported mesh variant
                    // with the diffuse generated for its resource type, so a
                    // sector shows real shape + colour variety. Textures are
                    // built lazily and cached (a handful of types, not per rock).
                    _asteroidRenderers[variant].Draw(rWorld, view, proj, EnsureAsteroidTexture(type));
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
                var warps = new List<(SimVec Pos, Orientation Ori, float Radius)>();
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
                            warps.Add((Vec(wp.Position), wo, wp.Radius));
                        }
                    }
                }
                double dt = gameTime.ElapsedGameTime.TotalSeconds;
                foreach (var (wPos, wOri, wRadius) in warps)
                {
                    var wXnaPos = PoseConverter.ToXna(wPos);
                    var wRot = PoseConverter.ToXnaRotation(wOri);
                    var wWorld = wRot * Matrix.CreateScale(wRadius * Rendering.WarpRenderer.VisualScaleMultiplier)
                                 * Matrix.CreateTranslation(wXnaPos - cameraEye);
                    _warpRenderer.Draw(wWorld, view, proj, dt);
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
                // convention (friendly = HUD teal accent, hostile = warm).
                var stationViews = new List<(SimVec Pos, Orientation Ori, short SideId, float Radius)>();
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var ownSt))
                    {
                        short myStSector = ownSt.SectorId;
                        foreach (var (_, st) in world.Stations)
                        {
                            if (st.ClusterId != myStSector) continue;
                            var sto = new Orientation();
                            sto.Set(Vec(st.Forward), Vec(st.Up));
                            stationViews.Add((Vec(st.Position), sto, st.SideId, ResolveStationRadius(st)));
                        }
                    }
                }
                foreach (var (sPos, sOri, sSide, sRadius) in stationViews)
                {
                    var sXnaPos = PoseConverter.ToXna(sPos);
                    var sRot = PoseConverter.ToXnaRotation(sOri);
                    var sWorld = sRot * Matrix.CreateScale(sRadius)
                                 * Matrix.CreateTranslation(sXnaPos - cameraEye);
                    var sTint = (world.MySideId ?? -1) == sSide
                        ? Rendering.StationRenderer.FriendlyColor
                        : Rendering.StationRenderer.EnemyColor;
                    _stationRenderer.Draw(sWorld, view, proj, sTint);
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
                var buildViews = new List<(Vector3 Pos, float Radius, float Alpha)>();
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
                            buildViews.Add((PoseConverter.ToXna(be.PositionStop), radius, alpha));
                        }
                    }
                }
                foreach (var (bPos, bRadius, bAlpha) in buildViews)
                {
                    var bWorld = Matrix.CreateScale(bRadius) * Matrix.CreateTranslation(bPos - cameraEye);
                    _buildingEffectRenderer.Draw(bWorld, view, proj, bAlpha);
                }

                // P231 — projectiles. Unlike asteroids/warps a projectile carries
                // no ClusterId/sector on the wire, and the client has no static
                // core to map a type id to a sector, so the "sector filter"
                // analog here is a distance-based visibility test against the
                // local ship (the closest the wire supports to "is this in my
                // sector" — a same-side teammate's projectile in another sector
                // would otherwise render). Each is positioned by its real
                // creation Position advanced along its Velocity by the elapsed
                // seconds since receipt (PositionAt — simple extrapolation, the
                // brief's explicit scope, so fast shots don't snap between
                // broadcasts), oriented along its actual velocity (falling back
                // to its Forward heading for a near-static bolt), and sized:
                // length ∝ speed (faster shots read as longer streaks) with a
                // fixed default width (no per-type Width on the wire). The tint
                // is a single default tracer colour — real per-type
                // DataProjectileTypeIGC.Color is not reachable client-side this
                // stage (no static core; see the P231 summary), so one amber
                // tracer stands in for every type. Pruning + gather both ride
                // the same Gate lock as every other world read.
                var bolts = new List<(SimVec Pos, SimVec Dir, float Len)>();
                lock (_session.Gate)
                {
                    long projNow = Environment.TickCount64;
                    world.PruneExpiredProjectiles(projNow);
                    if (world.Ships.TryGetValue(me, out var ownProj) && ownProj.Latest is { } meUp)
                    {
                        var anchorPos = meUp.Position;
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
                                    len, ProjectileWidth, ProjectileNearCamClearance))
                                continue;

                            bolts.Add((pos, dir, len));
                        }
                    }
                }
                foreach (var (bPos, bDir, bLen) in bolts)
                {
                    var bOrient = new Orientation();
                    bOrient.Set(bDir);
                    var bRot = PoseConverter.ToXnaRotation(bOrient);
                    var bXna = PoseConverter.ToXna(bPos);
                    // Scale MUST come before rotation here, unlike the asteroid/
                    // warp draws above: those use a uniform CreateScale(radius),
                    // which commutes with rotation so the order there is
                    // harmless. This bolt's scale is non-uniform (thin width,
                    // long length) — rotating the still-symmetric unit box
                    // first, then stretching it along the fixed WORLD axes,
                    // warps it into a slanted shape instead of a straight arrow
                    // along the travel direction (confirmed live: bolts looked
                    // "sideways"/flattened rather than pointing like an arrow).
                    // Scaling first (in local box space) then rotating that
                    // already-elongated box into world orientation is correct.
                    var bWorld = Matrix.CreateScale(ProjectileWidth, ProjectileWidth, bLen) * bRot
                                 * Matrix.CreateTranslation(bXna - cameraEye);
                    _projectileRenderer.Draw(bWorld, ProjectileTint, view, proj);
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
                var missileList = new List<(SimVec Pos, Orientation Ori, XnaModel? Model)>();
                var trailMissiles = new List<TrailMissile>();
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
                            missileList.Add((ms.Position, mo, PickMissileModel(ms.TypeId)));
                            trailMissiles.Add(new TrailMissile(
                                ms.MissileId, ms.Position, ms.Forward, _trailNowSec));
                        }
                    }
                }
                foreach (var (mPos, mOri, mModel) in missileList)
                {
                    var mXna = PoseConverter.ToXna(mPos);
                    var mRot = PoseConverter.ToXnaRotation(mOri);
                    var mWorld = mRot * Matrix.CreateTranslation(mXna - cameraEye);
                    if (mModel is not null)
                    {
                        // P288 — one of the three imported content models
                        // (mis05/mis400/artifact), drawn like a ship model
                        // (cull-none FBX winding, mesh.ParentBone baked pose).
                        DrawMissileContentModel(mModel, mWorld, view, proj);
                    }
                    else
                    {
                        _missileRenderer.Draw(mWorld, view, proj);
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
                var probeViews = new List<(Vector3 Pos, short SideId)>();
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var ownPb))
                    {
                        short myProbeSector = ownPb.SectorId;
                        foreach (var (_, pe) in world.Probes)
                        {
                            if (pe.ClusterId != myProbeSector) continue;
                            probeViews.Add((PoseConverter.ToXna(pe.Position), pe.SideId));
                        }
                    }
                }
                foreach (var (pbPos, pbSide) in probeViews)
                {
                    var pbWorld = Matrix.CreateTranslation(pbPos - cameraEye);
                    var pbTint = (world.MySideId ?? -1) == pbSide
                        ? Rendering.StationRenderer.FriendlyColor
                        : Rendering.StationRenderer.EnemyColor;
                    _probeRenderer.Draw(pbWorld, view, proj, pbTint);
                }

                // P291 — mines. Like probes/missiles, ClientWorld.Mines is keyed
                // by MineId from the deploy EXPORT(26)/OT_mine regardless of
                // sector, so the draw loop filters to the local player's OWN
                // sector: me.ClusterId == mySector. A mine is static — its
                // EXPORT position is final for its life — so each is drawn at
                // that real position as a native MineFieldGeo decal cloud
                // (MineModelSource / MineFieldGeometry / MineRenderer), sized by
                // the type's real MineRadius resolved from the static core.
                var mineViews = new List<(Vector3 Pos, float Radius, int Seed)>();
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var ownMine))
                    {
                        short myMineSector = ownMine.SectorId;
                        var mineTypes = _shopCatalog?.MineTypes;
                        foreach (var (_, me2) in world.Mines)
                        {
                            if (me2.ClusterId != myMineSector) continue;
                            mineViews.Add((
                                PoseConverter.ToXna(me2.Position),
                                Rendering.MineModelSource.ResolveRadius(me2.MinetypeId, mineTypes),
                                me2.MineId));
                        }
                    }
                }
                foreach (var (mnPos, mnRadius, mnSeed) in mineViews)
                    _mineRenderer.DrawCloud(mnPos - cameraEye, mnRadius, mnSeed, cameraEye, view, proj);

                // P292 — chaff. ClientWorld.Chaff is transient (CREATE_CHAFF(109)
                // carries no id and no destroy message), so — like projectiles —
                // the draw loop prunes by local receipt time + the type's real
                // lifespan and uses the distance-from-own-ship visibility filter
                // (the "sector filter" analog for a wire that carries no chaff
                // cluster). Each chaff coasts at its real V0 (ChaffEntity.
                // PositionAt) and grows from a 1.0 floor to its type radius over
                // its lifespan (ChaffAnimation); the renderer draws it as a
                // single camera-facing decal of the type's real texture.
                var chaffViews = new List<(Vector3 Pos, float Radius, string Stem, float Roll)>();
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
                            chaffViews.Add((PoseConverter.ToXna(pos), radius, stem, Rendering.ChaffAnimation.RollAngle(age)));
                        }
                    }
                }
                foreach (var (cfPos, cfRadius, cfStem, cfRoll) in chaffViews)
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
                var treasureViews = new List<Vector3>();
                lock (_session.Gate)
                {
                    if (world.Ships.TryGetValue(me, out var ownTr))
                    {
                        short myTreasureSector = ownTr.SectorId;
                        foreach (var (_, te) in world.Treasures)
                        {
                            if (te.ClusterId != myTreasureSector) continue;
                            treasureViews.Add(PoseConverter.ToXna(te.Position));
                        }
                    }
                }
                foreach (var tp in treasureViews)
                {
                    var tworld = Matrix.CreateScale(Rendering.TreasureRenderer.DefaultRadius)
                                 * Matrix.CreateTranslation(tp - cameraEye);
                    _treasureRenderer.Draw(tworld, view, proj, dt);
                }

                // P235 — the exhaust trail. Advance the pool's clock by the real
                // elapsed frame time, emit/spawn + age from the gathered samples,
                // then draw every live particle as a camera-facing soft quad on
                // top of the missiles (additive, so the bright flame shows at the
                // tail). Both the pool step and the gather ride the same Gate lock
                // discipline (the gather above); the pool itself is mutated only
                // on this render thread.
                _trailNowSec += dt;
                _trailParticles.Update(_trailNowSec, (float)dt, trailMissiles, _trailRandom);
                var trailViews = _trailParticles.Enumerate().ToList();
                _trailRenderer.Draw(trailViews, cameraEye, view, proj);

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
                    _warpParticles.EmitBurst(
                        burst.Position, burst.Velocity, burst.Forward, burst.Right,
                        Rendering.WarpTransitParticles.ShipRadiusDefault,
                        (float)dt, _trailRandom);
                }
                _warpParticles.Update((float)dt);
                var sparkViews = _warpParticles.Enumerate().ToList();
                _warpParticleRenderer.Draw(sparkViews, cameraEye, view, proj);

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
                    _damageDebris.Emit(em.Position, em.Velocity, em.Orientation,
                        Rendering.DamageDebrisParticles.ShipRadiusDefault,
                        kv.Key, em.VentCount, (float)dt, _trailRandom);
                }
                _damageDebris.Update((float)dt);
                var debrisViews = _damageDebris.Enumerate().ToList();
                _damageDebrisRenderer.Draw(debrisViews, cameraEye, view, proj);

                // P263 — explosions. Under the Gate, prune finished blasts and
                // snapshot the local ship's own-sector explosions; the renderer
                // plays them additively on top of the scene with native
                // size/duration/fade (the real sprite strips, not procedural).
                short ownSectorId = -1;
                long nowLocalMs = 0;
                var explosionList = new List<ExplosionEntity>();
                lock (_session.Gate)
                {
                    nowLocalMs = Environment.TickCount64;
                    world.PruneExpiredExplosions(nowLocalMs);
                    if (world.MyShipId is { } meX && world.Ships.TryGetValue(meX, out var ownX))
                    {
                        ownSectorId = ownX.SectorId;
                        foreach (var ex in world.Explosions)
                            if (ex.ClusterId == ownSectorId) explosionList.Add(ex);
                    }
                }
                if (explosionList.Count > 0)
                    _explosionRenderer.Draw(explosionList, ownSectorId, nowLocalMs, cameraEye, view, proj);

                // P250 — local current-sector tactical radar. Drawn here (inside
                // the camera block, so view/proj/cameraEye are live) over the 3D
                // scene; the P211 HUD gauges render on top of it afterwards. It
                // only ever appears when the player has a ship + camera, which is
                // exactly when a local tactical picture is wanted.
                DrawRadar(view, proj, cameraEye);
            }

            // 2D HUD overlay on top of the 3D scene (P211).
            DrawHud();

            // P249 — whole-mission starmap pane, over the HUD but beneath the
            // chat overlay so typed text stays readable while the map is up.
            if (_starmapVisible)
                DrawStarmap();

            // P214 — real chat overlay (log + composing line) on top of the HUD.
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
            var rows = Rendering.ScoreboardLayout.Build(_session.World.ScoreRoster);
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
        if (_dockedActive && _teleportActive && _teleportModel is { } telPane)
        {
            var telRows = Rendering.TeleportLayout.Build(telPane);
            _dockedScreenRenderer.Draw(GraphicsDevice.Viewport, telRows);
        }
        else if (_dockedActive && _shopModel is { } dockShop)
        {
            var shopRows = Rendering.ShopLayout.Build(dockShop);
            // P276 — right-side part-info / connector-compat panel for the
            // currently-highlighted shop row (real stats + fits/no-fit).
            var detailRows = Rendering.PartInfoLayout.Build(dockShop);
            _dockedScreenRenderer.Draw(GraphicsDevice.Viewport, shopRows, detailRows);
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
            _gameOverScreenRenderer.Draw(GraphicsDevice.Viewport, info);
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

        base.Draw(gameTime);
    }

    /// <summary>P306 — the capture overview camera: frame the WHOLE fleet (all
    /// known <see cref="_poses"/> sim positions) from a raised viewpoint
    /// behind the formation centroid, sizing the distance so the bounding
    /// sphere of the fleet fits the vertical FOV with margin. Camera-relative
    /// like every other camera (P207 floating-origin rule): returns the real
    /// sim-space eye in XNA coords via <paramref name="cameraEye"/> and every
    /// object draw subtracts it.</summary>
    private static bool _capCamLogged;
    private void ComputeCaptureCamera(float aspect, out Matrix view, out Matrix proj, out Vector3 cameraEye)
    {
        const float FovDeg = 100f;    // vertical FOV — a wide, spacious fleet shot so individual ships read
        float cx = 0f, cy = 0f, cz = 0f;
        int n = 0;
        foreach (var (_, pose) in _poses)
        {
            var p = pose.Position;
            cx += p.X; cy += p.Y; cz += p.Z; n++;
        }
        SimVec center = n > 0 ? new SimVec(cx / n, cy / n, cz / n) : SimVec.Zero;

        double maxR2 = 0;
        foreach (var (_, pose) in _poses)
        {
            double dx = pose.Position.X - center.X, dy = pose.Position.Y - center.Y, dz = pose.Position.Z - center.Z;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > maxR2) maxR2 = d2;
        }
        float r = (float)Math.Sqrt(maxR2) + 220f;   // bounding sphere + margin so ships aren't clipped
        float halfTan = MathF.Tan(MathHelper.ToRadians(FovDeg / 2f));
        float dist = Math.Max(420f, r / halfTan) * 1.08f;

        SimVec eyeSim = new SimVec(center.X, center.Y + dist * 0.40f, center.Z + dist);
        SimVec lookSim = new SimVec(center.X, center.Y, center.Z - dist * 0.10f);   // look slightly ahead of motion (-Z)
        Vector3 eyeX = PoseConverter.ToXna(eyeSim);
        Vector3 lookX = PoseConverter.ToXna(lookSim);
        view = Matrix.CreateLookAt(eyeX, lookX, Vector3.Up);
        proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(FovDeg), aspect, 10f, 80000f);
        cameraEye = eyeX;

        if (!_capCamLogged)
        {
            _capCamLogged = true;
            Console.WriteLine($"[capture] OVERVIEW cam: nPoses={n} center=({center.X:0.#},{center.Y:0.#},{center.Z:0.#}) radius={r - 320:0.#} dist={dist:0.#} eye=({eyeSim.X:0.#},{eyeSim.Y:0.#},{eyeSim.Z:0.#})");
        }
    }

    /// <summary>P306 — stop capture, persist the per-frame timeline +
    /// measured render FPS to the capture dir, print a summary, and exit.</summary>
    private void FinishCapture()
    {
        if (!_capActive) return;
        _capActive = false;
        double wall = _capClock.Elapsed.TotalSeconds;
        // Loop throughput: rendered frames per wall-second of the capture.
        // Includes the per-frame GPU readback but NOT the (async, background)
        // PNG encode — an honest measure of N-ship render cost.
        double renderFps = wall > 0 ? _capRenderFrames / wall : 0;
        double writtenFps = wall > 0 ? _capFrame / wall : 0;
        _capSaver?.Dispose();
        _capSaver = null;

        var tsv = new StringBuilder();
        tsv.AppendLine("#frame\twallSec");
        for (int i = 0; i < _capWriteTimes.Count; i++)
            tsv.AppendLine($"{i}\t{_capWriteTimes[i]:0.000000}");
        File.WriteAllText(Path.Combine(_capDir, "frames.tsv"), tsv.ToString());

        File.WriteAllText(Path.Combine(_capDir, "metrics.txt"),
            $"renderFrames={_capRenderFrames}\n" +
            $"renderMeanFps={renderFps:0.##}\n" +
            $"writtenFrames={_capFrame}\n" +
            $"writtenFps={writtenFps:0.##}\n" +
            $"wallSec={wall:0.000}\n");

        Console.WriteLine($"[capture] DONE frames={_capFrame} renderFps={renderFps:0.##} writtenFps={writtenFps:0.##} wall={wall:0.000}s dir={_capDir}");
        Exit();
    }

    /// <summary>
    /// P232 — drive the weapon-fire one-shot and the per-projectile in-flight
    /// ambient sounds from the local ship's live world state each frame. Under
    /// the Gate lock it prunes expired projectiles, gathers every projectile
    /// within audible range of the local ship (with its listener distance via
    /// <see cref="ProjectileEntity.PositionAt"/>), and hands that to the pure
    /// <see cref="ProjectileSoundPlanner"/>, whose Start/Update/Stop commands
    /// are replayed against <see cref="SoundManager"/>: a newly-spawned bolt
    /// that left OUR muzzle (spawn within
    /// <see cref="Audio.ProjectileAmbientSoundCurve.FireOriginRadius"/> of the
    /// listener — the port's own local-vs-other discrimination, since the P88
    /// wire carries no shooter id) also plays the weapon-fire one-shot,
    /// mirroring native's fFiringShot → single-shot path (trekigc.cpp:1005-1012);
    /// every in-range bolt gets a distance-scaled ambient loop that swells as it
    /// passes the cockpit and stops on expiry/recession (native's per-object
    /// AmbientSoundSite, trekigc.cpp:694-728). The console lines are the live
    /// audio-state surface (the same [monogame] audio convention as P213/P216),
    /// used for the P232 live proof.
    /// </summary>
    private void UpdateProjectileSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        long now = Environment.TickCount64;
        List<(ProjectileEntity Entity, float Distance)> inRange = new();

        lock (_session.Gate)
        {
            world.PruneExpiredProjectiles(now);
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                foreach (var pj in world.Projectiles)
                {
                    var pos = pj.PositionAt(now);
                    float dist = (pos - anchor).Length();
                    if (dist > Audio.ProjectileAmbientSoundCurve.AudibleRadius) continue;
                    inRange.Add((pj, dist));
                }
            }
        }

        foreach (var cmd in _projectileSoundPlanner.Plan(inRange))
        {
            switch (cmd.Action)
            {
                case Audio.ProjectileSoundAction.Start:
                    if (cmd.IsLocalFire)
                    {
                        snd.PlayWeaponFire();
                        Console.WriteLine("[monogame] audio: weapon fire");
                    }
                    snd.StartProjectileAmbient(cmd.Projectile, cmd.Volume);
                    Console.WriteLine("[monogame] audio: projectile ambient start");
                    break;
                case Audio.ProjectileSoundAction.Update:
                    snd.UpdateProjectileAmbient(cmd.Projectile, cmd.Volume);
                    break;
                case Audio.ProjectileSoundAction.Stop:
                    snd.StopProjectileAmbient(cmd.Projectile);
                    Console.WriteLine("[monogame] audio: projectile ambient stop");
                    break;
            }
        }
    }

    /// <summary>
    /// P236 — drive the three missile sound cues from the local ship's live world
    /// state each frame, mirroring the P232 projectile-sound wiring (same
    /// pure-planner pattern). Under the Gate lock it gathers every missile in the
    /// LOCAL ship's sector (the draw loop's own visibility filter — a missile in a
    /// neighbouring sector is separated by a warp and inaudible), pairs each with
    /// its listener distance and local-launch attribution, and hands that to the
    /// pure <see cref="Audio.MissileSoundPlanner"/>. Its commands are replayed
    /// against <see cref="Audio.SoundManager"/>: a newly-spawned missile that left
    /// OUR muzzle (spawn within
    /// <see cref="Audio.MissileAmbientSoundCurve.LaunchOriginRadius"/> — the port's
    /// local-vs-other discrimination, since native positions the launch sound at
    /// the firing ship) plays the launch one-shot; every in-sector missile gets a
    /// distance-scaled in-flight ambient loop; a missile that vanishes (impact /
    /// expiry / prune) plays the tiered explosion one-shot at its last-known
    /// detonation site. Console lines are the live audio-state surface (the same
    /// [monogame] audio convention as P213/P216/P232), used for the P236 live proof.
    /// </summary>
    private void UpdateMissileSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inSector = new List<Audio.MissileSoundSample>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                foreach (var (_, ms) in world.Missiles)
                {
                    if (ms.ClusterId != mySector) continue;
                    float dist = (ms.Position - anchor).Length();
                    inSector.Add(new Audio.MissileSoundSample(
                        ms.MissileId, ms.Position, dist,
                        Audio.MissileAmbientSoundCurve.IsLocalLaunch(dist)));
                }
            }
        }

        foreach (var cmd in _missileSoundPlanner.Plan(inSector))
        {
            switch (cmd.Action)
            {
                case Audio.MissileSoundAction.Launch:
                    snd.PlayMissileLaunch();
                    Console.WriteLine("[monogame] audio: missile launch");
                    break;
                case Audio.MissileSoundAction.AmbientStart:
                    snd.StartMissileAmbient(cmd.MissileId, cmd.Volume);
                    Console.WriteLine("[monogame] audio: missile ambient start");
                    break;
                case Audio.MissileSoundAction.AmbientUpdate:
                    snd.UpdateMissileAmbient(cmd.MissileId, cmd.Volume);
                    break;
                case Audio.MissileSoundAction.AmbientStop:
                    snd.StopMissileAmbient(cmd.MissileId);
                    Console.WriteLine("[monogame] audio: missile ambient stop");
                    break;
                case Audio.MissileSoundAction.Explosion:
                    snd.PlayMissileExplosion(cmd.Tier, cmd.Volume);
                    Console.WriteLine($"[monogame] audio: missile explosion (tier {cmd.Tier})");
                    break;
            }
        }
    }

    /// <summary>
    /// P264 — drive the incoming-missile warning loop from the server-streamed
    /// "an enemy missile is tracking YOU" state: each frame the pure
    /// <see cref="Audio.MissileWarningController"/> edge-detects arm / per-frame
    /// pitch / disarm from <c>ClientWorld.MissileTrack</c> (populated by the
    /// owner-only <c>MISSILE_TRACK_WARNING(208)</c>, the port of native's
    /// client-side check at <c>WinTrek/trekigc.cpp:5025-5071</c>), and the command
    /// is replayed against <see cref="Audio.SoundManager"/>: an arm starts the
    /// <c>missilelockonme</c> loop at the best-lock pitch, each armed frame
    /// refreshes the pitch ramp, a disarm stops it. Console lines are the live
    /// audio-state surface (the same [monogame] audio convention as P236).
    /// </summary>
    private void UpdateMissileWarning()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var track = world.MissileTrack;   // plain-data snapshot; no Gate needed
        var cmd = _missileWarningController.Plan(track.IsTracked, track.BestLock);
        if (cmd.Action == Audio.MissileWarningAction.None) return;

        snd.UpdateMissileWarning(cmd);
        switch (cmd.Action)
        {
            case Audio.MissileWarningAction.Arm:
                Console.WriteLine($"[monogame] audio: incoming missile warning ARMED (bestLock {cmd.BestLock:F3})");
                break;
            case Audio.MissileWarningAction.Disarm:
                Console.WriteLine("[monogame] audio: incoming missile warning CLEARED");
                break;
        }
    }

    /// <summary>
    /// P260 — drive the two probe sound cues from the live in-sector probe set
    /// each frame, mirroring the P236 missile wiring (same pure-planner pattern).
    /// Under the Gate lock it gathers every probe in the LOCAL ship's sector,
    /// pairs each with its listener distance and its own-side / own-deploy
    /// attribution, and hands that to the pure <see cref="Audio.ProbeSoundPlanner"/>.
    /// Its commands are replayed against <see cref="Audio.SoundManager"/>: a
    /// freshly-deployed probe that the LOCAL ship dropped (launcherShipId ==
    /// MyShipId) plays the deploy one-shot; every own-side in-sector probe gets a
    /// distance-scaled scanning ambient loop; a probe that vanishes (destroyed /
    /// expired / the player left its sector) stops its ambient. Console lines are
    /// the live audio-state surface (the same [monogame] audio convention as
    /// P232/P236), used for the P260 live proof.
    /// </summary>
    private void UpdateProbeSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inSector = new List<Audio.ProbeSoundSample>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                short mySide = world.MySideId ?? -1;
                foreach (var (_, pe) in world.Probes)
                {
                    if (pe.ClusterId != mySector) continue;
                    float dist = (pe.Position - anchor).Length();
                    inSector.Add(new Audio.ProbeSoundSample(
                        pe.ProbeId, pe.Position, dist,
                        IsOwnSide: pe.SideId == mySide,
                        IsOwnDeploy: pe.LauncherShipId == me));
                }
            }
        }

        foreach (var cmd in _probeSoundPlanner.Plan(inSector))
        {
            switch (cmd.Action)
            {
                case Audio.ProbeSoundAction.Deploy:
                    snd.PlayProbeDeploy();
                    Console.WriteLine("[monogame] audio: probe deploy");
                    break;
                case Audio.ProbeSoundAction.AmbientStart:
                    snd.StartProbeAmbient(cmd.ProbeId, cmd.Volume);
                    Console.WriteLine("[monogame] audio: probe ambient start");
                    break;
                case Audio.ProbeSoundAction.AmbientUpdate:
                    snd.UpdateProbeAmbient(cmd.ProbeId, cmd.Volume);
                    break;
                case Audio.ProbeSoundAction.AmbientStop:
                    snd.StopProbeAmbient(cmd.ProbeId);
                    Console.WriteLine("[monogame] audio: probe ambient stop");
                    break;
            }
        }
    }

    /// <summary>
    /// P237 — drive the warp ambient loop (native's <c>alephSound</c>) from the
    /// local ship's live world state each frame, mirroring the P232/P236
    /// pure-planner wiring. Under the Gate lock it gathers every warp in the
    /// LOCAL ship's sector that is within audible range (the same sector filter
    /// the draw loop uses — a warp in a neighbouring sector is a transit away and
    /// inaudible), pairs each with its listener distance, and hands that to the
    /// pure <see cref="Audio.WarpSoundPlanner"/>. Its commands are replayed
    /// against <see cref="Audio.SoundManager"/>: a warp that just came into range
    /// starts its looped <c>alephloop</c>; a warp still in range refreshes its
    /// volume to the current distance (the "increases as you get close" swell);
    /// a warp that left range (or the player left the sector) stops it. This is
    /// this port's distance-scaled approximation of native's per-warp
    /// <c>AmbientSoundSite</c> (<c>WinTrek/trekigc.cpp:1436-1437</c>), which
    /// starts <c>alephSound</c> (a <c>ThreeDSound(200, LoopingSound(...))</c> loop,
    /// sounddef.mdl:385) at each warp and lets the positional engine attenuate it.
    /// Console lines are the live audio-state surface (the same [monogame] audio
    /// convention as P213/P216/P232/P236), used for the P237 live proof.
    /// </summary>
    private void UpdateWarpSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inRange = new List<(short WarpId, float Distance)>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                foreach (var (warpId, wp) in world.Warps)
                {
                    if (wp.ClusterId != mySector) continue;
                    float dist = (Vec(wp.Position) - anchor).Length();
                    if (dist > Audio.WarpAmbientSoundCurve.AudibleRadius) continue;
                    inRange.Add((warpId, dist));
                }
            }
        }

        foreach (var cmd in _warpSoundPlanner.Plan(inRange))
        {
            switch (cmd.Action)
            {
                case Audio.WarpSoundAction.Start:
                    snd.StartWarpAmbient(cmd.WarpId, cmd.Volume);
                    Console.WriteLine($"[monogame] audio: warp ambient start (warp {cmd.WarpId}, vol {cmd.Volume:F3})");
                    break;
                case Audio.WarpSoundAction.Update:
                    snd.UpdateWarpAmbient(cmd.WarpId, cmd.Volume);
                    break;
                case Audio.WarpSoundAction.Stop:
                    snd.StopWarpAmbient(cmd.WarpId);
                    Console.WriteLine($"[monogame] audio: warp ambient stop (warp {cmd.WarpId})");
                    break;
            }
        }
    }

    /// <summary>P267 — drive the station exterior ambient loops (a looped
    /// <c>shipout</c> per station) from the live in-sector stations within
    /// audible range, mirroring the warp ambient wiring (P237) exactly: under
    /// the Gate lock it gathers every station in the local ship's sector with
    /// its listener distance, hands that to the pure
    /// <see cref="Audio.StationSoundPlanner"/>, and replays its
    /// Start/Update/Stop commands against <see cref="Audio.SoundManager"/>.
    /// Native attaches a per-object <c>AmbientSoundSite</c> to every station
    /// (<c>trekigc.cpp:1465-1468</c>), so this is a distance-based positional
    /// ambient — the real default is <c>shipout</c> (see
    /// <see cref="Audio.StationAmbientSoundCurve"/>). The console lines are the
    /// live audio-state surface.</summary>
    private void UpdateStationSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inRange = new List<(short StationId, float Distance)>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                foreach (var (stationId, st) in world.Stations)
                {
                    if (st.ClusterId != mySector) continue;
                    float dist = (Vec(st.Position) - anchor).Length();
                    if (dist > Audio.StationAmbientSoundCurve.AudibleRadius) continue;
                    inRange.Add((stationId, dist));
                }
            }
        }

        foreach (var cmd in _stationSoundPlanner.Plan(inRange))
        {
            switch (cmd.Action)
            {
                case Audio.StationSoundAction.Start:
                    snd.StartStationAmbient(cmd.StationId, cmd.Volume);
                    Console.WriteLine($"[monogame] audio: station ambient start (station {cmd.StationId}, vol {cmd.Volume:F3})");
                    break;
                case Audio.StationSoundAction.Update:
                    snd.UpdateStationAmbient(cmd.StationId, cmd.Volume);
                    break;
                case Audio.StationSoundAction.Stop:
                    snd.StopStationAmbient(cmd.StationId);
                    Console.WriteLine($"[monogame] audio: station ambient stop (station {cmd.StationId})");
                    break;
            }
        }
    }

    /// <summary>P267 — drain the world's one-shot sound events (station
    /// destroyed/captured, development completion) into the audio manager,
    /// replaying each exactly once with a console line for the live proof.
    /// Driven by the real <c>STATION_DESTROYED(59)</c>/<c>STATION_CAPTURE(58)</c>/
    /// <c>DEV_COMPLETED(8)</c> wire events (see ClientWorld's Apply handlers).</summary>
    private void UpdateSoundEvents()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        List<ClientSoundEvent> events;
        lock (_session.Gate)
            events = world.TakeSoundEvents();

        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case ClientSoundEventKind.StationDestroyed:
                    snd.PlayStationEvent(Audio.StationEventSoundKind.Destroyed);
                    Console.WriteLine($"[monogame] audio: station destroyed (station {ev.Id})");
                    break;
                case ClientSoundEventKind.StationEnemyDestroyed:
                    snd.PlayStationEvent(Audio.StationEventSoundKind.EnemyDestroyed);
                    Console.WriteLine($"[monogame] audio: enemy station destroyed (station {ev.Id})");
                    break;
                case ClientSoundEventKind.StationCaptured:
                    snd.PlayStationEvent(Audio.StationEventSoundKind.Captured);
                    Console.WriteLine($"[monogame] audio: station captured (station {ev.Id})");
                    break;
                case ClientSoundEventKind.StationEnemyCaptured:
                    snd.PlayStationEvent(Audio.StationEventSoundKind.EnemyCaptured);
                    Console.WriteLine($"[monogame] audio: enemy station captured (station {ev.Id})");
                    break;
                case ClientSoundEventKind.DevCompleted:
                    snd.PlayDevComplete();
                    Console.WriteLine($"[monogame] audio: development complete chime (dev {ev.Id})");
                    break;
            }
        }
    }

    /// <summary>
    /// P271 — whether the mission-briefing popup should be shown this frame and,
    /// when the countdown is known, the integer seconds remaining. The popup is
    /// visible once the server's SET_BRIEFING_TEXT(9) has arrived, until it is
    /// dismissed (Enter/Esc, see <see cref="DismissBriefing"/>) or the briefing
    /// countdown expires (<c>remaining &lt;= 0</c> — the mission is going live,
    /// native MissionBriefPopup's stage-advances-and-closes behaviour,
    /// teamscreen.cpp:1668-1704). <paramref name="remainingSeconds"/> is null
    /// while the countdown hasn't begun (SET_BRIEFING_TEXT can arrive before the
    /// later SET_START_TIME countdown start).
    /// </summary>
    private bool IsBriefingVisible(out int? remainingSeconds)
    {
        remainingSeconds = null;
        var world = _session.World;
        if (string.IsNullOrEmpty(world.BriefingText) || _briefingDismissed)
            return false;
        if (world.CountdownStartLocalMs is { } cd)
        {
            double elapsed = (Environment.TickCount64 - cd) / 1000.0;
            int remaining = (int)Math.Ceiling(Audio.CountdownSound.BriefingCountdownSeconds - elapsed);
            if (remaining <= 0)
                return false;      // countdown over → mission live → auto-close
            remainingSeconds = remaining;
        }
        return true;
    }

    /// <summary>P271 — latch a manual Enter/Esc dismissal of the mission-briefing
    /// popup so it does not reappear for the rest of the mission (SET_BRIEFING_TEXT
    /// is one-shot per join).</summary>
    private void DismissBriefing()
    {
        _briefingDismissed = true;
        Console.WriteLine("[monogame] briefing dismissed");
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
                // P228 — feed the local ship's hull fraction into the damage-flash
                // tracker and read whether the HUD should be flashing NOW. Only
                // this frame path touches the tracker, and only with the LOCAL
                // ship's hull, so the "damage to another ship never flashes"
                // guarantee holds by construction (native's pShip==GetShip() gate,
                // trekigc.cpp:4281). Monotonic clock = _nowSeconds (float cast is
                // ample precision for 0.25s flash halves).
                bool flashing = _flashTracker.Update((float)_nowSeconds, up.HullFraction);

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
                    RollDegrees: up.Orientation.GetRollDegrees());
            }
        }
        _hudRenderer.Draw(frame, _cameraMode, GraphicsDevice.Viewport, _lockBracket, _leadIndicator,
            _captureSpec is not null ? "OVERVIEW" : null);
    }

    /// <summary>
    /// P249 — gather the whole-mission cluster graph (positions from the real
    /// EXPORT OT_cluster data, edges from the CLUSTERINFO(200) graph) under the
    /// Gate lock and draw the starmap pane. The current sector comes from the
    /// local ship's SectorId, so the highlight tracks where the pilot actually
    /// is (and updates live as they transit sectors). Positions are the AUTHORED
    /// ScreenX/ScreenY layout coordinates native's sectormap uses; the renderer
    /// fits them into the panel via <see cref="StarmapLayout"/>. No world/wire
    /// change — this data already exists and is already correct (the brief's
    /// confirmed premise); this stage only renders it.
    /// </summary>
    private void DrawStarmap()
    {
        var world = _session.World;
        var sites = new List<StarmapLayout.ClusterSite>();
        IReadOnlyList<ClusterInfo.WarpEdge>? edges = null;
        short? current = null;
        SectorInfoPaneLayout.Data? pane = null;
        lock (_session.Gate)
        {
            if (world.Graph is { } g)
            {
                edges = g.Warps;
                foreach (var ce in g.Clusters)
                {
                    // A graph cluster always has a matching EXPORT OT_cluster
                    // (both are built from the mission's sectors), but guard
                    // anyway so a missing position can never crash the draw.
                    if (world.Clusters.TryGetValue(ce.Id, out var dc))
                        sites.Add(new StarmapLayout.ClusterSite(ce.Id, dc.Name, dc.ScreenX, dc.ScreenY));
                }
                if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own))
                    current = own.SectorId;

                // P272 — the info pane for the selected cluster: gather that
                // cluster's asteroids from the client's own decoded EXPORT
                // data (world.Asteroids is keyed by AsteroidId and carries
                // AabmCapabilities + Ore + ClusterId) and build the pure pane
                // content. Real data — nothing fabricated.
                if (_selectedSectorId is { } sid)
                {
                    string selName = world.Clusters.TryGetValue(sid, out var sdc) ? sdc.Name : "";
                    var samples = new List<SectorInfoPaneLayout.AsteroidSample>();
                    foreach (var ast in world.Asteroids.Values)
                        if (ast.ClusterId == sid)
                            samples.Add(new SectorInfoPaneLayout.AsteroidSample(ast.AabmCapabilities, ast.Ore));
                    pane = SectorInfoPaneLayout.Build(selName, samples);
                }
            }
        }
        if (edges is null || sites.Count == 0)
            return;

        var vp = GraphicsDevice.Viewport;
        float panelW = MathF.Min(vp.Width * 0.80f, 1040f);
        float panelH = MathF.Min(vp.Height * 0.66f, 640f);
        float panelX = (vp.Width - panelW) / 2f;
        float panelY = (vp.Height - panelH) / 2f;
        _starmapRenderer.Draw(sites, edges, current, vp, panelW, panelH);
        if (pane is { } p)
            _sectorInfoPaneRenderer.Draw(p, vp, panelX, panelY, panelW, panelH);
    }

    /// <summary>
    /// P272 — advance the starmap's selected cluster one step (delta +1 next,
    /// -1 prev) through the whole-mission graph's cluster-id order. First press
    /// (no selection yet) picks the player's current sector. Pure wrap-around
    /// lives in <see cref="SectorInfoPaneLayout.NextSelection"/> (unit-tested);
    /// this only pulls the ordered id list + the player's sector out of the
    /// world under the Gate lock.
    /// </summary>
    private void CycleSelectedSector(int delta)
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.Graph is not { } g || g.Clusters.Count == 0)
                return;
            var ids = g.Clusters.Select(c => c.Id).ToList();
            short mySector = ids[0];
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own))
                mySector = own.SectorId;
            _selectedSectorId = SectorInfoPaneLayout.NextSelection(_selectedSectorId, ids, mySector, delta);
            Console.WriteLine($"[monogame] starmap select: {_selectedSectorId}");
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
    private void DrawRadar(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        var world = _session.World;
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width, h = vp.Height;
        const float inset = 14f;

        var contacts = new List<RadarBracketRenderer.RadarContact>();
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return;
            if (!world.Ships.TryGetValue(me, out var ownEnt)) return;
            short mySector = ownEnt.SectorId;
            if (ownEnt.Latest is not { } ownUp) return;
            var ownPos = PoseConverter.ToXna(ownUp.Position);

            void Add(RadarBracketRenderer.RadarContactType type, Vector3 xnaPos, float dist)
            {
                var site = RadarProjection.Project(xnaPos, ownPos, view, proj, w, h, inset);
                float op = RadarBracketLayout.Opacity(dist,
                    RadarBracketLayout.CloseRange, RadarBracketLayout.FarRange);
                contacts.Add(new RadarBracketRenderer.RadarContact(
                    type, new NumericsVec2(site.Point.X, site.Point.Y),
                    site.Placement == RadarBracketLayout.RadarPlacement.OnScreen, op));
            }

            // Ships — other, living craft in my sector (skip self + corpses).
            foreach (var (id, ship) in world.Ships)
            {
                if (id == me || ship.IsDead) continue;
                if (ship.SectorId != mySector) continue;
                if (ship.Latest is not { } up) continue;
                Add(RadarBracketRenderer.RadarContactType.Ship,
                    PoseConverter.ToXna(up.Position), (ownUp.Position - up.Position).Length());
            }

            // Stations in my sector.
            foreach (var (_, st) in world.Stations)
            {
                if (st.ClusterId != mySector) continue;
                var sp = Vec(st.Position);
                Add(RadarBracketRenderer.RadarContactType.Station,
                    PoseConverter.ToXna(sp), (ownUp.Position - sp).Length());
            }

            // Warps in my sector.
            foreach (var (_, wp) in world.Warps)
            {
                if (wp.ClusterId != mySector) continue;
                var wPos = Vec(wp.Position);
                Add(RadarBracketRenderer.RadarContactType.Warp,
                    PoseConverter.ToXna(wPos), (ownUp.Position - wPos).Length());
            }
        }

        if (contacts.Count == 0)
            return;
        _radarRenderer.Draw(contacts, vp);
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
                RebuildVisibleRows();
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
        // _selectedIndex is an index INTO the filtered _visibleRows, so with no
        // filter active it behaves exactly as before (every row visible).
        bool up = ks.IsKeyDown(XnaKeys.Up);
        if (up && !_upWasDown && _visibleRows.Count > 0)
            _selectedIndex = (_selectedIndex - 1 + _visibleRows.Count) % _visibleRows.Count;
        _upWasDown = up;

        bool down = ks.IsKeyDown(XnaKeys.Down);
        if (down && !_downWasDown && _visibleRows.Count > 0)
            _selectedIndex = (_selectedIndex + 1) % _visibleRows.Count;
        _downWasDown = down;

        // Manual retry: R forces an immediate refetch (helps an unreachable lobby).
        bool rRetry = ks.IsKeyDown(XnaKeys.R);
        if (rRetry && !_rWasDown)
        {
            _lastLobbyFetch = double.NegativeInfinity;
            _lobbyError = null;
        }
        _rWasDown = rRetry;

        // P274 — F toggles the text filter (mnemonic: Find, native's
        // FindDialogPopup). Opening preserves any existing buffer; closing
        // clears it. The opening F keystroke never leaks into the buffer:
        // MonoGame pumps Window.TextInput events BEFORE this frame's Update
        // poll, so when the 'f' event fires _filterActive is still false and
        // OnTextInput's gate drops it.
        bool fKey = ks.IsKeyDown(XnaKeys.F);
        if (fKey && !_fWasDown)
        {
            _filterActive = !_filterActive;
            if (!_filterActive)
                _filterBuffer.Clear();
            RebuildVisibleRows();
            Console.WriteLine($"[monogame] lobby filter {( _filterActive ? $"ON '{_filterBuffer}'" : "OFF" )}");
        }
        _fWasDown = fKey;

        // While filtering, Backspace deletes the previous character and live-
        // narrows the list (mirrors the in-flight chat backspace handling).
        if (_filterActive)
        {
            bool back = ks.IsKeyDown(XnaKeys.Back);
            if (back && !_backWasDown && _filterBuffer.Length > 0)
            {
                _filterBuffer.Length -= 1;
                RebuildVisibleRows();
            }
            _backWasDown = back;
        }

        // Enter joins the highlighted server. The advertised address is parsed
        // for display and handed to BeginConnect; ConnectionSession pins the
        // transport to loopback, so a LAN-advertised address still reaches a
        // server on this host. On join failure we revert to the lobby with a
        // message rather than exiting.
        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        if (enter && !_enterWasDown && _visibleRows.Count > 0)
        {
            var s = _servers[_visibleRows[Math.Min(_selectedIndex, _visibleRows.Count - 1)]];
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

    /// <summary>Rebuild the <see cref="_visibleRows"/> index list from
    /// <see cref="_servers"/> under the current filter text, then re-clamp the
    /// selection into it. Called whenever the server list or the filter text
    /// changes (fetch result, F toggle, typing via OnTextInput, Backspace).
    /// Always leaves <see cref="_selectedIndex"/> in range.</summary>
    private void RebuildVisibleRows()
    {
        string needle = _filterActive ? _filterBuffer.ToString() : string.Empty;
        _visibleRows.Clear();
        for (int i = 0; i < _servers.Count; i++)
            if (_servers[i].Matches(needle))
                _visibleRows.Add(i);
        _selectedIndex = _visibleRows.Count > 0
            ? Math.Clamp(_selectedIndex, 0, _visibleRows.Count - 1)
            : 0;
    }

    /// <summary>Close the lobby filter and forget its text (Esc while filtering,
    /// or the off-half of the F toggle).</summary>
    private void ClearFilter()
    {
        _filterActive = false;
        _filterBuffer.Clear();
        RebuildVisibleRows();
        Console.WriteLine("[monogame] lobby filter cleared");
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
                    new Vector2(60f, 143f), dimCol);
            }
            else if (_filterActive && _visibleRows.Count == 0)
            {
                _spriteBatch.DrawString(_hudSmall, $"No servers match '{_filterBuffer}'.",
                    new Vector2(60f, 143f), new Color(1f, 0.6f, 0.4f));
            }

            // P274 — the live filter/edit line. Drawn above the list so the
            // narrowed selection is read against it. '_' is a blinking cursor
            // stand-in (fonts bake ASCII 32..126 only, no box-drawing glyphs).
            if (_filterActive)
            {
                string prompt = "Filter: " + _filterBuffer.ToString() + "_";
                var psz = _hudSmall.MeasureString(prompt);
                _spriteBatch.DrawString(_hudSmall, prompt, new Vector2(60f, 121f),
                    new Color(0.98f, 1.0f, 0.65f));
                string cnt = $"{_visibleRows.Count}/{_servers.Count}";
                _spriteBatch.DrawString(_hudSmall, cnt, new Vector2(60f + psz.X + 24f, 124f), dimCol);
            }

            const float rowH = 30f;
            const float listTop = 172f;
            for (int i = 0; i < _visibleRows.Count; i++)
            {
                var s = _servers[_visibleRows[i]];
                float y = listTop + i * rowH;
                bool sel = i == _selectedIndex;
                // Highlight bar behind the selected row.
                _spriteBatch.Draw(_whitePixel, new Rectangle(50, (int)y, (int)(w * 0.7f), (int)(rowH - 6f)),
                    sel ? new Color(0.08f, 0.35f, 0.33f, 0.55f) : new Color(0f, 0f, 0f, 0f));

                var line = s.FormatRow();
                _spriteBatch.DrawString(_hudSmall, line, new Vector2(56f, y + 2f), sel ? titleCol : textCol);
            }

            string footer = _filterActive
                ? "TYPE to filter   BACKSPACE delete   UP/DOWN select   ENTER join   ESC clear"
                : "UP/DOWN select   ENTER join   F find   R refresh   ESC quit";
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
            // P281 — the server now defers our spawn to a post-connect side-select
            // step: after LOGONACK it pushes a SIDE_ROSTER(211) snapshot and holds
            // the peer (no ship, no SET_CLUSTER) until we send FM_C_POSITIONREQ(39).
            // Enter the side-select screen here; UpdateSideSelect sends POSITIONREQ
            // and transitions to Flying once the server seats us (SET_CLUSTER sets
            // World.MyShipId).
            _state = ClientState.SideSelect;
            _sideSelectIndex = 0;
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

    // ── P281 — post-connect side-select ───────────────────────────────────

    /// <summary>Drive the post-connect side-select screen. We sit here (no ship)
    /// until the server seats us after we send FM_C_POSITIONREQ(39); the moment
    /// SET_CLUSTER assigns us a ship (<see cref="ClientWorld.MyShipId"/> is set),
    /// transition to the pre-flight ReadyCheck screen (P282) — NOT straight to
    /// Flying: the mission only starts once every occupied side is
    /// aggregate-ready, so the player must toggle ready first.</summary>
    private void UpdateSideSelect(KeyboardState ks)
    {
        // P306 — headless capture auto-seats on side 0 (where all the stress
        // bots live) instead of waiting for a human to pick a side / the
        // server's 30s auto-assign, so the video starts promptly.
        if (_captureSpec is not null)
        {
            if (_session.World.MyShipId is not null)
            {
                _state = ClientState.ReadyCheck;
                return;
            }
            var rosterCap = _session.World.SideRoster;
            if (rosterCap?.Sides.Length > 0 && !_capSideRequested)
            {
                _capSideRequested = true;
                short sideCap = rosterCap.Sides[0].SideId;
                _session.SendPositionReq(sideCap);
                Console.WriteLine($"[capture] auto side-select -> side {sideCap}");
            }
            return;
        }

        if (_session.World.MyShipId is not null)
        {
            _state = ClientState.ReadyCheck;
            return;
        }

        var roster = _session.World.SideRoster;
        int count = roster?.Sides.Length ?? 0;

        bool up = ks.IsKeyDown(XnaKeys.Up);
        if (up && !_ssUpWasDown && count > 0)
            _sideSelectIndex = (_sideSelectIndex - 1 + count) % count;
        _ssUpWasDown = up;

        bool down = ks.IsKeyDown(XnaKeys.Down);
        if (down && !_ssDownWasDown && count > 0)
            _sideSelectIndex = (_sideSelectIndex + 1) % count;
        _ssDownWasDown = down;

        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        bool space = ks.IsKeyDown(XnaKeys.Space);
        if (count > 0 && (enter && !_ssEnterWasDown || space && !_ssSpaceWasDown))
        {
            int sel = _sideSelectIndex % count;
            short sideId = roster!.Sides[sel].SideId;
            Console.WriteLine($"[monogame] side-select: requesting side {sideId}");
            _session.SendPositionReq(sideId);
        }
        _ssEnterWasDown = enter;
        _ssSpaceWasDown = space;

        bool r = ks.IsKeyDown(XnaKeys.R);
        if (r && !_ssRWasDown && _session.World.AmMissionOwner)
        {
            Console.WriteLine("[monogame] side-select: mission owner -> RANDOMIZE_TEAMS");
            _session.SendRandomizeTeams();
        }
        _ssRWasDown = r;
    }

    /// <summary>Render the post-connect side-select overlay from the current
    /// SIDE_ROSTER snapshot and the highlighted roster index.</summary>
    private void DrawSideSelect()
    {
        var rows = Rendering.SideSelectLayout.Build(
            _session.World.SideRoster, _sideSelectIndex, _session.World.AmMissionOwner);
        _sideSelectRenderer.Draw(GraphicsDevice.Viewport, rows);
    }

    // ── P282 — pre-flight ready-check ───────────────────────────────────────

    /// <summary>Drive the READY-CHECK screen (shown once seated, before the
    /// mission starts). The server holds the mission at NotStarted until every
    /// occupied side is aggregate-ready (P180 TryAutoStart); here the player
    /// toggles their own ready flag (Y), which sends a real PLAYER_READY(45)
    /// — the exact thing the old auto-ready hack faked. The mission owner can
    /// force their whole side ready (O, FORCE_TEAM_READY(47)), gated the same
    /// way P281's randomize-teams trigger is. As soon as the start countdown
    /// begins (all sides ready → server SET_START_TIME broadcast), leave for
    /// Flying — the client's Mission.Stage is pinned to Started by the join
    /// burst, so the countdown-began receipt is the real "the game is
    /// launching" signal (P216).</summary>
    private void UpdateReadyCheck(KeyboardState ks)
    {
        var world = _session.World;

        // P306 — headless capture: auto-ready once seated, then force the
        // ReadyCheck→Flying handoff after a short grace even if the server
        // never re-broadcasts a start countdown to a join-after-start client.
        if (_captureSpec is not null)
        {
            if (!_capReadySent && world.MyShipId is { } cpid)
            {
                _capReadySent = true;
                _capReadyWallMs = Environment.TickCount64;
                _session.SendPlayerReady(true, cpid);
                Console.WriteLine($"[capture] auto-ready PLAYER_READY ship {cpid}");
            }
            if (world.CountdownStartLocalMs is not null)
            {
                Console.WriteLine("[capture] ready-check: countdown began -> flying");
                _state = ClientState.Flying;
                return;
            }
            if (_capReadySent && !_capFlyForced &&
                Environment.TickCount64 - _capReadyWallMs >= 2000)
            {
                _capFlyForced = true;
                _state = ClientState.Flying;
                Console.WriteLine("[capture] ready-check: forced -> flying (2s grace)");
            }
            return;
        }

        // P309 — the server now reports the mission's REAL stage, including a
        // MISSION_STAGE(37) Started broadcast the moment the mission truly starts
        // (CompleteStartCountdown). A peer who joined during the pre-game hold or
        // countdown never received the countdown-began SET_START_TIME broadcast
        // (it went out to the peers present at that moment, before this peer
        // joined), so CountdownStartLocalMs stays null for them; the stage
        // reaching Started is the reliable, server-authoritative "the game is
        // live" signal that releases them into Flying. This is the client half of
        // the P309 stage fix: the local flight simulation is gated on MissionStage
        // reaching Started (the same stage the server's pre-game hold enforces),
        // closing the mid-countdown / late-join gap the old SET_START_TIME-only
        // handoff left open. Normal pre-game peers still exit on the countdown
        // below (stage is Starting, not yet Started, while the countdown runs).
        if (world.Mission?.Stage == (int)MissionStage.Started)
        {
            Console.WriteLine("[monogame] ready-check: mission stage -> Started, entering flying");
            _state = ClientState.Flying;
            return;
        }

        if (world.CountdownStartLocalMs is not null)
        {
            Console.WriteLine("[monogame] ready-check: mission start countdown began -> flying");
            _state = ClientState.Flying;
            return;
        }

        if (world.MyShipId is not { } pid) return;   // not seated yet (shouldn't happen)

        bool y = ks.IsKeyDown(XnaKeys.Y);
        if (y && !_rcYWasDown)
        {
            _ownReady = !_ownReady;
            _session.SendPlayerReady(_ownReady, pid);
            Console.WriteLine($"[monogame] ready-check: toggled own ready -> {_ownReady} (PLAYER_READY ship {pid})");
        }
        _rcYWasDown = y;

        bool o = ks.IsKeyDown(XnaKeys.O);
        if (o && !_rcOWasDown && world.AmMissionOwner && world.MySideId is { } sideId)
        {
            // Mission-owner override, gated exactly like P281's randomize-teams
            // trigger (the same AmMissionOwner / IsMissionOwnerPlayer gate).
            _session.SendForceTeamReady(true, sideId);
            Console.WriteLine($"[monogame] ready-check: mission owner FORCE_TEAM_READY side {sideId}");
        }
        _rcOWasDown = o;
    }

    /// <summary>Render the READY-CHECK overlay from the live world ready state
    /// (own toggle + same-side roster + per-side TEAM_READY aggregate).</summary>
    private void DrawReadyCheck()
    {
        var world = _session.World;
        var (sideName, players, agg) = Rendering.ReadyCheckRoster.Gather(world, _session.Gate, _ownReady);
        bool forced = world.MySideId is { } sideId
            && world.ForceReadyBySideId.TryGetValue(sideId, out var f) && f;
        var rows = Rendering.ReadyCheckLayout.Build(
            sideName, _ownReady, players, agg, forced, world.AmMissionOwner);
        _readyCheckRenderer.Draw(GraphicsDevice.Viewport, rows);
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
    /// appends printable characters to the active composer — the in-flight chat
    /// line, or (P274) the lobby filter box while <see cref="_filterActive"/>.
    /// Control characters (Enter/Backspace/Escape) are handled by the key-edge
    /// logic in <see cref="UpdateFlying"/> / <see cref="UpdateLobby"/>, so they
    /// are filtered here to avoid double handling.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        char c = e.Character;
        if (c == '\r' || c == '\n' || c == '\b' || c == (char)27) // Enter / Back / Esc
            return;
        if (c < 32 || c == 127)
            return;

        if (_state == ClientState.Lobby && _filterActive)
        {
            if (_filterBuffer.Length < MaxFilterLen)
            {
                _filterBuffer.Append(c);
                RebuildVisibleRows();   // narrow the visible list live as you type
            }
            return;
        }

        if (_state == ClientState.Flying && _chatActive)
        {
            if (_chatBuffer.Length < MaxChatLen)
                _chatBuffer.Append(c);
        }
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

        // P319 (finding H-2) — the watermark scan + forward walk index into the
        // same bounded 500-entry list the receive thread appends/evicts under
        // _session.Gate (EventLog.Record -> Append, driven from OnReceive).
        // Enumerating it unlocked during busy chat traffic threw
        // InvalidOperationException ("Collection was modified") on the render
        // thread, or could skip/duplicate a voice. Following the P306
        // ReadyCheckRoster.Gather precedent: snapshot the new arrivals and
        // advance the watermark under the lock, then play voices outside it
        // (playback touches only _sound / the static core db, never the shared
        // list). _lastVoiceProcessed is render-thread-only, so advancing it
        // inside the lock is safe and keeps behavior identical to before.
        var toVoice = new List<ChatMessage>();
        lock (_session.Gate)
        {
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
                toVoice.Add(chats[i]);
            if (toVoice.Count > 0)
                _lastVoiceProcessed = toVoice[^1];
        }

        foreach (var m in toVoice)
        {
            var e = _quickChatDb.ByVoiceOver(m.VoiceOver);
            if (e is not null && e.SoundStem.Length > 0
                && string.Equals(m.Message, e.Caption, StringComparison.Ordinal))
            {
                _sound.PlayVoice(e.SoundStem);
                Console.WriteLine($"[monogame] audio: quick-chat voice \"{e.SoundStem}\" (\"{m.Message}\")");
            }
        }
    }

    // ── P255 mining-facing helpers ───────────────────────────────────────────

    /// <summary>
    /// P255 — the own ship's mining-facing render override, delegated to the
    /// pure shared <see cref="MiningOrientation.Facing"/> (aim at the rock +
    /// gentle roll for effect; see its remarks for the native shipIGC.cpp:2251
    /// citation and why this is the render-side mirror of the mining autopilot).
    /// </summary>
    private Orientation MiningFacing(Orientation original, SimVec shipPos, float[] rockPos, float dtSeconds)
        => MiningOrientation.Facing(original, shipPos, rockPos, dtSeconds);

    // ── P212 player-autopilot helpers ─────────────────────────────────────────

    /// <summary>The kind of object an autopilot target can be.</summary>
    internal enum TargetKind { Ship, Station, Asteroid, Warp }

    /// <summary>A selected autopilot target: enough to re-resolve its live
    /// position each frame from <see cref="ClientWorld"/> (ships may move),
    /// plus a display name for the console / window-title readout.</summary>
    internal readonly record struct AutopilotTarget(TargetKind Kind, short Id, string Name);

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
    /// P230 — project the server-authoritative lock target (from
    /// <see cref="ClientCore.State.ClientWorld.MissileLock"/>, the owner-only
    /// <c>MISSILE_LOCK_UPDATE(204)</c>) into screen space for the target-box overlay,
    /// or return null when there is no lock target or it lies outside the view
    /// frustum. Same camera-relative projection as <see cref="CollectTargetCandidates"/>
    /// (subtract <paramref name="cameraEye"/>, transform through <c>view*proj</c>, reject
    /// behind-camera and out-of-frustum), then NDC → viewport px.
    /// </summary>
    private LockBracketView? ComputeLockBracket(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        var world = _session.World;
        MissileLockState lockState;
        Vector3 xnaTarget;
        lock (_session.Gate)
        {
            lockState = world.MissileLock;
            if (!lockState.HasTarget) return null;
            xnaTarget = PoseConverter.ToXna(lockState.TargetPosition);
        }

        var viewProj = view * proj;
        var rel = xnaTarget - cameraEye;
        var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
        if (clip.W <= 1e-6f) return null;                                   // behind the camera
        float sx = clip.X / clip.W;
        float sy = clip.Y / clip.W;
        if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return null;          // outside the FOV frustum

        var vp = GraphicsDevice.Viewport;
        var screen = new Vector2((sx * 0.5f + 0.5f) * vp.Width, (0.5f - sy * 0.5f) * vp.Height);
        return new LockBracketView(screen, MissileLockFeedback.Classify(lockState.LockFraction), _nowSeconds);
    }

    /// <summary>
    /// P266 — project the server-authoritative predictive lead (from
    /// <see cref="ClientCore.State.ClientWorld.LeadIndicator"/>, the owner-only
    /// <c>LEAD_INDICATOR_UPDATE(210)</c>) into screen space for the lead reticle
    /// overlay, or return null when there is no granted lead or the point is out
    /// of view. Uses the same camera-relative projection as
    /// <see cref="ComputeLockBracket"/> (subtract <paramref name="cameraEye"/>,
    /// transform through <c>view*proj</c>, reject behind-camera and out-of-frustum,
    /// then NDC → viewport px) for BOTH the lead point and the target's current
    /// centre — the two together expose the real predictive offset on screen.
    /// </summary>
    private LeadIndicatorView? ComputeLeadIndicator(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        LeadIndicatorState state;
        lock (_session.Gate)
        {
            state = _session.World.LeadIndicator;
            if (!state.HasLead) return null;
        }

        if (!ProjectToScreen(PoseConverter.ToXna(state.LeadWorldPos), view, proj, cameraEye, out Vector2 leadPx)) return null;
        if (!ProjectToScreen(PoseConverter.ToXna(state.TargetPos), view, proj, cameraEye, out Vector2 targetPx)) return null;

        return new LeadIndicatorView(leadPx, targetPx, state.InRange, state.Green, _nowSeconds);
    }

    /// <summary>
    /// P266 — the shared world→screen projection used by
    /// <see cref="ComputeLeadIndicator"/>: subtract the camera eye, transform
    /// through <c>view*proj</c>, reject a point behind the camera or outside the
    /// FOV frustum, then map NDC to viewport pixels. Returns false (and nothing in
    /// <paramref name="screen"/>) when the point is not visible.
    /// </summary>
    private bool ProjectToScreen(Vector3 world, Matrix view, Matrix proj, Vector3 cameraEye, out Vector2 screen)
    {
        screen = default;
        var viewProj = view * proj;
        var rel = world - cameraEye;
        var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
        if (clip.W <= 1e-6f) return false;                 // behind the camera
        float sx = clip.X / clip.W;
        float sy = clip.Y / clip.W;
        if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return false;   // outside the FOV frustum
        var vp = GraphicsDevice.Viewport;
        screen = new Vector2((sx * 0.5f + 0.5f) * vp.Width, (0.5f - sy * 0.5f) * vp.Height);
        return true;
    }

    /// <summary>
    /// P295 — whether the local pilot is currently boarded as a gunner on a
    /// carrier's turret. Reuses the port's existing turret-boarding state
    /// (<see cref="Allegiance.ClientCore.State.ShipEntity.ParentId"/> ≠ NA/-1),
    /// the same state P109/P273 use to identify a boarded gunner; it is not
    /// re-derived here. Drives which modes the <c>V</c> camera cycle offers.
    /// </summary>
    private bool IsTurretGunner()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return false;
            if (!world.Ships.TryGetValue(me, out var own)) return false;
            return own.ParentId != -1;   // NA when not docked/turreted
        }
    }

    /// <summary>
    /// P295 — the carrier's render pose for the external-turret camera: the
    /// position/rotation of the ship the local pilot is gunning on (its
    /// <see cref="Allegiance.ClientCore.State.ShipEntity.ParentId"/>). Returns
    /// false when not gunning, or when the carrier's pose is not currently
    /// known to the renderer (caller falls back to a normal chase view).
    /// </summary>
    private bool TryGetGunnerCarrierPose(out Vector3 carrierPos, out Matrix carrierRot)
    {
        carrierPos = default;
        carrierRot = Matrix.Identity;
        short parent;
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return false;
            if (!world.Ships.TryGetValue(me, out var own)) return false;
            parent = own.ParentId;
            if (parent == -1) return false;   // NA — not docked/turreted
        }
        if (!_poses.TryGetValue(parent, out var cp)) return false;
        carrierPos = PoseConverter.ToXna(cp.Position);
        carrierRot = PoseConverter.ToXnaRotation(cp.Orientation);
        return true;
    }

    /// <summary>
    /// P295 — the world position of the missile the external-missile camera
    /// should watch: the most recently created live missile in the local
    /// player's sector (cluster). The client has no per-ship "last missile
    /// fired" pointer and the missile wire carries no shooter id
    /// (<see cref="Allegiance.ClientCore.State.MissileEntity"/>), so — faithful
    /// in spirit to native's <c>GetLastMissileFired()</c> — it watches the
    /// newest in-sector missile (missile ids are monotonically increasing
    /// shorts). Returns false when no missile is flying in-sector (caller falls
    /// back to a chase view, matching native WinTrek.cpp:7259-7268).
    /// </summary>
    private bool TryGetLatestMissilePos(out Vector3 missileXna)
    {
        missileXna = default;
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return false;
            if (!world.Ships.TryGetValue(me, out var own)) return false;
            short mySector = own.SectorId;
            MissileEntity? best = null;
            foreach (var (_, m) in world.Missiles)
            {
                if (m.ClusterId != mySector) continue;
                if (best is null || (short)(m.MissileId - best.MissileId) > 0)
                    best = m;
            }
            if (best is null) return false;
            missileXna = PoseConverter.ToXna(best.Position);
        }
        return true;
    }

    /// <summary>
    /// Soft-lock target candidates (P212, cycling added live 2026-09-14; P256
    /// priority tiers added live 2026-09-18): every known non-self, non-dead ship,
    /// station, asteroid and warp whose projected screen position lands inside the
    /// view frustum, sorted nearest-to-crosshair first within a strict priority
    /// tier. Candidates are projected through the same view/projection matrices
    /// <see cref="Draw"/> builds for the active camera (the chase camera is
    /// sampled with a zero delta so its turn-smoothing state is not disturbed by
    /// the extra call). No true 3D ray-pick — this cone/nearest-to-crosshair
    /// approximation is the intended POC scope.
    ///
    /// Priority tiers, highest first — a tier is offered ONLY when no higher tier
    /// has anything in view, and when it is offered it is the only tier in the pool
    /// (everything below it is suppressed outright, not merely deprioritized):
    ///   1. Ships — with ~134 asteroids per map, one rock is almost always more
    ///      centered on the crosshair than a moving ship, so a plain
    ///      nearest-to-crosshair contest across everything made ship-targeting
    ///      nearly unreachable whenever any asteroid shared the view (confirmed
    ///      live: a real ship was in-frustum and an asteroid still won).
    ///   2. Warps — the alephs between sectors are sparse (~2-4 per sector) yet
    ///      are the whole reason to hit T/G, so they must not be buried behind the
    ///      dense asteroid field. P256's direct fix for "I can't target alephs":
    ///      previously warps were pooled with stations and all ~134 asteroids and
    ///      sorted purely by crosshair distance, so asteroids dominated the order
    ///      and T stepped past many rocks before ever reaching a warp.
    ///   3. Stations — also sparse (~2 per map) and a primary navigation
    ///      destination (dock / refuel / buy), so they earn their own tier for the
    ///      exact same crowding reason as warps.
    ///   4. Asteroids — the most numerous static scenery; offered only when no
    ///      ship, warp or station is in view.
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

        var objs = new List<CandidateObject>();
        lock (_session.Gate)
        {
            // Tier 1 source — ships (always gathered; only offered when one is in
            // view). The self / dead / no-update filters match the original method.
            if (world.MyShipId is { } me)
            {
                foreach (var (id, ship) in world.Ships)
                {
                    if (id == me || ship.IsDead || ship.Latest is not { } up) continue;
                    string nm = string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName;
                    objs.Add(new CandidateObject(up.Position, TargetKind.Ship, id, nm));
                }
            }
            // Tier 2/3/4 sources — warps, stations, asteroids (all gathered; the
            // tier ordering decides which is actually offered).
            foreach (var (id, wp) in world.Warps)
                objs.Add(new CandidateObject(Vec(wp.Position), TargetKind.Warp, id, wp.Name));
            foreach (var (id, st) in world.Stations)
                objs.Add(new CandidateObject(Vec(st.Position), TargetKind.Station, id, st.Name));
            foreach (var (id, ast) in world.Asteroids)
                objs.Add(new CandidateObject(Vec(ast.Position), TargetKind.Asteroid, id, ast.Name));
        }

        var ordered = SelectCandidates(objs, p =>
        {
            var rel = PoseConverter.ToXna(p) - eye;
            var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
            if (clip.W <= 1e-6f) return null;                         // behind the camera
            float sx = clip.X / clip.W;
            float sy = clip.Y / clip.W;
            if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return null;// outside the FOV frustum
            return (sx, sy);
        }, out int shipsInFrustum, out int warpsInFrustum, out int stationsInFrustum,
           out int asteroidsInFrustum, out string winningTier);

        int shipsSeen = objs.Count(o => o.Kind == TargetKind.Ship);
        int warpsSeen = objs.Count(o => o.Kind == TargetKind.Warp);
        int stationsSeen = objs.Count(o => o.Kind == TargetKind.Station);
        int asteroidsSeen = objs.Count(o => o.Kind == TargetKind.Asteroid);

        var preview = string.Join(", ", ordered.Take(6).Select(t => t.Name));
        Console.WriteLine($"[monogame] target-pick: ships {shipsInFrustum}/{shipsSeen} in view, " +
                          $"{stationsSeen} station(s), {asteroidsSeen} asteroid(s), {warpsSeen} warp(s) scanned, " +
                          $"tier={winningTier}, candidates={ordered.Count} [{preview}{(ordered.Count > 6 ? ", …" : "")}], mode={_cameraMode}");

        return ordered;
    }

    /// <summary>A raw target candidate to feed <see cref="SelectCandidates"/>: a
    /// world position (<see cref="SimVec"/>), its <see cref="TargetKind"/>, and its
    /// display name/id. The world-object filters (self / dead / no-update ships)
    /// are the caller's job; the tier ordering is pure here.</summary>
    internal readonly record struct CandidateObject(SimVec Position, TargetKind Kind, short Id, string Name);

    /// <summary>P256 — pure candidate collection + priority-tier ordering. Given
    /// every object to consider and a projector that maps a world position to a
    /// normalized screen-space offset (sx, sy) — null when the point falls outside
    /// the view frustum — returns the in-view candidates ordered nearest-to-
    /// crosshair within the HIGHEST present tier, offering ONLY that tier
    /// (everything below it is suppressed outright, not merely deprioritized). Tier
    /// order: <b>Ships → Warps → Stations → Asteroids</b>.
    ///
    /// Extracted from <see cref="CollectTargetCandidates"/> so the priority logic is
    /// a single, headless, testable source of truth (reached from the live-verify
    /// harness via InternalsVisibleTo). See <see cref="CollectTargetCandidates"/>
    /// for the full rationale of each tier.</summary>
    internal static List<AutopilotTarget> SelectCandidates(
        IReadOnlyList<CandidateObject> objects,
        Func<SimVec, (float Sx, float Sy)?> projectToScreen,
        out int shipsInFrustum, out int warpsInFrustum, out int stationsInFrustum, out int asteroidsInFrustum,
        out string winningTier)
    {
        // Fill a per-kind bucket with only the in-view members. The enum's integer
        // order (Ship, Station, Asteroid, Warp) is NOT the priority order, so rank
        // each kind explicitly; then offer the single lowest-rank non-empty bucket.
        var buckets = new List<(AutopilotTarget Target, float DistSq)>[4];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<(AutopilotTarget Target, float DistSq)>();
        foreach (var o in objects)
        {
            if (projectToScreen(o.Position) is not { } scr) continue;
            buckets[(int)o.Kind].Add((new AutopilotTarget(o.Kind, o.Id, o.Name), scr.Sx * scr.Sx + scr.Sy * scr.Sy));
        }

        shipsInFrustum = buckets[(int)TargetKind.Ship].Count;
        warpsInFrustum = buckets[(int)TargetKind.Warp].Count;
        stationsInFrustum = buckets[(int)TargetKind.Station].Count;
        asteroidsInFrustum = buckets[(int)TargetKind.Asteroid].Count;

        static int Rank(TargetKind k) => k switch
        {
            TargetKind.Ship => 0,
            TargetKind.Warp => 1,
            TargetKind.Station => 2,
            TargetKind.Asteroid => 3,
            _ => 4,
        };

        int best = int.MaxValue;
        for (int t = 0; t < buckets.Length; t++)
            if (buckets[t].Count > 0) best = Math.Min(best, Rank((TargetKind)t));

        if (best == int.MaxValue)
        {
            winningTier = "none";
            return new List<AutopilotTarget>();
        }

        for (int t = 0; t < buckets.Length; t++)
        {
            if (buckets[t].Count == 0 || Rank((TargetKind)t) != best) continue;
            winningTier = ((TargetKind)t).ToString().ToLowerInvariant();
            buckets[t].Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
            return buckets[t].ConvertAll(c => c.Target);
        }
        winningTier = "none";
        return new List<AutopilotTarget>();
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

    /// <summary>
    /// P286 — the shared per-resource-type diffuse for asteroids, built lazily
    /// from <see cref="RockTextureFactory.GeneratePixels"/> (deterministic,
    /// seeded) and cached so the draw loop only pays for the handful of types
    /// a sector actually shows, never one texture per rock.
    /// </summary>
    private Texture2D EnsureAsteroidTexture(RockTextureFactory.AsteroidResourceType type)
    {
        if (!_asteroidTextures.TryGetValue(type, out var tex))
        {
            var px = RockTextureFactory.GeneratePixels(
                RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSeed, type);
            tex = new Texture2D(GraphicsDevice, RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSize);
            tex.SetData(px);
            _asteroidTextures[type] = tex;
        }
        return tex;
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
        if (_hullResolver is null || _shopCatalog is null) return null;
        if (hullId == 0) return null;                              // no SHIP_STATUS/EXPORT yet
        var civKey = CivKeyForShipSide(_session.World, sideId);
        if (civKey is null) return null;
        var asset = _hullResolver.ResolveAsset(civKey, hullId, _shopCatalog);
        if (asset is null) return null;
        return _shipModels.TryGetValue(asset, out var m) ? m : null;
    }

    /// <summary>Resolve a ship's side id to a manifest civ key via the real
    /// side roster, or null when unknown (no roster, or a civ with no art).</summary>
    private static string? CivKeyForShipSide(Allegiance.ClientCore.State.ClientWorld world, short sideId)
    {
        if (world.SideRoster is not { } roster) return null;
        foreach (var s in roster.Sides)
            if (s.SideId == sideId)
                return Rendering.HullModelResolver.CivKeyByIdShort(s.CivId);
        return null;
    }

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
    /// nose points along the world transform's forward).</summary>
    private void DrawMissileContentModel(XnaModel model, Matrix world, Matrix view, Matrix projection)
    {
        foreach (var mesh in model.Meshes)
        {
            foreach (var effect in mesh.Effects)
            {
                if (effect is not BasicEffect basic) continue;
                basic.World = mesh.ParentBone.Transform * Rendering.ShipRenderer.SharedNoseAlignment * world;
                basic.View = view;
                basic.Projection = projection;
                basic.EnableDefaultLighting();
                basic.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            }
            mesh.Draw();
        }
    }

    protected override void UnloadContent()
    {
        _sound?.Dispose();
        _music?.Dispose();   // P304 — release the two music beds
        if (_asteroidRenderers != null)
            foreach (var r in _asteroidRenderers) r?.Dispose();
        if (_asteroidTextures != null)
            foreach (var t in _asteroidTextures.Values) t.Dispose();
        _warpRenderer?.Dispose();
        _projectileRenderer?.Dispose();
        _missileRenderer?.Dispose();
        _probeRenderer?.Dispose();
        _mineRenderer?.Dispose();
        _trailRenderer?.Dispose();
        _warpParticleRenderer?.Dispose();
        _explosionRenderer?.Dispose();
        _explosionAssets?.Dispose();
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

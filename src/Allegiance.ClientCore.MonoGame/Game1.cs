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
public sealed partial class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly ConnectionSession _session;
    private readonly string _playerName;

    private XnaModel _fallbackModel = null!;
    private readonly Dictionary<string, XnaModel> _shipModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XnaModel> _missileModels = new(StringComparer.Ordinal);
    // P337 — the real Meshy baseColor textures for the P287 ship/missile content
    // models, keyed by the same asset names HullModelResolver enumerates and
    // loaded in parallel with the models (the geometry-only FBX has no embedded
    // material, so the texture is bound explicitly at render time). Loaded
    // eagerly like _shipModels/_missileModels. The fallback gets its own texture
    // (bios_scout_upscaled) so it doesn't stay matte while everything else
    // gains paint.
    private readonly Dictionary<string, Texture2D> _shipTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _missileTextures = new(StringComparer.Ordinal);
    private Texture2D _fallbackTexture = null!;
    // P337 — the per-imported-missile-model texture, keyed by the singleton Model
    // instance (one per asset), mirroring _missileScaleByModel.
    private readonly Dictionary<XnaModel, Texture2D> _missileTextureByModel = new();
    // P336 — per-imported-missile-model uniform scale that makes the content
    // model's true rendered radius equal its real .mdl native size, keyed by the
    // singleton Model instance (one per asset). 1 (no scaling) when unresolved.
    private readonly Dictionary<XnaModel, float> _missileScaleByModel = new();
    private HullModelResolver _hullResolver = null!;
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

    // P358 — the ship engine-thrust trail (native AddThrustParticles, the
    // "rocket" fire+smoke case, thinggeo.cpp:758-917). The pure shared pool +
    // camera-facing billboard renderer; every visible ship with real thrust
    // emits a fire+smoke pair from its aft each frame. Driven by a per-ship
    // ramped "thrust power" (native CafterburnerIGC power, afterburnerIGC.cpp:
    // 133-146) tracked here from the real StateM.AfterburnerButton bit already
    // on the wire — so the trail appears when a ship boosts and thins/stops as
    // the power decays once it stops (matching native's m_fThrustPower > 0 gate).
    private readonly Rendering.ShipEngineTrailParticles _engineTrail = new();
    private Rendering.ShipEngineTrailRenderer _engineTrailRenderer = null!;
    // Per-frame emission inputs for every currently-thrusting visible ship +
    // the native-style afterburner power ramp, both rebuilt under the Gate lock
    // in the same pass that gathers _poses.
    private readonly Dictionary<short, EngineTrailEmitter> _engineTrailEmitters = new();
    private readonly Dictionary<short, float> _shipThrustPower = new();

    // P365 — the engine nozzle glow decal (native ThingGeoImpl::RenderGlow +
    // UpdateGlow, thinggeo.cpp:1419-1443,440-460). A static camera-facing soft
    // sprite drawn at every thrusting visible ship's aft nozzle, tinted and
    // scaled by the REAL HSB thrust math (EngineNozzleGlow) — so it shifts from a
    // dim small red (low thrust) to a bright large yellow-white (full thrust /
    // afterburner). Distinct from the P358 exhaust particle plume (a separate
    // "smoke"/"rocket"-hardpoint effect): this is the flat nozzle bloom. Driven by
    // the same already-known per-ship wire data P358 uses (StateM.AfterburnerButton
    // + the real Controls.Throttle axis); no new wire data.
    private Rendering.EngineNozzleGlowRenderer _nozzleGlowRenderer = null!;
    private readonly Dictionary<short, EngineNozzleGlowEmitter> _nozzleGlowEmitters = new();
    private readonly List<Rendering.EngineGlowView> _drawNozzleGlows = new();
    // P367 — navigation strobe lights (native LightsGeo, efapp.cpp:99-286). A
    // small tinted decal at each visible ship's/structure's wingtips+stern (the
    // standard red-port/green-starboard/white-tail scheme), each driven by the
    // REAL LightsGeo::Render pulse envelope (NavStrobeMath.Pulse: ramp-up → hold →
    // ramp-down → off, efapp.cpp:177-206) from elapsed game time, so the lights
    // genuinely blink — alternating red/green sides, not a static glow. Drawn for
    // the local hull (non-cockpit) AND every visible remote ship/structure.
    private Rendering.NavStrobeRenderer _navStrobeRenderer = null!;
    private readonly Dictionary<short, NavStrobeEmitter> _navStrobeEmitters = new();
    private readonly List<Rendering.NavStrobeView> _drawNavStrobes = new();

    // P363 — the ship side-coloured motion trail (native TrailGeo ribbon,
    // efgeo.cpp:492-661; CreateTrailGeo built by ThingSite::SetTrailColor,
    // thinggeo.cpp:921-954). Genuine continuous strip geometry (NOT the
    // particle-puff systems P297/P358 use): a per-ship rolling history of
    // recent anchor positions that stretches behind the ship while it moves
    // and is tinted by the ship's real side colour at half intensity. Fed
    // every visible ship's anchor/right/colour in the pose-gather (Update)
    // and advanced+drawn additively in Draw.
    private readonly Rendering.ShipMotionTrailRibbons _motionTrail = new();
    private Rendering.ShipMotionTrailRenderer _motionTrailRenderer = null!;

    // P364 — the projectile-impact spark (native AddHullHit debris bits,
    // thinggeo.cpp:1324-1345). The pure shared pool + camera-facing billboard
    // renderer; each frame's 3D pass drains the buffered PROJECTILE_HULL_HIT(213)
    // hits (gathered under the Gate), emits the two bits per own-sector hit at
    // the real impact point/normal, then ages + draws them.
    private readonly Rendering.HullHitSparkParticles _hullHitParticles = new();
    private Rendering.HullHitSparkRenderer _hullHitSparkRenderer = null!;
    // Hits drained this frame (world.TakeNewHullHits), filtered to the local
    // ship's own sector, emitted into the pool exactly once.
    private readonly List<Allegiance.ClientCore.State.HullHitEntity> _pendingHullHits = new();
    private readonly List<HullHitSparkView> _drawHullHitViews = new();

    // P326 — per-frame Draw gather buffers (review finding M-4). The 3D pass
    // used to allocate ~15 fresh List<>s + 3 LINQ ToList()s every Draw call —
    // a steady stream of gen-0 garbage that grows with ship/particle count.
    // These are persistent, capacity-growing buffers cleared (.Clear()) and
    // refilled each frame instead of reallocated — the same reuse pattern this
    // file already applies to _poses/_damageEmitters. Touched only on the
    // render thread (Draw), so the existing per-gather Gate lock suffices.
    // Drawn content and order are unchanged: this is pure allocation reduction.
    private readonly List<(AsteroidSpin Spin, SimVec Pos, Orientation Ori, float Radius, int Variant, RockTextureFactory.AsteroidResourceType Type)> _drawRooks = new();
    private readonly List<(SimVec Pos, Orientation Ori, float Radius)> _drawWarps = new();
    private readonly List<(SimVec Pos, Orientation Ori, short SideId, float Radius)> _drawStations = new();
    private readonly List<(Vector3 Pos, float Radius, float Alpha)> _drawBuilds = new();
    private readonly List<(SimVec Pos, SimVec Dir, float Len)> _drawBolts = new();
    private readonly List<(SimVec Pos, Orientation Ori, XnaModel? Model)> _drawMissiles = new();
    private readonly List<TrailMissile> _drawTrailMissiles = new();
    private readonly List<(Vector3 Pos, short SideId)> _drawProbes = new();
    private readonly List<(Vector3 Pos, float Radius, int Seed)> _drawMines = new();
    private readonly List<(Vector3 Pos, float Radius, string Stem, float Roll)> _drawChaff = new();
    private readonly List<Vector3> _drawTreasures = new();
    private readonly List<ExplosionEntity> _drawExplosions = new();
    // Scratch buffers for the particle-pool Enumerate() snapshots (no fresh
    // ToList() per frame; AddRange consumes the lazy enumerator synchronously
    // into the reused list — safe, the pools mutate only on this render thread).
    private readonly List<TrailParticleView> _drawTrailViews = new();
    private readonly List<WarpSparkView> _drawSparkViews = new();
    private readonly List<DamageDebrisView> _drawDebrisViews = new();
    private readonly List<TrailParticleView> _drawEngineTrailViews = new();
    private readonly List<Rendering.ShipTrailStrip> _drawMotionTrailStrips = new();
    private readonly List<RadarBracketRenderer.RadarContact> _drawRadarContacts = new();
    // P372 — the per-ship in-world side-colour "team tags" (hull-attached side
    // indicator for the 3D view), gathered in the same DrawRadar pass as the
    // brackets and rendered by ShipSideTagRenderer.
    private readonly List<Rendering.ShipSideTagRenderer.ShipTag> _drawShipTags = new();

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
    private Rendering.ShipSideTagRenderer _shipTagRenderer = null!;
    private Rendering.HelpOverlayRenderer _helpOverlayRenderer = null!;
    private Rendering.GameOverScreenRenderer _gameOverScreenRenderer = null!;
    // P320 — the session-loss screen (server crash / network drop), drawn when
    // the established connection is lost. Mirrors the game-over renderer's
    // pattern but with an alarm palette so it reads as trouble, not as a win.
    private Rendering.ConnectionLostScreenRenderer _connectionLostRenderer = null!;

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

    // P385 — client-only --debugMode launch flag: flips the capture-overview AND
    // the in-flight (chase/cockpit/missile) camera FOVs to native's real default
    // (s_fDefaultFOV, WinTrek.h:26 = 50°) so live framing can be compared against
    // native without a permanent change. Paired by hand with the server's
    // liveSettings.debugMode; never sent on the wire.
    private readonly bool _debugMode;

    // P385 — capture-overview vertical FOV in degrees. Normal launches use the
    // wide 100° fleet shot (~130° horizontal at 16:9) so individual ships read;
    // --debugMode overrides this to native's real 50° default. Default stays
    // 100f; flag-absent launches are unaffected.
    private float _captureFovDeg = 100f;

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

    // P337 — per-ship real texture bound in the Draw loop, collected under the
    // same Gate lock as _shipModelById (parallel array: same asset that resolved
    // the model resolves its texture; null → fallback texture). Lock-free read.
    private readonly Dictionary<short, Texture2D?> _shipTextureById = new();

    // P333 — per-ship PURE VISUAL render-scale factor (from the ship's real
    // HullType.Length and the true rendered size of its model), collected under
    // the same Gate lock as _shipModelById so the Draw loop reads it lock-free.
    // 1.0 = no scaling (unknown hull/model); it normalizes each ship's raw model
    // size to the real hull size so ships read at proportional real-world scale.
    private readonly Dictionary<short, float> _shipScaleById = new();

    // P338 — the local ship's real rendered radius (0.5 × HullType.Length,
    // the radius P333 normalized the model to), captured under the Gate in the
    // pose-gather pass and used to frame the chase camera so the whole ship
    // always fills the frame regardless of class. 0 when the hull is unknown →
    // the chase camera falls back to the reference framing.
    private float _myRenderRadius;

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
    // P333 — hull id → real HullType.Length, built once from the loaded shop
    // catalogue (missing hull/empty catalogue → no entry → scale 1, no scaling).
    private Dictionary<short, float> _hullLengthById = new();
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
    private bool _iKeyWasDown; // edge-detect the I (invert mouse pitch) toggle so holding it doesn't re-fire

    // P380 — real analog joystick flight input, a port of native
    // SenseJoystick (WinTrek/WinTrek.cpp:10818-11016) onto MonoGame's real
    // cross-platform joystick API (Microsoft.Xna.Framework.Input.Joystick,
    // previously unused in this codebase). The axis→role layout, the exact
    // MapJoystick deadzone/rescale and the throttle latch all live in
    // JoystickFlightController; this glue only gathers the raw axis ints from
    // the connected joystick once per frame. Keyboard remains the always-
    // present base layer; joystick axes override specific ControlData fields
    // post-Translate exactly like the mouse virtual-joystick above, and
    // joystick buttons OR into stateM alongside the keyboard (never replace).
    private readonly JoystickFlightController _joystickFlight = new();
    private int? _joystickIndex;   // cached connected joystick index (hotplug re-resolved each frame)

    // P212 — player autopilot ("click-and-go"). T selects the candidate nearest
    // the crosshair; G toggles autopilot on/off toward the selected target; ANY
    // real manual flight input auto-disengages (native WinTrek SenseJoystick
    // behaviour). _autoTarget is null when nothing is selected; engaged is
    // orthogonal — you can hold a target while autopilot is off.
    private AutopilotTarget? _autoTarget;
    /// <summary>Target's world position at the moment autopilot engaged — used
    /// only to tell a stationary target from a moving one for the arrival
    /// disengage check in <see cref="TryGetAutopilotControls"/>.</summary>
    private SimVec? _autoTargetAtEngage;
    private bool _autoPilotEngaged;
    // M-1 — the mouse-joystick deflection captured the moment the autopilot
    // engaged (native's trekJoyStick baseline, WinTrek.cpp:8283-8289). Manual-
    // input disengage only fires when the live deflection leaves this baseline
    // by more than 2× the deadzone, so hand tremor never drops the autopilot.
    private MouseDeflection _autoPilotMouseBaseline;
    // P230 — the per-frame lock-target box descriptor computed in Draw (projected
    // from the world's lock target) and consumed by DrawHud; null when there is no
    // lock target or it is out of view.
    private LockBracketView? _lockBracket;
    // P345 — the per-frame descriptor for the player's OWN T/G autopilot-target
    // selection bracket, computed in Draw (projected from _autoTarget's resolved
    // live position) and consumed by DrawHud; null when no target is selected or it
    // is out of view. Deliberately distinct from _lockBracket (a server
    // missile-lock concept) — they can both be active at once.
    private TargetSelectionView? _targetSelectionBracket;
    // P359 — the per-frame screen-space unit DIRECTION from the camera toward the
    // selected target (regardless of whether it projects into the view frustum),
    // computed in Draw and consumed by DrawHud as the bearing arrow. Null when no
    // target is selected or it is not resolvable. Kept SEPARATE from the bracket
    // so the arrow stays visible even when the target is off-screen (the whole
    // point of a bearing indicator) — the bracket itself is null out of view.
    private Vector2? _targetBearingScreenDir;
    // P366 — the leader-only bot dock/launch command, resolved in DrawHud (under
    // the gate lock) from _autoTarget + world.IsTeamLeader; null when the local
    // player is NOT the team leader or the selected target is not a same-side
    // bot. Drawn as a HUD prompt and consumed by the X-key handler in Update.
    private LeaderBotCommand? _leaderBotCommand;
    private bool _xKeyWasDown; // edge-detect X (leader bot dock/launch) so holding it doesn't re-fire
    // P266 — the per-frame lead-indicator descriptor computed in Draw (projected
    // from the world's lead point + target) and consumed by DrawHud; null when
    // there is no lead granted or it is out of view.
    private LeadIndicatorView? _leadIndicator;
    private bool _tKeyWasDown; // edge-detect T so holding it doesn't re-fire
    private bool _gKeyWasDown; // edge-detect G so holding it doesn't re-fire
    private bool _kKeyWasDown; // edge-detect K (suicide) so holding it doesn't re-fire
    private bool _cKeyWasDown; // edge-detect C (chaff drop) so holding it doesn't re-fire
    private bool _nKeyWasDown; // edge-detect N (mine/probe drop) so holding it doesn't re-fire

    // P249+P360 — whole-mission starmap / persistent mini-map. P249 originally
    // hid the map behind Tab (nothing on screen until pressed). P360 restores
    // native's real always-on behaviour (native shows the sector map in the HUD
    // by default, m_bShowSectorMapPane; see the P249 overview's own note): a
    // COMPACT mini-map is now ALWAYS drawn in the bottom-right corner during
    // normal flight (no key needed), and Tab expands it into the full centered
    // starmap pane / collapses it back (matching this keyboard-centric HUD's
    // conventions — the mouse is the M virtual-joystick steering axis, not a
    // picker). _minimapExpanded=false → the corner mini-map; true → the full
    // pane. The game keeps simulating underneath (an integrated HUD pane like
    // native's, not a paused full-screen mode).
    private bool _minimapExpanded;
    private bool _tabKeyWasDown; // edge-detect Tab so holding it doesn't re-fire
    // P272 — starmap cluster selection. While the starmap is EXPANDED, ',' /
    // '.' cycle the selected cluster (info pane) back/forward; keyboard is
    // chosen over mouse hover because there is no map-cursor picking wired
    // into the MonoGame flight controls (the mouse is the M virtual-joystick
    // steering axis, not a picker) — see the UpdateFlying block for the
    // key-choice rationale. _selectedSectorId is null until the pilot picks
    // one; it only drives the info pane in the EXPANDED view.
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
    // P362 — per-mounted-system status derived from the live ammo fraction.
    private readonly Flight.WeaponReloadTracker _weaponReloadTracker = new();
    private short _weaponFrameShipId = -1;
    private long _lastWeaponReloadCount;

    private readonly string _windowTitleBase;



    // P320 — latched once the transport reports the established session lost, so
    // we transition to Disconnected exactly once (never re-fire per frame) and
    // stop sending controls to a dead socket.
    private bool _disconnectTransitioned;

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
    private bool _altF4WasDown;

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
        // P385 — client-only --debugMode launch flag (paired by hand with the
        // server's liveSettings.debugMode while testing; never sent on the wire).
        // Flips the camera FOVs (capture-overview and in-flight) to native's real
        // 50° default so framing can be compared live against native.
        _debugMode = opts.DebugMode;
        if (_debugMode)
        {
            _captureFovDeg = 50f;
            Console.WriteLine("[client] --debugMode: camera FOV set to native default 50° (vertical), was 100°");
        }
        // P338 — a chase-mode capture drives the local ship's real chase camera;
        // force that camera mode so the production chase path runs.
        if (_captureSpec is { Chase: true })
            _cameraMode = CameraMode.Chase;
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














    // P358 — the native-style afterburner power ramp rates (afterburnerIGC.cpp:
    // 137-144; native uses per-afterburner core data onRate/offRate — these
    // representative values reach full power in ~0.17s and drain in ~0.33s, so
    // the trail appears promptly on boost and visibly thins over ~⅓s after the
    // afterburner button is released before stopping).
    private const float ThrustPowerOnRate = 6f, ThrustPowerOffRate = 3f;









    // P363 — the ribbon's half-width per side as a fraction of the ship's
    // radius, native ThingSite::SetOrientation: m_vecRightTrail = GetRight() * m_radius * 0.25
    // (thinggeo.cpp:1176-1178).
    private const float TrailWidthFraction = 0.25f;



    /// <summary>P306 — the capture overview camera: frame the WHOLE fleet (all
    /// known <see cref="_poses"/> sim positions) from a raised viewpoint
    /// behind the formation centroid, sizing the distance so the bounding
    /// sphere of the fleet fits the vertical FOV with margin. Camera-relative
    /// like every other camera (P207 floating-origin rule): returns the real
    /// sim-space eye in XNA coords via <paramref name="cameraEye"/> and every
    /// object draw subtracts it.</summary>
    private static bool _capCamLogged;


















    // ── P214 — lobby / server browser ────────────────────────────────────────

    /// <summary>How often the lobby list is re-polled (a few seconds, not every
    /// frame — the directory is a slowly-changing remote source).</summary>
    private const double LobbyRefreshSeconds = 3.0;









    // ── P281 — post-connect side-select ───────────────────────────────────



    // ── P282 — pre-flight ready-check ───────────────────────────────────────







    // ── P217 — quick-chat voice macros ───────────────────────────────────────









    // ── P255 mining-facing helpers ───────────────────────────────────────────


    // ── P212 player-autopilot helpers ─────────────────────────────────────────































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
        _hullHitSparkRenderer?.Dispose();
        _explosionAssets?.Dispose();
        _session.Dispose();
        _http.Dispose();
        base.UnloadContent();
    }

}

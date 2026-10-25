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
    private ConnectionSession _session;
    private string _playerName;   // P500 — no longer readonly: the connect screen edits it in-game
    // P498 — the asset source (Artwork tree) all real-asset loaders read through;
    // defaulted to the desktop on-disk source in the constructor.
    private readonly Assets.IAssetSource _assets;

    private XnaModel _fallbackModel = null!;
    // P607 — the guaranteed-non-null procedural stand-in used when a ship has
    // neither native art nor a Meshy pack fallback, so the colour pass can
    // never pass null to ShipRenderer.Draw (the P607 live crash).
    private XnaModel _shipStandInModel = null!;
    private readonly Dictionary<string, XnaModel> _shipModels = new(StringComparer.Ordinal);
    // P581 — the set of loaded model asset keys (incl. any "repaired/<asset>"
    // variants), maintained alongside _shipModels for the repaired preview resolver.
    private readonly HashSet<string> _shipModelKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XnaModel> _missileModels = new(StringComparer.Ordinal);
    // P593b — the per-type NATIVE-<c>.mdl</c> missile renderer (own real mesh +
    // native texture per missile type), closing the lit-solid "white shard"
    // defect for the 45/56 missile types that are not P287-imported content
    // assets. Created in Lifecycle; field declared with the other missile fields.
    private HdMaterialRenderer _hdMaterials = null!;
    private Rendering.NativeMissileRenderer _nativeMissileRenderer = null!;
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
    private Rendering.RockTextures _rockTextures = null!;   // P421 — real original surface bitmaps
    private Rendering.LockTextures _lockTextures = null!;    // P428 — real missile-lock sprites
    private Rendering.IndicatorTextures _indicatorTextures = null!;   // HUD-8 — flight indicator bitmaps
    private Rendering.TargetPaneTextures _targetPaneTextures = null!;   // HUD-3 — native target-pane bitmaps
    private Rendering.FeedbackTextures _feedbackTextures = null!;   // P578 — eject-pod panel + death banner art
    private Rendering.HudNativeTextures _hudNativeTextures = null!; // HUD-10 — compose/money/command/endurance art
    private Rendering.StatusTextures _statusTextures = null!;   // HUD-6 — own-ship status icons
    private Rendering.InventoryPaneTextures _inventoryTextures = null!;   // HUD-7 — inventory pane art
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
    // P544 — the new native-style projectile renderers: a textured, spinning,
    // camera-facing billboard / directional-decal sprite path
    // (ProjectileSpriteRenderer) and a 3D-model path (ProjectileModelRenderer)
    // for the laser-cannon/MassDriver rounds, plus a cached per-type visual
    // resolver (ProjectileVisual) fed by the loaded static core. The legacy
    // _projectileRenderer (the tinted box) remains as the missing-asset
    // fallback so nothing renders invisible.
    private ProjectileSpriteRenderer _projectileSpriteRenderer = null!;
    private ProjectileModelRenderer _projectileModelRenderer = null!;
    private readonly Dictionary<short, ProjectileVisual> _projectileVisuals = new();
    private MissileRenderer _missileRenderer = null!;
    // P290 — the shared REAL probe model mesh (the same runtime native-.mdl
    // story as the missile renderer; see ProbeModelSource). Built once here
    // from a real probe .mdl parsed at runtime and drawn per deployed probe at
    // its real position. Unlike the missile mesh, the probe mesh winds for
    // MonoGame's DEFAULT back-face culling (the P290 empirical finding — see
    // ProbeRenderer), so no special flip is applied.
    private ProbeRenderer _probeRenderer = null!;
    private readonly Dictionary<short, ProbeRenderer?> _probeTypes = new();
    private MineRenderer _mineRenderer = null!;
    private TreasureRenderer _treasureRenderer = null!;
    private NativeTreasureRenderer _nativeTreasureRenderer = null!;
    private ChaffRenderer _chaffRenderer = null!;

    // P235 — missile exhaust trail. The pure particle pool (no MonoGame on its
    // surface) plus the camera-facing billboard renderer. _trailNowSec is the
    // pool's monotonic clock, advanced by real elapsed time each frame in Draw
    // (feeds both the per-missile velocity estimate and particle aging).
    private readonly Rendering.FlightEffectPools _flightEffects = new();
    private MissileTrailParticles _trailParticles => _flightEffects.Missiles;
    private ITrailRandom _trailRandom = new RandomTrailSource();
    private MissileTrailRenderer _trailRenderer = null!;
    private double _trailNowSec;

    // P296 — warp-transit teleport spark spray (native AddTeleportParticles).
    // The pure particle pool + camera-facing billboard renderer; the burst fires
    // once per transit reusing WarpTransitFlash's one-shot edge (see DrawHud).
    private Rendering.WarpTransitParticles _warpParticles => _flightEffects.Warp;
    private Rendering.WarpTransitParticleRenderer _warpParticleRenderer = null!;
    // Transit-instant ship state (position/velocity/axes) buffered in DrawHud
    // when WarpTransitFlash.JustTransited arms, consumed on the next frame's 3D
    // scene render (DrawHud runs after the 3D pass, so the burst is one frame
    // late — imperceptible, and structurally exactly-once per transit).
    private (Allegiance.Core.Collision.Vector3 Position, Allegiance.Core.Collision.Vector3 Velocity,
        Allegiance.Core.Collision.Vector3 Forward, Allegiance.Core.Collision.Vector3 Right)? _pendingTeleportBurst;

    // P781 — the lead-up spark emitters gathered under the Gate in the same pose
    // pass that fills _poses (one per ship whose hull is drawn this frame: the
    // own ship outside the cockpit, plus every visible remote), consumed by the
    // 3D render pass. Preallocated and reused; no per-frame allocation.
    private readonly Rendering.TeleportEmitter[] _teleportEmitters = new Rendering.TeleportEmitter[64];
    private int _teleportEmitterCount;
    // P781 — previous rendered positions for the per-ship frame velocity
    // (native vecFrameVelocity, thinggeo.cpp:506-512). Reused dictionary,
    // pruned on BeginView; first frame falls back to the wire velocity.
    private readonly Dictionary<short, Allegiance.Core.Collision.Vector3> _lastSparkPose = new();
    // P781 — the 60 Hz emission accumulator (native emits per rendered frame,
    // which would scale the spray with fps; see WarpTransitParticles remarks).
    private double _teleportEmitAccumulator;
    // P781 — per-hull RipcordSpeed, cached so the per-frame pose pass never
    // walks the shop catalogue per ship (the side GA is applied live).
    private readonly Dictionary<short, float> _ripcordSpeedByHull = new();
    // P781 — the view sector's aleph centres for the per-ship nearest-aleph ETA
    // (native WinTrek.cpp:7783-7802), rebuilt only on a sector / warp-count change.
    private readonly List<Allegiance.Core.Collision.Vector3> _viewWarpCentres = new();
    private short _viewWarpSector = short.MinValue;
    private int _viewWarpWorldCount = -1;

    // P297 — the continuous damage-debris smoke stream (native AddDamageParticles,
    // thinggeo.cpp:699-755). The pure shared pool + camera-facing billboard
    // renderer; each frame's 3D pass emits one puff per damage vent per damaged
    // visible ship (driven by each ship's real hull fraction via
    // Flight.DamageDebrisTracker), then ages + draws them. See the emit block
    // in Draw for how the per-ship emission inputs are gathered under the Gate.
    private Rendering.DamageDebrisParticles _damageDebris => _flightEffects.Damage;
    private Rendering.DamageDebrisRenderer _damageDebrisRenderer = null!;
    // Per-frame emission inputs for every currently-damaged visible ship,
    // rebuilt under the Gate lock in the same pass that gathers _poses.
    private readonly Dictionary<short, DamageEmitter> _damageEmitters = new();

    // P449a — ambient space dust (native DebrisGeo, debrisgeo.cpp:17-205). The
    // pure camera-relative particle pool + camera-facing soft-quad renderer; stepped
    // every flying frame with the ACTIVE camera's eye/basis/focus so it follows
    // whatever pose the camera uses (including the P447b predicted render pose) in
    // every flight camera mode, and drawn behind the ships over the starfield. The
    // density preference (native's user `Debris`, default 1.0, 0 = off) comes from
    // the --debris launch setting. Created in the ctor (needs opts + the RNG seam).
    private Rendering.DebrisGeo _debrisGeo;
    private Rendering.AmbientDustRenderer _debrisRenderer = null!;
    // Per-frame grain snapshot (reused list; native renders from the fixed points,
    // so we only re-derive brightness/size/colour each frame).
    private readonly List<Rendering.AmbientDustView> _drawDustViews = new();

    // P358 — the ship engine-thrust trail (native AddThrustParticles, the
    // "rocket" fire+smoke case, thinggeo.cpp:758-917). The pure shared pool +
    // camera-facing billboard renderer; every visible ship with real thrust
    // emits a fire+smoke pair from its aft each frame. Driven by a per-ship
    // ramped "thrust power" (native CafterburnerIGC power, afterburnerIGC.cpp:
    // 133-146) tracked here from the real StateM.AfterburnerButton bit already
    // on the wire — so the trail appears when a ship boosts and thins/stops as
    // the power decays once it stops (matching native's m_fThrustPower > 0 gate).
    private Rendering.ShipEngineTrailParticles _engineTrail => _flightEffects.Engines;
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
    private Rendering.ShipMotionTrailRibbons _motionTrail => _flightEffects.Motion;
    private Rendering.ShipMotionTrailRenderer _motionTrailRenderer = null!;

    // P364 — the projectile-impact spark (native AddHullHit debris bits,
    // thinggeo.cpp:1324-1345). The pure shared pool + camera-facing billboard
    // renderer; each frame's 3D pass drains the buffered PROJECTILE_HULL_HIT(213)
    // hits (gathered under the Gate), emits the two bits per own-sector hit at
    // the real impact point/normal, then ages + draws them.
    private Rendering.HullHitSparkParticles _hullHitParticles => _flightEffects.HullHits;
    private Rendering.HullHitSparkRenderer _hullHitSparkRenderer = null!;
    // Hits drained this frame (world.TakeNewHullHits), filtered to the local
    // ship's own sector, emitted into the pool exactly once.
    private readonly List<Allegiance.ClientCore.State.HullHitEntity> _pendingHullHits = new();
    private readonly List<HullHitSparkView> _drawHullHitViews = new();
    private readonly List<Rendering.HullHitFlashView> _drawHullHitFlashes = new();
    // P545 — the shared native effect-texture cache (owns the decoded art +
    // soft fallback; disposed here after the renderers that borrow it).
    private Rendering.NativeEffectTextures _nativeEffects = null!;

    // P463b — the shield-hit flare (native ThingGeo::AddFlare, thinggeo.cpp:
    // 1243-1301). Consumes the SAME drained ProjectileHullHit stream as the hull
    // spark, but only the AbsorbedByShield=true case (a shield-absorbed strike
    // spawns no hull spark — Game1.Rendering.cs); a flare is emitted on the
    // struck ship's shield toward the hit, aged, and drawn additively.
    private Rendering.ShieldFlareParticles _shieldFlareParticles => _flightEffects.Shields;
    private Rendering.ShieldFlareRenderer _shieldFlareRenderer = null!;
    private readonly List<ShieldFlareView> _drawShieldFlares = new();

    // P326 — per-frame Draw gather buffers (review finding M-4). The 3D pass
    // used to allocate ~15 fresh List<>s + 3 LINQ ToList()s every Draw call —
    // a steady stream of gen-0 garbage that grows with ship/particle count.
    // These are persistent, capacity-growing buffers cleared (.Clear()) and
    // refilled each frame instead of reallocated — the same reuse pattern this
    // file already applies to _poses/_damageEmitters. Touched only on the
    // render thread (Draw), so the existing per-gather Gate lock suffices.
    // Drawn content and order are unchanged: this is pure allocation reduction.
    private readonly List<(AsteroidSpin Spin, SimVec Pos, Orientation Ori, float Radius, int Variant, RockTextureFactory.AsteroidResourceType Type, string ModelStem, string TextureName)> _drawRooks = new();
    // P529 — the current local sector's lighting, computed once per frame from
    // the local ship's cluster and applied to every asteroid draw that frame.
    private Allegiance.Core.Rendering.SectorLighting? _sectorLighting;
    private readonly List<(short Id, SimVec Pos, Orientation Ori, float Radius, string TextureName)> _drawWarps = new();
    /// <summary>P758 — per-warp bomb countdowns (seconds until the carried
    /// detonation deadline), snapshotted under the session gate each frame for
    /// the destabilized-aleph draw pass (<see cref="Rendering.WarpBombVisual"/>).</summary>
    private readonly Dictionary<short, double> _warpBombRemaining = new();
    private readonly List<(SimVec Pos, Orientation Ori, short SideId, float Radius, string? ModelStem, string? TextureName)> _drawStations = new();
    private readonly List<(Vector3 Pos, float Radius, float Alpha)> _drawBuilds = new();
    private readonly List<(SimVec Pos, SimVec Dir, float Len, float Spin, ProjectileVisual Visual)> _drawBolts = new();
    private readonly List<(short TypeId, SimVec Pos, Orientation Ori, XnaModel? Model)> _drawMissiles = new();
    private readonly List<TrailMissile> _drawTrailMissiles = new();
    private readonly List<(Vector3 Pos, short SideId, short TypeId)> _drawProbes = new();
    private readonly List<(Vector3 Pos, float Radius, int Seed, float Strength)> _drawMines = new();
    private readonly List<(Vector3 Pos, float Radius, string Stem, float Roll)> _drawChaff = new();

    /// <summary>P586 — a treasure's resolved native visual (part/development
    /// model, acs14, artifact or the biosflag decal) plus its position and
    /// object id, gathered each frame for the native treasure draw pass (the
    /// pre-P586 <see cref="_drawTreasures"/> held only positions).</summary>
    private readonly List<TreasureDrawItem> _drawTreasureItems = new();

    /// <summary>P586 — per-treasure accumulated age (seconds since received),
    /// the elapsed input to the deterministic spin (<see cref="Rendering.NativeTreasureRenderer.SpinTransform"/>).</summary>
    private readonly Dictionary<short, double> _treasureAge = new();

    /// <summary>P586 — per-object-id latch so an unresolved treasure (missing
    /// model / unresolvable part id) is logged once, not every frame.</summary>
    private readonly HashSet<short> _treasureUnresolvedLogged = new();
    private readonly List<ExplosionEntity> _drawExplosions = new();
    // Scratch buffers for the particle-pool Enumerate() snapshots (no fresh
    // ToList() per frame; AddRange consumes the lazy enumerator synchronously
    // into the reused list — safe, the pools mutate only on this render thread).
    private readonly List<TrailParticleView> _drawTrailViews = new();
    private readonly List<WarpSparkView> _drawSparkViews = new();
    private readonly List<DamageDebrisView> _drawDebrisViews = new();
    private readonly List<TrailParticleView> _drawEngineTrailViews = new();
    private readonly List<Rendering.ShipTrailStrip> _drawMotionTrailStrips = new();
    private readonly List<HudContact> _drawRadarContacts = new();
    // HUD-11 — long-lived snapshot scratch: the per-frame HUD snapshot copies
    // the hot contact/inventory lists into these reused buffers (no ToArray per
    // frame) and shares the HUD-5b label cache with the row-count pass.
    private readonly Rendering.HudSnapshotBuffers _hudSnapshotBuffers = new();
    // HUD-5b — reused candidate buffer for the native nearest-enemy lookup
    // (RadarLabelModel.NearestEnemy), filled only while bRangeLabels is on.
    private readonly List<RadarLabelModel.NearestEnemyCandidate> _nearestEnemyScratch = new();
    // HUD-5b — opt-in real-client evidence: ALLEGIANCE_RADAR_PROOF=1 makes the
    // client log the radar snapshot held on the frame each autoshot captures
    // (which contacts, which of them enemies, their range labels and the
    // nearest-enemy bracket), so a screenshot can be tied to real radar state.
    private readonly bool _radarProofEnabled = Environment.GetEnvironmentVariable("ALLEGIANCE_RADAR_PROOF") == "1";
    private string? _radarProofLine;
    // P372 — the per-ship in-world side-colour "team tags" (hull-attached side
    // indicator for the 3D view), gathered in the same DrawRadar pass as the
    // brackets and rendered by ShipSideTagRenderer.
    private readonly List<Rendering.ShipSideTagRenderer.ShipTag> _drawShipTags = new();

    // P263 — the real explosion VFX: decoded sprite artwork + the billboard
    // renderer that plays each blast with native size/duration/fade.
    private ExplosionAssets _explosionAssets = null!;
    private ExplosionRenderer _explosionRenderer = null!;

    // P546 — the ENHANCED explosion + smoke path: CC0 flipbook fire-balls,
    // animated premultiplied sector-lit smoke, near-camera fade and a density
    // budget. NULL unless EnhancedVfx is ON (Native mode keeps the exact
    // P263/P545 path above and never constructs these, so no ~10 MiB of CC0
    // atlases are loaded).
    private bool _enhancedVfx;
    private Rendering.VfxAssets? _vfxAssets;
    private Rendering.EnhancedSmokeField? _enhancedSmoke;
    private Rendering.EnhancedExplosionRenderer? _enhancedExplosionRenderer;

    // P573 — the Volumetric tier: effective effects quality, the selected
    // volumetric technique (A raymarch / B displaced sphere), the live renderer,
    // and the one-shot GPU fallback log.
    private EffectsQuality _effectsQuality;
    private EffectsQuality _requestedQuality;
    private VolumetricTechnique _volumetricTech;
    private Rendering.VolumetricRenderer? _volumetricRenderer;
    private Rendering.DisplacedSphereRenderer? _displacedRenderer;
    private bool _volumetricFallbackLogged;

    /// <summary>P573 — platform-family probe used to pick the effects-tier
    /// default: DesktopGL is the powerful case (Volumetric default); Android/iOS
    /// get the baked Enhanced tier (A's fill-rate is too heavy for GLES2).</summary>
    private static bool IsMobilePlatform =>
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS();

    // P573 — GPU over-budget window state for the Volumetric tier fallback.
    private double _volBudgetSum, _volBudgetCount, _volBudgetWindow;

    /// <summary>P573 — the Volumetric tier's frame-time budget. If the smoothed
    /// frame time exceeds this (ms) for a sustained window while volumetric is
    /// active, the tier drops to Enhanced once (logged). A generous default: it
    /// only trips under genuinely pathological fill-rate, not ordinary scenes.</summary>
    public const double VolumetricFrameBudgetMs = 80.0;
    public const double VolumetricBudgetWindowSeconds = 3.0;

    /// <summary>P573 — frame-time over-budget probe. Smoothes <paramref name="dtSeconds"/>
    /// over a ~3 s window; if the average frame time breaches the budget while the
    /// Volumetric tier is live, fall back to Enhanced once (GPU can't sustain the
    /// raymarch) and log it. This is the "frame-time over budget → drop a tier"
    /// half of the automatic fallback (the shader-load half runs in LoadContent).</summary>
    private void EnsureVolumetricBudget(double dtSeconds)
    {
        if (_effectsQuality != EffectsQuality.Volumetric || (_volumetricRenderer is null && _displacedRenderer is null)) return;
        _volBudgetSum += dtSeconds;
        _volBudgetCount += 1;
        _volBudgetWindow += dtSeconds;
        if (_volBudgetWindow < VolumetricBudgetWindowSeconds) return;

        double avgMs = _volBudgetSum / Math.Max(1, _volBudgetCount) * 1000.0;
        _volBudgetSum = 0; _volBudgetCount = 0; _volBudgetWindow = 0;
        if (avgMs <= VolumetricFrameBudgetMs) return;

        if (!_volumetricFallbackLogged)
        {
            Console.WriteLine($"[client] P573: effects quality VOLUMETRIC -> ENHANCED (frame time {avgMs:0}ms > {VolumetricFrameBudgetMs:0}ms budget)");
            _volumetricFallbackLogged = true;
        }
        _volumetricRenderer?.Dispose();
        _volumetricRenderer = null;
        _displacedRenderer?.Dispose();   // P573
        _displacedRenderer = null;
        _effectsQuality = EffectsQuality.Enhanced;
    }

    // P547 — custom shaders + post-processing for the ENHANCED VFX path:
    // the shader cache, the post-processor (linear-depth target, scene target,
    // soft-particle renderer, bloom), and the per-frame soft-particle context.
    private Rendering.ShaderAssetCache? _shaders;
    private Rendering.VfxPostProcessor? _post;
    private bool _bloomEnabled;   // Bloom setting (default Off; --bloom on)
    private bool _softEnabled;    // soft particles (default On in Enhanced; --soft off)
    private Rendering.SoftParticleContext? _softContext;
    // Depth-pass ship gather: (model, world, scale) re-drawn with the linear
    // depth effect before the smoke, so soft particles see hulls.
    private readonly List<(Microsoft.Xna.Framework.Graphics.Model Model, Matrix World, float Scale)> _drawShipDepth = new();
    // P566 — per-frame native-ship depth geometry (original .mdl, native scale),
    // gathered alongside _drawShipDepth so the depth pass silhouettes match the
    // color pass exactly in native mode.
    private readonly List<(Rendering.NativeShipRenderer.NativeShipGeometry Geo, Matrix World, float Scale)> _drawNativeShipDepth = new();
    // Depth-pass asteroid/station world matrices, gathered alongside their
    // color draws so the depth pass re-renders the EXACT same geometry.
    // P567 — carries the native (no-radius-scale) world + native scale + model
    // stem so the depth pass can draw a rock from its native .mdl mesh too.
    private readonly List<(Matrix World, int Variant, Matrix NativeWorld, float NativeScale, string ModelStem)> _drawRookDepth = new();
    private readonly List<(Matrix World, Matrix NativeWorld, float NativeScale, string? ModelStem)> _drawStationDepth = new();

    // P231 + P465b — projectile bolt sizing/tint. A projectile's wire
    // (PROJECTILE_INSTANCE) carries only a type id; the per-type visual data —
    // real width and (when a core carries a non-black one) colour — now comes
    // from the loaded static core via ProjectileStyle.For(typeId, _projectileTypes)
    // (P465b). What the port still supplies here are the fallbacks and the
    // speed-proportional length: a fixed default width (ProjectileStyle.DefaultWidth,
    // the P231/P243 0.35), a length that grows with speed (faster shots read as
    // longer streaks, clamped to stay sane), and the amber tracer colour that
    // stands in for every type when the type is unknown or the core is missing
    // (ProjectileStyle.AmberFallback — PCore001 carries no per-type colour, so
    // its bolts all render amber; see the ProjectileStyle remarks for why native
    // does the same). Visibility radius ≈ the camera far plane, the "sector
    // filter" analog for a wire that carries no projectile sector.
    // P248 (2026-09-17): shrunk from 0.12/32/480 — Domingo live, after the
    // sideways-orientation fix above: "the line looks as long as a missile,
    // make it shorter... just like the missiles". A real missile's measured
    // extent (mis05.mdl, P233) is 1.73x1.73x11.35 units; these three constants
    // are retuned so a typical ~600u/s Gatling shot (the reference figure
    // cited in P239/P243) lands at 12 units — close to that missile length —
    // instead of the old 72u.
    private const float BoltLengthPerUnitSeconds = Allegiance.ClientCore.State.ProjectileVisibility.BoltLengthPerUnitSeconds;
    private const float MinBoltLength = Allegiance.ClientCore.State.ProjectileVisibility.MinBoltLength;
    private const float MaxBoltLength = Allegiance.ClientCore.State.ProjectileVisibility.MaxBoltLength;
    private const float ProjectileVisibilityRadiusSqr = Allegiance.ClientCore.State.ProjectileVisibility.VisibilityRadiusSqr;
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
    private const float ProjectileNearCamClearance = Allegiance.ClientCore.State.ProjectileVisibility.NearCamClearance;
    // P465b — the amber tint constant is gone: the per-bolt tint and width now
    // come from ProjectileStyle.For(typeId, _projectileTypes) per bolt (real
    // width where the core carries it, else the 0.35 default above; amber
    // fallback for unknown/missing). The near-cam cull and the box scale in
    // Game1.Rendering read the per-bolt width from the draw tuple, so a real
    // 0.1-wide bolt (PCore001's Mini-Gun/A-B Can/Pulse Laser series) renders
    // thin and its cull gap is proportionally safer.

    // P234 — missile surface tint. The client has no static core to read
    // per-type DataMissileTypeIGC colouring, and the missile renders solid/un-
    // textured (texture-mapping is out of scope), so a single neutral ordnance
    // grey stands in for every missile type — the same single-default convention
    // ProjectileStyle.AmberFallback uses for projectiles.
    private static readonly Vector3 MissileDiffuseColor = new(0.55f, 0.55f, 0.58f);
    private ChaseCamera _chaseCamera = null!;
    private CockpitCamera _cockpitCamera = null!;
    // P295 — the external-missile view camera (TrackCamera port).
    private MissileCamera _missileCamera = null!;
    private Starfield _starNear = null!;
    private Starfield _starMid = null!;
    private Starfield _starFar = null!;
    // P576 — the native per-sector sky (seeded stars + environment dome + planet
    // poster), replacing the three generic parallax starfields as the default
    // sky. See Rendering/NativeSkyRenderer.cs.
    private Rendering.NativeSkyRenderer _nativeSky = null!;
    private short _skySector = -1;
    // P576 — the sky tier (default black; --sky black|native|hd) and the
    // optional aleph destination-glimpse (default OFF). Client-only launch
    // settings, never on the wire.
    private string _skyTier = "black";
    private bool _alephGlimpse = false;
    // P589 — black-sky space-object toggles (moons / distant objects /
    // galaxies; all ON by default in the black sky).
    private bool _moonsOn = true;
    private bool _distantObjectsOn = true;
    private bool _galaxiesOn = true;
    // P592 — the black-sky GIANT sky object (face-on galaxy curved sky patch).
    private bool _giantObjectsOn = true;
    private Rendering.AlephGlimpseRenderer _alephGlimpseRenderer = null!;
    // P585 — the opt-in transit-pulse envelope: when a ship crosses an aleph in
    // the viewer's sector (ClientWorld's AlephTransitSignals) the affected warp
    // briefly brightens, its glimpse opens wide and a lens-flare bursts. Drives
    // both the warp funnel boost and the glimpse/flare draw; idle = no cost.
    private Rendering.WarpPulseController _warpPulses => _flightEffects.WarpPulses;
    // P588 — the black sky tier renderer (point stars + native planet, no
    // dome), the new DEFAULT. Constructed alongside the other tiers; the
    // active tier is dispatched in DrawSkyP576.
    private Rendering.BlackSkyRenderer _blackSky = null!;
    // P577 — the HD sky tier renderer (real telescope data). Constructed
    // alongside the native sky; the active tier is dispatched in DrawSkyP576.
    private Rendering.HdSkyRenderer _hdSky = null!;
    private float _skyIntensity = 1.0f;
    private bool _skyLowRes = false;

    // 2D HUD overlay (P211). Drawn with SpriteBatch on top of the 3D scene each
    // frame; the reticle + camera-mode tag are camera-independent, the telemetry
    // widgets read the local ship's live ServerShipUpdate (see HudFrame).
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _whitePixel = null!;
    private SpriteFont _hudSmall = null!;
    private SpriteFont _hudBig = null!;
    private HudRenderer _hudRenderer = null!;
    private ModernHudRenderer _modernHudRenderer = null!;
    private StarmapRenderer _starmapRenderer = null!;
    private RadarBracketRenderer _radarRenderer = null!;
    // HUD-16 — the native per-contact radar icon surfaces and their
    // type-id → surface-stem resolution cache (built once from the core).
    private Rendering.RadarIconNameCache? _radarIconNames;
    private Rendering.RadarIconTextures? _radarIconTextures;
    private Rendering.ShipSideTagRenderer _shipTagRenderer = null!;
    private Rendering.HelpOverlayRenderer _helpOverlayRenderer = null!;
    // P577 — the in-game Credits/Licences screen (F12), the visible-attribution
    // surface for the CC BY / public-domain telescope data behind the HD sky.
    private Rendering.CreditsOverlayRenderer _creditsOverlayRenderer = null!;
    private Rendering.GameOverScreenRenderer _gameOverScreenRenderer = null!;
    // P320 — the session-loss screen (server crash / network drop), drawn when
    // the established connection is lost. Mirrors the game-over renderer's
    // pattern but with an alarm palette so it reads as trouble, not as a win.
    private Rendering.ConnectionLostScreenRenderer _connectionLostRenderer = null!;

    // P213 — real audio. Null until LoadContent runs; inert (silent, non-fatal)
    // if the host has no usable audio device (see SoundManager remarks).
    private Audio.SoundManager? _sound;

    /// <summary>P764 — test-only UI-sound sink override. Production resolves
    /// every UI one-shot cue through <see cref="Audio.IUiSoundSink"/>; the
    /// headless tests set this to a recording sink to pin the CALL at each
    /// trigger site. Null in production (the sink is then <c>_sound</c>).</summary>
    internal Audio.IUiSoundSink? _uiSoundOverride;

    /// <summary>P764 — the UI one-shot sink the trigger sites play through:
    /// the test override when installed, else the real <see cref="_sound"/>.
    /// Null when the host has no audio device and no test override.</summary>
    private Audio.IUiSoundSink? UiSounds => _uiSoundOverride ?? _sound;

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
    // P787b — per-asteroid ambient loop (SIM-0345): the same enter/refresh/stop
    // planner shape, keyed by AsteroidId (native's per-asteroid AmbientSoundSite,
    // trekigc.cpp:1432-1434). The planner class is id-generic — a second
    // instance with independent state drives the asteroid layer.
    private readonly Audio.StationSoundPlanner _asteroidSoundPlanner = new();

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
    // P580 — edge-detect state for the native-default F-key camera bindings
    // (F4/F5/F7/F10/F11), so holding a key doesn't re-toggle.
    private bool _f4KeyWasDown, _f5KeyWasDown, _f7KeyWasDown, _f10KeyWasDown, _f11KeyWasDown;

    // P385 — client-only --debugMode launch flag: flips the capture-overview AND
    // the in-flight (chase/cockpit/missile) camera FOVs to native's real default
    // (s_fDefaultFOV, WinTrek.h:26 = 50°) so live framing can be compared against
    // native without a permanent change. Paired by hand with the server's
    // liveSettings.debugMode; never sent on the wire.
    private readonly bool _debugMode;

    // P448b — the client-side kill switch for the remote-ship dead-reckoner (port of
    // native CalculateShip via ShipDeadReckoner). Default ON; --noDeadReckon turns it
    // off so remote ships render on their raw last-known pose for a live A/B. Never
    // touches the wire; own-ship posing is always raw (P447).
    private bool _remoteShipDeadReckoning;

    // P448b — wall-clock (seconds) the re-seed correction statistics were last logged;
    // the pose gather polls ClientWorld.TakeRemoteCorrectionStats every interval so the
    // typical re-seed jump is observable live (and, if it proved visibly large, a
    // decision on item 4's error-decay is data-backed).
    private double _remoteCorrectionLoggedAt;

    // P508 — the own-ship local simulation (replaces the P447b OwnShipPredictionTransport).
    // The client flies its own ship locally and reports the pose to the server; the
    // rendered own-ship pose is this simulation. Driven from Game1.FlightInput and
    // seeded/re-seeded from Game1.Prediction.cs (see OwnShipSimulator's doc).
    private readonly Allegiance.ClientCore.Flight.OwnShipSimulator _ownSim = new();

    // P508 — the own-ship server clock (ServerTimeFromClientTime): stamps the native
    // SHIP_UPDATE with server-converted milliseconds so the server's ±1.5 s gate
    // accepts the pose (see OwnShipServerClock's doc).
    private readonly Allegiance.ClientCore.Flight.OwnShipServerClock _ownServerClock = new();

    // P508 — the native SHIP_UPDATE send cadence (c_dsecUpdateClient = 0.15 s).
    private readonly Allegiance.ClientCore.Flight.ShipUpdateCadence _shipSendCadence = new();

    // P508 — the ship cookie the next native SHIP_UPDATE must echo, adopted from the
    // own ship's SET_CLUSTER / SHIP_RESET each frame under the gate (the server drops
    // a native update whose cookie mismatches).
    private byte _ownShipCookie;

    // P508 — true this frame when the client is flying its own ship (not a turret
    // gunner), so the cadence can emit a native SHIP_UPDATE. Set in Game1.FlightInput.
    private bool _ownShipSendArmed;

    // P518 — last frame's SHIP_UPDATE send ELIGIBILITY (armed && hull && !frozen &&
    // server-clock ready). Used by MaybeSendOwnShipUpdate to log the stop/resume
    // edges (with the reason) so a silent "client stopped sending" — the class of
    // bug that hid P518 — is never invisible again.
    private bool _ownShipEligible;

    // P508 — cumulative count of received SHIP_RESETs (live diagnostics + reset count).
    private long _shipResetCount;

    // P786B — monotonically increasing Draw frame stamp; keys the per-frame
    // friendly-scanner cache (TargetVisibility side-leg gather) so the side
    // visibility set is computed once per frame/cluster, never per HUD row.
    private uint _hudFrame;

    // P508 — a client-only debug switch: render the local simulated pose (ON, default)
    // vs the raw last ServerShipUpdate pose (OFF). --noPrediction is now this
    // visualization toggle only — the local simulation ALWAYS runs and always drives
    // the native SHIP_UPDATE (it is the authority); turning it off just shows the old
    // 20 Hz server-stepped view for an A/B.
    private bool _ownShipPrediction;

    // P508 — bookkeeping for the per-frame wiring in Game1.Prediction.cs:
    //  - _ownSimSeeded: the sim has been seeded from the server pose at first sight.
    //  - _ownSimHullId/_ownSimAbThrust: the hull identity SetHull was last configured
    //    with, so the hull/afterburner is re-set only when it changes (respawn/lifepod/
    //    hull swap), not every frame.
    //  - _ownSimLastSectorId: the own ship's SectorId last seen, to detect a warp /
    //    sector change (a hard-transition freeze/re-seed).
    //  - _ownSimFrozenAtTick: the server update tick recorded when the sim froze,
    //    so the resume edge re-seeds only once a FRESH authoritative update (the
    //    post-undock SET_CLUSTER launch pose) has arrived (P522).
    private bool _ownSimSeeded;
    private short _ownSimHullId = -1;
    private float _ownSimAbThrust;
    private short _ownSimLastSectorId = -1;
    private uint _ownSimFrozenAtTick;

    // P562 — the last sector each KNOWN ship occupied, to detect a transit
    // (sector change) for ANY ship and play the arrival aleph's star swell.
    private readonly Dictionary<short, short> _lastShipSector = new();

    // P385 — capture-overview vertical FOV in degrees. Normal launches use the
    // wide 100° fleet shot (~130° horizontal at 16:9) so individual ships read;
    // --debugMode overrides this to native's real 50° default. Default stays
    // 100f; flag-absent launches are unaffected.
    private float _captureFovDeg = 100f;

    // P449b — the in-flight (chase/cockpit/missile) camera vertical FOV in
    // degrees. Domingo's decision: default to native's real s_fDefaultFOV = 50°
    // (WinTrek.h:26) in BOTH normal and debug mode, configurable via the
    // client-only --flightFov launch setting. The old _debugMode ? 50f : 75f
    // (P385) is gone — normal launches now use 50° too.
    private readonly float _flightFovDeg;

    // P449b — the native camera-jiggle accumulator (WinTrek.cpp:374-399),
    // driven each frame from the own ship's afterburner state. Shared trail
    // random source (render-thread only) for the per-axis offset.
    private readonly Rendering.FlightJiggle _flightJiggle;

    // Monotonic render clock (lobby refresh timing, chat timestamps, etc).
    private double _nowSeconds;

    // P618 — the sim-scaled GAME clock: wall time × the server's live sim time
    // scale (ClientWorld.TimeScale, fed by TimescaleUpdate). Everything that
    // animates a GAME EVENT — soft particles, damage/engine debris, warp pulses,
    // explosion animations — is timed from this clock, so during time dilation
    // the battle's visual effects play in matching slow motion (particles keep
    // travelling along their trajectories, just slower). UI feedback and network
    // liveness stay on the wall clock _nowSeconds. See the P618 report for the
    // full wall-vs-sim classification.
    private double _gameSeconds;

    // P436 — transient on-screen "no <candidate> in sector" feedback for the
    // P415 U/Z filtered-target keys (that feedback was console-only; now also
    // visible while flying). Null = nothing to show; the line self-expires
    // TargetNoMatchDisplaySeconds after being set via _targetNoMatchUntil.
    private const double TargetNoMatchDisplaySeconds = 2.5;
    private string? _targetNoMatchNoun;
    private double _targetNoMatchUntil;
    private readonly Dictionary<short, Flight.PredictedPose> _poses = new();
    // P462 — the local own-shot echo: emits a visual tracer (and fire one-shot)
    // at the trigger frame and de-dupes the server's PROJECTILE_INSTANCE echo so
    // each shot renders/sounds exactly once. See Flight.OwnShotEcho.
    private Flight.OwnShotEcho _ownShotEcho => _flightEffects.OwnShots;
    // P465 — own-shot burst-loop lifecycle: which mounts are in sustained fire
    // (a looping burst wave is playing) and which mounts fired last frame (the
    // rising-edge discriminator that keeps a tap a single-shot, not a burst).
    private readonly HashSet<int> _burstingMounts = new();
    private readonly HashSet<int> _prevFiringMounts = new();
    // P787b — own-shot activate-loop lifecycle: the mounts whose weapon
    // activate wave is currently looping (native's m_vpActiveSound[mount],
    // trekigc.cpp:984-986 — started while the weapon is active, stopped on
    // release). Keyed by mount, mirroring the burst-loop set above.
    private readonly HashSet<int> _activateLoopMounts = new();
    // P270 — ship ids rendered cloaked this frame (subset of _poses), collected
    // in the same locked pass that builds _poses so the Draw loop can read the
    // cloaked state lock-free and alpha-blend those ships' models.
    private readonly HashSet<short> _cloakedShips = new();
    // P288 — per-ship resolved content model this frame, collected under the
    // same Gate lock as _poses (a different faction/class mesh per ship, or null
    // → bios_scout fallback), so the Draw loop picks models lock-free.
    private readonly Dictionary<short, XnaModel?> _shipModelById = new();
    // P607 — ship ids already logged as drawing the procedural stand-in (log
    // each unresolvable hull/ship once, not every frame).
    private readonly HashSet<short> _standInLoggedShips = new();

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

    // P628 — per-ship level-of-detail state. Collected under the Gate with the
    // other per-ship render tables; the Draw loop reads them lock-free.
    // _shipRadiusById: the ship's rendered bounding-sphere radius in world units
    // (0.5 × HullType.Length), the metric input to ProjectedPixelRadius.
    // _shipSideRgbById: the ship's real side colour (team colour), fetched from
    // the side roster under the Gate; the far-tier sprite's tint.
    private readonly Dictionary<short, float> _shipRadiusById = new();
    private readonly Dictionary<short, (float R, float G, float B)> _shipSideRgbById = new();
    // Discrete far-tier state with hysteresis (ShipLod.NextFarState); suppresses
    // engine trails / motion ribbons for far ships and stabilises the toggle.
    private readonly Dictionary<short, bool> _shipLodFar = new();
    // Debug overlay counters (per-tier counts) for the LOD hud line.
    private int _lodFullCount, _lodFarCount;
    // P628 — ship-pass draw-call counter (full-model draws + far-sprite batches),
    // reset each frame; reported by the ALLEGIANCE_VOL_PERF frame-stats line so
    // the before/after draw-call benchmark is measurable on the real client.
    private int _lodDrawCalls;
    private readonly List<Rendering.FarSprite> _drawFarSprites = new();
    private readonly HashSet<string> _lodRegisteredTextures = new(StringComparer.Ordinal);

    // P628 — configurable LOD thresholds (env overridable) and the far-tier
    // sprite batcher (constructed in LoadContent when a GraphicsDevice exists).
    private Rendering.ShipLodProfile _shipLodProfile = Rendering.ShipLodProfile.FromEnv();
    private Rendering.ShipLodSpriteRenderer? _shipLodSpriteRenderer;

    // P759 — the runtime LOD slider (native WinTrek.cpp:9448/10415/10421). Pure
    // model, driven by the action map; its Bias is copied into _shipLodProfile
    // every frame before ship rendering.
    private readonly Rendering.LodControl _lodControl = new();

    // P566 — the native-ship renderer switch (--ships native|meshy; default native).
    private readonly bool _shipsNative;
    // P565 — the committed per-hull art override (assets/ships/ship-art.json) consulted by
    // the ship render path so the game honours "keep this Meshy hull" choices made in the
    // inspector. Empty (all-native default) when the file is absent. See ShipsNativeFor.
    private Rendering.ShipArtOverride _shipArtOverride = new();
    // P581b — the committed replacement-model catalogue (assets/ships/catalogue.json),
    // used to resolve "catalogue:<id>" ship-art overrides to a loaded model (repaired
    // preferred over raw). Empty when the file is absent or malformed.
    private Rendering.ShipCatalogue _shipCatalogue = Rendering.ShipCatalogue.Parse("{}");
    // P565 — hulls whose Meshy override pointed at a missing asset (logged once, fall to native).
    private readonly HashSet<string> _missingMeshyLogged = new(StringComparer.OrdinalIgnoreCase);
    // P601 — the private Meshy asset pack (optional). True only when the pack is
    // configured AND its compiled content exists (MeshyPack.IsAvailable); when
    // false every hull renders native and Meshy options log once then fall back.
    private bool _meshyPackAvailable;
    // P601 — the optional ContentManager rooted at the pack's compiled content
    // directory (MeshyPack.BuiltContentDir), or null when the pack is absent.
    private Microsoft.Xna.Framework.Content.ContentManager? _meshyContent;
    // P601 — once-logged flag for the "pack unavailable → native" message.
    private bool _meshyUnavailableLogged;
    private void LogMeshyUnavailableOnce()
    {
        if (_meshyUnavailableLogged) return;
        _meshyUnavailableLogged = true;
        Console.WriteLine("[client] " + MeshyPack.DescribeUnavailable());
    }
    // P567 — the native-asteroid renderer switch (--asteroids native|imported; default native).
    private readonly bool _asteroidsNative;
    // P566 — the GPU native ship renderer (parses .mdl meshes + native textures).
    private NativeShipRenderer _nativeShipRenderer = null!;
    // P567 — the GPU native asteroid renderer (parses each rock's own .mdl mesh + texture).
    private NativeAsteroidRenderer _nativeAsteroidRenderer = null!;
    // P564 — the GPU native per-station-type renderer (parses each station's own
    // .mdl station model + native texture), replacing the procedural placeholder.
    private NativeStationRenderer _nativeStationRenderer = null!;
    // HUD-21 — the GPU target-model-preview renderer (owns one 256×256 RT;
    // renders the eligible target's native mesh each frame before the scene).
    private Rendering.TargetModelPreviewRenderer _targetModelPreviewRenderer = null!;
    // HUD-21 — this frame's preview texture fed to the HUD (the renderer's RT
    // when a model rendered, else null → the pane's empty fallback). Cleared at
    // the top of the Flying branch (HUD-21b) so a failed scene reference can
    // never leave the previous target's RT on screen.
    private Texture2D? _hudTargetModelPreview;
    // HUD-21b — the resolved preview art cached per TargetPreviewKey (kind,
    // target id, hull/model). The resolve path allocates (NativeShipRef, the
    // TextureStemFor closure + string concat) and is refreshed only when the
    // key changes, so the steady per-frame path stays allocation-free.
    // Claude review: the key/cache/gate step lives in the headless-testable
    // TargetPreviewCache (a no-mesh resolution is cached too); built lazily on
    // the first preview frame (it binds ResolvePreviewArt).
    private Rendering.TargetPreviewCache<PreviewArt>? _previewCache;
    // HUD-21b — ONE reusable Orientation for station/asteroid previews
    // (Orientation is a class; HUD-21 allocated one per frame).
    private readonly Orientation _previewOri = new();
    // P566 — per-ship resolved NATIVE model stem + texture stem + native scale,
    // collected under the same Gate lock as _shipModelById so the Draw loop reads
    // it lock-free. Null → this ship's native art failed to decode → Meshy fallback.
    private readonly Dictionary<short, NativeShipRef?> _nativeShipRefById = new();

    /// <summary>P566 — the resolved native art for one ship: the <c>.mdl</c> model
    /// stem, its texture stem, and the native uniform scale
    /// <c>m_scale = (0.5·Length)/meshRadius</c>.</summary>
    private sealed record NativeShipRef(string? ModelStem, string? TextureStem, float NativeScale);

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
    // P761 — the team lobby configuration controls' edge flags.
    private bool _aaKeyWasDown, _llKeyWasDown, _lsKeyWasDown;
    // P802 — the team lobby's CIVILIZATION picker: open state, cursor, key
    // edges, and a per-civ cache of the native icon<civ>bmp artwork (loaded
    // lazily from the artwork source when the picker/current-civ row draws).
    private bool _civPickerOpen;
    private int _civPickerIndex;
    private bool _civOpenWasDown, _civUpWasDown, _civDownWasDown, _civConfirmWasDown;
    private readonly Dictionary<string, Texture2D?> _civIcons = new(StringComparer.Ordinal);
    // P598 — the ship id this client last sent PLAYER_READY(true) for. The
    // server re-seats a migrated peer after a game-over rotation with a FRESH
    // ship id (new SET_CLUSTER), so a non-null mismatch here means the mission
    // rotated and we must re-propagate the ready intent to the new ship (native
    // lobby flow: re-ready on the fresh NotStarted mission).
    private short? _readyConfirmedShip;

    // P268 — docked/hangar loadout shop state. When the local ship is docked
    // (ShipEntity.StationId != -1) the client shows this screen instead of
    // flight; launching sends a real BUY_LOADOUT(fLaunch=true) with the chosen
    // hull+parts. P519 — the player launches ONLY on their own action; the POC
    // 30 s auto-launch fallback (DockFallbackMs) was removed so an unattended
    // dock never forcibly undocks a player (native docked players stay docked).
    private Rendering.ShopCatalog? _shopCatalog;
    private Rendering.ShopModel? _shopModel;
    // P787b — the docked loadout list's last mouse-hovered row (native
    // m_nLastHoverIndex, loadout.cpp:1186); -1 = pointer off the list. Reset
    // whenever the shop model is rebuilt (a fresh dock).
    private int _shopHoverRow = -1;
    // P465b — the loaded static core's projectile-type table, captured ONCE at
    // load time (Game1.Lifecycle) so the per-frame bolt gather resolves each
    // ProjectileEntity's ProjectileTypeId against a cached dictionary instead of
    // re-deriving it. Null when the client has no static core → every bolt falls
    // back to ProjectileStyle.AmberFallback.
    private IReadOnlyDictionary<short, DataProjectileTypeIGC>? _projectileTypes;
    // P542 — last wallclock we emitted a [proj-diag] line (ms), so the
    // once-per-second throttle in the projectile gather can pace it.
    private long LastProjDiagEmitMs;
    // P333 — hull id → real HullType.Length, built once from the loaded shop
    // catalogue (missing hull/empty catalogue → no entry → scale 1, no scaling).
    private Dictionary<short, float> _hullLengthById = new();
    // P566 — hull id → real DataHullTypeIGC (ModelName/TextureName/Length) for the
    // native ship renderer's model/texture/scale resolution. Built once at boot.
    private Dictionary<short, DataHullTypeIGC> _hullById = new();
    // P465c — hull id → per-mount weapon-muzzle (hardpoint) offsets in the
    // ship's local frame, computed once per hull from its real .cvh under the
    // artwork root (HullWeaponPositions.Compute) so the P462 own-shot local
    // echo leaves the same guns the server's real rounds leave. Absent hull /
    // missing .cvh → zero offsets (historic ship-centre tracer).
    private readonly Dictionary<short, Allegiance.Core.Collision.Vector3[]> _muzzleOffsetsById = new();
    private Rendering.DockedScreenRenderer _dockedScreenRenderer = null!;
    private bool _dockedActive;
    private bool _launchSent;
    // P757 — BoardFailureRevision observed when the teleport pane last acted;
    // a later BOARD_NACK releases the pending teleport (EndLockDown equivalent).
    private long _teleportBoardFailure;
    private bool _dockUpWasDown, _dockDownWasDown, _dockSpaceWasDown, _dockLWasDown, _dockUWasDown;
    // P519 — latches for the tab/cycle/back/enter keys of the reworked docked shop.
    private bool _dockTabWasDown, _dockQWasDown, _dockEWasDown, _dockBWasDown, _dockEnterWasDown;

    // P764 — per-digit edge latches for the custom-loadout keys (load.cpp:2219).
    private readonly bool[] _loadoutDigitWasDown = new bool[10];

    // P519/P526 — the hull-preview spin angle (radians), advanced per-frame at
    // native loadout motion (WinTrek/load.cpp:877-887 — 1 rad/s about the ship's
    // UP with a fixed π/8 X tilt applied after the spin) behind the docked
    // HULLS/PARTS panes.
    private float _hullPreviewSpin;

    // P273 — the docked teleport pane (native TeleportPane), a sub-view of the
    // docked screen. T opens it (board a teammate's turret / teleport to a
    // friendly station), B returns to the hangar. It reads the live world to
    // rebuild the destination list each time it is opened.
    private Rendering.TeleportPaneModel? _teleportModel;
    private bool _teleportActive;
    private bool _telUpWasDown, _telDownWasDown, _telSpaceWasDown, _telTWasDown, _telBWasDown;

    // P487 — the docked investment pane (native PurchasesPaneImpl), a sub-view of
    // the docked screen alongside the teleport pane: I opens it, SPACE invests
    // (full: min(remaining, wallet)), R partial-invests (native OnPartial's 10%),
    // S toggles show-all, H toggles hide-completed, B returns to the hangar. The
    // synthesized bucket definitions are captured once per mission (lazily, on
    // first dock after the side's techs are known) so their ids stay aligned with
    // the server's fixed list; cursor and the two toggles persist across frames so
    // rebuilding the pane model each frame for live funding/wallet never loses
    // navigation state.
    private IReadOnlyList<Rendering.InvestmentBucket>? _investDefs;
    private bool _investActive;
    private bool _investShowAll;
    private bool _investHideCompleted; // native Show Complete starts checked (loadout.cpp:763)
    private int _investCursor;
    // Native misparms.nMaxDronesPerTeam (drone-type cap for CanInvest).
    private const int MaxDronesPerTeam = 4;
    private bool _invUpWasDown, _invDownWasDown, _invSpaceWasDown, _invRWasDown;
    private bool _invSWasDown, _invHWasDown, _invIWasDown, _invBWasDown;
    // P495 — this frame's investment-pane model (built once per frame in the
    // radar/asteroid-hint pre-pass and reused by the pane draw so the radar's
    // build-spot highlight and the list always agree), and the resolved AabmBuild
    // of the station-type bucket under the cursor (null when no station-type
    // bucket is hovered) that drives the radar asteroid highlight (native
    // SetInvestAsteroid).
    private Rendering.InvestmentPaneModel? _investModel;
    private short? _investAsteroidAabm;

    private long _nextMatchGeneration;
    private readonly Audio.MissionCountdownSound _missionCountdownSound = new();
    private readonly Audio.RipcordCountdownSound _ripcordCountdownSound = new();
    private bool _gameOverCuePlayed;

    // P271 — mission-briefing popup. _briefingRenderer draws the dismissible
    // pre-launch briefing panel; _briefingDismissed is the one-shot latch for
    // a manual Enter/Esc dismissal. The popup otherwise auto-closes when the
    // briefing countdown expires (mission goes live), mirroring native's
    // MissionBriefPopup closing as the stage leaves STAGE_STARTING.
    private Rendering.MissionBriefingRenderer _briefingRenderer = null!;
    private Allegiance.Core.Wire.MissionBriefingInfo? _builtBriefingFor;
    private (string Title, string Objective, string Body) _builtBriefing;
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

    // P481 — true when the SIDE-SELECT screen was entered from the game-over
    // Back action on an ELIMINATED (inactive) side, rather than from a fresh
    // logon. In this mode the player already owns a ship (MyShipId is set on
    // the dead, eliminated hull) but must request a still-active side; the
    // picker excludes inactive sides and exits to ReadyCheck/Flying only once
    // the local side is active again (JOIN_SIDE from the server's TryJoinSide).
    private bool _sideSelectFromGameOver;

    // P481 — edge latch for the game-over Back key (Backspace).
    private bool _backKeyWasDown;
    private bool _gameOverBackWasDown; // P625b — edge-detect the game-over Back action so holding it doesn't re-fire

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
    // P506 — edge latches for the mouse-flight response-setting toggles (D5=DZ
    // preset cycle, D6=linear/quadratic, D7/D8=mouse sensitivity −/+), so holding
    // a digit never re-fires, exactly like every other toggle here.
    private bool _mouseDzKeyWasDown, _mouseLinKeyWasDown, _mouseSensDownKeyWasDown, _mouseSensUpKeyWasDown;

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

    // P499 — Bluetooth gamepad flight input (Android head + any desktop pad).
    // The MonoGame GamePad analog sticks are converted to role axes by the pure
    // GamePadFlightRoles mapper and fed through their OWN JoystickFlightController
    // (separate instance so the USB joystick and a pad never contend on the same
    // throttle-latch state). Button semantics reuse the keyboard's P221 fire /
    // P220 booster / P445 throttle-step rules via ApplyGamePadFlight.
    private readonly JoystickFlightController _gamePadFlight = new();

    // P445 — the client-local persistent throttle axis in [-1, 1], ported from
    // native's trekClient.trekThrottle (WinTrek.cpp:10515-10960). Init to FULL
    // (+1) — native's on-line default (WinTrek.cpp:10860-10862) — and reset to
    // it on every launch/undock (ReadyCheck→Flying handoff in Update, and the
    // docked L/U/fallback launches in HandleDockedScreen). Sent every frame as
    // ControlData.Throttle via FlightInputState; stepping/presets/wheel edges
    // below mutate it. Edge latches so each press steps exactly once, never
    // per-frame while held.
    private float _throttle = FlightInput.DefaultThrottle;
    private bool _thUpWasDown, _thDownWasDown, _thZeroWasDown, _thFullWasDown, _th33WasDown, _th66WasDown;
    // P504 — the raw wheel-delta diagnostic pacing (log the raw pre-normalisation
    // delta at ~4 Hz while scrolling, never per-frame).
    private long _lastWheelDiagMs;
    // Mouse-wheel pacing: native's WheelDelay default preference = 2 →
    // delay[] = {0.066, 0.033, 0.022, 0.016, 0.0} → delay[2] = 0.022 s
    // (WinTrek.cpp:3052, 8178). Each wheel notch (ScrollWheelValue delta of
    // FlightInput.WheelNotch, normalised by FlightInput.WheelSignNormalizer for
    // the Linux sign inversion) queues a step, applied at most once per
    // WheelStepIntervalMs.
    private const long WheelStepIntervalMs = 22;
    private int? _lastWheelValue;   // null until first read (avoids a spurious step)
    private int _wheelQueue;        // accumulated, normalised notches (±120 each)
    private long _lastWheelStepMs;

    // P212 — player autopilot ("click-and-go"). T selects the candidate nearest
    // the crosshair; G toggles autopilot on/off toward the selected target; ANY
    // real manual flight input auto-disengages (native WinTrek SenseJoystick
    // behaviour). _autoTarget is null when nothing is selected; engaged is
    // orthogonal — you can hold a target while autopilot is off.
    private AutopilotTarget? _autoTarget;
    /// <summary>P425 — the COMMANDEE: the same-side ship/drone the local player
    /// is currently commanding (selected by the commandee key, distinct from the
    /// target reticle <see cref="_autoTarget"/>). A drone order needs TWO
    /// selections at once — who to command (_commandee) and what to order them
    /// at (_autoTarget) — so this is a separate slot from the reticle. null when
    /// no ship is being commanded.</summary>
    private AutopilotTarget? _commandee;
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
    // P424 — edge-detect the accept/reject/clear queued-order keys (Home/End/Delete).
    private bool _viewMissileWasDown;
    private bool _viewChaseWasDown;
    private bool _viewTurretWasDown;
    private bool _lodToggleWasDown;
    private bool _lodUpWasDown;
    private bool _lodDownWasDown;
    // P266 — the per-frame lead-indicator descriptor computed in Draw (projected
    // from the world's lead point + target) and consumed by DrawHud; null when
    // there is no lead granted or it is out of view.
    private LeadIndicatorView? _leadIndicator;
    // HUD-8 — the per-frame projected velocity-direction marker (cockpit only),
    // computed in Draw from the own ship's real velocity direction.
    private Vector2? _velocityMarker;
    // HUD-8 — the per-frame projected chase/missile weapon-range aim point with
    // no target (indicator.cpp:367-405); null when unavailable/not applicable.
    private Vector2? _chaseAim;
    // HUD-8 — accepted/current order turn-arrow directions (indicator.cpp:411-457).
    private HudOrderArrows? _orderArrows;
    // HUD-8 — the exact control vector this frame sent (manual or autopilot),
    // staged in FlightInput and read by the HUD snapshot for the joystick pointer.
    private ControlData _lastSentControls;
    private bool _gKeyWasDown; // edge-detect G so holding it doesn't re-fire
    private bool _kKeyWasDown; // edge-detect K (suicide) so holding it doesn't re-fire
    private bool _cKeyWasDown; // edge-detect C (chaff drop) so holding it doesn't re-fire
    private bool _nKeyWasDown; // edge-detect N (mine/probe drop) so holding it doesn't re-fire
    private bool _fCloakWasDown; // edge-detect F (cloak toggle) so holding it doesn't re-fire
    private bool _cloakToggled;  // P413 — latched cloak-engagement state folded into outgoing stateM
    private bool _lKeyWasDown;   // P414 — edge-detect L (ripcord) so holding it doesn't re-fire
    private bool _semicolonNothingKeyWasDown; // P417 — edge-detect ';' (target nothing) so holding it doesn't re-fire
    private bool _apostropheCenterKeyWasDown; // P417 — edge-detect '\'' (target center) so holding it doesn't re-fire
    private bool _shotKeyWasDown;              // P417 — edge-detect screenshot (PrintScreen, action ScreenShot) so holding it doesn't re-fire
    private bool _pageDownKeyWasDown;          // P425 — edge-detect PageDown (commandee cycle) so holding it doesn't re-fire
    private bool _insertKeyWasDown;            // P425 — edge-detect Insert (issue order to commandee) so holding it doesn't re-fire
    private bool _p1TurretKeyWasDown; // P416 — edge-detect 1 (promote turret seat 1) so holding it doesn't re-fire
    private bool _p2TurretKeyWasDown; // P416 — edge-detect 2 (promote turret seat 2)
    private bool _p3TurretKeyWasDown; // P416 — edge-detect 3 (promote turret seat 3)
    private bool _p4TurretKeyWasDown; // P416 — edge-detect 4 (promote turret seat 4)

    // P594 — the last queued-order command id this client announced (HUD tip +
    // "new command" chime), so a freshly-arriving rescue offer is voiced/shown
    // exactly once. -1/cleared when no queued order is pending, so a later order
    // re-announces.
    private short _queuedCmdAnnouncedId = -1;

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
    // P456 — chat-compose Tab cycling uses its OWN edge flag so a Tab that cycles
    // the recipient while composing never interferes with the minimap toggle's
    // edge (they are mutually exclusive by the composing gate, but the shared
    // flag is also consumed by the minimap line below, so keep them separate).
    private bool _chatTabKeyWasDown;
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
    private bool _f12WasDown;
    // P577 — the in-game Credits/Licences screen visibility (F12 toggle).
    private bool _creditsVisible;
    private bool _helpKeyWasDown;
    // P757 — help-overlay manual navigation edge latches (topic Up/Down,
    // Back history, page scroll).
    private bool _helpNavUpWasDown, _helpNavDownWasDown, _helpNavBackWasDown, _helpNavPgUpWasDown, _helpNavPgDnWasDown; // edge-detect H so holding it doesn't re-fire
    private Rendering.ScoreboardOverlayRenderer _scoreboardRenderer = null!;
    private bool _scoreboardVisible;
    private bool _scoreboardKeyWasDown; // edge-detect B so holding it doesn't re-fire
    // P275 — the Pilot Info overlay (your own live stats; P toggles). Same
    // independent-flag / edge-detect discipline as the scoreboard/help toggles.
    private Rendering.PilotInfoOverlayRenderer _pilotInfoRenderer = null!;
    private bool _pilotInfoVisible;
    private bool _pilotInfoKeyWasDown; // edge-detect P so holding it doesn't re-fire
    // P761 — the character sheet's profile editor (native CharInfoScreen's
    // Edit/Apply pair, CharInfoScreen.cpp:837-880) and its edge flags.
    private readonly Rendering.PilotInfoLayout.ProfileEditor _pilotProfile = new();
    private bool _pilotProfileKeyWasDown, _pilotProfileEnterWasDown, _pilotProfileBackWasDown;
    // P639 — the Team Pane (full roster; Ctrl+T toggles, rebindable). Same
    // independent-flag / edge-detect discipline as the scoreboard/pilot toggles.
    private Rendering.TeamPaneRenderer _teamPaneRenderer = null!;
    private bool _teamPaneVisible;
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
    // HUD-10 — last real VICTIM_DAMAGED(217) revision the flash armed from, so a
    // hit arms the window once (native DamageShipEvent → SetTimeDamaged).
    private long _lastFlashHitVersion;

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

    private string _lobbyUrl;   // P500 — no longer readonly: the connect screen edits it in-game
    private string? _connectHost;
    private ushort _connectPort;
    private bool _returnToLobbyOnFailure;   // join-from-lobby failed → revert, don't exit
    private bool _returnToIntroOnFailure;   // P731 — join-from-intro failed → return to the intro, don't exit
    private string? _connectFailureReason;  // P759 — staged connect/logon failure for the retry/abort screen
    private bool _connectRetryWasDown;
    private bool _connectAbortWasDown;
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

    // P761 — the server list's sort-header stack + game filters + find-player
    // and details popups (native gamescreen.cpp:858-898 OnButtonBarGames,
    // :900-927 ResetFilters, :1308-1326 details, :1348-1352 find player). The
    // pure models live in LobbyGameSort / LobbyGameFilters /
    // ServerDirectorySearch; the fields below are the UI state only.
    private readonly LobbyGameSort _lobbySort = new();
    private readonly LobbyGameFilters _gameFilters = new();
    private bool _lobbyResetWasDown;
    private bool _headerLeftWasDown, _headerRightWasDown, _headerClickWasDown;
    private bool _findPlayerActive;                    // the find-player popup
    private readonly StringBuilder _findBuffer = new();
    private bool _findPWasDown, _findBackWasDown, _findEnterWasDown;
    // P761b — the find-player query is a lobby round trip now (the open listing
    // carries no rosters): its result is staged off the network thread exactly
    // like the server fetch (native's FM_C_FIND_PLAYER reply, gamescreen.cpp:
    // 1348-1352).
    private volatile bool _findPlayerResultPending;
    private string? _findPlayerMatchId;       // the answering listing's id, null = nobody
    // P761c — the worker stages the asked NAME here; _findPlayerQuery is written
    // only by the main thread (it was a two-writer race: QueryFindPlayerAsync
    // and UpdateLobby both assigned it). Staged before the volatile flag, like
    // the fetch payload above.
    private string? _findPlayerQueryStaged;
    private string? _findPlayerQuery;         // main thread only: the name to show in the popup line
    private string? _findPlayerError;
    private bool _findPlayerBusy;             // main-thread: a query is in flight
    private bool _detailsActive;                       // the details popup
    private bool _detailsIWasDown;
    // P806 — the filter dialog (native FilterDialogPopup): the pure draft lives in
    // LobbyFilterDialog, the latches here are UI state only.
    private readonly LobbyFilterDialog _filterDialog = new();
    private bool _filtersKeyWasDown, _fdUpWasDown, _fdDownWasDown,
        _fdLeftWasDown, _fdRightWasDown, _fdSpaceWasDown, _fdEnterWasDown, _fdEscWasDown;

    private int _selectedIndex;
    private string? _lobbyError;
    private double _lastLobbyFetch = double.NegativeInfinity;
    private volatile bool _lobbyFetchInFlight;   // read on the main thread, set on the fetch continuation
    private volatile bool _lobbyResultPending;   // a fetch finished; data/error staged for the main thread
    private List<ServerListing>? _lobbyResultData;
    private string? _lobbyResultError;

    // Chat (P214): a text-input line composed from Window.TextInput characters
    // and sent via ConnectionSession.SendChat; a log overlay drawn from the
    // already-populated ClientWorld.Events.Chats. P456: the compose recipient
    // (_chatTarget) defaults to Team and persists across compose sessions
    // (native's m_chsChat is a member, console.cpp:644), so it lives here beside
    // the buffer, never reset when the input line closes.
    private bool _chatActive;
    private readonly StringBuilder _chatBuffer = new();
    // P818 — native keeps the compose recipient AND its ship list in one
    // ChatSubject member, m_chsChat(CHAT_TEAM) (console.cpp:537,644), which
    // survives QuitComposing. ChatSubjects is the port of that struct; _chatTarget
    // stays the recipient view of it so every existing reader is unchanged.
    private readonly ChatSubjects _chatRecipient = new();
    private ChatTarget _chatTarget => _chatRecipient.Target;
    // P787b — the compose's sector recipient override (SIM-0332): native's
    // console PickCluster starts a chat compose whose recipient is
    // CHAT_FRIENDLY_SECTOR addressed to the RIGHT-CLICKED cluster
    // (console.cpp:1743-1751, SetRecipient(CHAT_FRIENDLY_SECTOR, clusterID,
    // pcluster)); the compose state (m_chsChat) is a member, so the recipient
    // persists across compose sessions exactly like _chatTarget does. Null
    // until the first sector right-click; BaseClient::SendChat's NA →
    // GetChatCluster() fallback (clintlib.cpp:2352-2366: the viewed cluster,
    // else the ship's own) keeps applying while null.
    private short? _chatSectorRecipient;
    // P784 — the live compose state (native m_csComposeState,
    // console.cpp:524-529). _chatActive now means "a keyboard composer owns
    // typing" (Chat or Shell); Command/MouseCommand are mouse-driven compose
    // states that live only here. Quick stays derived from _qcMenuOpen.
    private NativeComposeState _composeState = NativeComposeState.NotComposing;
    private const int MaxChatLen = 880;
    private ChatRenderer _chatRenderer = null!;
    // P757 — the chat pane's history view: native ChatListPaneImpl's channel
    // filter + autoscroll rules (chatpane.cpp), replacing the bare P626
    // offset. The rendered window still reads the model's Offset (lines pulled
    // from the newest edge; 0 = live edge).
    private readonly Rendering.ChatPaneModel _chatPane = new();
    // P806 — the native chat filter is TWO independent mechanisms, and the port
    // now drives both the native way (no cycle):
    //  * m_bPlayerChatsOnly (chatpane.cpp:146,228-233), the Game Options
    //    "Filter/Chats" switch: drops non-critical (non-player) chatter. Seeded
    //    from the persisted preference; the ChatPaneFilter key toggles it.
    //  * the LOBBY chat pane's channel list, which native sets from the chat
    //    target buttons [target, Individual, IndividualNoFilter, Admin]
    //    (teamscreen.cpp:3085-3118) and which the in-flight console pane leaves
    //    EMPTY (console.cpp:3439-3442). The port applies the lobby set on the
    //    ReadyCheck (team lobby) screen and clears it on the way into flight.
    private bool _chatPlayerChatsOnly;
    // P806 — native m_bFilterChatsToAll (WinTrek.cpp:3271, default true): drop
    // CHAT_EVERYONE lines while in game (trekigc.cpp:4493). Seeded from the
    // persisted Game Options value.
    private bool _chatFilterChatsToAll = true;
    private bool _chatPaneFilterWasDown;
    private bool _chatPaneFilterSeeded;
    // P806 — the team lobby's chat target (native m_chattargetChannel, default
    // Everyone on first open: teamscreen.cpp:1384-1390 restores the last tab,
    // 0 = Everyone) and the Tab edge latch for its three-button cycle.
    private ChatTarget _lobbyChatTarget = ChatTarget.Everyone;
    private bool _rcChatTabWasDown;
    private bool _lobbyChatChannelsApplied;
    private bool _chatPgUpWasDown, _chatPgDnWasDown, _chatMsgUpWasDown, _chatMsgDnWasDown;
    private int _lastChatCount;
    // P626 — vote keys (TK_VoteYes/VoteNo) edge latches.
    private bool _voteYesWasDown, _voteNoWasDown;
    // P626 — in-flight cargo keys (NextCargo/NextPart/PrevPart/DropCargo/Reload).
    // The selected cargo mount is a negative native mount (-1..-5), -1 = none.
    private int _selectedCargoMount;
    private bool _nextCargoWasDown, _nextPartWasDown, _prevPartWasDown, _dropCargoWasDown, _reloadWasDown;

    // P217 — real quick-chat (SonicChat) voice-macro system. The menu tree +
    // voice table are parsed at runtime from the shipped Content/*.mdl files
    // (_quickChatDb). R (free in flight) opens the quick-chat menu; selecting a
    // leaf sends a real CHATMESSAGE with the command's voiceOver index; the
    // receive path plays the matching voice for incoming quick-chats (which
    // includes the sender's own server relay echo — see
    // ClientWorld sound events).
    private QuickChatDatabase? _quickChatDb;
    private QuickChatMenuRenderer _quickChatRenderer = null!;
    private bool _qcMenuOpen;
    private QuickChatMenu? _qcMenuNode;
    private List<QuickChatMenu> _qcMenuPath = new();
    private readonly HashSet<Keys> _qcKeysPrev = new();  // last-frame nav keys (rising-edge detection)
    private bool _qcRWasDown;                            // edge-detect the R open toggle

    // Rising-edge detectors so a held key neither re-fires nor (Escape) both
    // cancels chat and then quits the game across consecutive frames.
    private bool _escWasDown, _padBackWasDown, _enterWasDown, _backWasDown, _upWasDown, _downWasDown, _rWasDown;
    private bool _altF4WasDown;

    // P306 — headless video-capture state (see LaunchOptions.Capture).
    private const int CapSettleGraceMs = 2500;   // wait after entering Flying so every bot is known before recording
    private readonly CaptureSpec? _captureSpec;
    private RenderTarget2D? _capTarget;
    // P417 — a screenshot was requested this frame (F11 key, edge-triggered in
    // UpdateFlying). Consumed at the end of Draw where the backbuffer holds the
    // completed frame (Game1.Rendering.cs Draw-end hook).
    private bool _screenshotRequested;
    // P559 — one-shot live-proof hook: ALLEGIANCE_AUTOSHOT=<delayMs> requests a
    // single real-backbuffer screenshot (the SAME CaptureBackbuffer path as F11)
    // that many ms after the first Flying update, so a 2560x1440 window proof can
    // capture the actual presented backbuffer without keyboard injection.
    private long _autoShotStartMs;
    private bool _autoShotDone;
    // P572 — live-proof automation: the parsed plan (null in a normal session)
    // and the pure driver that sequences side → ready → launch → goto → shots →
    // dock-sweep → exit. All flags off by default; the client only ever does
    // what a player does (requests a side, toggles ready, buys a loadout,
    // issues a chat-goto buoy, engages its own autopilot).
    private readonly LiveProof.AutoProofPlan _autoPlan;
    private LiveProof.AutoProofMachine? _auto;
    private bool _hideHud;              // --hide-hud: skip the HUD overlay
    private bool _autoSideDone;         // side already requested (one-shot)
    private bool _autoReadyDone;        // ready already sent (one-shot)
    private bool _autoLaunchApplied;    // docked auto-launch already handled (one-shot)
    private int? _pendingSweepStep;     // docked preview sweep step waiting to run
    private bool _autoDockHeading;      // --auto-dock-after: home-station autopilot engaged
    private string _autoshotDir = "";   // --autoshot-dir
    private bool _autoShotScheduled;    // an autoshot is pending for the next Draw end
    private string _autoShotName = "";  // descriptive basename (time+sector+camera)
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

    private readonly bool _launchBorderless;

    public Game1(LaunchOptions opts)
    {
        _launchBorderless = opts.Borderless;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = opts.WindowWidth ?? 1280,
            PreferredBackBufferHeight = opts.WindowHeight ?? 720,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        // P582b — keep the backbuffer's contents when a pass switches render
        // targets mid-frame. P579's volumetric HALF-RES pass (near+large blast)
        // does gd.SetRenderTarget(halfRes) … draw … restore mid-frame, AFTER the
        // opaque scene is drawn; with the default DiscardContents that switch
        // and restore WIPES the opaque scene (asteroids/stations vanish, ships
        // survive) — the P547/P559 failure reintroduced by P579. PreserveContents
        // makes the backbuffer survive the excursion (proven live, 2026-10-07
        // 15v15 on 221). Colour RTs (SceneRT bloom, capture target) get the same
        // treatment at their construction sites; VolumetricRenderer now restores
        // the ACTUAL previous targets (GetRenderTargets()) instead of null.
        _graphics.PreparingDeviceSettings += (_, e) =>
        {
            // P582b — keep the backbuffer's contents across the P579 volumetric
            // half-res mid-frame excursion (fix). ALLEGIANCE_BW_NOFIX=1 reverts
            // to the old DiscardContents default for the retained A/B diagnostic
            // (proves the fix cures a reproduced black-out in the same scene).
            if (System.Environment.GetEnvironmentVariable("ALLEGIANCE_BW_NOFIX") != "1")
                e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
        };
        // P579 — perf measurement aid: ALLEGIANCE_NOVSYNC=1 disables the vsync
        // lock so real GPU frame time (not the 60 Hz presentation period) is
        // observable via ALLEGIANCE_VOL_PERF. Inert for normal play.
        if (System.Environment.GetEnvironmentVariable("ALLEGIANCE_NOVSYNC") == "1")
        {
            _graphics.SynchronizeWithVerticalRetrace = false;
            IsFixedTimeStep = false;
        }
        _benchmark = opts.Benchmark;
        InitializeBenchmarkSharing(opts.BenchmarkCollectorUrl);
        if (_benchmark is not null) _trailRandom = new RandomTrailSource(729);
        _session = new ConnectionSession();
        // P448b — register the remote-ship hull-stats resolver so the ClientWorld
        // dead-reckoner can (re-)seed each remote ship's ShipDeadReckoner from real
        // hull stats × the side's global-attribute factors. ClientWorld stays pure of
        // the catalogue/GA data; this body owns it.
        _session.World.RemoteShipHullResolver = ResolveRemoteHullStats;
        _playerName = opts.PlayerName;
        _assets = opts.AssetSource ?? Assets.AssetSources.Game;   // sky pack (if configured) layered over the artwork
        _captureSpec = opts.Capture;
        // P572 — live-proof automation. Null in a normal session (no --auto-*
        // flag passed → every automation off, exactly like a player). When
        // active, the initial camera + HUD toggles apply immediately; the rest
        // is sequenced by the pure AutoProofMachine as the world observes.
        _autoPlan = opts.AutoProof ?? new LiveProof.AutoProofPlan();
        if (_autoPlan.AnyActive)
        {
            _auto = new LiveProof.AutoProofMachine(_autoPlan);
            if (CameraModeFromToken(_autoPlan.Camera) is { } cam)
                _cameraMode = cam;
            _hideHud = _autoPlan.HideHud;
            // P805b — the session-only native ShowChatHistoryHUD toggle
            // (WinTrek.cpp:2897,5074-5078), so proof chatter does not sit over a
            // capture.
            if (_autoPlan.AutoHideChatHistory) _chatHistoryVisible = false;
            _autoshotDir = _autoPlan.AutoshotDir;
            // P580 — director / spectator-follow armed at launch: the same thing
            // the F2/F3 keys toggle, seeded for repeatable recordings.
            if (_autoPlan.Director || _autoPlan.FollowShip != -1)
            {
                _directorActive = true;
                _spectatorFollowId = _autoPlan.FollowShip;
                int seed = _autoPlan.DirectorSeed ?? MakeSeed();
                _directorSeed = seed;
                _director.Reseed(seed);
                Console.WriteLine($"[monogame] director armed at launch seed={seed} follow={(_spectatorFollowId == -1 ? "-" : _spectatorFollowId.ToString())}");
            }
            Console.WriteLine($"[autoproof] live-proof automation active: side={_autoPlan.AutoSide?.ToString() ?? "-"} ready={_autoPlan.AutoReady} " +
                              $"launch={( _autoPlan.AutoLaunchRequested ? (_autoPlan.LaunchHull.Length == 0 ? "current" : _autoPlan.LaunchHull) : "-")} " +
                              $"goto={(_autoPlan.GotoSector.Length == 0 ? "-" : _autoPlan.GotoSector)} target={_autoPlan.Target} " +
                              $"camera={_autoPlan.Camera} hidehud={_autoPlan.HideHud} shots=[{string.Join(",", _autoPlan.AutoshotSeconds)}] " +
                              $"dockafter={_autoPlan.AutoDockAfter?.ToString() ?? "-"} sweep={_autoPlan.DockSweepCount} exit={_autoPlan.ExitAfter?.ToString() ?? "-"}");
        }
        // P385 — client-only --debugMode launch flag (paired by hand with the
        // server's liveSettings.debugMode while testing; never sent on the wire).
        // Flips the camera FOVs (capture-overview and in-flight) to native's real
        // 50° default so framing can be compared live against native.
        _debugMode = opts.DebugMode;
        // P546 — VFX renderer switch (Enhanced default; --fx native forces the
        // P263/P545 native-art path for parity checks).
        // P573 — extended to a three-tier effects QUALITY: Native | Enhanced |
        // Volumetric (with the volumetric technique A/B picked by --fx volumetric/
        // displaced). The requested tier + platform default produce the REQUESTED
        // quality; the EFFECTIVE tier is resolved in LoadContent once the shader's
        // availability is known (Volumetric -> Enhanced on shader-load failure or
        // GPU over-budget, logged once).
        _volumetricTech = opts.VolumetricTech;
        _requestedQuality = opts.EffectsRequested ?? EffectsQualityResolver.PlatformDefault(IsMobilePlatform);
        _enhancedVfx = _requestedQuality != EffectsQuality.Native;
        // P547 — soft particles (default ON in Enhanced; --soft off disables;
        // absent in Native). Bloom default OFF (--bloom on).
        _softEnabled = LaunchOptions.EffectiveSoftParticles(_enhancedVfx, opts.SoftParticles);
        _bloomOverride = opts.BloomOverride ?? (opts.Bloom ? true : null);
        _allowSoft = opts.SoftParticles;
        ApplyEffectsDetail(opts.EffectsDetail ?? _upscaling.Effects);
        // P576 — sky tier + aleph glimpse (client-only; --sky black|native|hd, --aleph-glimpse on|off).
        _skyTier = LaunchOptions.EffectiveSkyTier(opts.SkyTier);
        _alephGlimpse = opts.AlephGlimpse;
        // P607 — the glimpse/transit-flare defaults ON; log the ACTIVE value once
        // at startup so a bare run's behaviour is obvious (and an explicit
        // --aleph-glimpse off run is obviously not glitching).
        Console.WriteLine(_alephGlimpse
            ? "[client] aleph glimpse/transit-flare: ON (--aleph-glimpse off to disable the glimpse + flare; the native arrival swell stays on either way)"
            : "[client] aleph glimpse/transit-flare: OFF (--aleph-glimpse off given; the native arrival swell for visible transits is still on, P607)");
        // P577 — HD sky global intensity + low-res asset set (client-only).
        _skyIntensity = Math.Clamp(opts.SkyIntensity, 0f, 2f);
        _skyLowRes = opts.SkyLowRes;
        // P589 — black-sky space-object toggles (client-only).
        _moonsOn = opts.MoonsOn;
        _distantObjectsOn = opts.DistantObjectsOn;
        _galaxiesOn = opts.GalaxiesOn;
        // P592 — the black-sky GIANT object toggle (client-only).
        _giantObjectsOn = opts.GiantObjectsOn;
        if (_requestedQuality == EffectsQuality.Native)
            Console.WriteLine("[client] VFX: NATIVE (P263/P545 native-art path)");
        else if (_requestedQuality == EffectsQuality.Volumetric)
            Console.WriteLine($"[client] VFX: VOLUMETRIC requested ({(_volumetricTech == VolumetricTechnique.RaymarchA ? "raymarch A" : "displaced sphere B")}) — resolving in LoadContent");
        else
            Console.WriteLine("[client] VFX: ENHANCED (CC0 explosions + animated premultiplied smoke); pass --fx native for the native-art path");
        if (_enhancedVfx)
            Console.WriteLine($"[client] soft particles: {(opts.SoftParticles ? "ON" : "OFF")}; bloom: {(opts.Bloom ? "ON" : "OFF (default)")}");
        // P547 — keep the post targets in step with window/backbuffer resizes
        // (AllowUserResizing is on), so the depth/scene/bloom targets never
        // drift from the viewport the smoke/HUD draw into.
        Window.ClientSizeChanged += (_, _) =>
        {
            _postWidth = _postHeight = 0; // recreated at next scene boundary, including dynamic scale
        };
        if (_debugMode)
        {
            _captureFovDeg = 50f;
            Console.WriteLine("[client] --debugMode: camera FOV set to native default 50° (vertical), was 100°");
        }
        // P449b — the in-flight camera FOV defaults to native's real 50° in BOTH
        // normal and debug mode (Domingo's decision: match native), overridable
        // via the client-only --flightFov setting. The old P385 "_debugMode ?
        // 50f : 75f" is replaced by this single value.
        _flightFovDeg = opts.FlightFovDegrees;
        if (_flightFovDeg != 50f)
            Console.WriteLine($"[client] --flightFov: in-flight camera FOV = {_flightFovDeg:0.#}° (native default is 50°)");
        // P566 — the ship renderer: native .mdl meshes (default) vs Meshy content
        // models. Log so a bare --ships meshy run is obvious in the console.
        _shipsNative = opts.ShipsNative;
        // P601 — the optional Meshy pack availability affects whether Meshy art can
        // load at all; report it next to the --ships mode so a bare --ships meshy
        // run without the pack is obviously going to render native.
        bool meshyPackHere = MeshyPack.IsAvailable();
        Console.WriteLine((_shipsNative
            ? "[client] ships: NATIVE .mdl meshes + native textures"
            : "[client] ships: MESHY content models (--ships native to use the original .mdl meshes)")
            + (_shipsNative ? "" : meshyPackHere ? $" (Meshy pack: {MeshyPack.ResolveRoot()})"
                             : " (NO Meshy pack — native only; see ALLEGIANCE_MESHY_PACK_PATH)"));
        // P567 — the asteroid renderer: each rock's own native .mdl mesh (default)
        // vs the shared imported/procedural variants. Log so a bare --asteroids
        // imported run is obvious in the console.
        _asteroidsNative = opts.AsteroidsNative;
        Console.WriteLine(_asteroidsNative
            ? "[client] asteroids: NATIVE per-rock .mdl meshes + native textures (--asteroids imported to use the shared imported variants)"
            : "[client] asteroids: IMPORTED shared rock variants (--asteroids native to use each rock's original .mdl mesh)");
        // P449b — the camera-jiggle model, sharing the render thread's random source.
        _flightJiggle = new Rendering.FlightJiggle(_trailRandom);
        // P612 — the spectator cameras (free cam / orbit cam), HUD toggle and the
        // smooth-transition "instant" setting (ALLEGIANCE_INSTANT_CAMERA=1).
        InitSpectatorCameras();
        // P448b — the remote-ship dead-reckoner kill switch (default ON in the
        // launch options; --noDeadReckon turns it off). Log so a bare --noDeadReckon
        // run is obvious in the console before any ship is drawn.
        _remoteShipDeadReckoning = opts.RemoteShipDeadReckoning;
        if (!_remoteShipDeadReckoning)
            Console.WriteLine("[client] --noDeadReckon: remote ships rendered on RAW poses (P448b dead-reckoner OFF)");
        // P508 — a client-only DEBUG switch (default ON): render the local simulated
        // pose vs the raw last ServerShipUpdate pose. The local simulation ALWAYS runs
        // and drives the native SHIP_UPDATE either way; --noPrediction only switches
        // the VIEW back to the old 20 Hz server-stepped pose for an A/B.
        _ownShipPrediction = opts.OwnShipPrediction;
        if (!_ownShipPrediction)
            Console.WriteLine("[client] --noPrediction: OWN ship rendered on RAW server pose (P508 sim still flies/drives the wire; debug A/B only)");
        // P449a — the ambient-dust field. Density mirrors native's user `Debris`
        // preference (default 1.0; 0 = off) via the --debris launch setting; the
        // RNG seam is the shared trail-random source (render-thread only).
        _debrisGeo = new Rendering.DebrisGeo(
            opts.DebrisDensity, _trailRandom,
            initialPosition: SimVec.Zero, initialTime: 0f);
        if (opts.DebrisDensity <= 0f)
            Console.WriteLine("[client] ambient space dust DISABLED (--debris 0)");
        else
            Console.WriteLine($"[client] ambient space dust: density={opts.DebrisDensity:0.###} (native Debris pref)");
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
        _connectStore = opts.ConnectStore;
        // P761 — the character sheet's persisted profile description (applied
        // edits go to the same file; native would send them to the club server).
        _pilotProfile.Description = IntroPreferences.Load().PilotDescription;
        _textPrompt = opts.TextPrompt ?? new KeyboardTextPrompt();
        // P761 — native's browser boots through ResetFilters
        // (gamescreen.cpp:708-710 → :900-927): default sort is players
        // REVERSED (the big numbers on top).
        _lobbySort.ResetFilters();

        if (_connectStore is not null)
        {
            // P500 — load the persisted connect profile (saved servers, name,
            // lobby URL, last-used) so the connect screen reflects what the
            // player configured. connect.json was folded into the store by the
            // Android head on first run (first-run seed), so nothing is baked
            // into the binary anymore.
            var profile = _connectStore.Load();
            if (profile is not null)
            {
                _savedServers = profile.Servers ?? new List<SavedServer>();
                _lastUsedKey = profile.LastUsedKey;
                if (!string.IsNullOrWhiteSpace(profile.PlayerName))
                    _playerName = profile.PlayerName;
                if (!string.IsNullOrWhiteSpace(profile.LobbyUrl))
                    _lobbyUrl = profile.LobbyUrl;
            }
            // Seed the combined row list now (saved servers are the bulk of the
            // Android list; the lobby fetch is a supplement when a URL is set).
            RebuildDisplay(restoreLastUsed: true);
        }

        string lobbyBase = string.IsNullOrEmpty(_lobbyUrl) ? ServerListing.LobbyUrlDefault : _lobbyUrl;
        _http = new HttpClient
        {
            BaseAddress = new Uri(lobbyBase.EndsWith('/') ? lobbyBase : lobbyBase + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5),
        };

        if (opts.Inspect)
        {
            _inspectCivArg = opts.InspectCiv;
            _inspectHullArg = opts.InspectHull;
            _inspectStationsArg = opts.InspectStations;
            _inspectStationArg = opts.InspectStation;
            _inspectLightTime = opts.InspectLightTime;
            _inspectPitch = opts.InspectPitch;
            _inspectNativeOnly = opts.InspectNativeOnly;
            _inspectHdCompare = opts.InspectHd;
            _inspectSideBySide = opts.InspectHd;
            _inspectAutoShotsRemaining = opts.InspectShots;
            _inspectAutoShotsRequested = opts.InspectShots;
            _inspectDemo = opts.InspectDemo;
            _inspectUseRepaired = opts.InspectRepaired;
            _inspectSweepAngles = opts.InspectAngles;
            _inspectDist = opts.InspectDist;
            _inspectCatalogueArg = opts.InspectCatalogue;
            _state = ClientState.Inspecting;   // offline; kicked off in Initialize
            Console.WriteLine("[inspect] --inspect: offline hull inspector (no server connect)");
        }
        else if (opts.DirectHost is not null)
        {
            _connectHost = opts.DirectHost;
            _connectPort = opts.DirectPort;
            _state = ClientState.Connecting;   // kicked off in Initialize
        }
        else if (opts.Intro)
        {
            // P731 — no command-line game-start arguments: the intro screen
            // (native's introscreen.cpp) where the player chooses single
            // player or multiplayer. P757 — native boots through the SPLASH
            // screen first (WinTrek.cpp:2690 m_screen(ScreenIDSplashScreen)),
            // which times out to the intro (splashscreen.cpp:34-46).
            _state = ClientState.Splash;
            _splashStartSeconds = 0;
        }
        else
        {
            _state = ClientState.Lobby;        // server browser / connect screen
        }
    }

    protected override void Initialize()
    {
        base.Initialize();
        if (_launchBorderless)
        {
            Window.IsBorderless = true;
            Window.Position = Point.Zero;
        }

        // Real text input for the chat line (MonoGame Window.TextInput — NOT a
        // manual Keyboard.GetState() scan, which misses shift/caps/punctuation).
        // The handler filters everything itself.
        //
        // P499 — subscribe via REFLECTION, not `Window.TextInput += ...`. The
        // shared library compiles against DesktopGL, whose GameWindow HAS
        // TextInput; MonoGame.Android's GameWindow does NOT, and a direct
        // `+=` on the DesktopGL-compiled body emits an add_TextInput member-ref
        // that fails at JIT resolution on Android with an UNCATCHABLE
        // MissingMethodException (surfacing in Game.DoInitialize). Reflecting
        // the event keeps the call indirect: desktop subscribes normally (the
        // event exists), Android sees no event and disables typed chat (fine for
        // the gamepad-first head).
        var textInputEvent = typeof(GameWindow).GetEvent(nameof(GameWindow.TextInput));
        if (textInputEvent is not null)
        {
            textInputEvent.AddEventHandler(Window, new EventHandler<TextInputEventArgs>(OnTextInput));
        }
        else
        {
            Console.WriteLine("[monogame] Window.TextInput unavailable on this platform; typed chat disabled");
        }

        if (_state == ClientState.Connecting)
            BeginConnect(_connectHost!, _connectPort);
        else if (_state == ClientState.Inspecting)
            InitializeInspector();
        InitializeBenchmark();
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
        DisposeBenchmark();
        DisposeSkirmish();
        _sound?.Dispose();
        _music?.Dispose();   // P304 — release the two music beds
        if (_asteroidRenderers != null)
            foreach (var r in _asteroidRenderers) r?.Dispose();
        _nativeAsteroidRenderer?.Dispose();   // P567 — native per-rock .mdl meshes + textures
        _nativeStationRenderer?.Dispose();    // P564 — native per-station-type .mdl meshes + textures
        _nativeShipRenderer?.Dispose();       // P646 — was never disposed (own GPU buffers/textures/effects)
        _targetModelPreviewRenderer?.Dispose(); // HUD-21 — the 256×256 preview RT
        _shipLodSpriteRenderer?.Dispose();    // P628 — far-tier sprite batcher
        _nativeTreasureRenderer?.Dispose();   // P646 — was never disposed (own GPU buffers/textures/effects)
        if (_asteroidTextures != null)
            foreach (var t in _asteroidTextures.Values) t.Dispose();
        _rockTextures?.Dispose();   // P421 — release the real original surface bitmaps
        _lockTextures?.Dispose();   // P428 — release the real missile-lock sprites
        _indicatorTextures?.Dispose();   // HUD-8 — release the flight indicator bitmaps
        _targetPaneTextures?.Dispose();   // HUD-3 — release the native target-pane bitmaps
        _feedbackTextures?.Dispose();   // P578 — release the eject-pod panel / death banner art
        _hudNativeTextures?.Dispose();  // HUD-10 — release compose/money/command/endurance art
        _statusTextures?.Dispose();   // HUD-6 — release the own-ship status icons
        _radarIconTextures?.Dispose();   // HUD-16 — release the native radar contact icons
        _inventoryTextures?.Dispose();   // HUD-7 — release the inventory-pane art
        _warpRenderer?.Dispose();
        _projectileRenderer?.Dispose();
        _projectileSpriteRenderer?.Dispose();
        _projectileModelRenderer?.Dispose();
        _missileRenderer?.Dispose();
        _nativeMissileRenderer?.Dispose();   // P593b
        _probeRenderer?.Dispose();
        foreach (var renderer in _probeTypes.Values) renderer?.Dispose();
        _mineRenderer?.Dispose();
        _trailRenderer?.Dispose();
        _warpParticleRenderer?.Dispose();
        _explosionRenderer?.Dispose();
        _hullHitSparkRenderer?.Dispose();
        _shieldFlareRenderer?.Dispose();   // P463b
        // P646 — the review flagged _nativeShipRenderer/_nativeTreasureRenderer;
        // the disposal guard then surfaced these 11 more GraphicsDevice-owning
        // renderers that UnloadContent also never disposed. Dispose all of them.
        _stationRenderer?.Dispose();
        _buildingEffectRenderer?.Dispose();
        _treasureRenderer?.Dispose();
        _chaffRenderer?.Dispose();
        _damageDebrisRenderer?.Dispose();
        _commandGridRenderer?.Dispose();
        _debrisRenderer?.Dispose();
        _engineTrailRenderer?.Dispose();
        _nozzleGlowRenderer?.Dispose();
        _navStrobeRenderer?.Dispose();
        _motionTrailRenderer?.Dispose();
        _alephGlimpseRenderer?.Dispose();
        _explosionAssets?.Dispose();
        // P546 — dispose the enhanced renderers/smoke before their VfxAssets.
        _enhancedExplosionRenderer?.Dispose();
        _volumetricRenderer?.Dispose();   // P573
        _displacedRenderer?.Dispose();    // P573
        _enhancedSmoke?.Dispose();
        _vfxAssets?.Dispose();
        // P547 — the post-processor owns the shader effects + depth/scene/bloom
        // targets (and the SoftParticleRenderer/Bloom effect); the cache owns
        // the shared Effect objects. Dispose both after the renderers.
        _gpuTimer?.Dispose();
        _frameTelemetry?.Dispose();
        _flightGc.Dispose();
        _modernHudRenderer?.Dispose();
        _fsr?.Dispose();
        _post?.Dispose();
        _hdMaterials?.Dispose();
        _shaders?.Dispose();
        _nativeEffects?.Dispose();   // P545 — after the renderers that borrow it
        _identityCancellation?.Cancel();
        _identityClient?.Dispose();
        _session.Dispose();
        _http.Dispose();
        base.UnloadContent();
    }

}

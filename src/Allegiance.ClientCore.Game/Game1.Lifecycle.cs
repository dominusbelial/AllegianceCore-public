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
/// The lifecycle slice of <see cref="Game1"/> (P384 stage-8 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{


    protected override void LoadContent()
    {
        base.LoadContent();
        try
        {
            // P288 — the per-ship/per-civ model catalog. The fallback is the
            // original single Bios Scout model; every P287 ship asset is loaded
            // alongside it so the resolver can pick real per-faction geometry.
            // P337 — each model's real Meshy baseColor texture loads alongside
            // it (the geometry-only FBX has no embedded material; the texture is
            // bound explicitly in the render loop).
            _fallbackModel = Content.Load<XnaModel>("Models/" + HullModelResolver.FallbackAsset);
            try
            {
                _fallbackTexture = Content.Load<Texture2D>(
                    "Textures/ships/" + HullModelResolver.FallbackAsset);
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[monogame] fallback texture unavailable: {inner.Message}");
            }
            foreach (var asset in HullModelResolver.ShipAssets)
            {
                try
                {
                    _shipModels[asset] = Content.Load<XnaModel>("Models/" + asset);
                    // P520 — fold the per-asset orientation override (e.g. ic-dev
                    // −90° about Y) into the mesh bones once at load, on top of the
                    // global NoseAlignmentCorrection, so a Meshy model that came
                    // out yawed renders nose-forward like its class siblings.
                    // This replaces the former ad-hoc LOCAL PLAY PATCH in the play
                    // worktree; the table now drives it data-wise for every asset.
                    ShipOrientationApply.Apply(_shipModels[asset], asset);
                }
                catch (Exception inner)
                {
                    Console.WriteLine($"[monogame] ship model '{asset}' unavailable: {inner.Message}");
                }
                try
                {
                    _shipTextures[asset] = Content.Load<Texture2D>("Textures/ships/" + asset);
                }
                catch (Exception inner)
                {
                    Console.WriteLine($"[monogame] ship texture '{asset}' unavailable: {inner.Message}");
                }
            }
            _hullResolver = new HullModelResolver();
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
        // P449b — the in-flight (chase/cockpit/missile) cameras all use the same
        // FOV: native's real default s_fDefaultFOV = 50° (WinTrek.h:26), Domingo's
        // decision to match native, in BOTH normal and debug mode. Configurable via
        // the client-only --flightFov setting (defaults 50). The old P385 expression
        // `_debugMode ? 50f : 75f` is gone — normal launches are 50° too.
        float flightFovDeg = _flightFovDeg;
        _chaseCamera = new ChaseCamera(distanceBehind: 350f, heightAbove: 90f, nearPlane: 10f, farPlane: 6000f,
            fieldOfViewDegrees: flightFovDeg, turnSmoothingTauSeconds: 0.18f);

        // First-person cockpit camera (P211) — same FOV/near/far family as the
        // chase camera so switching views keeps perceived scale; a small forward
        // offset keeps the near clip clear of the (un-rendered) cockpit interior.
        _cockpitCamera = new CockpitCamera(nearPlane: 1f, farPlane: 6000f,
            fieldOfViewDegrees: flightFovDeg, forwardOffset: 14f);

        // P295 — the external-missile view camera. Same FOV/far family as the
        // chase camera so switching views keeps perceived scale; the framing
        // factor backs the eye off proportionally to the missile↔ship separation
        // so a freshly-fired missile and the ship it left stay in frame together.
        // nearPlane=1 (P340/U1): the eye sits ~2×framingFactor×separation behind
        // the missile, so immediately after launch — when separation is still
        // small (e.g. ~5u → eye only ~3.5u behind) — the old 10-unit near plane
        // put the missile inside the clip volume and missile-cam showed nothing
        // at exactly the moment you toggled to it. 1 matches the cockpit camera.
        _missileCamera = new MissileCamera(nearPlane: 1f, farPlane: 6000f,
            fieldOfViewDegrees: flightFovDeg, framingFactor: 0.35f);

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
        var gauges = GaugeTextures.Load(GraphicsDevice, _assets);
        // P428 — decode the two real missile-lock sprites (plockbmp.mdl /
        // lockedbmp.mdl) the same way. Graceful: a missing sprite falls back to
        // the procedural bracket.
        var lockTextures = LockTextures.Load(GraphicsDevice, _assets);
        _lockTextures = lockTextures;
        _hudRenderer = new HudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, gauges, lockTextures);

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
        _shopCatalog = Rendering.ShopCatalog.TryLoad(_assets);
        // P540 — give the ClientWorld a way to resolve an ejected ship's lifepod
        // hull id (it has no core of its own). Wired once the catalogue loads,
        // before the wire receive path starts.
        _session.World.LifepodHullResolver = ResolveLifepodHull;
        // P465b — cache the core's projectile-type table once (from the same
        // loaded core the shop catalogue reads) so the per-frame bolt gather can
        // style each bolt by its real type. Null when the core is missing →
        // every bolt falls back to the amber tracer.
        _projectileTypes = _shopCatalog?.ProjectileTypes;
        Console.WriteLine($"[monogame] shop catalogue: {(_shopCatalog is null ? "UNAVAILABLE (core not found)" : LauncherSummary(_shopCatalog))}");
        // P333 — cache hull id → real Length for render-scale normalization.
        _hullLengthById = _shopCatalog is null
            ? new Dictionary<short, float>()
            : _shopCatalog.Hulls.ToDictionary(h => h.HullId, h => h.Length);
        if (_shopCatalog is null)
            Console.WriteLine("[monogame] P333 ship scaling DISABLED — artwork core unavailable, every ship renders at scale 1");
        _dockedScreenRenderer = new Rendering.DockedScreenRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P262 — game-over result screen: the large centred panel announced at
        // mission end (winner + real wire reason). Built only on demand in Draw;
        // nothing here depends on mission state.
        _gameOverScreenRenderer = new Rendering.GameOverScreenRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P320 — session-loss screen (server crash / network drop), the same
        // translucent-panel conventions as the game-over renderer above.
        _connectionLostRenderer = new Rendering.ConnectionLostScreenRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

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
        // P372 — the per-side in-world ship "team tags" (side-coloured chevron
        // above each visible ship's hull), same SpriteBatch plumbing.
        _shipTagRenderer = new Rendering.ShipSideTagRenderer(_spriteBatch, _whitePixel);

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
        // procedural mesh to FOUR shared variants: the three genuine native
        // rock meshes (drgrock/tacrock/bgrnd05, loaded through the content
        // pipeline exactly like the ship model and normalized to a RockMesh by
        // ImportedRockMeshLoader) plus the original procedural RockMeshFactory
        // rock as a fallback. Each is built once here and re-used for every
        // asteroid instance, scaled per-instance by its real Radius in Draw.
        // A per-type diffuse cache (built lazily in Draw) tints each asteroid
        // by its real AabmCapabilities, so rocks vary by both shape and colour.
        // P357: removed "Models/rrocket" — that asset is a guided rocket
        // (R-ROCKET) projectile model (thrust/rthrust/lthrust/smoke/trail
        // hardpoints in the native rrocket.mdl), not a rock; its blocky
        // panel-and-spike silhouette showed up among asteroids. Replaced by
        // "Models/bgrnd05", a genuine native background rock (bgrnd05.mdl),
        // confirmed rock-shaped by Blender renders.
        _asteroidRenderers = new AsteroidRenderer[4];
        string[] meshAssets = ["Models/drgrock", "Models/tacrock", "Models/bgrnd05"];
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

        // P421 — the real original per-resource-type asteroid surface bitmaps
        // (bgrndNNbmp.mdl, 2048×2048), decoded via the same MdlTextureDecoder
        // pipeline GaugeTextures uses. Types with no real asset (gold) — or whose
        // file is missing — fall back to the procedural RockTextureFactory below.
        _rockTextures = Rendering.RockTextures.Load(
            GraphicsDevice, _assets);

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

        // P544 — the native-style projectile renderers. The sprite path decodes
        // real .mdl projectile textures from the artwork tree and draws
        // billboards / directional decals; the model path parses real .mdl meshes
        // for the laser-cannon / MassDriver rounds. Both fall back to the box
        // above when an asset is missing (never an invisible round).
        _projectileSpriteRenderer = new ProjectileSpriteRenderer(GraphicsDevice, _assets);
        _projectileModelRenderer = new ProjectileModelRenderer(GraphicsDevice, _assets);

        // P234 — the shared REAL missile model mesh (the P218/P227 "generated or
        // loaded once, drawn per instance" pattern, but the geometry comes from
        // P233's parsed .mdl rather than a procedural generator). Loaded once
        // here from the artwork path; see MissileModelSource for the single-model
        // scope (this stage renders one missile type's real mesh). The renderer
        // applies the P234 winding fix internally (CullClockwiseFace for the
        // DirectX-authored mesh — verified: 0 interior holes vs 20 with the
        // default cull, see docs/MISSILE_RENDERING_P234.md).
        _missileRenderer = new MissileRenderer(
            GraphicsDevice, MissileModelSource.LoadDemoModel(_assets).Meshes, MissileDiffuseColor);

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
            GraphicsDevice, ProbeModelSource.LoadDemoModel(_assets).Meshes);

        // P291 — the shared REAL mine sprite + decal-field renderer (the P290
        // runtime-native-asset pattern, applied to the mine's real TEXTURE
        // representation). The mine census disproved a probe-style mesh: no mine
        // type in the corpus has a ModelName, and native renders mines as a
        // MineFieldGeo decal cloud of the fxmine texture (see MineModelSource /
        // MineFieldGeometry). Loaded once here from the artwork path.
        _mineRenderer = new MineRenderer(GraphicsDevice, Rendering.MineModelSource.LoadTexture(_assets));

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
                var mdlt = Rendering.ChaffModelSource.LoadTexture(_assets, stem);
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
        foreach (var asset in HullModelResolver.MissileAssets)
        {
            try
            {
                _missileModels[asset] = Content.Load<XnaModel>("Models/" + asset);
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[monogame] missile model '{asset}' unavailable: {inner.Message}");
            }
            // P337 — the real Meshy baseColor for this missile model, bound in
            // DrawMissileContentModel the same way as the ship textures.
            try
            {
                _missileTextures[asset] = Content.Load<Texture2D>("Textures/ships/" + asset);
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[monogame] missile texture '{asset}' unavailable: {inner.Message}");
            }
        }

        // P336 — normalize each imported missile content model's size to the real
        // native-.mdl size for that asset, so the imported render path agrees
        // with the native-.mdl path (previously ~100×-too-big Meshy models drew
        // ~200-300 units while the native path drew the real ~5-unit missile).
        // Live-proof log: for each imported asset show its real native .mdl
        // merged radius, the content model's true rendered radius, and the scale
        // applied — the product (scale × rendered) is the size it now draws at,
        // which equals the native radius (paths agree).
        foreach (var (asset, model) in _missileModels)
        {
            float nativeRadius = Rendering.MissileModelSource.NativeMdlRadius(_assets, asset);
            float renderedRadius = Rendering.ShipRenderScale.RenderRadius(model);
            float scale = Rendering.MissileModelSource.ScaleForNativeSize(nativeRadius, renderedRadius);
            _missileScaleByModel[model] = scale;
            // P337 — bind the real texture to the singleton model instance so the
            // missile draw path can look it up like the scale above.
            if (_missileTextures.TryGetValue(asset, out var mt))
                _missileTextureByModel[model] = mt;
            Console.WriteLine(
                $"[monogame] missile '{asset}': native .mdl radius={nativeRadius:F2}, " +
                $"content rendered radius={renderedRadius:F2}, P336 scale={scale:F4} " +
                $"(now draws at {scale * renderedRadius:F2})");
        }

        // P235 — the missile exhaust-trail billboard renderer (shared generated
        // soft-sprite + additive effect; per-frame vertex data is rebuilt in
        // Draw, so no persistent buffers are held here).
        _trailRenderer = new MissileTrailRenderer(GraphicsDevice);

        // P296 — the warp-transit teleport-spark billboard renderer (native
        // hot-blue additive glow; same generated-soft-sprite convention).
        _warpParticleRenderer = new WarpTransitParticleRenderer(GraphicsDevice);
        _damageDebrisRenderer = new DamageDebrisRenderer(GraphicsDevice);
        _engineTrailRenderer = new Rendering.ShipEngineTrailRenderer(GraphicsDevice);
        _nozzleGlowRenderer = new Rendering.EngineNozzleGlowRenderer(GraphicsDevice);
        _navStrobeRenderer = new Rendering.NavStrobeRenderer(GraphicsDevice);   // P367
        _motionTrailRenderer = new Rendering.ShipMotionTrailRenderer(GraphicsDevice);

        // P263 — the real explosion sprite artwork (decoded from the .mdl
        // strips at runtime) + the explosion billboard renderer that plays each
        // blast with native size/duration/fade.
        _explosionAssets = Rendering.ExplosionAssets.Load(GraphicsDevice, _assets);
        _explosionRenderer = new ExplosionRenderer(GraphicsDevice, _explosionAssets);
        _hullHitSparkRenderer = new Rendering.HullHitSparkRenderer(GraphicsDevice);
        _shieldFlareRenderer = new Rendering.ShieldFlareRenderer(GraphicsDevice);   // P463b
        // P449a — the ambient space-dust renderer (native DebrisGeo decal). Loads
        // the real debris1bmp.mdl artwork through the .mdl pipeline; falls back to a
        // procedural soft dot if the asset can't be decoded (AmbientDustRenderer).
        _debrisRenderer = new Rendering.AmbientDustRenderer(GraphicsDevice, _assets);

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

    private void ComputeCaptureCamera(float aspect, out Matrix view, out Matrix proj, out Vector3 cameraEye)
    {
        // P385 — vertical FOV. Normal launches: the port's wide 100° fleet shot
        // (~130° horizontal at 16:9) so individual ships read; --debugMode flips
        // this to native's real default (s_fDefaultFOV = 50°) via _captureFovDeg.
        float FovDeg = _captureFovDeg;
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

    /// <summary>Mirror the autopilot/target state in the window title — a cheap
    /// on-screen indication that avoids touching P211's HUD surface.</summary>
    private void RefreshWindowTitle()
    {
        Window.Title = _autoPilotEngaged && _autoTarget is { } t
            ? $"{_windowTitleBase} [AUTOPILOT: {t.Name}]"
            : _windowTitleBase;
    }

    /// <summary>P435 — the shop-catalogue boot line: hulls, parts, and a
    /// per-kind breakdown of the real launchers/dispensers the client now offers
    /// (missile + mine + chaff + probe).</summary>
    private static string LauncherSummary(Rendering.ShopCatalog cat)
    {
        if (cat.Launchers is not { } ls) return $"{cat.Hulls.Count} hulls, {cat.Parts.Count} parts, 0 launchers";
        int Count(LauncherKind k) => ls.Count(l => l.Kind == k);
        return $"{cat.Hulls.Count} hulls, {cat.Parts.Count} parts, {ls.Count} launchers "
               + $"({Count(LauncherKind.Missile)} missile, {Count(LauncherKind.Mine)} mine, "
               + $"{Count(LauncherKind.Chaff)} chaff, {Count(LauncherKind.Probe)} probe)";
    }
}

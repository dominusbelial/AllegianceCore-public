using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Assets;
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

    /// <summary>FONT-1 — load the bundled native-classic-HUD font bakes (one 1× +
    /// 2× SpriteFont per native identity) and wrap them in the registry the
    /// classic HUD/chat renderers consult. Identities that share a (face, Size)
    /// bake (medVerdana/smallFont) load it once. FONT-1b moved medBoldVerdana to
    /// Size 9, so it no longer shares promptFont's B8 bake; the registry memo
    /// here is keyed by asset name and keeps working for any future overlap.</summary>
    private Rendering.NativeFonts LoadNativeFonts()
    {
        var loaded = new Dictionary<string, SpriteFont>();
        SpriteFont Load(string asset)
        {
            if (!loaded.TryGetValue(asset, out var font))
                font = loaded[asset] = Content.Load<SpriteFont>(asset);
            return font;
        }
        var map = new Dictionary<Rendering.NativeFont, (SpriteFont One, SpriteFont Two)>();
        foreach (var id in Enum.GetValues<Rendering.NativeFont>())
        {
            var spec = Rendering.NativeFontCatalog.Spec(id);
            map[id] = (Load(spec.Asset1x), Load(spec.Asset2x));
        }
        return new Rendering.NativeFonts(map);
    }

    protected override void LoadContent()
    {
        base.LoadContent();
        // P749 — capture the GPU name for the crash header now that the device
        // exists (best effort; a graphics failure never prevents the report).
        try { Diagnostics.CrashLog.SetGpu(GraphicsDevice.Adapter.Description); }
        catch { }
        // P601 — the optional private Meshy asset pack. When it is configured and
        // built (MeshyPack.IsAvailable) we root a second ContentManager at its
        // compiled content and load every Meshy model/texture through it; when it
        // is absent we load nothing Meshy and every hull renders native.
        _meshyPackAvailable = MeshyPack.IsAvailable();
        if (_meshyPackAvailable)
            _meshyContent = new Microsoft.Xna.Framework.Content.ContentManager(Services)
            { RootDirectory = MeshyPack.BuiltContentDir() };
        var mcm = _meshyContent;   // null when the pack is absent
        try
        {
            // P288 — the per-ship/per-civ model catalog. The fallback is the
            // original single Bios Scout model; every P287 ship asset is loaded
            // alongside it so the resolver can pick real per-faction geometry.
            // P337 — each model's real Meshy baseColor texture loads alongside
            // it (the geometry-only FBX has no embedded material; the texture is
            // bound explicitly in the render loop). P601 — all of this loads from
            // the optional Meshy pack's ContentManager (mcm) when it is present.
            if (mcm is not null)
            {
            try { _fallbackModel = mcm.Load<XnaModel>("Models/" + HullModelResolver.FallbackAsset); }
            catch (Exception inner)
            {
                Console.WriteLine($"[monogame] fallback model unavailable: {inner.Message}");
            }
            try
            {
                _fallbackTexture = mcm.Load<Texture2D>(
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
                    _shipModels[asset] = mcm.Load<XnaModel>("Models/" + asset);
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
                    _shipTextures[asset] = mcm.Load<Texture2D>("Textures/ships/" + asset);
                }
                catch (Exception inner)
                {
                    Console.WriteLine($"[monogame] ship texture '{asset}' unavailable: {inner.Message}");
                }
            }
            // P581 — load any REPAIRED Meshy variants shipped in Content/Models/repaired/
            // (produced by tools/meshy-repair/repair.py). Absent for most assets, so the
            // per-asset try/catch skips them; the inspector's repaired preview resolves
            // through these keys. Originals are never overwritten (repaired/ is a new dir).
            foreach (var asset in HullModelResolver.ShipAssets)
            {
                string repKey = Rendering.RepairedModelPreview.RepairedKey(asset);
                try
                {
                    _shipModels[repKey] = mcm.Load<XnaModel>("Models/" + repKey);
                    Console.WriteLine($"[monogame] repaired meshy variant '{repKey}' loaded");
                }
                catch { /* no repaired variant for this asset */ }
            }
            }   // end if (mcm is not null)
            _shipModelKeys.UnionWith(_shipModels.Keys);
            _hullResolver = new HullModelResolver();
            // P565 — load the committed per-hull art override (assets/ships/ship-art.json);
            // absent file → empty override (all-native default). The ship render path consults
            // it so "keep this Meshy hull" choices persist into normal play.
            try
            {
                string artFile = ShipArtOverridePath();
                if (System.IO.File.Exists(artFile))
                {
                    _shipArtOverride = Rendering.ShipArtOverride.LoadFile(artFile);
                    Console.WriteLine($"[client] ship-art override: {_shipArtOverride.Count} entries from {artFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[client] ship-art override load failed ({ex.Message}); using all-native default");
            }
            // P581b — load the committed replacement-model catalogue (assets/ships/catalogue.json);
            // absent/malformed → empty catalogue (no "catalogue:<id>" resolutions succeed, so any
            // such override falls back to native the same way a missing Meshy asset does).
            // P601 — the public file keeps the schema + native entries only; the Meshy entries
            // live in the private pack's catalogue.json and are MERGED on top when the pack is
            // present. When the pack is absent the public (native-only) catalogue stands.
            try
            {
                var publicCat = Rendering.ShipCatalogue.Parse("{}");
                string catFile = Path.Combine(Directory.GetCurrentDirectory(), "assets", "ships", "catalogue.json");
                if (System.IO.File.Exists(catFile))
                    publicCat = Rendering.ShipCatalogue.LoadFile(catFile);
                if (_meshyPackAvailable)
                {
                    string packCatFile = MeshyPack.CatalogueFile();
                    if (packCatFile.Length > 0 && System.IO.File.Exists(packCatFile))
                    {
                        var packCat = Rendering.ShipCatalogue.LoadFile(packCatFile);
                        _shipCatalogue = Rendering.ShipCatalogue.Merge(publicCat, packCat);
                        Console.WriteLine($"[client] ship catalogue: {_shipCatalogue.Count} replacement models (public {publicCat.Count} + pack {packCat.Count})");
                    }
                    else
                    {
                        _shipCatalogue = publicCat;
                        Console.WriteLine($"[client] ship catalogue: {_shipCatalogue.Count} public entries; pack catalogue.json not found ({packCatFile})");
                    }
                }
                else
                {
                    _shipCatalogue = publicCat;
                    Console.WriteLine($"[client] ship catalogue: {_shipCatalogue.Count} public entries");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[client] ship catalogue load failed ({ex.Message}); empty catalogue");
            }
            _shipRenderer = new ShipRenderer(GraphicsDevice);
            // P566 — the native ship renderer (parses .mdl meshes + native textures
            // at runtime from the artwork source, like ProjectileModelRenderer).
            _hdMaterials = new HdMaterialRenderer(GraphicsDevice, NativeMaterialParts.NativeReader(_assets), seconds: () => ClientNowMilliseconds / 1000d);
            _nativeShipRenderer = new Rendering.NativeShipRenderer(GraphicsDevice, _assets, hd: _hdMaterials);
            // P628 — the far-tier sprite batcher (distant ships as lit, side-tinted
            // sprites/points, batched per hull type) + the env-configurable LOD
            // thresholds. Profile is parsed at construction (FromEnv) and kept.
            _shipLodSpriteRenderer = new Rendering.ShipLodSpriteRenderer(GraphicsDevice);
            _shipLodProfile = Rendering.ShipLodProfile.FromEnv();

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
                    false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
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
        // HUD-8 — decode the real flight-indicator bitmaps (reticle, velocity
        // marker, crosshair and lead surfaces) the same way. Graceful: a missing
        // bitmap falls back to the existing procedural shapes.
        var indicatorTextures = Rendering.IndicatorTextures.Load(GraphicsDevice, _assets);
        _indicatorTextures = indicatorTextures;
        // HUD-3 — the real native bottom target-pane bitmaps (gauges, range
        // icon, oval decoration); graceful text/procedural fallback when absent.
        var targetPaneTextures = TargetPaneTextures.Load(GraphicsDevice, _assets);
        _targetPaneTextures = targetPaneTextures;
        // P578 — the real eject-pod panel + death banner .mdl bitmaps (graceful
        // procedural fallback when the artwork is absent).
        _feedbackTextures = Rendering.FeedbackTextures.Load(GraphicsDevice, _assets);
        // HUD-10 — the native chat-compose panels, command bar, money creditbox
        // and lifepod endurance gauge (dialog.mdl:89-170,1885-1908,1927-1935).
        _hudNativeTextures = Rendering.HudNativeTextures.Load(GraphicsDevice, _assets);
        // HUD-6 — the own-ship centre-HUD status icons (radonbmp/concloakbmp/
        // killcounterbmp/captureflag* + the two CortUI PNGs).
        _statusTextures = Rendering.StatusTextures.Load(GraphicsDevice, _assets);
        // HUD-7 — the native classic inventory-pane art (top/line/bottom
        // backgrounds, icons, mounting/arming bar fills). Graceful fallback.
        _inventoryTextures = Rendering.InventoryPaneTextures.Load(GraphicsDevice, _assets);
        // FONT-1 — the bundled native-classic-HUD font bakes (DejaVu Sans /
        // DejaVu Sans Bold at the native pixel sizes), one 1× + 2× pair per
        // native identity, passed to the classic HUD and classic chat renderers.
        var nativeFonts = LoadNativeFonts();
        _hudRenderer = new HudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, gauges, lockTextures,
            _statusTextures, indicatorTextures, targetPaneTextures, _inventoryTextures,
            damageWash: _hudSettings.DamageWash, nativeHudTextures: _hudNativeTextures,
            gaugePercentage: _hudSettings.GaugePercentage, nativeFonts: nativeFonts,
            selectionBracket: _hudSettings.SelectionBracketOn,
            joystickMesh: Rendering.JoystickMeshTexture.Create(GraphicsDevice));

        // P214 — in-flight chat overlay (same SpriteBatch/font plumbing as the HUD).
        _modernHudRenderer = new ModernHudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, _hudSettings,
            Content.Load<SpriteFont>("HUDModern"), nativeArt: _statusTextures, nativeHudTextures: _hudNativeTextures);
        _chatRenderer = new ChatRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudNativeTextures, nativeFonts);

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
        _starmapRenderer = new StarmapRenderer(_spriteBatch, _whitePixel, _hudSmall, GraphicsDevice, _assets);
        // P272 — its per-cluster info pane (mirrors native's separate
        // SectorInfoPane class, sectormap.cpp:139).
        _sectorInfoPaneRenderer = new SectorInfoPaneRenderer(_spriteBatch, _whitePixel, _hudSmall);

        // P257 — H keybinding-reference overlay panel.
        _helpOverlayRenderer = new Rendering.HelpOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        // P625 — the in-game Controls (rebind) popup, wired to the same store
        // the help overlay reads so a rebind is reflected immediately.
        InitKeyBindingUi(_spriteBatch);
        // P577 — in-game Credits/Licences screen (F12 toggle; visible CC BY
        // attribution for the HD sky's telescope data).
        _creditsOverlayRenderer = new Rendering.CreditsOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall);
        _scoreboardRenderer = new Rendering.ScoreboardOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        // P275 — Pilot Info overlay (your own live stats), same panel conventions.
        _pilotInfoRenderer = new Rendering.PilotInfoOverlayRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        // P639 — Team Pane (full roster), same panel conventions.
        _teamPaneRenderer = new Rendering.TeamPaneRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);

        // P268 — the docked/hangar loadout shop. Loads the real static core the
        // server boots (PCore001.igc under the artwork dir) purely as the shop's
        // display catalogue (hull/part names, prices, categories); the client
        // otherwise has no static core (MissileModelSource). Graceful: a missing
        // core yields an empty catalogue, so the docked screen still renders the
        // current ship and launch/undock stay usable.
        _shopCatalog = Rendering.ShopCatalog.TryLoad(_assets);
        if (_shopCatalog?.Core is { } startupCore) _assetMissionCores["PCore001"] = startupCore;
        _session.World.MissionCoreResolver = ResolveAssetMissionCore;
        // P540 — give the ClientWorld a way to resolve an ejected ship's lifepod
        // hull id (it has no core of its own). Wired once the catalogue loads,
        // before the wire receive path starts.
        _session.World.LifepodHullResolver = ResolveLifepodHull;
        _session.World.PartTypeResolver = ResolveFlightPart;
        // P758 — the part-treasure ack decision surface: the session answers a
        // c_tcPart ACQUIRE_TREASURE offer with the native HitTreasure mount
        // pick (shipIGC.h:1239-1305), resolved through the same loaded shop
        // catalogue the docked screen uses.
        _session.TreasureMountSource = new ShopCatalogMountSource(_shopCatalog);
        // P465b — cache the core's projectile-type table once (from the same
        // loaded core the shop catalogue reads) so the per-frame bolt gather can
        // style each bolt by its real type. Null when the core is missing →
        // every bolt falls back to the amber tracer.
        _projectileTypes = _shopCatalog?.ProjectileTypes;
        Console.WriteLine($"[monogame] shop catalogue: {(_shopCatalog is null ? "UNAVAILABLE (core not found)" : LauncherSummary(_shopCatalog))}");
        // P586b — startup self-check: every part type, launcher and development
        // in the loaded core must resolve to a native visual (0 fallbacks). Any
        // that would fall back to the gold gem are logged here, at startup,
        // instead of appearing mid-flight as gem loot.
        if (_shopCatalog is { } sc)
            Rendering.TreasureVisualResolver.SelfCheck(sc, s => Console.WriteLine(s));
        // P333 — cache hull id → real Length for render-scale normalization.
        _hullLengthById = _shopCatalog is null
            ? new Dictionary<short, float>()
            : _shopCatalog.Hulls.ToDictionary(h => h.HullId, h => h.Length);
        if (_shopCatalog is null)
            Console.WriteLine("[monogame] P333 ship scaling DISABLED — artwork core unavailable, every ship renders at scale 1");
        // P566 — hull id → real DataHullTypeIGC for native ship resolution.
        _hullById = _shopCatalog is null
            ? new Dictionary<short, DataHullTypeIGC>()
            : _shopCatalog.Hulls.ToDictionary(h => h.HullId);
        // P607 — build the guaranteed-non-null procedural stand-in model once,
        // independent of the artwork corpus and the Meshy pack, so the ship
        // colour pass can never pass null to ShipRenderer.Draw (the P607 live
        // crash). Logged once here so a build failure is loud, not silent.
        _shipStandInModel = Rendering.ShipStandInModel.Build(GraphicsDevice);
        // P607 — startup audit of NATIVE ship resolution across the whole core:
        // every hull's ModelName .mdl must resolve (exists + decodes to a mesh),
        // mirroring the P593b audit. Reports N hulls and the unresolved list
        // (0 = every hull renders native; anything else is logged by name).
        AuditNativeShipResolution();
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
        _radarRenderer = new RadarBracketRenderer(_spriteBatch, _whitePixel, _statusTextures);
        // HUD-16 — the native per-contact radar icon surfaces: resolve every
        // contact kind's IGC iconName from the static core once (hull/station/
        // missile/probe/mine type id → '<iconName>bmp') and decode each surface
        // once, exactly like native's LoadRadarIcon at model creation
        // (trekigc.cpp:2731-2744). Missing/blank art keeps the procedural shape.
        _radarIconNames = new Rendering.RadarIconNameCache(_shopCatalog, _hullById);
        _radarIconTextures = Rendering.RadarIconTextures.Load(GraphicsDevice, _assets, _radarIconNames);
        _radarRenderer.SetRadarIcons(_radarIconTextures);
        _modernHudRenderer.SetRadarIcons(_radarIconTextures);
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
        // P576 — the native per-sector sky (seeded stars + environment dome +
        // planet poster) and P588 — the BLACK sky (point stars + native planet,
        // no dome), the new DEFAULT. The three generic parallax starfields above
        // are retained as the pre-cluster fallback backdrop (drawn before any
        // cluster's sky is known — identical to the black sky minus the planet).
        _blackSky = new Rendering.BlackSkyRenderer(GraphicsDevice, _assets,
            moonsOn: _moonsOn, distantObjectsOn: _distantObjectsOn, galaxiesOn: _galaxiesOn,
            giantObjectsOn: _giantObjectsOn);
        _nativeSky = new Rendering.NativeSkyRenderer(GraphicsDevice, _assets);
        _alephGlimpseRenderer = new Rendering.AlephGlimpseRenderer(GraphicsDevice, _assets);
        // P577/P588 — the aleph destination-glimpse renders the SAME sky tier
        // as the arriving sector (HD cubemap + HD nebula/planet, native dome +
        // planet, or black point stars + planet).
        _alephGlimpseRenderer.SkyTier = _skyTier;
        // P577 — the in-game Credits/Licences screen shows the HD telescope
        // attribution ONLY when the HD tier's real data is actually in use;
        // otherwise (black/native, P588) it notes no external sky assets apply.
        _creditsOverlayRenderer.ShowHdCredits = string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase);
        // P577 — the HD sky tier (real telescope data). lowRes selects the
        // Android asset set (see HdSkyRenderer). SkyIntensity is the global
        // readability guard multiplier.
        _hdSky = new Rendering.HdSkyRenderer(GraphicsDevice, _assets, lowRes: _skyLowRes)
        {
            SkyIntensity = _skyIntensity,
        };

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
                if (mcm is not null)
                {
                    imported = ImportedRockMeshLoader.FromModel(mcm.Load<XnaModel>(meshAssets[i]));
                    Console.WriteLine($"[monogame] asteroid mesh '{meshAssets[i]}': {imported.VertexCount} verts, {imported.TriangleCount} tris");
                }
                else
                {
                    imported = RockMeshFactory.Generate();
                }
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

        // P567 — the native per-asteroid renderer: parses each rock's OWN .mdl
        // mesh + native texture at runtime from the artwork source (the P566 native
        // pipeline reused). Cached per model; the per-instance radius scale,
        // orientation and spin are folded in at draw time (native scale =
        // radius/meshRadius, native RY(π) flip). The imported/procedural variants
        // above remain as the fallback chain behind the --asteroids setting.
        _nativeAsteroidRenderer = new Rendering.NativeAsteroidRenderer(GraphicsDevice, _assets, _hdMaterials);

        // P564 — the native per-station-type renderer: parses each station's OWN
        // .mdl station model + native texture at runtime from the artwork source
        // (the P566 native pipeline reused), replacing the procedural placeholder
        // hub+ring. Cached per model; the per-type radius scale and native RY(π)
        // flip are folded in at draw time (native scale = radius/meshRadius).
        // The procedural StationRenderer above remains as the fallback behind it
        // when a station type's art is missing.
        _nativeStationRenderer = new Rendering.NativeStationRenderer(GraphicsDevice, _assets, _hdMaterials);

        // HUD-21 — the target-model-preview renderer: one 256×256 RT created on
        // first use; renders the eligible target's native mesh each frame.
        // P805 (row 409) — also takes the native treasure renderer so
        // part/development/cash/powerup/artifact treasure MODELS and the flag
        // biosflag decal can render into the preview (native's SetTargetGeo
        // shows any target with a hit test + ThingSite, WinTrek.cpp:8783-8818;
        // a treasure's ThingGeo is its LoadDecal billboard or buyable model,
        // treasureIGC.cpp:118-171).
        _nativeTreasureRenderer = new NativeTreasureRenderer(GraphicsDevice, _assets, _hdMaterials);

        // P227 — the shared procedural warp funnel mesh (the P218 pattern,
        // different shape). Generated once here (no content-pipeline asset)
        // and drawn per-instance, scaled by each warp's real Radius and
        // constantly swirling around its own forward axis. See WarpRenderer.
        // P562 — now a NATIVE-textured flowing vortex (alephbmp) with a
        // counter-rotating star core and transit swell; resolves the per-warp
        // funnel texture and the f02 star from the artwork tree via _assets.
        // P805b — created before the target preview so the pane can draw the SAME
        // funnel art an aleph target shows in the world.
        _warpRenderer = new WarpRenderer(GraphicsDevice, _assets, _hdMaterials);

        _targetModelPreviewRenderer = new Rendering.TargetModelPreviewRenderer(
            GraphicsDevice, _nativeShipRenderer, _nativeStationRenderer, _nativeAsteroidRenderer,
            _nativeTreasureRenderer, _warpRenderer);

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
        _projectileModelRenderer = new ProjectileModelRenderer(GraphicsDevice, _assets, _hdMaterials);

        // P593 — startup self-check: every distinct 3D-model projectile round must
        // resolve its mesh + native texture (0 lit-solid-white fallbacks — the
        // "white popcorn" defect this task closes). Mirrors P586b's SelfCheck.
        if (_shopCatalog is { ProjectileTypes: { } pts })
        {
            var modelNames = pts.Values
                .Where(p => !string.IsNullOrWhiteSpace(p.ModelName)
                            && char.IsLetter(p.ModelName.Trim()[0]))
                .Select(p => p.ModelName!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int litSolid = _projectileModelRenderer.AuditDistinctModelTextures(modelNames, s => Console.WriteLine(s));
            Console.WriteLine($"[monogame] P593 projectile-model audit: {modelNames.Count} model(s), {litSolid} lit-solid fallback(s)");
        }

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
        // P615 — the probe is TEXTURED with its real native texture (native
        // paints it from probeigc.cpp:112-113's model load; all 53 PCore001
        // probe types carry an empty textureName so the demo utl02 resolves its
        // own imported utl02bmp), decoded + bound like missiles (P593b/P609).
        var probeMdl = ProbeModelSource.LoadDemoModel(_assets);
        Texture2D? probeTex = null;
        try
        {
            var probeStem = Rendering.ProbeModelSource.ResolveDemoTextureStem(_assets);
            if (probeStem is not null)
            {
                var pt = Rendering.MdlTextureDecoder.Decode(
                    AssetSources.ReadAllBytes(_assets, probeStem + ".mdl"));
                probeTex = new Texture2D(GraphicsDevice, pt.Width, pt.Height);
                probeTex.SetData(Rendering.MdlTextureDecoder.ToPremultipliedRgba(pt));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] probe texture unavailable ({ex.Message}) — lit-solid fallback");
        }
        _probeRenderer = new ProbeRenderer(
            GraphicsDevice, probeMdl.Meshes, probeTex, _hdMaterials, _assets);

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
        // P586 — alongside it, the NATIVE treasure renderer (part/development
        // models, acs14, artifact, the biosflag decal) now takes over; the gem
        // remains the fallback when a resolved model is unavailable.
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
                if (mcm is not null)
                    _missileModels[asset] = mcm.Load<XnaModel>("Models/" + asset);
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[monogame] missile model '{asset}' unavailable: {inner.Message}");
            }
            // P337 — the real Meshy baseColor for this missile model, bound in
            // DrawMissileContentModel the same way as the ship textures.
            try
            {
                if (mcm is not null)
                    _missileTextures[asset] = mcm.Load<Texture2D>("Textures/ships/" + asset);
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

        // P593b — the per-type NATIVE missile renderer: every missile whose type
        // is not one of the three P287 content assets now draws its OWN real
        // native .mdl mesh + native texture (closing the lit-solid "white
        // shard" defect — native loads each missile with its own model + texture,
        // missileigc.cpp:49-51). Startup audit resolves mesh + texture for every
        // distinct missile model in the core and reports lit-solid fallbacks.
        _nativeMissileRenderer = new Rendering.NativeMissileRenderer(GraphicsDevice, _assets, _hdMaterials);
        if (_shopCatalog is { MissileTypes: { } mts })
        {
            var missileModels = mts.Values
                .Where(p => p.ModelName is { Length: > 0 } mn && char.IsLetter(mn[0]))
                .Select(p => p.ModelName!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int litSolid = _nativeMissileRenderer.AuditDistinctModelTextures(missileModels, s => Console.WriteLine(s));
            Console.WriteLine($"[monogame] P593b missile-model audit: {missileModels.Count} model(s), {litSolid} lit-solid fallback(s)");

            // P593b — widened render-model audit: every 3D model any renderer can
            // draw for projectiles/missiles/probes across the whole core, plus the
            // mine/chaff sprites, must resolve its mesh + native texture (0
            // lit-solid fallbacks). Complements the treasure SelfCheck (P586b).
            if (_shopCatalog.Core is { } core)
            {
                int renderFallbacks = Rendering.RenderModelAudit.AuditCore(core, s => Console.WriteLine(s));
                int treasureFallbacks = Rendering.TreasureVisualResolver.SelfCheck(_shopCatalog, s => Console.WriteLine(s));
                Console.WriteLine($"[monogame] P593b render-model audit: 0-lit-solid guarantee -> {renderFallbacks} model fallback(s), {treasureFallbacks} treasure fallback(s)");
            }
        }

        // P235 — the missile exhaust-trail billboard renderer (shared generated
        // soft-sprite + additive effect; per-frame vertex data is rebuilt in
        // Draw, so no persistent buffers are held here).
        //
        // P545 — the shared native effect-texture cache: every particle/effect
        // renderer below (trails, damage smoke, warp sparks, shield flares,
        // hull hits, nozzle glow, strobes) now draws real native artwork from
        // this one cache instead of a procedural white soft blob, with the
        // native dim/additive tints that stop dense combat from blowing out to
        // white. Loaded once here and shared by all of them.
        var nativeEffects = Rendering.NativeEffectTextures.Load(GraphicsDevice, _assets);
        _nativeEffects = nativeEffects;

        // P546 — the ENHANCED smoke path: the shared animated CC0 smoke field
        // (premultiplied, sector-lit, near-faded, density-budgeted). Only
        // constructed in Enhanced mode; the native-art trail renderers below
        // get it as their optional smoke source and the enhanced explosion
        // renderer reuses it so the on-screen budget spans all smoke sources.
        _shaders = Rendering.ShaderAssetCache.ForDevice(GraphicsDevice);
        InitializeGpuBudget();
        if (_enhancedVfx)
        {
            // P547 — the shader cache + post-processor (linear-depth target,
            // scene target, soft-particle + bloom effects). Constructed before
            // the smoke field so its soft-particle renderer can be handed in.
            _post = new Rendering.VfxPostProcessor(GraphicsDevice, _shaders);
            if (_post.Bloom is { } bloom) bloom.Polished = _upscaling.Effects != State.EffectsDetail.Low;
            if (_softEnabled && _post.SoftReady)
                Console.WriteLine("[monogame] P547: soft particles ACTIVE (linear-depth shader + target ready)");
            else if (_softEnabled)
                Console.WriteLine("[monogame] P547: soft particles UNAVAILABLE (shader/target missing) — near-fade only");
            if (_bloomEnabled && _post.BloomReady)
                Console.WriteLine("[monogame] P547: bloom ACTIVE (thresholded, off in native)");
            _vfxAssets = new Rendering.VfxAssets(GraphicsDevice);
            _enhancedSmoke = new Rendering.EnhancedSmokeField(GraphicsDevice, _vfxAssets, _post?.Soft);
        }

        _trailRenderer = new MissileTrailRenderer(GraphicsDevice, nativeEffects, _enhancedSmoke);

        // P296 — the warp-transit teleport-spark billboard renderer (native
        // hot-blue additive glow; real e_telebmp artwork).
        _warpParticleRenderer = new WarpTransitParticleRenderer(GraphicsDevice, nativeEffects);
        _damageDebrisRenderer = new DamageDebrisRenderer(GraphicsDevice, nativeEffects, _enhancedSmoke);
        _engineTrailRenderer = new Rendering.ShipEngineTrailRenderer(GraphicsDevice, nativeEffects, _enhancedSmoke);
        _nozzleGlowRenderer = new Rendering.EngineNozzleGlowRenderer(GraphicsDevice, nativeEffects);
        _navStrobeRenderer = new Rendering.NavStrobeRenderer(GraphicsDevice, nativeEffects);   // P367
        _motionTrailRenderer = new Rendering.ShipMotionTrailRenderer(GraphicsDevice);

        // P263 — the real explosion sprite artwork (decoded from the .mdl
        // strips at runtime) + the explosion billboard renderer that plays each
        // blast with native size/duration/fade.
        _explosionAssets = Rendering.ExplosionAssets.Load(GraphicsDevice, _assets);
        _explosionRenderer = new ExplosionRenderer(GraphicsDevice, _explosionAssets);
        if (_enhancedVfx)
        {
            _enhancedExplosionRenderer = new Rendering.EnhancedExplosionRenderer(
                GraphicsDevice, _vfxAssets!, _explosionAssets, _enhancedSmoke!);
            Console.WriteLine("[monogame] P546: enhanced explosions + smoke active (CC0 atlases loaded)");
        }

        // P573 — resolve the Volumetric tier now that the shader cache exists.
        // The shader load can fail (missing/corrupt .mgfxo); the effective tier
        // then drops to Enhanced, logged once. A GPU over-budget fallback is
        // applied live in Update (see _EnsureVolumetricBudget).
        if (_requestedQuality == EffectsQuality.Volumetric)
        {
            if (_shaders is not null)
            {
                if (_volumetricTech == VolumetricTechnique.RaymarchA)
                {
                    var volEffect = _shaders.Load("Content/Shaders/Volumetric.ogl.mgfxo");
                    if (volEffect is not null)
                    {
                        var vr = new Rendering.VolumetricRenderer(volEffect);
                        if (vr.IsUsable) { _volumetricRenderer = vr; }
                        else { vr.Dispose(); Console.WriteLine("[monogame] P573: volumetric shader unusable — falling back to ENHANCED tier"); }
                    }
                    else Console.WriteLine("[monogame] P573: volumetric shader missing — falling back to ENHANCED tier");
                }
                else // DisplacedSphereB
                {
                    var bsEffect = _shaders.Load("Content/Shaders/DisplacedSphere.ogl.mgfxo");
                    if (bsEffect is not null)
                    {
                        var br = new Rendering.DisplacedSphereRenderer(bsEffect);
                        if (br.IsUsable) { _displacedRenderer = br; }
                        else { br.Dispose(); Console.WriteLine("[monogame] P573: displaced-sphere shader unusable — falling back to ENHANCED tier"); }
                    }
                    else Console.WriteLine("[monogame] P573: displaced-sphere shader missing — falling back to ENHANCED tier");
                }
            }
            if (_volumetricRenderer is null && _displacedRenderer is null)
            {
                _effectsQuality = EffectsQualityResolver.Resolve(
                    EffectsQuality.Volumetric, volumetricAvailable: false, gpuUnderBudget: false);
                if (!_volumetricFallbackLogged)
                {
                    Console.WriteLine("[client] P573: effects quality VOLUMETRIC -> ENHANCED (shader unavailable)");
                    _volumetricFallbackLogged = true;
                }
            }
            else
            {
                _effectsQuality = EffectsQualityResolver.Resolve(
                    EffectsQuality.Volumetric, volumetricAvailable: true, gpuUnderBudget: true);
                Console.WriteLine("[monogame] P573: VOLUMETRIC tier ACTIVE" +
                    (_volumetricTech == VolumetricTechnique.RaymarchA ? " (raymarch A)" : " (displaced sphere B)"));
            }
            // P582b — pre-warm the volumetric/displaced shaders + render targets
            // at load so the first real explosion causes no first-draw hitch
            // (shader compile + half-res target allocation moved off the blast).
            _volumetricRenderer?.Preload(GraphicsDevice);
            _displacedRenderer?.Preload(GraphicsDevice);
        }
        else
        {
            _effectsQuality = _requestedQuality;   // Native or Enhanced exactly as requested
        }
        _hullHitSparkRenderer = new Rendering.HullHitSparkRenderer(GraphicsDevice, nativeEffects, _assets);
        _shieldFlareRenderer = new Rendering.ShieldFlareRenderer(GraphicsDevice, nativeEffects, _assets);   // P463b / P569
        // P449a — the ambient space-dust renderer (native DebrisGeo decal). Loads
        // the real debris1bmp.mdl artwork through the .mdl pipeline; falls back to a
        // procedural soft dot if the asset can't be decoded (AmbientDustRenderer).
        _debrisRenderer = new Rendering.AmbientDustRenderer(GraphicsDevice, _assets);

        // P213 — real audio. Loaded after the render content so a sound failure
        // (no audio device) never blocks the visuals; SoundManager degrades
        // gracefully to silent rather than throwing.
        _sound = new Audio.SoundManager(Content);
        _sound.Load();
        // P614 — the voice-budget telemetry console line (active voices by
        // category + drops/s) is opt-in via env var for quiet normal-play logs.
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_AUDIO_TELEMETRY") == "1")
            _sound.LogBudgetTelemetry = true;

        // P304 — background music beds. Loaded after the render content like the
        // SFX manager, and equally graceful: no audio device leaves it inert
        // (silent) rather than blocking the visuals. Tracks are real-asset-first,
        // missing-bed silence (see Audio.Music remarks).
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

    /// <summary>P758 — <see cref="Session.ConnectionSession.TreasureMountSource"/>
    /// backed by the loaded shop catalogue: the client-side static-core lookup
    /// the part-treasure ack decision runs against (the port's stand-in for
    /// native's local IGC core, <c>clintlib/appmsg.cpp:270-275</c>). A null
    /// catalogue declines every offer with <c>c_mountNA</c> (the part drops back
    /// into space — never silently buffered).</summary>
    private sealed class ShopCatalogMountSource(Rendering.ShopCatalog? catalog) : ITreasureMountSource
    {
        public StaticCore? Core => catalog?.Core;
        public bool TryGetHull(short hullId, out DataHullTypeIGC hull)
        {
            hull = default;
            if (catalog is null) return false;
            foreach (var h in catalog.Hulls)
                if (h.HullId == hullId) { hull = h; return true; }
            return false;
        }
        public bool TryGetPart(short partTypeId, out DataPartTypeIGC part)
        {
            part = default;
            if (catalog is null) return false;
            foreach (var p in catalog.Parts)
                if (p.PartId == partTypeId) { part = p; return true; }
            return false;
        }
    }
}

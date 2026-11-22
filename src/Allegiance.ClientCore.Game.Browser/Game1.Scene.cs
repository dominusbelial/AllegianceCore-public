using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly BrowserSceneEffects _sceneEffects = new();
    private readonly SubtleDust _sceneDust = new();
    private readonly DebrisGeo _sceneDebris = new(1, new RandomTrailSource(1066));
    private readonly SpaceVisualMotion _sceneMotion = new();
    private readonly List<AmbientDustView> _sceneDustViews = new(300);
    private AmbientDustRenderer? _sceneDustRenderer;
    private NativeEffectTextures? _sceneTextures;
    private NavStrobeRenderer? _sceneStrobes;
    private WarpTransitParticleRenderer? _sceneSparks;
    private BuildingEffectRenderer? _sceneBuildings;
    private AlephGlimpseRenderer? _sceneGlimpses;
    // P1069 — the desktop production renderers for exhaust, nozzle glow and
    // side-coloured ribbons (unchanged desktop classes; KNI/WebGL-safe paths).
    private ShipEngineTrailRenderer? _sceneEngineTrail;
    private EngineNozzleGlowRenderer? _sceneNozzleGlow;
    private ShipMotionTrailRenderer? _sceneRibbons;
    private readonly List<TrailParticleView> _sceneEngineViews = new(512);
    private readonly List<ShipTrailStrip> _sceneTrailStrips = new(64);
    private readonly ITrailRandom _sceneRandom = new RandomTrailSource(1069);
    private bool _sceneEnginesReady;
    private SectorLighting _sceneLighting;
    private double _sceneGameSeconds;
    private bool _fullEffectLoad = true, _sceneNativeReady;
    private long _sceneMissionGeneration = -1;
    private readonly Allegiance.Core.Sim.Orientation _stationFacing = new(), _warpFacing = new();
    public Func<string, bool>? BattlePackReady { get; set; }
    public void SetEffectLoad(bool full) => _fullEffectLoad = full;
    public bool FullEffectLoad => _fullEffectLoad;
    public bool SceneAssetsReady => _sceneNativeReady;
    public object SceneMetrics => new { ready = SceneAssetsReady, effectLoad = _fullEffectLoad ? "full" : "reduced",
        dust = _sceneDustViews.Count, strobes = _sceneEffects.Strobes.Count, sparks = _sceneEffects.Sparks.Count,
        // P1069 — live engine effect counts the harness samples.
        exhaust = _sceneEffects.Engines.ActiveCount, glows = _sceneEffects.Glows.Count,
        ribbons = _sceneEffects.Motion.RibbonCount, enginesReady = _sceneEnginesReady,
        glimpses = _sceneGlimpses?.LoadedTargetCount ?? 0, skyTier = "black",
        paintingsReady = BattlePackReady?.Invoke("scene-paintings") == true,
        constructionReady = BattlePackReady?.Invoke("scene-build") == true };

    private void ResetScene()
    {
        _sceneEffects.Reset(); _sceneDust.Reset(); _sceneDebris.Clear(); _sceneDustViews.Clear();
        _sceneEngineViews.Clear(); _sceneTrailStrips.Clear();
        _hullUploads?.Reset();
        _sceneGlimpses?.InvalidateAll();
        _skyTextureRevision = -1;
        _sceneMotion.Reset(); _sceneGameSeconds = 0; _sceneMissionGeneration = -1; _skySector = -1;
    }

    private void EnsureSceneRenderers()
    {
        bool hadSky = _flightSky is not null;
        if (!hadSky)
        {
            // Stars, the resident planet/flare and dust draw immediately. A
            // failed deferred moon/spark pack must never leave an empty sky.
            _flightSky = new BlackSkyRenderer(GraphicsDevice, _assets);
            _sceneDustRenderer = new(GraphicsDevice, _assets);
            _sceneTextures = NativeEffectTextures.Load(GraphicsDevice, _assets);
            _sceneStrobes = new(GraphicsDevice, _sceneTextures);
            _sceneGlimpses = new(GraphicsDevice, () => _flightSky) { Visuals = _visuals };
            // P1069 — exhaust and nozzle glow draw with the real desktop
            // renderers and native effect art (e_fire/e_smoke/f101); the ribbon
            // renderer owns its generated soft-strip texture. Missing art falls
            // back to the shared soft sprite, never an invisible effect.
            _sceneEngineTrail = new(GraphicsDevice, _sceneTextures);
            _sceneNozzleGlow = new(GraphicsDevice, _sceneTextures);
            _sceneRibbons = new(GraphicsDevice);
        }
        // P1069 — the tiny engine-art pack (e_fire/e_smoke + their PNGs) is
        // requested through the P1066 verified scene-pack path and decoded once.
        if (!_sceneEnginesReady)
        {
            RequestBattlePack?.Invoke("scene-engines");
            if (BattlePackReady?.Invoke("scene-engines") == true && _sceneTextures is not null)
            {
                _ = _sceneTextures.TextureFor(NativeEffectTextures.FireStem);
                _ = _sceneTextures.TextureFor(NativeEffectTextures.SmokeStem);
                _sceneEnginesReady = true;
            }
        }
        if (_sceneNativeReady || BattlePackReady?.Invoke("scene-b1") != true) return;
        if (hadSky)
        {
            // The fallback tier remembers unavailable moon files. Recreate it
            // once at verified promotion; never retry a decode in a steady frame.
            _flightSky!.Dispose(); _flightSky = new BlackSkyRenderer(GraphicsDevice, _assets);
            _sceneGlimpses!.InvalidateAll(); _skySector = -1; _skyTextureRevision = -1;
        }
        _sceneSparks = new(GraphicsDevice, _sceneTextures!);
        _ = _sceneTextures!.TextureFor(NativeEffectTextures.GlowStrobeStem);
        _ = _sceneTextures!.TextureFor(NativeEffectTextures.TeleStem);
        _sceneNativeReady = true;
    }

    private void BeginScene(ClientWorld world, ShipEntity own, Matrix view, Matrix projection, Vector3 eye)
    {
        if (_sceneMissionGeneration != world.MissionGeneration)
        { ResetScene(); _sceneMissionGeneration = world.MissionGeneration; }
        EnsureSceneRenderers();
        _sceneLighting = NativeScene.Light(world.Clusters.TryGetValue(own.SectorId, out var cluster) ? cluster : null);
        // P1069 — the browser-only reduced-effect decision gates exhaust only.
        _sceneEffects.FullEffectLoad = _fullEffectLoad;
        _sceneEffects.BeginFrame(world, own, (float)_flightElapsedSeconds, _nowSeconds, _flightWarps);
        var frame = _sceneMotion.Step(_visuals, view, eye, _nowSeconds,
            ((long)own.SectorId << 32) ^ ((long)_cameraMode << 16) ^ 1L);
        if (_flightSky is not null)
        {
            // P1073 — keep requesting the sector's generated-map art (deduped by the
            // streamer) and hold the cluster switch until the sky closures that this
            // generation declares are verified-resident. A later promotion invalidates
            // the renderers' logged misses through the content revision.
            RequestSectorArt(own.SectorId);
            long revision = ((_sectorTextures?.Revision ?? 0) << 32)
                ^ ((_assets as StagedAssetSource)?.ContentRevision ?? 0);
            if (_skyTextureRevision != revision)
            {
                _flightSky.InvalidateMissingArt();
                _skySector = -1;
                _skyTextureRevision = revision;
                foreach (var sector in world.Clusters.Keys) _sceneGlimpses!.Invalidate(sector);
            }
            if (_skySector != own.SectorId && !SkyArtReady(own.SectorId))
            {
                // Bytes are still in flight: keep the previous sky instead of
                // permanently logging the new sector's textures as missing.
            }
            else if (_skySector != own.SectorId)
            {
                _flightSky.SetCluster(cluster); _skySector = own.SectorId;
            }
            if (_flightSky.PromoteResidentLayers(BattlePackReady?.Invoke("scene-paintings") == true,
                    BattlePackReady?.Invoke("scene-giants") == true)) _sceneGlimpses!.InvalidateAll();
            NativeScene.DrawSky(_flightSky, view, projection, eye, frame);
        }
        if (_fullEffectLoad && _sceneDustRenderer is not null)
        {
            NativeScene.GatherDust(_visuals, _sceneDust, _sceneDebris, frame, eye, view, projection,
                _sceneGameSeconds, _sceneDustViews, _rockFrustum);
            _sceneDustRenderer.Draw(_sceneDustViews, eye, view, projection);
        }
        else { _sceneDust.Reset(); _sceneDebris.Clear(); _sceneDustViews.Clear(); }
    }

    private void DrawSceneExtras(ClientWorld world, ShipEntity own, Matrix view, Matrix projection, Vector3 eye)
    {
        _sceneEffects.FinishParticles((float)_flightElapsedSeconds);
        _sceneSparks?.Draw(_sceneEffects.Sparks, eye, view, projection);
        // P1069 — desktop pass order, after the teleport sparks: afterburner
        // exhaust (AddThrustParticles), then the nozzle glow decal (RenderGlow),
        // then ship strobes (already below), then the motion ribbons (TrailGeo).
        if (_sceneEngineTrail is not null && _fullEffectLoad)
        {
            for (int i = 0; i < _sceneEffects.EngineTrailShips.Count; i++)
                _sceneEffects.Engines.Emit(_sceneEffects.EngineTrailShips[i], (float)_flightElapsedSeconds, _sceneRandom);
            _sceneEngineViews.Clear();
            _sceneEngineViews.AddRange(_sceneEffects.Engines.Enumerate());
            _sceneEngineTrail.Draw(_sceneEngineViews, eye, view, projection, _sceneLighting);
        }
        _sceneNozzleGlow?.Draw(_sceneEffects.Glows, eye, view, projection);
        _sceneStrobes?.Draw(_sceneEffects.Strobes, eye, view, projection);
        if (_sceneRibbons is not null)
        {
            _sceneTrailStrips.Clear();
            _sceneTrailStrips.AddRange(_sceneEffects.Motion.Enumerate());
            _sceneRibbons.Draw(_sceneTrailStrips, eye, view, projection);
        }
        long now = ClientNowMilliseconds;
        world.PruneExpiredBuildingEffects(now);
        var effects = _visuals.IsOn(VisualFlags.ConstructionHandoffKey) ? world.ConstructionVisuals : world.BuildingEffects;
        // ClientWorld exposes its owned dictionary through a read-only view.
        // Concrete iteration avoids a boxed enumerator in the interpreter.
        foreach (var entry in (Dictionary<short, BuildingEffectEntity>)effects)
        {
            if (entry.Value.ClusterId != own.SectorId) continue;
            var phase = entry.Value.Phase(now);
            if (!BattleShipScene.RockInView(V(phase.Position) - eye, phase.Radius, _rockFrustum)) continue;
            if (_sceneBuildings is null && BattlePackReady?.Invoke("scene-build") == true)
                _sceneBuildings = new(GraphicsDevice, _assets);
            if (_sceneBuildings is not null)
                NativeScene.DrawBuilding(_sceneBuildings, phase, eye, view, projection, (float)_nowSeconds);
        }
        if (_sceneGlimpses is not null && world.Clusters.TryGetValue(own.SectorId, out var cluster))
        {
            _sceneEffects.GatherGlimpses(world, eye);
            NativeScene.DrawGlimpses(_sceneGlimpses, _sceneEffects.Glimpses, eye, view, projection,
                cluster, _sceneEffects.PulseStrength, static (_, _, _, _) => false);
        }
    }

    private void DisposeScene()
    {
        _sceneDustRenderer?.Dispose(); _sceneStrobes?.Dispose(); _sceneSparks?.Dispose();
        _sceneBuildings?.Dispose(); _sceneGlimpses?.Dispose(); _sceneTextures?.Dispose();
        _sceneEngineTrail?.Dispose(); _sceneNozzleGlow?.Dispose(); _sceneRibbons?.Dispose();
    }
}

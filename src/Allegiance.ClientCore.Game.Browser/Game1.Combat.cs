using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly BrowserCombatEffects _combat = new();
    private NativeMissileRenderer? _combatMissiles;
    private MissileTrailRenderer? _combatTrails;
    private ExplosionAssets? _combatExplosionArt;
    private ExplosionRenderer? _combatExplosions;
    private HullHitSparkRenderer? _combatHits;
    private ShieldFlareRenderer? _combatFlares;
    private DamageDebrisRenderer? _combatDebris;
    private MineRenderer? _combatMines;
    private ChaffRenderer? _combatChaff;
    private NativeTreasureRenderer? _combatTreasures;
    private TreasureRenderer? _combatGem;
    private readonly Dictionary<short, ProbeRenderer> _combatProbes = new();
    private readonly Dictionary<short, long> _probeMisses = new();
    private readonly Dictionary<short, double> _treasureBorn = new();
    private readonly List<short> _treasureDead = new();
    private readonly List<ExplosionEntity> _combatBlasts = new();
    private readonly List<object> _combatBlastReceipts = new();
    private readonly HashSet<ExplosionEntity> _combatSeenBlasts = new();
    private readonly Orientation _missileFacing = new();
    private int _combatMissileCount, _combatExplosionCount, _combatPodCount, _combatOwnPodCount,
        _combatMineCount, _combatChaffCount, _combatProbeCount, _combatTreasureCount, _combatOwnPodMeshes;
    private bool _combatReady;
    public object CombatMetrics => new {
        frame = FlightFrames, sector = _lastCombatSector, localMs = ClientNowMilliseconds,
        ready = _combatReady, quality = _browserVolumes is null ? "Native" : "Volumetric", volumetric = VolumetricMetrics, density = _fullEffectLoad ? 512 : 128,
        missiles = _combatMissileCount, missileTrailParticles = _combat.TrailViews.Count,
        explosions = _combatExplosionCount, shieldFlares = _combatFlares is null ? 0 : _combat.FlareViews.Count,
        hullSparks = _combatHits is null ? 0 : _combat.HitViews.Count + _combat.Flashes.Count, debris = _combat.DebrisViews.Count,
        pods = _combatPodCount, ownPods = _combatOwnPodCount, ownPodMeshes = _combatOwnPodMeshes, mines = _combatMineCount,
        chaff = _combatChaffCount, probes = _combatProbeCount, treasures = _combatTreasureCount,
        explosionDraws = _combatBlastReceipts };
    private short _lastCombatSector = -1;

    private void QueueCombatPack(string key)
    {
        if (PrioritizeBattlePack is { } prioritize) prioritize(key, 1);
        else RequestBattlePack?.Invoke(key);
    }

    private void EnsureCombatRenderers()
    {
        QueueCombatPack("scene-combat");
        _combatMissiles ??= new(GraphicsDevice, _assets);
        _combatTreasures ??= new(GraphicsDevice, _assets);
        _combatGem ??= new(GraphicsDevice);
        _combatTrails ??= new(GraphicsDevice, _sceneTextures!);
        _combatDebris ??= new(GraphicsDevice, _sceneTextures!);
        // These constructors snapshot immutable cap/bit/strip art. Create them
        // only after the complete verified closure is resident, avoiding a
        // permanently latched pre-promotion fallback (P1075).
        if (_combatReady || BattlePackReady?.Invoke("scene-combat") != true) return;
        _combatExplosionArt = ExplosionAssets.Load(GraphicsDevice, _assets); // slices exp22 BEFORE any GPU upload
        _combatExplosions = new(GraphicsDevice, _combatExplosionArt);
        _combatHits = new(GraphicsDevice, _sceneTextures!, _assets);
        _combatFlares = new(GraphicsDevice, _sceneTextures!, _assets);
        _combatMines = new(GraphicsDevice, MineModelSource.LoadTexture(_assets));
        var chaff = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        foreach (string stem in ChaffModelSource.RealStems)
        {
            if (!_assets.Exists(stem + ".mdl")) continue;
            var decoded = ChaffModelSource.LoadTexture(_assets, stem);
            var texture = new Texture2D(GraphicsDevice, decoded.Width, decoded.Height);
            texture.SetData(MdlTextureDecoder.ToPremultipliedRgba(decoded)); chaff.Add(stem, texture);
        }
        _combatChaff = new(GraphicsDevice, chaff); // owns these textures
        _combatReady = true;
    }

    private void BeginCombat(ClientWorld world, ShipEntity own)
    {
        EnsureCombatRenderers();
        EnsureBrowserVolumetric();
        // Warm the two sides' small pod closures before an eject can replace a
        // hull. The normal hull roster still requests remote pods and stand-ins
        // remain available if any request fails. Delivery policy only.
        if (world.SideRoster is { } roster)
            foreach (var side in roster.Sides)
                if (ResolveLifepodHull(side.SideId) is { } pod) QueueCombatPack("hull-" + pod);
        _lastCombatSector = own.SectorId;
        _combatMissileCount = _combatExplosionCount = _combatPodCount = _combatOwnPodCount = _combatOwnPodMeshes = 0;
        _combatMineCount = _combatChaffCount = _combatProbeCount = _combatTreasureCount = 0;
        _combat.BeginFrame(world, own.SectorId, (float)_flightElapsedSeconds);
    }

    // Desktop Game1.Rendering.cs:1192-1442: missiles → probes → mines → chaff → treasures.
    // Native model/texture inputs: missileigc.cpp:49-51; probeigc.cpp:112-115;
    // mineigc.cpp:103 / efgeo.cpp:15-87; chaffigc.cpp:42-55; treasureIGC.cpp:118-171.
    private void DrawCombatObjects(ClientWorld world, ShipEntity own, Matrix view, Matrix projection, Vector3 eye)
    {
        long now = ClientNowMilliseconds;
        world.PruneExpiredMissiles(now);
        foreach (var missile in world.Missiles.Values)
        {
            if (missile.ClusterId != own.SectorId) continue;
            _combat.GatherMissile(missile);
            string model = MissileModelSource.ResolveModelName(missile.TypeId, _shopCatalog?.MissileTypes);
            QueueCombatPack("missile-" + missile.TypeId);
            _missileFacing.Set(-missile.Forward);
            var matrix = PoseConverter.ToXnaRotation(_missileFacing) * Matrix.CreateTranslation(V(missile.Position) - eye);
            if (_combatMissiles!.MeshFor(model) is not null)
                _combatMissiles.Draw(matrix, view, projection, model,
                    _combatMissiles.TextureFor(model, MissileModelSource.ResolveTextureName(missile.TypeId, _shopCatalog?.MissileTypes)), _sceneLighting);
            else _flightBolts!.Draw(Matrix.CreateScale(1, 1, 4) * matrix, new(.8f, .7f, .5f), view, projection);
            _combatMissileCount++;
        }
        foreach (var probe in world.Probes.Values)
        {
            if (probe.ClusterId != own.SectorId) continue;
            QueueCombatPack("probe-" + probe.ProbetypeId);
            if (CombatProbe(probe.ProbetypeId) is { } renderer)
            {
                float radius = _shopCatalog?.ProbeTypes?.GetValueOrDefault(probe.ProbetypeId).Radius ?? 1;
                float scale = renderer.MeshRadius > 0 ? radius / renderer.MeshRadius : 1;
                var tint = own.SideId == probe.SideId ? StationRenderer.FriendlyColor : StationRenderer.EnemyColor;
                renderer.Draw(Matrix.CreateScale(scale) * Matrix.CreateTranslation(V(probe.Position) - eye), view, projection, tint, _sceneLighting);
            }
            else _flightBolts!.Draw(Matrix.CreateScale(4) * Matrix.CreateTranslation(V(probe.Position) - eye), new(.4f, .8f, .7f), view, projection);
            _combatProbeCount++;
        }
        if (_combatMines is not null)
            foreach (var mine in world.Mines.Values)
            {
                if (mine.ClusterId != own.SectorId) continue;
                _combatMines.DrawCloud(V(mine.Position) - eye, MineModelSource.ResolveRadius(mine.MinetypeId, _shopCatalog?.MineTypes),
                    mine.Strength, mine.MineId, eye, view, projection); _combatMineCount++;
            }
        world.PruneExpiredChaff(now, _shopCatalog?.ChaffTypes);
        if (_combatChaff is not null)
            foreach (var chaff in world.Chaff)
            {
                var position = chaff.PositionAt(now);
                if ((position - own.Latest!.Position).LengthSquared() > 5900f * 5900f) continue;
                float age = (float)Math.Max(0, chaff.ElapsedSeconds(now));
                _combatChaff.Draw(V(position) - eye, ChaffAnimation.CurrentRadius(age,
                    ChaffModelSource.ResolveLifespan(chaff.Etid, _shopCatalog?.ChaffTypes),
                    ChaffModelSource.ResolveRadiusCeiling(chaff.Etid, _shopCatalog?.ChaffTypes)),
                    ChaffModelSource.ResolveStem(chaff.Etid, _shopCatalog?.ChaffTypes), ChaffAnimation.RollAngle(age), view, projection);
                _combatChaffCount++;
            }
        foreach (var treasure in world.Treasures.Values)
        {
            if (treasure.ClusterId != own.SectorId) continue;
            var visual = TreasureVisualResolver.Resolve(treasure.Code, treasure.PayloadId, _shopCatalog);
            QueueCombatPack(visual.IsDecal ? "treasure-flags" : "treasure-model-" + visual.ModelStem?.ToLowerInvariant());
            if (!_treasureBorn.TryGetValue(treasure.TreasureId, out double began)) _treasureBorn[treasure.TreasureId] = began = _nowSeconds;
            bool drawn = visual.IsDecal
                ? _combatTreasures!.DrawDecal(visual, treasure.TreasureId, V(treasure.Position) - eye, view, projection, _sceneLighting)
                : _combatTreasures!.DrawModel(visual, treasure.TreasureId, Matrix.CreateTranslation(V(treasure.Position) - eye), view, projection, _nowSeconds - began, _sceneLighting);
            if (!drawn) _combatGem!.Draw(Matrix.CreateScale(TreasureRenderer.DefaultRadius) * Matrix.CreateTranslation(V(treasure.Position) - eye), view, projection, _flightElapsedSeconds);
            _combatTreasureCount++;
        }
        _treasureDead.Clear();
        foreach (short id in _treasureBorn.Keys) if (!world.Treasures.ContainsKey(id)) _treasureDead.Add(id);
        foreach (short id in _treasureDead) _treasureBorn.Remove(id);
    }

    private ProbeRenderer? CombatProbe(short id)
    {
        if (_combatProbes.TryGetValue(id, out var renderer)) return renderer;
        long revision = (_assets as IAssetContentRevision)?.ContentRevision ?? 0;
        if (_probeMisses.GetValueOrDefault(id, -1) == revision) return null;
        if (_shopCatalog?.ProbeTypes?.TryGetValue(id, out var type) == true && !string.IsNullOrEmpty(type.ModelName)
            && _assets.Exists(type.ModelName + ".mdl"))
        {
            try
            {
                var file = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, type.ModelName + ".mdl"));
                string? texture = ProbeModelSource.ResolveTextureStemFor(file, type.TextureName, type.ModelName, s => _assets.Exists(s + ".mdl"));
                renderer = new(GraphicsDevice, file.Meshes, assets: _assets, modelStem: type.ModelName, textureStem: texture);
                _combatProbes.Add(id, renderer); return renderer;
            }
            catch (Exception error) { Console.WriteLine($"[browser] probe {id}: {error.Message}"); }
        }
        _probeMisses[id] = revision; return null;
    }

    private (SimVec Ellipse, float MeshRadius) CombatShieldEllipse(short id, float radius)
    {
        if (_battleScene?.Hull(id) is { } hull && _hullUploads?.Uploaded(id) == true
            && _flightShips?.ShieldEllipse(hull.ModelName) is { X: > 0, Y: > 0, Z: > 0 } ext)
            return BrowserCombatEffects.ShieldShape(new(ext.X, ext.Y, ext.Z), _flightShips.MeshRadius(hull.ModelName), radius);
        return (new(radius, radius, radius), radius);
    }

    // Desktop: trails:1453-1469; debris:1485-1500; blasts:1575-1620; hits:1623-1711.
    private void DrawCombatTrails(Matrix view, Matrix projection, Vector3 eye)
    {
        _combat.FinishFrame((float)_flightElapsedSeconds, _fullEffectLoad);
        _combatTrails!.Draw(_combat.TrailViews, eye, view, projection, _sceneLighting);
    }
    private void DrawCombatDebris(Matrix view, Matrix projection, Vector3 eye) =>
        _combatDebris!.Draw(_combat.DebrisViews, eye, view, projection, _sceneLighting);

    private void DrawCombatHits(ClientWorld world, ShipEntity own, Matrix view, Matrix projection, Vector3 eye)
    {
        long now = ClientNowMilliseconds;
        BrowserCombatEffects.GatherExplosions(world, own.SectorId, now, _combatBlasts);
        _combat.AppendEjectExplosions(world, _combatBlasts);
        if (_combatExplosions is not null)
        {
            if (!DrawBrowserVolumetric(own.SectorId, now, eye, view, projection))
                _combatExplosions.Draw(_combatBlasts, own.SectorId, now, eye, view, projection);
            _combatExplosionCount = _combatBlasts.Count;
            foreach (var blast in _combatBlasts)
                if (_combatSeenBlasts.Add(blast))
                {
                    if (_combatBlastReceipts.Count == 128) _combatBlastReceipts.RemoveAt(0);
                    _combatBlastReceipts.Add(new { sector = blast.ClusterId, type = blast.Type,
                        position = new[] { blast.Position.X, blast.Position.Y, blast.Position.Z },
                        receivedMs = blast.ReceivedLocalMs, drawnMs = now,
                        ejectShipId = _combat.EjectExplosions.TryGetValue(blast, out short victim) ? (short?)victim : null });
                }
        }
        _combatSeenBlasts.RemoveWhere(blast => !world.Explosions.Contains(blast) && !_combat.EjectExplosions.ContainsKey(blast));
        foreach (var hit in world.TakeNewHullHits())
            _combat.IngestHit(hit, world, id => _battleScene!.Hull(id), CombatShieldEllipse);
        // New hits are presented in this same frame, without ageing them twice.
        _combat.HitViews.Clear(); _combat.HitViews.AddRange(_combat.HullHits.Enumerate());
        _combat.Flashes.Clear(); _combat.Flashes.AddRange(_combat.HullHits.EnumerateFlashes());
        _combat.FlareViews.Clear(); _combat.FlareViews.AddRange(_combat.Flares.Enumerate());
        _combatHits?.Draw(_combat.HitViews, _combat.Flashes, eye, view, projection);
        _combatFlares?.Draw(_combat.FlareViews, eye, view, projection);
    }

    private void ResetCombat()
    {
        _combat.Reset(); _treasureBorn.Clear(); _combatBlasts.Clear(); _combatSeenBlasts.Clear(); _combatBlastReceipts.Clear();
        _combatMissileCount = _combatExplosionCount = _combatPodCount = _combatOwnPodCount = _combatOwnPodMeshes = 0;
        _combatMineCount = _combatChaffCount = _combatProbeCount = _combatTreasureCount = 0; _lastCombatSector = -1;
    }
    private void DisposeCombat()
    {
        _browserVolumes?.Dispose(); _browserVolumes = null;
        _combatMissiles?.Dispose(); _combatTrails?.Dispose(); _combatExplosions?.Dispose(); _combatExplosionArt?.Dispose();
        _combatHits?.Dispose(); _combatFlares?.Dispose(); _combatDebris?.Dispose(); _combatMines?.Dispose();
        _combatChaff?.Dispose(); _combatTreasures?.Dispose(); _combatGem?.Dispose();
        foreach (var renderer in _combatProbes.Values) renderer.Dispose();
        _combatProbes.Clear();
    }
}

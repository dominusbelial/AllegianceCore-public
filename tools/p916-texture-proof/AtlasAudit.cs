using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// GPU readback of the actual production uploads at real mesh triangle UVs.
// Expected rows come from the independent file-order decoder invocation;
// the legacy default is deliberately retained for image/decal checks.
sealed class AtlasAudit(string output) : Game
{
    private int _samples, _changed, _textures, _models;
    private readonly List<object> _rows = [];
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private string _art = "";
    private readonly HashSet<string> _missingTextures = new();
    public static void Run(string output)
    {
        using var audit = new AtlasAudit(output);
        _ = new GraphicsDeviceManager(audit) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64 };
        audit.Run();
    }
    protected override void Initialize()
    {
        base.Initialize();
        try { CheckCorpus(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        Exit();
    }
    private void CheckCorpus()
    {
        _art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(_art, "PCore001.igc")));
        var assets = new DirectoryAssetSource(_art);
        using var hd = new HdMaterialRenderer(GraphicsDevice, NativeMaterialParts.NativeReader(assets), new HdPack(Path.Combine(output, "absent-pack")));
        using var ships = new NativeShipRenderer(GraphicsDevice, assets, hd: hd);
        using var stations = new NativeStationRenderer(GraphicsDevice, assets, hd);
        using var rocks = new NativeAsteroidRenderer(GraphicsDevice, assets, hd);
        using var missiles = new NativeMissileRenderer(GraphicsDevice, assets, hd);
        using var projectiles = new ProjectileModelRenderer(GraphicsDevice, assets, hd);
        using var treasure = new NativeTreasureRenderer(GraphicsDevice, assets, hd);
        foreach (var hull in core.HullTypes.Values)
        {
            if (ships.MeshFor(hull.ModelName) is null) throw new Exception("Missing hull " + hull.HullId);
            Model("hull", hull.ModelName, ships.TextureStemFor(hull.ModelName, hull.TextureName), ships.TextureFor);
        }
        foreach (var station in core.StationTypes.Values)
            Model("station", station.ModelName, stations.TextureStemFor(station.ModelName, station.TextureName), stations.TextureFor);
        foreach (var rock in Enumerable.Range(0, NativeAsteroidTypes.Count).Select(NativeAsteroidTypes.GetTypeDefaults))
            Model("rock", rock.ModelName, rocks.TextureStemFor(rock.ModelName, rock.TextureName), rocks.TextureFor);
        foreach (var missile in core.MissileTypes.Values.Where(t => !string.IsNullOrEmpty(t.ModelName)))
        {
            string? stem = missiles.TextureStemFor(missile.ModelName, missile.TextureName);
            Model("missile", missile.ModelName, stem, _ => missiles.TextureFor(missile.ModelName, missile.TextureName));
        }
        foreach (var projectile in core.ProjectileTypes.Values.Where(t => !string.IsNullOrEmpty(t.ModelName)))
            Model("projectile", projectile.ModelName, ProjectileModelRenderer.ResolveTextureStemFor(MdlFile.Parse(File.ReadAllBytes(Path.Combine(_art, projectile.ModelName + ".mdl"))), projectile.ModelName, s => File.Exists(Path.Combine(_art, s + ".mdl"))), _ => projectiles.TextureFor(projectile.ModelName));
        foreach (string name in new[] { "acs14", "artifact" })
            Model("treasure", name, treasure.TextureStemFor(name), treasure.TextureFor);
        // The initial and per-type probe bindings use these same native rows.
        // Exercise the renderer's independent per-part texture loader too.
        foreach (var probe in core.ProbeTypes.Values.Where(t => !string.IsNullOrEmpty(t.ModelName)))
        {
            var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(_art, probe.ModelName + ".mdl")));
            string? stem = ProbeModelSource.ResolveTextureStemFor(mdl, probe.TextureName, probe.ModelName, s => File.Exists(Path.Combine(_art, s + ".mdl")));
            using var renderer = new ProbeRenderer(GraphicsDevice, mdl.Meshes, null, hd, assets, probe.ModelName, null);
            var load = typeof(ProbeRenderer).GetMethod("TextureFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Model("probe", probe.ModelName, stem, s => (Texture2D?)load.Invoke(renderer, [s]));
        }
        int planets = CheckSkyAndEffects(assets, hd);
        (string Stem, Func<MdlTexture> Load)[] imageChecks = [
            (MineModelSource.SpriteStem, () => MineModelSource.LoadTexture(assets)),
            (ChaffModelSource.DefaultStem, () => ChaffModelSource.LoadTexture(assets, ChaffModelSource.DefaultStem)),
            ("f03bmp", () => NativeEffectTextures.Resolve(File.ReadAllBytes(Path.Combine(_art,"f03bmp.mdl"))).Texture!)];
        foreach (var (stem, load) in imageChecks)
        {
            var bytes = File.ReadAllBytes(Path.Combine(_art, stem + ".mdl"));
            var expected = MdlTextureDecoder.Decode(bytes, flipVertical: true);
            var actual = load();
            if (!actual.Rgba.SequenceEqual(expected.Rgba)) throw new Exception("Image rows changed: " + stem);
        }
        // The real known pod panels must sample the authored non-black rows.
        if (_changed < 1000) throw new Exception("Corpus did not distinguish old row order: " + _changed);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "corpus.json"), JsonSerializer.Serialize(new { hulls = core.HullTypes.Count, models = _models,
            textures = _textures, planets, missingTextures = _missingTextures.Order().ToArray(), uvSamples = _samples, samplesWrongBefore = _changed, rows = _rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"P916_ATLAS_PASS hulls={core.HullTypes.Count} models={_models} textures={_textures} uvSamples={_samples} samplesWrongBefore={_changed} imageGuards=3 planets={planets}");
    }
    private int CheckSkyAndEffects(IAssetSource assets, HdMaterialRenderer hd)
    {
        using (var sky = new NativeSkyRenderer(GraphicsDevice, assets))
        {
            var load = typeof(NativeSkyRenderer).GetMethod("TextureFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (int i = 1; i <= 8; i++)
                Model("sky", "globe" + i, "environment" + i + "bmp", stem => (Texture2D?)load.Invoke(sky, [stem]));
        }
        int planets = 0;
        foreach (string path in Directory.EnumerateFiles(_art,"nebplnt*bmp.mdl"))
        {
            using var planet = new PlanetPoster(GraphicsDevice,assets);
            string stem = Path.GetFileNameWithoutExtension(path);
            var load = typeof(PlanetPoster).GetMethod("PlanetTextureFor",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var texture = (Texture2D?)load.Invoke(planet,[stem]) ?? throw new Exception("Missing planet "+stem);
            var decoded = NativeStationTexture.Decode(GraphicsDevice,assets,stem);
            var actual = new byte[decoded.ByteCount]; texture.GetData(actual);
            if (!actual.SequenceEqual(MdlTextureDecoder.ToPremultipliedRgba(decoded,10))) throw new Exception("Wrong planet rows: "+stem);
            planets++;
        }
        var bitLoad = typeof(HullHitSparkRenderer).GetMethod("LoadBitTexture", BindingFlags.NonPublic | BindingFlags.Static)!;
        using (var bitTexture = (Texture2D)bitLoad.Invoke(null, [GraphicsDevice,assets])!)
            Model("debris-bit", HullHitSparkRenderer.BitModelStem, HullHitSparkRenderer.BitTextureStem, _ => bitTexture);
        using (var warp = new WarpRenderer(GraphicsDevice, assets, hd))
        {
            var load = typeof(WarpRenderer).GetMethod("FunnelTexture", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var texture = (Texture2D)load.Invoke(warp, ["aleph"])!;
            var bytes = File.ReadAllBytes(Directory.EnumerateFiles(_art).Single(p => Path.GetFileName(p).Equals("alephbmp.mdl",StringComparison.OrdinalIgnoreCase)));
            var resolved = NativeEffectTextures.Resolve(bytes, flipVertical:false);
            MdlTexture expected;
            if (resolved.Texture is { } binary) expected = binary;
            else
            {
                using var stream = File.OpenRead(Path.Combine(_art,resolved.PngName!));
                using var image = Texture2D.FromStream(GraphicsDevice,stream);
                var data = new byte[image.Width*image.Height*4]; image.GetData(data);
                expected = new(image.Width,image.Height,data);
            }
            var actual = new byte[expected.ByteCount]; texture.GetData(actual);
            if (!actual.SequenceEqual(expected.Rgba)) throw new Exception("Wrong aleph funnel rows");
        }
        return planets;
    }

    private void Model(string family, string name, string? inherited, Func<string, Texture2D?> load)
    {
        if (string.IsNullOrEmpty(name)) return;
        var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(_art, name + ".mdl")));
        var meshes = mdl.Meshes.Concat(mdl.LodLevels.SelectMany(l => l.Meshes)).ToArray();
        int samplesBefore = _samples;
        foreach (var mesh in meshes)
        {
            string? stem = mesh.TextureStem ?? inherited;
            if (stem is null) continue;
            string path = Path.Combine(_art, stem + ".mdl");
            if (!File.Exists(path)) { _missingTextures.Add(name + "/" + stem); continue; }
            var texture = load(stem) ?? throw new Exception($"Existing {family} texture failed to upload: {name}/{stem}");
            var bytes = File.ReadAllBytes(path);
            MdlTexture expected;
            if (NativeStationTexture.ImportedImagePath(bytes) is { } png)
            {
                using var stream = File.OpenRead(Path.Combine(_art, png));
                using var native = Texture2D.FromStream(GraphicsDevice, stream);
                var colors = new Color[native.Width * native.Height]; native.GetData(colors);
                expected = new(native.Width, native.Height, colors.SelectMany(c => new[] { c.R,c.G,c.B,c.A }).ToArray());
            }
            else expected = MdlTextureDecoder.Decode(bytes, flipVertical: false);
            var actual = new byte[expected.ByteCount]; texture.GetData(actual);
            // Full upload equality catches non-UV islands and all colour channels,
            // including team-colour marks and authored emissive pixels.
            bool keyed = family is "probe" or "missile" or "treasure" or "debris-bit" or "sky";
            var expectedPixels = keyed ? MdlTextureDecoder.ToPremultipliedRgba(expected, family == "sky" ? 10 : 40) : expected.Rgba;
            if (!actual.SequenceEqual(expectedPixels)) throw new Exception($"Wrong uploaded rows: {family} {name}/{stem}");
            if (_checked.Add(family + ":" + stem)) _textures++;
            foreach (var tri in mesh.Indices.Chunk(3))
            {
                float u = tri.Average(i => mesh.TexCoords[2*i]);
                float v = tri.Average(i => mesh.TexCoords[2*i+1]);
                int x = Math.Clamp((int)(u * texture.Width), 0, texture.Width-1);
                int y = Math.Clamp((int)(v * texture.Height), 0, texture.Height-1);
                int pixel = 4 * (y * texture.Width + x), old = 4 * ((texture.Height-1-y) * texture.Width+x);
                if (!expectedPixels.AsSpan(pixel,4).SequenceEqual(expectedPixels.AsSpan(old,4))) _changed++;
                if (!actual.AsSpan(pixel,4).SequenceEqual(expectedPixels.AsSpan(pixel,4))) throw new Exception("Wrong mesh UV sample");
                _samples++;
            }
        }
        _models++;
        _rows.Add(new { family, model = name, texture = inherited, uvSamples = _samples-samplesBefore });
    }
}

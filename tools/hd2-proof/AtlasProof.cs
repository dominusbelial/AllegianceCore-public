using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// Real GL draws through each production renderer with real native meshes and
// deliberately synthetic maps. This is a regression test, not visual evidence.
sealed class AtlasProof : Game
{
    private readonly string _repo, _kind, _art;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "hd3-gl-" + Guid.NewGuid());
    private ShaderAssetCache _shaders = null!;
    private string _model = "", _stem = "";
    private MdlFile? _mdl;
    private IAssetSource _assets = null!;
    private string[] _stems = [];
    public AtlasProof(string repo, string kind, string art)
    {
        _repo = repo; _kind = kind; _art = art;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 192, PreferredBackBufferHeight = 192,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
        IsFixedTimeStep = false;
    }
    protected override void LoadContent()
    {
        Directory.CreateDirectory(_scratch);
        _assets = new DirectoryAssetSource(_art);
        if (_kind == "aleph_png")
        {
            using var texture = new Texture2D(GraphicsDevice, 8, 8);
            texture.SetData(Enumerable.Repeat(Color.White, 64).ToArray());
            using var png = new MemoryStream(); texture.SaveAsPng(png, 8, 8);
            _assets = new PngAlephSource(_assets, png.ToArray());
        }
        _shaders = ShaderAssetCache.ForDevice(GraphicsDevice);
        string shader = "Content/Shaders/NativePbr.ogl.mgfxo";
        if (_shaders.LoadFrom(shader, () => File.OpenRead(Path.Combine(_repo, "src/Allegiance.ClientCore.MonoGame", shader))) is null)
            throw new Exception("PBR shader missing");
        _model = _kind switch { "ship" => "dn_faphshfig", "station" => "ss27", "missile" => "mis02",
            "probe" => "utl02", "projectile" => "bgrnd55", "asteroid" => "bgrnd03", "treasure" => "acs14", _ => "" };
        if (_model.Length > 0)
        {
            _mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(_art, _model + ".mdl")));
            _stems = _mdl.Meshes.Select(m => m.TextureStem).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _stem = _stems.First();
        }
        else { _stem = "Alephbmp"; _stems = [_stem]; }
        if (_kind == "ship" && _stems.Length != 2) throw new Exception("multi-atlas fixture lost its two atlas scopes");
        WritePack(_stems);
    }
    private void WritePack(string[] stems)
    {
        var entries = new Dictionary<string, object>();
        foreach (string stem in stems)
        {
            string dir = Path.Combine(_scratch, "textures", stem); Directory.CreateDirectory(dir);
            using var native = _assets.OpenRead(stem + ".mdl");
            entries[stem] = new { native = new { TextureSha256 = Convert.ToHexString(SHA256.HashData(native)) } };
            foreach (string map in HdPack.MapNames)
            {
                Color color = map switch { "basecolor" => Color.Magenta, "normal" => new Color(128, 128, 255),
                    "roughness" => new Color(180, 180, 180), "emission" => new Color(40, 0, 40), _ => Color.Black };
                using var texture = new Texture2D(GraphicsDevice, 8, 8); texture.SetData(Enumerable.Repeat(color, 64).ToArray());
                using var file = File.Create(Path.Combine(dir, map + ".png")); texture.SaveAsPng(file, 8, 8);
            }
        }
        File.WriteAllText(Path.Combine(_scratch, "manifest.json"), JsonSerializer.Serialize(new { schema = 2, textures = entries }));
    }
    protected override void Draw(GameTime time)
    {
        var native = Render(false, _scratch, out int offCount);
        var hd = Render(true, _scratch, out int count);
        if (offCount != 0 || count != _stems.Length) throw new Exception($"{_kind}: material counts off={offCount} HD={count}, expected={_stems.Length}");
        if (native.SequenceEqual(hd) || !hd.Any(c => c.R > 20 || c.G > 20 || c.B > 20)) throw new Exception(_kind + ": HD did not change visible mesh");
        var absent = Render(true, Path.Combine(_scratch, "absent"), out int missingCount);
        if (missingCount != 0 || !native.SequenceEqual(absent)) throw new Exception(_kind + ": missing pack changed native fallback");
        File.Delete(Path.Combine(_scratch, "textures", _stems[0], "normal.png"));
        var partial = Render(true, _scratch, out int partialCount);
        if (partialCount != _stems.Length - 1) throw new Exception(_kind + ": missing map affected unrelated atlas");
        if (_stems.Length == 1 && !native.SequenceEqual(partial)) throw new Exception(_kind + ": incomplete map fallback differs");
        Console.WriteLine($"HD3_ATLAS_PASS {_kind}: {_stems.Length} per-mesh materials; toggle, missing pack/maps, native parity");
        Exit();
    }
    private Color[] Render(bool enabled, string pack, out int count)
    {
        var assets = _assets;
        using var hd = new HdMaterialRenderer(GraphicsDevice, NativeMaterialParts.NativeReader(assets), new HdPack(pack), shaders: _shaders) { Enabled = enabled };
        using var target = new RenderTarget2D(GraphicsDevice, 192, 192, false, SurfaceFormat.Color, DepthFormat.Depth24);
        GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
        GraphicsDevice.BlendState = BlendState.Opaque; GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        var view = Matrix.CreateLookAt(new(0, 1, 7), Vector3.Zero, Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(.6f, 1, .1f, 100);
        var light = SectorLighting.FromCluster([.4f, .5f, 1], 0xffddddcc);
        float radius = _mdl is null ? 1 : NativeShipResolver.MergedRadius(_mdl.Meshes);
        float scale = 1.5f / radius;
        var world = Matrix.CreateScale(scale) * Matrix.CreateRotationY(.5f);
        switch (_kind)
        {
            case "ship":
                using (var renderer = new NativeShipRenderer(GraphicsDevice, assets, hd: hd))
                    renderer.Draw(renderer.MeshFor(_model)!, renderer.TextureFor(_stem), Matrix.Identity, view, proj, 1, scale, light, _model, _stem);
                break;
            case "station":
                using (var renderer = new NativeStationRenderer(GraphicsDevice, assets, hd))
                    renderer.Draw(renderer.MeshFor(_model)!, renderer.TextureFor(_stem), Matrix.Identity, view, proj, scale, light, _model, _stem);
                break;
            case "asteroid":
                using (var renderer = new NativeAsteroidRenderer(GraphicsDevice, assets, hd))
                    renderer.Draw(renderer.GeometryFor(_model)!, renderer.TextureFor(_stem), Matrix.Identity, view, proj, scale, light, _model, _stem);
                break;
            case "missile":
                using (var renderer = new NativeMissileRenderer(GraphicsDevice, assets, hd))
                    renderer.Draw(world, view, proj, _model, renderer.TextureFor(_model, null), light);
                break;
            case "projectile":
                using (var renderer = new ProjectileModelRenderer(GraphicsDevice, assets, hd))
                    renderer.Draw(renderer.MeshFor(_model)!, renderer.TextureFor(_model), Vector3.Zero, Vector3.UnitZ, .3f, view, proj, scale, _model, light);
                break;
            case "probe":
                var decoded = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(_art, _stem + ".mdl")));
                var tex = new Texture2D(GraphicsDevice, decoded.Width, decoded.Height); tex.SetData(MdlTextureDecoder.ToPremultipliedRgba(decoded));
                using (var renderer = new ProbeRenderer(GraphicsDevice, _mdl!.Meshes, tex, hd, assets, _model, _stem))
                    renderer.Draw(world, view, proj, Color.White, light);
                break;
            case "treasure":
                using (var renderer = new NativeTreasureRenderer(GraphicsDevice, assets, hd))
                    renderer.DrawModel(new TreasureVisual(TreasureVisualKind.Cash, _model, _stem, "", "", false), 1, world, view, proj, 0, light);
                break;
            case "aleph":
            case "aleph_png":
                using (var renderer = new WarpRenderer(GraphicsDevice, assets, hd))
                    renderer.Draw(Matrix.Identity, view, proj, 0, 1, "aleph", 1);
                break;
        }
        count = hd.ResidentMaterials;
        GraphicsDevice.SetRenderTarget(null);
        var pixels = new Color[192 * 192]; target.GetData(pixels); return pixels;
    }
    protected override void UnloadContent()
    {
        _shaders.Dispose(); Directory.Delete(_scratch, true); base.UnloadContent();
    }

    private sealed class PngAlephSource(IAssetSource native, byte[] png) : IAssetSource
    {
        public bool Exists(string path) => IsUnit(path) || path == "hd3_native.png" || native.Exists(path);
        public Stream OpenRead(string path) => IsUnit(path)
            ? new MemoryStream(System.Text.Encoding.ASCII.GetBytes("use \"model\"; image = ImageFromFile(\"hd3_native.png\", true);"))
            : path == "hd3_native.png" ? new MemoryStream(png) : native.OpenRead(path);
        public IEnumerable<string> Enumerate(string directory) => native.Enumerate(directory);
        private static bool IsUnit(string path) => path.Equals("alephbmp.mdl", StringComparison.OrdinalIgnoreCase);
    }
}

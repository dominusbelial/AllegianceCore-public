using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

string root = Path.GetFullPath(args[0]);
bool test = args.Contains("--test");
string? Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 ? args[i + 1] : null; }
if (Option("--compressed-test") is { } compressedPack)
{
    using var proof = new CompressedProof(root, compressedPack, Option("--art") ?? ExternalPaths.Require(ExternalResourceKind.Artwork));
    proof.Run(); return;
}
if (Option("--atlas-test") is { } kind)
{
    using var atlas = new AtlasProof(root, kind, Option("--art") ?? ExternalPaths.Require(ExternalResourceKind.Artwork));
    atlas.Run();
    return;
}
using var game = new HdProof(root, test, Option("--output"), Option("--pilot"), Option("--art"));
game.Run();

sealed class HdProof : Game
{
    private readonly string _root, _temporary = Path.Combine(Path.GetTempPath(), "hd2-proof-" + Guid.NewGuid());
    private readonly bool _test;
    private readonly string? _output, _pilot, _art;
    private ShaderAssetCache _shaders = null!;
    private NativeShipRenderer _renderer = null!;
    private NativeShipRenderer.NativeShipGeometry _geometry = null!;
    private Texture2D _nativeTexture = null!;
    private FsrPostProcessor _fsr = null!;
    private BloomPostProcessor _bloom = null!;
    private GlFrameTimer? _timer;
    private string _model = "hull", _texture = "hullbmp";
    private float _scale = 1;
    private int _frame, _phase;
    private readonly Dictionary<int, List<double>> _sceneSamples = new(), _postSamples = new();
    private readonly List<object> _results = [];
    private static readonly byte[] NativeModel = [1, 2, 3], NativeTexture = [4, 5, 6];
    private readonly SectorLighting _light = SectorLighting.FromCluster([.4f, .5f, 1], 0xffddddcc);
    private readonly Matrix _view = Matrix.CreateLookAt(new(0, 0, 7), Vector3.Zero, Vector3.Up);
    public HdProof(string root, bool test, string? output, string? pilot, string? art)
    {
        _root = root; _test = test; _output = output; _pilot = pilot; _art = art;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = test ? 321 : 2560,
            PreferredBackBufferHeight = test ? 181 : 1440, GraphicsProfile = GraphicsProfile.HiDef,
            SynchronizeWithVerticalRetrace = false };
        IsFixedTimeStep = false;
    }
    protected override void LoadContent()
    {
        Directory.CreateDirectory(_temporary);
        if (_output is not null) Directory.CreateDirectory(_output);
        _shaders = ShaderAssetCache.ForDevice(GraphicsDevice);
        foreach (string stem in new[] { "NativePbr", "FsrEasu", "FsrRcas", "Bloom" })
        {
            string relative = $"Content/Shaders/{stem}.ogl.mgfxo";
            if (_shaders.LoadFrom(relative, () => File.OpenRead(Path.Combine(_root, "src/Allegiance.ClientCore.MonoGame", relative))) is null)
                throw new Exception("shader failed: " + stem);
        }
        IAssetSource assets;
        if (_pilot is not null)
        {
            // Deliberate isolated REVIEW only: never edit the private manifest or
            // change production acceptance. No generated maps are copied to the repo.
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_pilot, "manifest.json")))!;
            _model = "wc_icfig";
            foreach (var job in manifest["jobs"]!.AsObject())
                if (job.Value?["metrics"] is JsonObject metrics) metrics["accepted"] = true;
            File.WriteAllText(Path.Combine(_temporary, "manifest.json"), manifest.ToJsonString());
            Directory.CreateSymbolicLink(Path.Combine(_temporary, "models"), Path.Combine(_pilot, "models"));
            assets = new DirectoryAssetSource(_art ?? ExternalPaths.Require(ExternalResourceKind.Artwork));
            _renderer = new(GraphicsDevice, assets, new HdPack(_temporary), _shaders);
            _geometry = _renderer.MeshFor(_model) ?? throw new Exception("pilot native model missing");
            _texture = _renderer.TextureStemFor(_model, null) ?? throw new Exception("pilot native texture missing");
            _nativeTexture = _renderer.TextureFor(_texture) ?? throw new Exception("pilot texture missing");
            _scale = 1.5f / _geometry.MeshRadius;
        }
        else
        {
            MakeSyntheticPack(); assets = new SyntheticSource();
            _renderer = new(GraphicsDevice, assets, new HdPack(_temporary), _shaders);
            _geometry = NativeShipRenderer.NativeShipGeometry.Build(GraphicsDevice, [Sphere()]);
            _nativeTexture = HdMaterialRenderer.CreateMipmapped(GraphicsDevice, BasePixels(256), 256, 256, "basecolor");
        }
        _fsr = new(GraphicsDevice, _shaders);
        _bloom = new(GraphicsDevice, _shaders.Load("Content/Shaders/Bloom.ogl.mgfxo")!);
        _timer = GlFrameTimer.TryCreate();
        Console.WriteLine($"HD2 adapter: {GraphicsAdapter.DefaultAdapter.Description}; GL_TIME_ELAPSED={_timer is not null}");
        if (_timer is null && !_test) throw new Exception("GPU timings unavailable");
    }
    protected override void Draw(GameTime time)
    {
        if (_frame == 0 && _phase == 0)
        {
            if (_pilot is null)
            {
                SelfTest();
                _renderer.Dispose();
                _renderer = new(GraphicsDevice, new SyntheticSource(), new HdPack(_temporary), _shaders);
            }
            if (_output is not null) Screenshot();
            if (_test) { Console.WriteLine("HD2_SELF_TEST_PASS: native fallback, maps, mipmaps, budget/LRU, sector lights, cloak, bloom + FSR"); Exit(); return; }
        }
        _timer?.Poll((id, post, ms) =>
        {
            int phase = (int)(id / 1000); int frame = (int)(id % 1000);
            if (frame < 21) return;
            var table = post ? _postSamples : _sceneSamples;
            if (!table.TryGetValue(phase, out var list)) table[phase] = list = [];
            list.Add(ms);
        });
        bool hd = _phase % 2 == 1, fsr = _phase % 4 >= 2;
        int width = _phase < 4 ? 1920 : 2560, height = _phase < 4 ? 1080 : 1440;
        _fsr.SetSize(width, height, fsr ? UpscalingSettings.Scale(FsrPreset.Quality) : 1);
        _frame++;
        long id = _phase * 1000 + _frame;
        _timer?.Start(id, false);
        GraphicsDevice.SetRenderTarget(_fsr.Scene); GraphicsDevice.Clear(new Color(5, 8, 16));
        var projection = Matrix.CreatePerspectiveFieldOfView(.75f, width / (float)height, .1f, 100);
        // 40 visible hulls, same native geometry/transforms in both A/B paths.
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 8; x++)
                DrawHull(Matrix.CreateScale(.28f) * Matrix.CreateTranslation((x - 3.5f) * .65f, (y - 2) * .65f, 0), projection, hd);
        _timer?.Stop(); _timer?.Start(id, true);
        Texture2D source = _fsr.Scene!;
        if (fsr)
        {
            _bloom.SetSize(source.Width, source.Height);
            GraphicsDevice.SetRenderTarget(_fsr.Bloomed); _bloom.Render(source); source = _fsr.Bloomed!;
            _fsr.Render(source, null, .5f);
        }
        else
        {
            GraphicsDevice.SetRenderTarget(null);
            using var batch = new SpriteBatch(GraphicsDevice);
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque); batch.Draw(source, new Rectangle(0, 0, width, height), Color.White); batch.End();
        }
        _timer?.Stop();
        // Readback is outside both queries. It drains the query ring, avoiding
        // the late-result saturation documented in P687's fast inspector.
        var drain = new Color[1]; GraphicsDevice.GetBackBufferData(new Rectangle(0, 0, 1, 1), drain, 0, 1);
        if (_frame >= 100)
        {
            _timer?.Poll((frame, post, ms) =>
            {
                int phase = (int)(frame / 1000);
                var table = post ? _postSamples : _sceneSamples;
                if (!table.TryGetValue(phase, out var list)) table[phase] = list = [];
                list.Add(ms);
            });
            var scene = _sceneSamples[_phase].Order().ToArray(); var post = _postSamples[_phase].Order().ToArray();
            Console.WriteLine($"HD2_TIMING {width}x{height} hd={hd} fsr+bloom={fsr} scene={scene[scene.Length / 2]:F4} post={post[post.Length / 2]:F4} ms samples={scene.Length}; textures={_renderer.HdResidentBytes}");
            _results.Add(new { width, height, hd, fsrAndBloom = fsr, sceneSamples = scene.Length,
                sceneMedianMs = scene[scene.Length / 2], sceneP95Ms = scene[(int)(scene.Length * .95)],
                postMedianMs = post[post.Length / 2], residentBytes = _renderer.HdResidentBytes });
            _frame = 0; _phase++;
            if (_phase == 8)
            {
                if (_output is not null) File.WriteAllText(Path.Combine(_output, "timings.json"), JsonSerializer.Serialize(new {
                    adapter = GraphicsAdapter.DefaultAdapter.Description, method = "P643/P687 GlFrameTimer; 20 warmup + 80 sampled frames; 40 native hulls; readback outside queries", results = _results }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("HD2_BENCH_PASS"); Exit();
            }
        }
        base.Draw(time);
    }
    private void DrawHull(Matrix world, Matrix projection, bool hd, string? model = null, SectorLighting? light = null, float opacity = 1)
    {
        // Cancel the renderer's nose flip for the synthetic sphere/quad view.
        _renderer.Draw(_geometry, _nativeTexture, Matrix.CreateRotationY(MathHelper.Pi) * Matrix.CreateScale(_scale) * world,
            _view, projection, opacity, 1, light ?? _light, model ?? _model, _texture, hd);
    }
    private Color[] Render(string model, bool hd, SectorLighting? light = null, float opacity = 1)
    {
        using var target = new RenderTarget2D(GraphicsDevice, 128, 128, false, SurfaceFormat.Color, DepthFormat.Depth24);
        GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
        DrawHull(Matrix.Identity, Matrix.CreatePerspectiveFieldOfView(.5f, 1, .1f, 100), hd, model, light, opacity);
        GraphicsDevice.SetRenderTarget(null); var pixels = new Color[128 * 128]; target.GetData(pixels); return pixels;
    }
    private void SelfTest()
    {
        var native = Render("hull", false); var missing = Render("missing", true);
        if (!native.SequenceEqual(missing)) throw new Exception("absent HD changed native fallback");
        if (_renderer.HdResidentBytes != 0) throw new Exception("maps loaded before first HD draw");
        var hd = Render("hull", true);
        if (hd.SequenceEqual(native) || hd.Count(c => c.R > 10 || c.G > 10 || c.B > 10) < 500)
            throw new Exception("PBR failed to render native geometry");
        foreach (string variant in new[] { "normal", "metal", "rough", "emission" })
            if (Render(variant, true).SequenceEqual(hd)) throw new Exception("map has no effect: " + variant);
        var emission = Render("emission", true);
        if (!emission.Any(c => c.G > c.R + 60 && c.G > c.B + 60)) throw new Exception("emission not visible");
        if (Render("hull", true, SectorLighting.FromCluster([-.8f, .1f, -1], 0xff555588)).SequenceEqual(hd))
            throw new Exception("sector lighting has no effect");
        if (Render("hull", true, opacity: .3f).SequenceEqual(hd)) throw new Exception("cloak opacity has no effect");
        if (!Render("bad", true).SequenceEqual(native)) throw new Exception("corrupt maps did not fall back");
        CheckBudgetAndMips();
        _fsr.SetSize(321, 181, 1f / 1.5f);
        GraphicsDevice.SetRenderTarget(_fsr.Scene); GraphicsDevice.Clear(Color.Black);
        DrawHull(Matrix.Identity, Matrix.CreatePerspectiveFieldOfView(.5f, 321f / 181, .1f, 100), true, "emission");
        _bloom.SetSize(_fsr.Scene!.Width, _fsr.Scene.Height);
        GraphicsDevice.SetRenderTarget(_fsr.Bloomed); _bloom.Render(_fsr.Scene);
        using var output = new RenderTarget2D(GraphicsDevice, 321, 181);
        _fsr.Render(_fsr.Bloomed!, output, .5f);
        GraphicsDevice.SetRenderTarget(null);
        var screen = new Color[321 * 181]; output.GetData(screen);
        if (!screen.Any(c => c.G > 80)) throw new Exception("PBR -> bloom -> FSR lost image");
    }
    private void CheckBudgetAndMips()
    {
        using var mip = HdMaterialRenderer.CreateMipmapped(GraphicsDevice, [Color.Red, Color.White, Color.Blue, Color.Black], 2, 2, "basecolor");
        var last = new Color[1]; mip.GetData(1, null, last, 0, 1);
        if (mip.LevelCount != 2 || last[0].R < 180 || last[0].B < 180) throw new Exception("sRGB mip filtering failed");
        using var normal = HdMaterialRenderer.CreateMipmapped(GraphicsDevice, [new(255,128,128),new(128,128,255),new(255,128,128),new(128,128,255)], 2, 2, "normal");
        normal.GetData(1, null, last, 0, 1);
        if (last[0].R < 210 || last[0].B < 210) throw new Exception("normal mip renormalization failed");
        long bytes = HdMaterialRenderer.MipBytes(256, 256) * 5;
        double clock = 0;
        using var cache = new HdMaterialRenderer(GraphicsDevice, new SyntheticSource().OpenRead, new HdPack(_temporary),
            bytes + HdMaterialRenderer.MipBytes(256, 256), _shaders, () => clock);
        GraphicsDevice.SetRenderTarget(_fsr.Scene); // null is also valid before the first SetSize
        foreach (string model in new[] { "hull", "metal", "hull" })
        {
            GraphicsDevice.BlendState = BlendState.Opaque; GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            if (!cache.TryDraw(model, _texture, () => _geometry.PbrVertices(GraphicsDevice), _geometry.Indices, _geometry.TriangleCount,
                Matrix.Identity, _view, Matrix.CreatePerspectiveFieldOfView(.5f, 1, .1f, 100), 1, _light))
                throw new Exception("LRU material load failed");
            if (cache.ResidentMaterials != 1 || cache.ResidentBytes != bytes) throw new Exception("texture budget exceeded");
            // A second simultaneously visible model must not cause a decode /
            // eviction cycle every frame when only one material fits.
            string other = model == "hull" ? "metal" : "hull";
            for (int frame = 0; frame < 8; frame++)
                if (cache.TryDraw(other, _texture, () => throw new Exception("hot resident was evicted"), _geometry.Indices,
                    _geometry.TriangleCount, Matrix.Identity, _view, Matrix.Identity, 1, _light))
                    throw new Exception("over-budget visible working set thrashed the cache");
            if (cache.ResidentMaterials != 1 || cache.ResidentBytes != bytes) throw new Exception("hot material residency changed");
            clock += HdMaterialRenderer.EvictionIdleSeconds + 1;
        }
        using var tiny = new HdMaterialRenderer(GraphicsDevice, new SyntheticSource().OpenRead, new HdPack(_temporary), 1, _shaders);
        if (tiny.TryDraw("hull", _texture, () => throw new Exception("overbudget geometry touched"), _geometry.Indices, _geometry.TriangleCount,
            Matrix.Identity, _view, Matrix.Identity, 1, _light) || tiny.ResidentBytes != 0) throw new Exception("oversized material loaded");
    }
    private void Screenshot()
    {
        using var target = new RenderTarget2D(GraphicsDevice, 1200, 600, false, SurfaceFormat.Color, DepthFormat.Depth24);
        GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(new Color(5, 8, 16));
        var projection = Matrix.CreatePerspectiveFieldOfView(.5f, 2, .1f, 100);
        DrawHull(Matrix.CreateTranslation(-1.65f, 0, 0), projection, false);
        DrawHull(Matrix.CreateTranslation(1.65f, 0, 0), projection, true);
        GraphicsDevice.SetRenderTarget(null);
        using var stream = File.Create(Path.Combine(_output!, _pilot is null ? "synthetic-native-hd.png" : "pilot-review-native-hd.png"));
        target.SaveAsPng(stream, 1200, 600);
    }
    private void MakeSyntheticPack()
    {
        var models = new Dictionary<string, object>();
        foreach (string model in new[] { "hull", "normal", "metal", "rough", "emission", "bad" })
        {
            models[model] = new { native = new { Model = model, Texture = "hullbmp", ModelSha256 = Hash(NativeModel), TextureSha256 = Hash(NativeTexture) } };
            string directory = Path.Combine(_temporary, "models", model); Directory.CreateDirectory(directory);
            foreach (string map in HdPack.MapNames)
            {
                var pixels = map == "basecolor" ? BasePixels(256) : new Color[256 * 256];
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                    {
                        int i = y * 256 + x;
                        if (map == "normal")
                        {
                            var n = Vector3.Normalize(new Vector3((model == "normal" ? .9f : .25f) * MathF.Sin(x * .4f), .2f * MathF.Cos(y * .4f), 1));
                            pixels[i] = new Color(n * .5f + new Vector3(.5f));
                        }
                        if (map == "metallic") pixels[i] = new Color(model == "metal" ? .95f : .15f, 0, 0);
                        if (map == "roughness") pixels[i] = new Color(model == "rough" ? .95f : .5f, 0, 0);
                        if (map == "emission") pixels[i] = model == "emission" ? Color.Lime : (x % 64 < 5 && y % 64 < 5 ? new Color(0, 120, 160) : Color.Black);
                        if (model == "emission" && map == "basecolor") pixels[i] = Color.Black;
                    }
                using var texture = new Texture2D(GraphicsDevice, 256, 256); texture.SetData(pixels);
                using var file = File.Create(Path.Combine(directory, map + ".png")); texture.SaveAsPng(file, 256, 256);
            }
        }
        File.WriteAllText(Path.Combine(_temporary, "models", "bad", "normal.png"), "corrupt PNG");
        File.WriteAllText(Path.Combine(_temporary, "manifest.json"), JsonSerializer.Serialize(new { schema = 1, models }));
    }
    private static Color[] BasePixels(int size)
    {
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            pixels[y * size + x] = x % 32 < 2 || y % 32 < 2 ? new Color(40, 50, 55) : (x / 32 + y / 32) % 2 == 0 ? new Color(160, 85, 40) : new Color(70, 115, 135);
        return pixels;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static MdlMesh Sphere()
    {
        const int rings = 24, segments = 48;
        var positions = new List<float>(); var normals = new List<float>(); var uv = new List<float>(); var indices = new List<ushort>();
        for (int r = 0; r <= rings; r++) for (int s = 0; s <= segments; s++)
        {
            float a = r * MathF.PI / rings, b = s * MathF.Tau / segments;
            var n = new Vector3(MathF.Sin(a) * MathF.Cos(b), MathF.Cos(a), MathF.Sin(a) * MathF.Sin(b));
            positions.AddRange([n.X, n.Y, n.Z]); normals.AddRange([n.X, n.Y, n.Z]); uv.AddRange([s / (float)segments, r / (float)rings]);
        }
        for (int r = 0; r < rings; r++) for (int s = 0; s < segments; s++)
        {
            int a = r * (segments + 1) + s, b = a + segments + 1;
            indices.AddRange([(ushort)a, (ushort)(a + 1), (ushort)b, (ushort)(a + 1), (ushort)(b + 1), (ushort)b]);
        }
        return new MdlMesh { Positions = positions.ToArray(), Normals = normals.ToArray(), TexCoords = uv.ToArray(), Indices = indices.ToArray() };
    }
    protected override void UnloadContent()
    {
        _timer?.Dispose(); _renderer.Dispose();
        if (_pilot is null) { _geometry.Dispose(); _nativeTexture.Dispose(); }
        _fsr.Dispose(); _bloom.Dispose(); _shaders.Dispose(); Directory.Delete(_temporary, true);
        base.UnloadContent();
    }
    private sealed class SyntheticSource : IAssetSource
    {
        public bool Exists(string path) => true;
        public Stream OpenRead(string path) => new MemoryStream(path == "hullbmp.mdl" ? NativeTexture : NativeModel);
        public IEnumerable<string> Enumerate(string directory) => [];
    }
}

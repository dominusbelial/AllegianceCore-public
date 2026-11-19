using System.Diagnostics;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// P1057: a deterministic native-mesh scene, shared verbatim with the Chromium
// comparison through scene.json. CPU vertex-normal lighting intentionally removes
// texture/shader differences from the geometry-coverage comparison. The desktop
// post processors and HUD renderers are production code.
internal sealed class AntialiasProof : Game
{
    private const int Width = 1920, Height = 1080;
    private readonly string _output;
    private readonly bool _postOn;
    private readonly bool _benchmark, _check, _excursion;
    private readonly double _seconds;
    private readonly int _samples;
    private readonly HudStyle _style;
    private readonly GraphicsDeviceManager _graphics;
    private readonly List<VertexPositionColor[]> _objects = [];
    private BasicEffect _effect = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _white = null!;
    private HudRenderer _hud = null!;
    private ShaderAssetCache _shaders = null!;
    private VfxPostProcessor _post = null!;
    private FsrPostProcessor _fsr = null!;
    private RenderTarget2D _scene = null!, _final = null!;
    private SceneAntialiasing _aa = null!;
    private RenderTarget2D _legacyScene = null!;
    private GlFrameTimer? _timer;
    private readonly List<double> _frames = [], _gpu = [];
    private readonly Stopwatch _watch = new();
    private long _previous, _frame;
    private int? _physicalSamples;
    private bool _done;
    private readonly Matrix _view = Matrix.Identity;
    private readonly Matrix _projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), (float)Width / Height, 1, 10000);
    public bool Ok { get; private set; }

    private AntialiasProof(string[] args, string repo)
    {
        string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        _output = Path.GetFullPath(Arg("--out") ?? ".scratch/p1057/scene");
        _postOn = args.Contains("--post");
        _benchmark = args.Contains("--aa-benchmark");
        _check = args.Contains("--aa-check");
        _excursion = args.Contains("--excursion");
        _seconds = double.Parse(Arg("--seconds") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(_seconds) || _seconds <= 0) throw new ArgumentException("seconds must be positive");
        _samples = int.Parse(Arg("--samples") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        _style = Arg("--style") == "modern" ? HudStyle.Modern : HudStyle.Classic;
        _graphics = new(this) { PreferredBackBufferWidth = Width, PreferredBackBufferHeight = Height,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
        _graphics.PreparingDeviceSettings += (_, e) => e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
        IsFixedTimeStep = false;
        Content.RootDirectory = Path.Combine(repo, "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
    }

    public static int Run(string[] args, string repo)
    {
        using var game = new AntialiasProof(args, repo);
        game.Run();
        return game.Ok ? 0 : 1;
    }

    protected override void LoadContent()
    {
        Directory.CreateDirectory(_output);
        string local = Path.Combine(AppContext.BaseDirectory, "Content");
        if (!Directory.Exists(local)) Directory.CreateSymbolicLink(local, Content.RootDirectory);
        _effect = new(GraphicsDevice) { VertexColorEnabled = true, View = _view, Projection = _projection };
        _batch = new(GraphicsDevice);
        _white = new(GraphicsDevice, 1, 1); _white.SetData(new[] { Color.White });
        var small = Content.Load<SpriteFont>("HUD");
        var big = Content.Load<SpriteFont>("HUDLarge");
        string art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        _hud = _style == HudStyle.Modern
            ? new ModernHudRenderer(_batch, _white, small, big, new HudSettings { Style = _style }, Content.Load<SpriteFont>("HUDModern"))
            : new HudRenderer(_batch, _white, small, big, GaugeTextures.Load(GraphicsDevice, art),
                statusTextures: StatusTextures.Load(GraphicsDevice, art), indicators: IndicatorTextures.Load(GraphicsDevice, art));
        var assets = new DirectoryAssetSource(art);
        Add(assets, "faohshafig", 115, new Vector3(-220, 65, -1050), Matrix.CreateFromYawPitchRoll(.45f, .3f, .28f), new Color(135, 183, 220));
        Add(assets, "ss24a", 240, new Vector3(225, 30, -1450), Matrix.CreateFromYawPitchRoll(.35f, .25f, .18f), new Color(205, 179, 130));
        Add(assets, "bgrnd03", 105, new Vector3(-10, -155, -1150), Matrix.CreateRotationY(.4f), new Color(157, 147, 132));
        Add(assets, "bgrnd03", 48, new Vector3(490, 195, -1800), Matrix.CreateRotationX(.8f), new Color(157, 147, 132));
        _shaders = new(bytes => new Effect(GraphicsDevice, bytes));
        _post = new(GraphicsDevice, _shaders); _post.SetHdr(_postOn);
        _fsr = new(GraphicsDevice, _shaders);
        float scale = _postOn ? UpscalingSettings.Scale(FsrPreset.Quality) : 1;
        var (w, h) = UpscalingSettings.Dimensions(Width, Height, scale);
        _post.SetSize(w, h);
        if (_postOn) _fsr.SetSize(Width, Height, scale, true);
        _aa = new(GraphicsDevice);
        _legacyScene = new(GraphicsDevice, w, h, false, HdrOutput.SceneFormat(_postOn), DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
        _scene = _aa.Prepare(w, h, HdrOutput.SceneFormat(_postOn), (Antialiasing)_samples) ?? _legacyScene;
        _final = new(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
        if (_postOn && (!_post.HdrActive || !_post.BloomReady || !_fsr.Ready)) throw new InvalidOperationException("Post stack missing");
        var projection = new[] { _projection.M11, _projection.M12, _projection.M13, _projection.M14,
            _projection.M21, _projection.M22, _projection.M23, _projection.M24, _projection.M31, _projection.M32, _projection.M33, _projection.M34,
            _projection.M41, _projection.M42, _projection.M43, _projection.M44 };
        File.WriteAllText(Path.Combine(_output, "scene.json"), JsonSerializer.Serialize(new { width = Width, height = Height, projection,
            objects = _objects.Select(v => v.Select(p => new[] { p.Position.X, p.Position.Y, p.Position.Z, p.Color.R / 255f, p.Color.G / 255f, p.Color.B / 255f }).ToArray()) }));
        Console.WriteLine($"P1057_DEVICE backbuffer={GraphicsDevice.PresentationParameters.MultiSampleCount} scene={_scene.MultiSampleCount} post={_postOn} format={_scene.Format} size={w}x{h} hud={_final.MultiSampleCount}");
        if (_benchmark) _timer = GlFrameTimer.TryCreate();
        _watch.Start();
    }

    private void Add(DirectoryAssetSource assets, string stem, float radius, Vector3 position, Matrix rotation, Color tint)
    {
        var model = NativeStationRenderer.DecodeModel(assets, stem);
        float meshRadius = NativeShipResolver.MaxVertexRadius(model.Meshes, model.LodLevels);
        Matrix world = Matrix.CreateScale(radius / meshRadius) * rotation * Matrix.CreateTranslation(position);
        var vertices = new List<VertexPositionColor>();
        foreach (var mesh in model.Meshes)
            foreach (var index in mesh.Indices)
            {
                int i = index * 3;
                var p = Vector3.Transform(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]), world);
                var n = Vector3.TransformNormal(new Vector3(mesh.Normals[i], mesh.Normals[i + 1], mesh.Normals[i + 2]), rotation);
                float light = .4f + .6f * Math.Max(0, Vector3.Dot(Vector3.Normalize(n), Vector3.Normalize(new Vector3(-1, 1, 1))));
                vertices.Add(new(p, new Color(tint.ToVector3() * light)));
            }
        _objects.Add(vertices.ToArray());
    }

    protected override void Draw(GameTime time)
    {
        if (_done) return;
        try
        {
            var gd = GraphicsDevice;
            if (_check) { Check(); Ok = true; _done = true; Exit(); return; }
            long now = Stopwatch.GetTimestamp();
            // Two seconds of warm-up are excluded from all statistics.
            if (_benchmark && _watch.Elapsed.TotalSeconds > 2 && _previous != 0)
                _frames.Add(Stopwatch.GetElapsedTime(_previous, now).TotalMilliseconds);
            _previous = now;
            _timer?.Poll((_, _, ms) => { if (_watch.Elapsed.TotalSeconds > 2) _gpu.Add(ms); });
            _timer?.Start(++_frame, false);
            gd.SetRenderTarget(_scene);
            _physicalSamples ??= GlSampleCount.Current();
            _aa.LogBoundSamples();
            gd.Clear(new Color(2, 4, 8));
            gd.BlendState = BlendState.Opaque; gd.DepthStencilState = DepthStencilState.Default;
            gd.RasterizerState = RasterizerState.CullNone;
            foreach (var vertices in _objects)
                foreach (var pass in _effect.CurrentTechnique.Passes)
                { pass.Apply(); gd.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length / 3); }
            if (_excursion)
            {
                // Same P582b bind/restore boundary as the volumetric half-res pass.
                using var half = new RenderTarget2D(gd, _scene.Width / 2, _scene.Height / 2, false, SurfaceFormat.Color, DepthFormat.None);
                gd.SetRenderTarget(half); gd.Clear(Color.Transparent); gd.SetRenderTarget(_scene);
            }
            _aa.Resolve(_final, _postOn);
            gd.SetRenderTarget(_final); // Off's existing single-sampled path.
            if (_postOn)
            {
                gd.SetRenderTarget(_fsr.Bloomed);
                _post.Bloom!.Render(_scene);
                _fsr.Render(_fsr.Bloomed!, null, .5f, true);
                _post.Output!.Render(_fsr.HdrUpscaled!, _final, HdrCurve.Neutral);
            }
            else if (_aa.Samples == 0)
            {
                _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                _batch.Draw(_scene, new Rectangle(0, 0, Width, Height), Color.White); _batch.End();
            }
            // Retain a clean scene for the browser geometry comparison.
            if (!_benchmark) Save(_final, "scene-only.png");
            var frame = default(HudFrame) with { HullFraction = 1, ShieldFraction = .8f, AmmoFraction = 1,
                FuelFraction = 1, EnergyFraction = 1, Power = .5f, Speed = 120, Throttle = .5f, GroupFire = true,
                WeaponSystems = Array.Empty<WeaponStatusRow>() };
            _hud.Draw(HudModelAdapter.Create(frame, CameraMode.Cockpit, cameraLabel: "COCKPIT"), gd.Viewport);
            if (!_benchmark) Save(_final, "scene.png");
            _timer?.Stop();
            gd.SetRenderTarget(null);
            _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            _batch.Draw(_final, new Rectangle(0, 0, Width, Height), Color.White); _batch.End();
            if (!_benchmark || _watch.Elapsed.TotalSeconds >= _seconds + 2)
            {
                if (_benchmark)
                {
                    double Percentile(List<double> values, double p) => values.Count == 0 ? double.NaN : values.Order().ElementAt(Math.Min(values.Count - 1, (int)Math.Floor((values.Count - 1) * p)));
                    var row = new { requestedSamples = _samples, selectedSamples = _aa.Samples, physicalSamples = _physicalSamples,
                        width = Width, height = Height, post = _postOn, style = _style.ToString(), seconds = _seconds,
                        frames = _frames.Count, frameP50Ms = Percentile(_frames, .5), frameP95Ms = Percentile(_frames, .95),
                        gpuAvailable = _timer is not null && _gpu.Count > 0,
                        gpuP50Ms = _gpu.Count > 0 ? (double?)Percentile(_gpu, .5) : null,
                        gpuP95Ms = _gpu.Count > 0 ? (double?)Percentile(_gpu, .95) : null,
                        gpuSamples = _gpu.Count, renderer = _timer?.Renderer, driver = _timer?.DriverVersion,
                        softwareRenderer = _timer?.Renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) == true
                            || _timer?.Renderer.Contains("softpipe", StringComparison.OrdinalIgnoreCase) == true
                            || _timer?.Renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase) == true,
                        workload = "fixed native flight geometry + production post + production HUD; 2s warmup, vsync off" };
                    File.AppendAllText(Path.Combine(_output, "measurements.jsonl"), JsonSerializer.Serialize(row) + "\n");
                    Console.WriteLine("P1057_MEASUREMENT " + JsonSerializer.Serialize(row));
                }
                Ok = true; _done = true; Exit();
            }
        }
        catch { _done = true; Exit(); throw; }
    }

    private void Check()
    {
        var gd = GraphicsDevice;
        using var aa = new SceneAntialiasing(gd);
        if (aa.Prepare(64, 64, SurfaceFormat.Color, Antialiasing.Off) is not null) throw new InvalidOperationException("Off allocated a scene");
        foreach (var format in new[] { SurfaceFormat.Color, SurfaceFormat.HalfVector4 })
        foreach (var level in new[] { Antialiasing.Msaa2, Antialiasing.Msaa4, Antialiasing.Msaa8 })
        {
            var target = aa.Prepare(64, 64, format, level);
            if (target is null) { Console.WriteLine($"P1057_CAPABILITY {format}/{level} unsupported"); continue; }
            gd.SetRenderTarget(target); aa.LogBoundSamples(); gd.Clear(Color.CornflowerBlue);
            // Bind/restore a half-res target; every colour sample must survive.
            using var half = new RenderTarget2D(gd, 32, 32);
            gd.SetRenderTarget(half); gd.Clear(Color.Red); gd.SetRenderTarget(target);
            aa.Resolve(_final, false);
            var pixels = new Color[Width * Height]; _final.GetData(pixels);
            if (pixels.Any(p => p != Color.CornflowerBlue)) throw new InvalidOperationException("MSAA scene lost across excursion/resolve");
            // Depth must survive too: a farther triangle submitted after the
            // volumetric excursion must remain behind the original opaque hull.
            using (var geometry = new BasicEffect(gd) { VertexColorEnabled = true,
                World = Matrix.Identity, View = Matrix.Identity, Projection = Matrix.Identity })
            {
                var triangle = new[] { new VertexPositionColor(new Vector3(-.8f, -.8f, .2f), Color.Red),
                    new VertexPositionColor(new Vector3(-.05f, .8f, .2f), Color.Red),
                    new VertexPositionColor(new Vector3(.8f, -.7f, .2f), Color.Red) };
                void DrawTriangle()
                {
                    gd.BlendState = BlendState.Opaque; gd.DepthStencilState = DepthStencilState.Default;
                    gd.RasterizerState = RasterizerState.CullNone;
                    geometry.CurrentTechnique.Passes[0].Apply();
                    gd.DrawUserPrimitives(PrimitiveType.TriangleList, triangle, 0, 1);
                }
                gd.SetRenderTarget(target); gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1, 0);
                DrawTriangle();
                gd.SetRenderTarget(half); gd.Clear(Color.Transparent); gd.SetRenderTarget(target);
                for (int i = 0; i < triangle.Length; i++)
                { triangle[i].Position.Z = .8f; triangle[i].Color = Color.Blue; }
                DrawTriangle();
                aa.Resolve(_final, false); _final.GetData(pixels);
                if (pixels[(Height / 2) * Width + Width / 2] != Color.Red || pixels.Any(p => p.B > 0))
                    throw new InvalidOperationException("Opaque depth lost across MSAA excursion");
                if (!pixels.Any(p => p.R is > 0 and < 255))
                    throw new InvalidOperationException("Resolved geometry has no fractional edge coverage");
            }
            // Post path must resolve and bind the destination without copying.
            gd.SetRenderTarget(_final); gd.Clear(Color.Green); gd.SetRenderTarget(target);
            aa.Resolve(_final, true);
            _final.GetData(pixels);
            if (pixels.Any(p => p != Color.Green)) throw new InvalidOperationException("Post resolve copied prematurely");
            // Draw an exact HUD colour on the single-sample output.
            _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            _batch.Draw(_white, new Rectangle(0, 0, Width, Height), new Color(128, 64, 7)); _batch.End();
            _final.GetData(pixels);
            if (pixels.Any(p => p != new Color(128, 64, 7))) throw new InvalidOperationException("HUD was modified");
            Console.WriteLine($"P1057_RESOLVE_PASS {format}/{level} selected={target.MultiSampleCount} colour+depth preservation/fractional geometry coverage/resolve/post/HUD");
        }
        gd.SetRenderTarget(null);
        aa.Prepare(64, 64, SurfaceFormat.Color, Antialiasing.Off);
        if (aa.Scene is not null) throw new InvalidOperationException("Off did not release target");
        CheckPostPaths();
        Console.WriteLine("P1057_CHECK_PASS");
    }

    private void CheckPostPaths()
    {
        var gd = GraphicsDevice;
        using var aa = new SceneAntialiasing(gd);
        foreach (bool hdr in new[] { false, true })
        foreach (bool fsr in new[] { false, true })
        foreach (bool bloom in new[] { false, true })
        {
            bool hasPost = hdr || fsr || bloom;
            float scale = fsr ? UpscalingSettings.Scale(FsrPreset.Quality) : 1;
            var (w, h) = UpscalingSettings.Dimensions(64, 64, scale);
            _post.SetHdr(hdr); _post.SetSize(w, h);
            if (fsr) _fsr.SetSize(64, 64, scale, hdr);
            Color[]? baseline = null;
            foreach (var level in new[] { Antialiasing.Off, Antialiasing.Msaa4 })
            {
                var multisampled = aa.Prepare(w, h, HdrOutput.SceneFormat(hdr), level);
                var scene = multisampled ?? (fsr ? _fsr.Scene! : _post.SceneRT!);
                gd.Textures[0] = null;
                gd.SetRenderTarget(multisampled is not null || hasPost ? scene : _final);
                gd.Clear(Color.CornflowerBlue);
                aa.Resolve(_final, hasPost);
                Texture2D source = scene;
                if (bloom)
                {
                    gd.SetRenderTarget(fsr ? _fsr.Bloomed : hdr ? _post.HdrCompositeRT : _final);
                    if (!_post.Bloom!.Render(source)) throw new InvalidOperationException("Bloom unavailable");
                    if (fsr) source = _fsr.Bloomed!;
                    else if (hdr) source = _post.HdrCompositeRT!;
                }
                if (fsr)
                {
                    _fsr.Render(source, hdr ? null : _final, .5f, hdr);
                    if (hdr) source = _fsr.HdrUpscaled!;
                }
                if (hdr) _post.Output!.Render(source, _final, HdrCurve.Neutral);
                var pixels = new Color[Width * Height]; _final.GetData(pixels);
                if (pixels.Any(p => p.R < 10 || p.B < 10)) throw new InvalidOperationException("Scene missing on post path");
                if (baseline is null) baseline = pixels;
                else if (!baseline.SequenceEqual(pixels)) throw new InvalidOperationException("Solid scene differs across AA resolve/post selection");
            }
            Console.WriteLine($"P1057_PATH_PASS hdr={hdr} bloom={bloom} fsr={fsr} Off/MSAA solid scene identical");
        }
        gd.SetRenderTarget(null);
    }

    private void Save(RenderTarget2D target, string name)
    {
        var data = new Color[Width * Height]; target.GetData(data);
        using var texture = new Texture2D(GraphicsDevice, Width, Height); texture.SetData(data);
        using var file = File.Create(Path.Combine(_output, name)); texture.SaveAsPng(file, Width, Height);
    }

    protected override void UnloadContent()
    {
        (_hud as IDisposable)?.Dispose(); _aa?.Dispose(); _legacyScene?.Dispose(); _final?.Dispose(); _effect?.Dispose(); _timer?.Dispose();
        _batch?.Dispose(); _white?.Dispose(); _fsr?.Dispose(); _post?.Dispose(); _shaders?.Dispose();
        base.UnloadContent();
    }
}

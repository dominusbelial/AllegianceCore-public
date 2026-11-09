using System.Diagnostics;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

// Local-only production-renderer fixture. Bound the process with timeout.
// --out DIR [--clips] [--cost] [--off-only]
string Option(string key, string fallback) { int i = Array.IndexOf(args, key); return i < 0 ? fallback : args[i + 1]; }
string root = Directory.GetCurrentDirectory();
using var game = new SpaceProof(root, Option("--out", ".scratch/p982/render"), args.Contains("--clips"), args.Contains("--cost"), args.Contains("--off-only"));
game.Run();
return 0;

sealed class SpaceProof : Game
{
    private readonly string _out;
    private readonly bool _clips, _cost, _offOnly;
    private readonly string _root;
    private SpriteBatch _batch = null!;
    private Texture2D _white = null!;
    private SpriteFont _font = null!, _big = null!, _outline = null!;
    private HudRenderer _classic = null!;
    private ModernHudRenderer _modern = null!;
    private AmbientDustRenderer _dustRenderer = null!;
    private NativeShipRenderer _ships = null!;
    private NativeShipRenderer.NativeShipGeometry _hull = null!;
    private Texture2D? _hullTexture;
    private readonly List<AmbientDustView> _dustViews = [];
    private readonly SectorLighting _light = SectorLighting.FromCluster([.2f, .3f, -1], 0xffdddddd);
    private readonly DataClusterIGC _cluster = new(982, [.25f, .12f, -1], 0xffffffff, 0, 0, 0, 250, 500,
        "P982 proof", "globe5", "", 0, 0, 0, true, false);
    private IAssetSource _assets = null!;
    private static readonly string[] Modes = ["off", "subtleDust", "simStars", "simGlare", "starStreaks", "all"];
    private static readonly string[] Scenes = ["stationary", "cruising", "boosting", "turning"];
    private ISkyRenderer[] _skies = [];
    private static readonly string[] Tiers = ["black", "native", "hd"];
    private static readonly string[] Styles = ["classic", "modern"];
    private const int Width = 960, Height = 540;
    private readonly Matrix _projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), Width / (float)Height, 1, 10000);

    public SpaceProof(string root, string output, bool clips, bool cost, bool offOnly)
    {
        _root = root; _out = output; _clips = clips; _cost = cost; _offOnly = offOnly;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
        Content.RootDirectory = Path.Combine(root, "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        Directory.CreateDirectory(_out);
        _batch = new SpriteBatch(GraphicsDevice); _white = new Texture2D(GraphicsDevice, 1, 1); _white.SetData(new[] { Color.White });
        _font = Content.Load<SpriteFont>("HUD"); _big = Content.Load<SpriteFont>("HUDLarge"); _outline = Content.Load<SpriteFont>("HUDModern");
        var art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var sky = ExternalPaths.Require(ExternalResourceKind.SkyPack);
        _assets = new LayeredAssetSource(new DirectoryAssetSource(sky), new DirectoryAssetSource(art));
        _dustRenderer = new AmbientDustRenderer(GraphicsDevice, _assets);
        var gauges = GaugeTextures.Load(GraphicsDevice, art); var status = StatusTextures.Load(GraphicsDevice, art);
        var indicators = IndicatorTextures.Load(GraphicsDevice, art);
        _classic = new HudRenderer(_batch, _white, _font, _big, gauges, statusTextures: status, indicators: indicators);
        _modern = new ModernHudRenderer(_batch, _white, _font, _big, new HudSettings { Style = HudStyle.Modern }, outlineSource: _outline, nativeArt: status);
        _ships = new NativeShipRenderer(GraphicsDevice, _assets);
        _hull = _ships.MeshFor("wc_icfig") ?? _ships.MeshFor("ss24a") ?? throw new InvalidOperationException("proof needs native geometry");
        _hullTexture = _ships.TextureFor(_ships.TextureStemFor("wc_icfig", null) ?? "wc_icfig");
        _skies = [new BlackSkyRenderer(GraphicsDevice, _assets, false, false, false, false),
            new NativeSkyRenderer(GraphicsDevice, _assets), new HdSkyRenderer(GraphicsDevice, _assets)];
        foreach (var renderer in _skies) renderer.SetCluster(_cluster);
        try
        {
            RunGpuContracts();
            if (_cost) RunCost();
            else if (_clips) RunClips();
            else RunStills();
        }
        finally
        {
            foreach (var renderer in _skies) renderer.Dispose();
            _modern.Dispose(); _dustRenderer.Dispose(); _ships.Dispose();
            status.Dispose(); indicators.Dispose(); _batch.Dispose(); _white.Dispose();
        }
        Exit();
    }

    private static VisualOptions Options(string mode)
    {
        var options = new VisualOptions();
        foreach (string key in Modes.Skip(1).Take(4)) options.SetToggle(key, mode == "all" || mode == key);
        return options;
    }

    private void RunGpuContracts()
    {
        using var target = new RenderTarget2D(GraphicsDevice, Width, Height);
        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.DepthStencilState = DepthStencilState.None;
        using var stars = new SpaceStarRenderer(GraphicsDevice);
        VertexStar[] points = [new(-Vector3.UnitZ, Color.White)];
        var options = Options("starStreaks");
        void Require(bool value, string rule) { if (!value) throw new InvalidOperationException("P982 GPU contract: " + rule); }
        stars.Draw(points, Matrix.Identity, _projection, new(options, 1f / 60, true, 1));
        Require(stars.LastTailCount == 0, "cut clears tails");
        var turned = Matrix.CreateRotationY(.01f);
        stars.Draw(points, turned, _projection, new(options, 1f / 60, false, 2));
        Require(stars.LastTailCount == 1, "rotation draws a star tail");
        stars.Draw(points, Matrix.CreateRotationY(-.5f), _projection, new(options, 0, true, AdvanceHistory: false));
        stars.Draw(points, Matrix.CreateRotationY(.02f), _projection, new(options, 1f / 60, false, 3));
        Require(stars.LastTailCount == 1, "postcard preserves flight history");
        stars.Draw(points, Matrix.CreateRotationY(.02f), _projection, new(options, 1f / 60, false, 4));
        Require(stars.LastTailCount == 0, "stationary view has no tails");
        stars.Draw(points, turned, _projection, new(options, 1f / 60, false, 7));
        Require(stars.LastTailCount == 0, "returning to a live mode clears stale history");
        stars.Draw(points, Matrix.Identity, _projection, new(Options("off"), 1f / 60, false, 8));
        Require(stars.LastTailCount == 0, "flag off has no tail batch");
        GraphicsDevice.BlendState = BlendState.Opaque;
        stars.Draw(points, Matrix.Identity, _projection, new(Options("off"), 1f / 60, false, 9), drawPoints: false);
        Require(GraphicsDevice.BlendState == BlendState.Opaque, "disabled tails preserve native blend state");
        GraphicsDevice.SetRenderTarget(null);
        Console.WriteLine("P982_GPU_CONTRACTS_PASS: rotation/stationary/cut/live toggle/postcard");
    }

    private void RunStills()
    {
        using var target = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        for (int tier = 0; tier < 3; tier++)
        foreach (string scene in Scenes)
        foreach (string mode in (_offOnly ? new[] { "off" } : Modes))
        {
            var options = Options(mode); var dust = new SubtleDust(); var native = new DebrisGeo(1, new RandomTrailSource(982));
            for (int frame = 0; frame <= 60; frame++)
            {
                // Advance the real renderers, not a still-frame imitation of tails.
                DrawFrame(target, tier, scene, options, dust, native, frame / 60f, 1 / 60f, frame == 0, 4);
                if (frame != 60) continue;
                foreach (string style in Styles)
                {
                    // Redraw only HUD after preserving the same scene in a texture.
                    using var sceneCopy = new Texture2D(GraphicsDevice, Width, Height);
                    var pixels = new Color[Width * Height]; target.GetData(pixels); sceneCopy.SetData(pixels);
                    GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
                    _batch.Begin(); _batch.Draw(sceneCopy, Vector2.Zero, Color.White); _batch.End();
                    DrawHud(style, scene, fresh: true);
                    Label($"P982 {Tiers[tier]} / {style} / {scene} / {mode}");
                    GraphicsDevice.SetRenderTarget(null);
                    using var file = File.Create(Path.Combine(_out, $"{Tiers[tier]}_{style}_{scene}_{mode}.png"));
                    target.SaveAsPng(file, Width, Height);
                    // Restore scene for the second HUD style.
                    GraphicsDevice.SetRenderTarget(target);
                    _batch.Begin(blendState: BlendState.Opaque); _batch.Draw(sceneCopy, Vector2.Zero, Color.White); _batch.End();
                }
            }
        }
        Console.WriteLine("P982_STILLS_PASS");
    }

    private void RunClips()
    {
        // 4 seconds, 20 fps; each frame is a 3-sky x 2-HUD contact sheet.
        using var target = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        using var sheet = new RenderTarget2D(GraphicsDevice, 960, 360, false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        foreach (string scene in new[] { "cruising", "turning" })
        foreach (string mode in new[] { "off", "all" })
        {
            var options = Options(mode);
            var dust = Enumerable.Range(0, 3).Select(_ => new SubtleDust()).ToArray();
            var native = Enumerable.Range(0, 3).Select(_ => new DebrisGeo(1, new RandomTrailSource(982))).ToArray();
            string directory = Path.Combine(_out, scene + "_" + mode); Directory.CreateDirectory(directory);
            for (int frame = 0; frame < 80; frame++)
            {
                GraphicsDevice.SetRenderTarget(sheet); GraphicsDevice.Clear(Color.Black);
                for (int tier = 0; tier < 3; tier++)
                {
                    DrawFrame(target, tier, scene, options, dust[tier], native[tier], frame / 20f, .05f, frame == 0, 4);
                    var pixels = new Color[Width * Height]; target.GetData(pixels);
                    using var sceneCopy = new Texture2D(GraphicsDevice, Width, Height); sceneCopy.SetData(pixels);
                    for (int style = 0; style < 2; style++)
                    {
                        GraphicsDevice.SetRenderTarget(target);
                        _batch.Begin(blendState: BlendState.Opaque); _batch.Draw(sceneCopy, Vector2.Zero, Color.White); _batch.End();
                        DrawHud(Styles[style], scene); Label($"{Tiers[tier]} / {Styles[style]} / {scene} / {mode}");
                        GraphicsDevice.SetRenderTarget(sheet);
                        _batch.Begin(); _batch.Draw(target, new Rectangle(tier * 320, style * 180, 320, 180), Color.White); _batch.End();
                    }
                }
                GraphicsDevice.SetRenderTarget(null);
                if (frame == 0)
                {
                    var pixels = new Color[960 * 360]; sheet.GetData(pixels);
                    for (int cellY = 0; cellY < 2; cellY++)
                        for (int cellX = 0; cellX < 3; cellX++)
                        {
                            int lit = 0;
                            for (int y = cellY * 180; y < (cellY + 1) * 180; y++)
                                for (int x = cellX * 320; x < (cellX + 1) * 320; x++)
                                    if (pixels[y * 960 + x].R + pixels[y * 960 + x].G + pixels[y * 960 + x].B > 12) lit++;
                            if (lit < 50) throw new InvalidOperationException($"P982 clip lost sky/HUD panel {cellX},{cellY}");
                        }
                    Console.WriteLine($"P982_CLIP_PANELS_PASS: {scene}/{mode} all six panels");
                }
                using var file = File.Create(Path.Combine(directory, $"{frame:000}.png")); sheet.SaveAsPng(file, 960, 360);
            }
        }
        Console.WriteLine("P982_CLIPS_PASS");
    }

    private void DrawFrame(RenderTarget2D target, int tier, string scene, VisualOptions options, SubtleDust dust, DebrisGeo native,
        float seconds, float dt, bool cut, int ships)
    {
        float speed = scene == "stationary" ? 0 : scene == "boosting" ? 500 : 180;
        var eye = new Vector3(0, 0, -speed * seconds);
        var view = scene == "turning" ? Matrix.CreateRotationY(seconds * .3f) : Matrix.Identity;
        var frame = new SpaceVisualFrame(options, dt, cut);
        GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1, 0);
        GraphicsDevice.BlendState = BlendState.Opaque; GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        _skies[tier].Draw(view, _projection, eye, frame);
        _dustViews.Clear();
        if (options.IsOn(VisualFlags.SubtleDustKey))
        {
            dust.Step(eye, 1, cut);
            foreach (var grain in dust.Grains)
            {
                float bright = grain.Brightness * SubtleDust.DistanceFade(Vector3.Distance(grain.Position, eye));
                _dustViews.Add(new AmbientDustView(new SimVec(grain.Position.X, grain.Position.Y, grain.Position.Z), grain.HalfExtent / _projection.M22, new Vector3(bright)));
            }
        }
        else
        {
            native.Step(new SimVec(eye.X, eye.Y, eye.Z), new SimVec(-view.M31, -view.M32, -view.M33),
                new SimVec(view.M11, view.M12, view.M13), new SimVec(view.M21, view.M22, view.M23), _projection.M22, seconds);
            foreach (var grain in native.Points)
            {
                float bright = DebrisGeo.Brightness(seconds - grain.StartTime);
                var position = new Vector3(grain.Position.X, grain.Position.Y, grain.Position.Z);
                if (bright <= 0 || Vector3.DistanceSquared(position, eye) <= DebrisGeo.NearCameraSkipSquared) continue;
                _dustViews.Add(new AmbientDustView(grain.Position, AmbientDustRenderer.DecalHalfExtent(_projection.M22),
                    new Vector3(grain.Colour.X, grain.Colour.Y, grain.Colour.Z) * bright));
            }
        }
        _dustRenderer.Draw(_dustViews, eye, view, _projection);
        for (int i = 0; i < ships; i++)
        {
            int cols = ships > 4 ? 20 : 4;
            var position = new Vector3((i % cols - (cols - 1) * .5f) * 50, ships > 4 ? (i / cols - 9.5f) * 28 : -30, -700 - i % 3 * 70);
            _ships.Draw(_hull, _hullTexture, Matrix.CreateTranslation(position), view, _projection, 1, .8f, _light);
        }
    }

    private void DrawHud(string style, string scene, bool fresh = false)
    {
        var frame = new HudFrame(.82f, .7f, .8f, .9f, 1, 1, scene == "stationary" ? 0 : scene == "boosting" ? 500 : 180,
            .6f, scene == "boosting", false, true, 0, false, 0, false, false, 15, 0, [], false, 0, 2500);
        HudRenderer renderer = style == "modern" ? _modern : _classic;
        if (fresh && style == "modern")
        {
            // Fixture-owned animation reset avoids regenerating SDF atlases for
            // every still. It leaves production draw/model code untouched.
            const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            ((Dictionary<GaugeKind, float>)typeof(ModernHudRenderer).GetField("_animated", hidden)!.GetValue(_modern)!).Clear();
            typeof(ModernHudRenderer).GetField("_previous", hidden)!.SetValue(_modern, 0L);
            _modern.Draw(HudModel.Create(frame, new HudContext { Camera = CameraMode.Cockpit }), GraphicsDevice.Viewport);
        }
        else renderer.Draw(HudModel.Create(frame, new HudContext { Camera = CameraMode.Cockpit }), GraphicsDevice.Viewport);
    }
    private void Label(string text)
    {
        _batch.Begin(); _batch.Draw(_white, new Rectangle(0, 0, Width, 24), Color.Black * .85f);
        _batch.DrawString(_font, text, new Vector2(8, 4), Color.White); _batch.End();
    }

    private void RunCost()
    {
        using var target = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        using var timer = GlFrameTimer.TryCreate();
        Console.WriteLine("renderer=" + (timer?.Renderer ?? "GPU timing unavailable"));
        using var csv = new StreamWriter(Path.Combine(_out, "cost.csv"));
        csv.WriteLine("tier,mode,ships,frames,cpuMeanMs,gpuMeanMs,drawCalls");
        for (int tier = 0; tier < 3; tier++)
        foreach (string mode in Modes)
        {
            var options = Options(mode); var dust = new SubtleDust(); var native = new DebrisGeo(1, new RandomTrailSource(982));
            var cpu = new List<double>(); var gpu = new List<double>(); var calls = new List<long>();
            for (int frame = 0; frame < 100; frame++)
            {
                timer?.Poll((_, _, ms) => gpu.Add(ms));
                var before = GraphicsDevice.Metrics.DrawCount;
                var started = Stopwatch.GetTimestamp();
                if (frame >= 20) timer?.Start(frame + 1, false);
                DrawFrame(target, tier, "turning", options, dust, native, frame / 60f, 1 / 60f, frame == 0, 400);
                if (frame >= 20) timer?.Stop();
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (frame >= 20) { cpu.Add(elapsed); calls.Add(GraphicsDevice.Metrics.DrawCount - before); }
                // Complete this frame after the measured submit interval. Timer results
                // are read only when available; this is proof synchronization, not game code.
                var pixel = new Color[1]; target.GetData(0, new Rectangle(0, 0, 1, 1), pixel, 0, 1);
            }
            timer?.Poll((_, _, ms) => gpu.Add(ms));
            string line = FormattableString.Invariant($"{Tiers[tier]},{mode},400,{cpu.Count},{cpu.Average():F4},{(gpu.Count > 0 ? gpu.Average() : double.NaN):F4},{calls.Average():F1}");
            csv.WriteLine(line); Console.WriteLine(line);
        }
        Console.WriteLine("P982_COST_PASS");
    }
}

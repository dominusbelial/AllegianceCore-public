using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaCol = Microsoft.Xna.Framework.Color;
using XnaMat = Microsoft.Xna.Framework.Matrix;
using XnaVec = Microsoft.Xna.Framework.Vector3;

// P979 proof harness — two headless evidence runs for visuals.radialVisibility.
//
//   SDL_VIDEODRIVER=offscreen dotnet run --project tools/p979-visual-proof -- \
//       scene          # aleph/station frames through the real camera rule
//   SDL_VIDEODRIVER=offscreen dotnet run --project tools/p979-visual-proof -- \
//       ui --style classic|modern [--visuals radialVisibility=on,viewRange=12000]
//
// `scene` renders the SAME aleph with the REAL native WarpRenderer under the
// camera matrices the rule produces (planar 6000 = the reported bug, planar
// 10000 = native on master, radial ON = P979), at screen centre and 35° off
// axis, plus a station at 9.5 km for the Depth24 precision check. Each frame
// prints the rule's verdict and the lit-pixel count, so the PNG and the numbers
// corroborate each other.
//
// `ui` runs the PRODUCTION Game1 intro screen and photographs the Options →
// Visuals page in both HUD styles (the real backbuffer).

string repo = RepoRoot();
string art = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") is { Length: > 0 } envArt
    ? envArt : ExternalPaths.Resolve(ExternalResourceKind.Artwork);
string outDir = Arg("--out") ?? Path.Combine(repo, ".scratch", "p979", "shots");
Directory.CreateDirectory(outDir);

if (Arg("--mode") == "ui" || args.Contains("ui"))
{
    RunUi();
    return 0;
}
RunScene();
return 0;

string? Arg(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

void RunScene()
{
    Console.WriteLine($"[p979-proof] scene art={art} out={outDir}");
    using var game = new SceneRunner(new SceneOptions(art, outDir));
    game.Run();
}

void RunUi()
{
    var style = (Arg("--style") ?? "classic").Equals("modern", StringComparison.OrdinalIgnoreCase)
        ? HudStyle.Modern : HudStyle.Classic;
    int width = int.Parse(Arg("--width") ?? "960", System.Globalization.CultureInfo.InvariantCulture);
    int height = int.Parse(Arg("--height") ?? "540", System.Globalization.CultureInfo.InvariantCulture);
    string content = Path.GetFullPath(Path.Combine(repo, "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content"));
    string link = Path.Combine(AppContext.BaseDirectory, "Content");
    if (!Directory.Exists(link) && Directory.Exists(content)) Directory.CreateSymbolicLink(link, content);

    string? visualsSpec = Arg("--visuals");
    using var game = new Game1(new Game1.LaunchOptions
    {
        Intro = true,
        PlayerName = "P979Pilot",
        WindowWidth = width,
        WindowHeight = height,
        VisualsOverride = visualsSpec,
    });
    game.Content.RootDirectory = content;
    string tag = (style == HudStyle.Modern ? "modern" : "classic")
        + (visualsSpec is { Length: > 0 } ? "_on" : "_off");
    game.Components.Add(new VisualsPageProof(game, style, outDir, tag));
    game.Run();
}

sealed record SceneOptions(string ArtRoot, string OutDir);

/// <summary>One frame under test: an object at <paramref name="Distance"/> along the
/// camera forward, rotated <paramref name="OffAxisDeg"/> about the camera up
/// (0 = dead ahead), drawn with a camera whose far plane is <paramref name="Far"/>
/// and either the planar (native) or the radial rule deciding.</summary>
sealed record SceneCase(string Name, float Distance, float OffAxisDeg, float Far, bool Radial);

sealed class SceneRunner : Game
{
    private const int Width = 960;
    private const int Height = 540;
    private const float Radius = 100f;          // m2xspiderw aleph radius (native 100)
    private static readonly XnaCol Background = new(2, 4, 8);
    private readonly GraphicsDeviceManager _gdm;
    private readonly SceneOptions _options;

    public SceneRunner(SceneOptions options)
    {
        _options = options;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
        };
    }

    protected override void Initialize()
    {
        base.Initialize();
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[p979-proof] FATAL: {ex}"); }
        Exit();
    }

    private void RunProof(GraphicsDevice gd)
    {
        var assets = new DirectoryAssetSource(_options.ArtRoot);
        using var warp = new WarpRenderer(gd, assets);
        using var stations = new NativeStationRenderer(gd, assets);
        var light = SectorLighting.FromCluster(new[] { 0.707f, 0f, 0.707f }, 0x00FFAAAA);

        // The production cockpit camera: near 1, native's 10000 far (WinTrek.cpp:323).
        var camera = new CockpitCamera(nearPlane: 1f, farPlane: ExternalCameras.FarPlane);
        var (view, proj) = camera.Matrices(XnaVec.Zero, XnaMat.Identity, (float)Width / Height, out var eye);

        var cases = new[]
        {
            // The reported bug: the OLD 6000 m plane hid an 8 km aleph dead ahead.
            new SceneCase("aleph_8km_planar6000_hidden", 8000f, 0f, 6000f, false),
            // Master today: native's 10000 m plane — visible dead ahead.
            new SceneCase("aleph_8km_native10000_visible", 8000f, 0f, 10000f, false),
            // P979: radial rule at the same 8 km.
            new SceneCase("aleph_8km_radial_on_visible", 8000f, 0f, RadialVisibility.RadialFarPlane(10000f), true),
            // The pop-in itself at 7 km: hidden dead ahead by a 6000 m plane,
            // visible at 35° because the plane measures view depth, not distance.
            new SceneCase("aleph_7km_planar6000_center_hidden", 7000f, 0f, 6000f, false),
            new SceneCase("aleph_7km_planar6000_edge_visible", 7000f, 35f, 6000f, false),
            // Radial ON: the same object is drawn at BOTH angles (and hidden past the range).
            new SceneCase("aleph_7km_radial_on_center_visible", 7000f, 0f, RadialVisibility.RadialFarPlane(10000f), true),
            new SceneCase("aleph_7km_radial_on_edge_visible", 7000f, 35f, RadialVisibility.RadialFarPlane(10000f), true),
            // Past the range off axis: native's plane still draws it (8600 m view
            // depth), radial hides it — the sphere rule.
            new SceneCase("aleph_10_5km_native10000_edge_visible", 10500f, 35f, 10000f, false),
            new SceneCase("aleph_10_5km_radial_on_edge_hidden", 10500f, 35f, RadialVisibility.RadialFarPlane(10000f), true),
        };

        foreach (var test in cases)
        {
            float offAxis = MathHelper.ToRadians(test.OffAxisDeg);
            float viewDepth = test.Distance * MathF.Cos(offAxis);
            bool drawn = RadialVisibility.IsDrawn(test.Radial, 10000f, test.Distance, Radius, viewDepth, 1f, test.Far);
            var projection = RadialVisibility.WithFarPlane(proj, test.Far);
            var position = XnaVec.Transform(-XnaVec.UnitZ * test.Distance, XnaMat.CreateRotationY(offAxis));
            var world = XnaMat.CreateScale(Radius * WarpRenderer.VisualScaleMultiplier) * XnaMat.CreateTranslation(position);
            int lit = RenderFrame(gd, view, projection, rt =>
            {
                // The rule's decision is what the client's per-object gate does:
                // a hidden object is never submitted, so the frame is empty.
                if (!drawn) return;
                warp.Draw(world, view, projection, 0.5, Radius * WarpRenderer.VisualScaleMultiplier,
                    "aleph", 1, 1f, 0f, advanceClock: false);
            },
                Path.Combine(_options.OutDir, test.Name + ".png"));
            Console.WriteLine($"[p979-proof] {test.Name}: distance={test.Distance:0} offAxis={test.OffAxisDeg:0}° "
                + $"viewDepth={viewDepth:0} far={test.Far:0} radial={(test.Radial ? "on" : "off")} "
                + $"rule={(drawn ? "DRAWN" : "HIDDEN")} litPixels={lit}");
        }

        // Depth precision at 9.5 km with the cockpit's near=1 / Depth24: a station
        // whose rings are far smaller than the ~5 m quantization would z-fight.
        // The near=5 control is native's own near plane (WinTrek.cpp:323): if the
        // speckle is depth quantization it changes; if it is the hull texture it
        // does not. A 1.5 km reference shows the same mesh up close.
        foreach (var (stem, name) in new[] { ("ss24a", "station"), ("bgrnd03", "asteroid") })
        {
            if (stations.MeshFor(stem) is not { } geo)
            {
                Console.WriteLine($"[p979-proof] {name} {stem}: geometry missing — skipped");
                continue;
            }
            var texture = stations.TextureFor(stations.TextureStemFor(stem, null) ?? stem);
            float radius = 500f;
            float nativeScale = NativeStationResolver.ScaleForRadius(radius, geo.MeshRadius);
            foreach (var (tag, distance, near) in new[] { ("9_5km_near1", 9500f, 1f), ("9_5km_near5", 9500f, 5f), ("1_5km_reference", 1500f, 1f) })
            {
                var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50f),
                    (float)Width / Height, near, ExternalCameras.FarPlane);
                var world = XnaMat.CreateTranslation(-XnaVec.UnitZ * distance);
                int lit = RenderFrame(gd, view, projection, rt =>
                    stations.Draw(geo, texture, world, view, projection, nativeScale, light, stem),
                    Path.Combine(_options.OutDir, $"{name}_{tag}_depth24.png"));
                Console.WriteLine($"[p979-proof] {name}_{tag}_depth24: distance={distance:0} near={near:0} far=10000 "
                    + $"quantization={RadialVisibility.DepthQuantization(distance, near, ExternalCameras.FarPlane):0.00} m litPixels={lit}");
            }
        }

        // CPU cost of the gate at a 200-ship scale: one squared-distance compare per
        // object per frame (the rule itself; no GPU work changes).
        var positions = new XnaVec[200];
        for (int i = 0; i < positions.Length; i++) positions[i] = new XnaVec(i * 37f, (i % 11) * 19f, -i * 41f);
        var stopwatch = Stopwatch.StartNew();
        const int frames = 600;
        int drawnCount = 0;
        for (int f = 0; f < frames; f++)
            foreach (var p in positions)
                if (RadialVisibility.RadialDrawn(XnaVec.Distance(p, eye), 0f, 10000f)) drawnCount++;
        stopwatch.Stop();
        double usPerFrame = stopwatch.Elapsed.TotalMilliseconds * 1000.0 / frames;
        Console.WriteLine($"[p979-proof] gate cost: 200 objects x {frames} frames = {stopwatch.Elapsed.TotalMilliseconds:0.000} ms total, "
            + $"{usPerFrame:0.000} us/frame at 60 Hz (drawn {drawnCount / frames}/200); see the report for the UM690 expectation");
    }

    private static int RenderFrame(GraphicsDevice gd, XnaMat view, XnaMat proj, Action<RenderTarget2D> draw, string path)
    {
        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Background, 1f, 0);
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        draw(rt);
        var pixels = new byte[Width * Height * 4];
        rt.GetData(pixels);
        gd.SetRenderTarget(null);
        int lit = 0;
        for (int i = 0; i < Width * Height; i++)
            if (pixels[i * 4] + pixels[i * 4 + 1] + pixels[i * 4 + 2] > 18) lit++;
        var colors = new XnaCol[Width * Height];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = new XnaCol((int)pixels[i * 4], (int)pixels[i * 4 + 1], (int)pixels[i * 4 + 2], (int)255);
        using (var tex = new Texture2D(gd, Width, Height, false, SurfaceFormat.Color))
        {
            tex.SetData(colors);
            using var fs = File.Create(path);
            tex.SaveAsPng(fs, Width, Height);
        }
        return lit;
    }
}

/// <summary>Photographs the Options → Visuals page of the real client after the
/// intro has settled (the P970 production-screen pattern: reflection only to
/// choose the screen, the page and its labels are the real draw).</summary>
sealed class VisualsPageProof(Game1 game, HudStyle style, string output, string tag) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step;
    private double _next;

    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);

    public override void Update(GameTime time)
    {
        game.GraphicsDevice.Textures[0] = null;
        if (Get<bool>("_autoShotScheduled")) return;
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P979 proof timeout");
        if (_watch.Elapsed.TotalSeconds < _next) return;

        switch (_step)
        {
            case 0:
                if (Get<object>("_state").ToString() != "Intro") return;
                typeof(Game1).GetMethod("StopIntroScan", Hidden)!.Invoke(game, null);
                Get<HudSettings>("_hudSettings").Style = style;
                // Open the production Options → Visuals page exactly as the menu
                // does: root row 3 activates the submenu.
                var menu = Get<OptionsMenuModel>("_optionsMenu");
                var state = (OptionsMenuModel.OptionsState)typeof(Game1)
                    .GetMethod("CurrentOptionsState", Hidden)!.Invoke(game, null)!;
                menu.Reset();
                menu.Row0 = menu.Rows(state).ToList()
                    .FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenVisualsMenu);
                menu.Activate(state);
                Set("_introView", Enum.Parse(typeof(Game1).GetField("_introView", Hidden)!.FieldType, "Settings"));
                Console.WriteLine($"[p979-proof] ui style={style} page={menu.Current} viewport="
                    + $"{game.GraphicsDevice.Viewport.Width}x{game.GraphicsDevice.Viewport.Height}");
                break;
            case 1:
                Set("_autoshotDir", output);
                Set("_autoShotName", $"p979_visuals_{tag}");
                Set("_autoShotScheduled", true);
                break;
            case 2:
                game.Exit();
                return;
        }
        _step++;
        _next = _watch.Elapsed.TotalSeconds + 1.2;
    }
}

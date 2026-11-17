// P1042 — screen-chrome proof: the intro menu, the docked hangar and the loadout/loadout
// V2 screen rendered through the PRODUCTION screen renderers (Classic, Modern, Jet green,
// Jet amber) at 800×480, 1920×1080 and 3840×2160.
//
// Modes
//   --out <dir>      render every scene × skin × resolution to PNG + manifest.json
//   --audit <dir>    Jet screens only: record every primitive and count the layout audit
//                    classes, including the new hit-vs-draw class (JSON, non-zero exit on a
//                    failure)
//   --alloc          measure bytes allocated per Jet screen frame (must be 0)
//   --compare <dir>  re-render --out's set and compare DECODED pixels (0 diffs required)
//
// Usage (offscreen SDL; the artwork corpus must be on local.env or ALLEGIANCE_ARTWORK_PATH):
//   SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 dotnet p1042-screen-proof.dll --out <dir>
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Screens;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using HudProof;
using P1042Proof;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

var options = Options.Parse(args);
using var proof = new ScreenProof(options);
try { proof.Run(); }
catch (Exception exception)
{
    Console.Error.WriteLine("P1042_FAIL " + exception);
    Environment.Exit(2);
}
return Environment.ExitCode;

sealed record Options(string? Out, string? Compare, string? Audit, bool Alloc, bool Quiet)
{
    public static Options Parse(string[] arguments)
    {
        string? output = null, compare = null, audit = null;
        bool alloc = false, quiet = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            string Next(string flag) => i + 1 < arguments.Length ? arguments[++i]
                : throw new ArgumentException($"{flag} needs a value");
            switch (arguments[i])
            {
                case "--out": output = Next("--out"); break;
                case "--compare": compare = Next("--compare"); break;
                case "--audit": audit = Next("--audit"); break;
                case "--alloc": alloc = true; break;
                case "--quiet": quiet = true; break;
                case "--help" or "-h":
                    Console.WriteLine("p1042-screen-proof [--out <dir>] [--compare <dir>] [--audit <dir>] [--alloc] [--quiet]");
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException("unknown argument " + arguments[i]);
            }
        }
        return new(output, compare, audit, alloc, quiet);
    }
}

/// <summary>One screen state the proof photographs.</summary>
sealed record Scene(string Name, string Family, int State)
{
    public static readonly Scene[] All =
    [
        new("intro-idle", "intro", 0),      // selection on Single Player, pilot field focused
        new("intro-select", "intro", 4),    // selection on Options, field released
        new("intro-last", "intro", 12),     // selection on Host internet game
        new("hangar-idle", "hangar", 0),    // no hover, door closed
        new("hangar-launch", "hangar", 1),  // launch hover, door fully open
        new("hangar-loadout", "hangar", 2), // loadout hover (inside face)
        new("hangar-turret", "hangar", 3),  // turret-gunner badge
        new("loadout", "loadout", 0),       // V2 shop, Buy hovered
    ];
}

/// <summary>One skin column. Jet is instantiated per palette variant (green/amber).</summary>
sealed record SkinColumn(string Name, string SkinId, string? Palette);

sealed class ScreenProof : Game
{
    private readonly Options _options;
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _batch = null!;
    private Texture2D _white = null!;
    private SpriteFont _small = null!, _big = null!;
    private IAssetSource _assets = null!;

    private IntroScreenPainter _painter = null!;
    private HangarScreenRenderer? _hangar;
    private DockedScreenRenderer? _docked;
    private LoadoutV2Renderer? _loadoutV2;
    private ClassicScreenSkinRenderer? _classicScreen, _modernScreen;
    private JetScreenSkinRenderer? _jetGreen, _jetAmber;
    private JetVectorPainter? _painterProbe;
    private ShopScene? _shop;

    private static readonly (int W, int H)[] Resolutions = [(800, 480), (1920, 1080), (3840, 2160)];

    public ScreenProof(Options options)
    {
        _options = options;
        _graphics = new(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 720 };
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData([Color.White]);
        _small = Content.Load<SpriteFont>("HUD");
        _big = Content.Load<SpriteFont>("HUDLarge");
        _assets = AssetSources.Artwork;
        _painter = new IntroScreenPainter(_batch, _white, _small, _big);
        _hangar = new HangarScreenRenderer(_batch, _white, _small, nativeFonts: null,
            GraphicsDevice, _assets);
        _docked = new DockedScreenRenderer(_batch, _white, _small, _big);
        _loadoutV2 = new LoadoutV2Renderer(_batch, _white, _small, _big);
        _classicScreen = new ClassicScreenSkinRenderer(HudSkinIds.Classic, _painter, _hangar, _docked, _loadoutV2);
        _modernScreen = new ClassicScreenSkinRenderer(HudSkinIds.Modern, _painter, _hangar, _docked, _loadoutV2);
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        _jetGreen = new JetScreenSkinRenderer(new JetSpriteBatchSurface(_batch, _white, _small, _big),
            _hangar, _docked, _loadoutV2, settings, null, JetPaletteIds.Green);
        _jetAmber = new JetScreenSkinRenderer(new JetSpriteBatchSurface(_batch, _white, _small, _big),
            _hangar, _docked, _loadoutV2, settings, null, JetPaletteIds.Amber);
        _painterProbe = new JetVectorPainter(new JetSpriteBatchSurface(_batch, _white, _small, _big),
            Color.White, Color.Gray, Color.White, Color.Black, Color.Black);
        _shop = ShopScene.TryBuild();

        Console.WriteLine($"P1042 screen proof: artwork={(Directory.Exists(ExternalPaths.Resolve(ExternalResourceKind.Artwork)) ? "present" : "absent")} " +
            $"hangarArt={_hangar.CanDrawNative} loadout={(_shop is not null ? "present" : "absent")}");

        try
        {
            if (_options.Audit is { } auditDir) Environment.ExitCode = RunAudit(auditDir);
            else if (_options.Alloc) Environment.ExitCode = RunAllocation();
            else if (_options.Compare is { } compareDir) Environment.ExitCode = RunCapture(compareDir, compare: true);
            else if (_options.Out is { } outDir) Environment.ExitCode = RunCapture(outDir, compare: false);
            else Environment.ExitCode = RunCapture(Path.Combine(".scratch", "p1042", "screens"), compare: false);
        }
        finally { Exit(); }
    }

    // ── frames ───────────────────────────────────────────────────────────────
    private IntroMainFrame IntroFrame(int w, int h, int selected, bool pilotFocused)
    {
        var labels = IntroScreenModel.MainLabels;
        var layout = IntroLayout.Compute(w, h, labels.Length);
        const string identity = "Not signed in - choose a pilot name";
        string hint = IntroScreenModel.MainHint(true);
        float identityScale = MathF.Min(1f, (w - 40f) / MathF.Max(1f, _small.MeasureString(identity).X));
        float hintScale = MathF.Min(1f, (w - 40f) / MathF.Max(1f, _small.MeasureString(hint).X));
        return new IntroMainFrame(new Viewport(0, 0, w, h),
            "ALLEGIANCE", "Choose how to play  -  Single Player or Multiplayer",
            labels, layout, selected,
            "Pilot:  (unverified)", "", pilotFocused, IntroLayout.PilotField(w, h), 0f,
            identity, identityScale, hint, hintScale,
            new ScreenInks(new Color(0.38f, 0.90f, 0.84f), new Color(0.88f, 0.96f, 0.95f),
                new Color(0.48f, 0.64f, 0.62f), Color.Orange));
    }

    private static HangarScreenFrame HangarFrame(int w, int h, int state)
    {
        var launch = new HangarLaunchAnimation();
        launch.Update(state == 1, 0f);              // entering the launch ramp at t=0
        launch.Update(state == 1, HangarLaunchAnimation.Duration);  // fully open
        return new HangarScreenFrame(new Viewport(0, 0, w, h),
            state switch { 1 => HangarAction.Launch, 2 => HangarAction.Loadout, _ => HangarAction.None },
            launch, state == 3 ? 2 : 0, Developments: true,
            new ScreenInks(HangarScreen.HoverTextColor, Color.White, HangarScreen.HoverTextColor, Color.Orange));
    }

    private static LoadoutScreenFrame LoadoutFrame(int w, int h, ShopScene shop, HudStyle style)
    {
        var viewport = new Viewport(0, 0, w, h);
        var layout = shop.Presentation.Layout(w, h, ModernHudScale.Core(1f, 1f, h));
        LoadoutV2Layout.Element? hover = null;
        foreach (var element in layout.Elements)
            if (element.Kind == LoadoutV2Layout.Kind.Buy) { hover = element; break; }
        return new LoadoutScreenFrame(viewport, [], null, 10000, 0f, 0f, false, null, false,
            shop.Presentation, layout, style, hover, null,
            new ScreenInks(new Color(1.00f, 0.85f, 0.45f), new Color(0.95f, 0.97f, 0.98f),
                new Color(0.45f, 0.48f, 0.52f), new Color(1.00f, 0.45f, 0.42f)));
    }

    private IScreenSkinRenderer? Column(SkinColumn column) => column.Name switch
    {
        "classic" => _classicScreen,
        "modern" => _modernScreen,
        "jet-green" => _jetGreen,
        "jet-amber" => _jetAmber,
        _ => null,
    };

    private void DrawScene(IScreenSkinRenderer screen, Scene scene, int w, int h, HudStyle style)
    {
        switch (scene.Family)
        {
            case "intro":
            {
                var frame = IntroFrame(w, h, scene.State, pilotFocused: scene.State == 0);
                _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
                try { screen.DrawIntro(frame); }
                finally { _batch.End(); }
                break;
            }
            case "hangar":
                if (_hangar!.CanDrawNative) screen.DrawHangar(HangarFrame(w, h, scene.State));
                break;
            case "loadout":
                if (_shop is not null) screen.DrawLoadout(LoadoutFrame(w, h, _shop, style));
                break;
        }
    }

    // ── capture ──────────────────────────────────────────────────────────────
    private int RunCapture(string directory, bool compare)
    {
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var manifest = new List<object>();
        int failures = 0, total = 0;
        var columns = new[] { new SkinColumn("classic", HudSkinIds.Classic, null),
            new SkinColumn("modern", HudSkinIds.Modern, null),
            new SkinColumn("jet-green", HudSkinIds.Jet, JetPaletteIds.Green),
            new SkinColumn("jet-amber", HudSkinIds.Jet, JetPaletteIds.Amber) };
        foreach (var scene in Scene.All)
        foreach (var (w, h) in Resolutions)
        foreach (var column in columns)
        {
            if (scene.Family == "hangar" && _hangar is null) continue;
            if (scene.Family == "loadout" && _shop is null) continue;
            var screen = Column(column);
            if (screen is null) continue;
            var style = column.Name == "classic" ? HudStyle.Classic : HudStyle.Modern;
            var pixels = Render(w, h, () => DrawScene(screen, scene, w, h, style));
            string name = $"{scene.Name}__{column.Name}__{w}x{h}";
            string path = Path.Combine(root, name + ".png");
            if (compare)
            {
                total++;
                Color[] baseline;
                try { baseline = PngCodec.Decode(path, w, h); }
                catch (Exception e) { Console.WriteLine($"MISSING {name}: {e.Message}"); failures++; continue; }
                int diff = 0;
                for (int i = 0; i < pixels.Length; i++)
                    if (pixels[i] != baseline[i]) diff++;
                if (diff != 0) { failures++; Console.WriteLine($"DIFF {name}: {diff} pixels"); }
                manifest.Add(new { name, width = w, height = h, differingPixels = diff });
            }
            else
            {
                PngCodec.Encode(path, pixels, w, h);
                manifest.Add(new { name, width = w, height = h });
            }
            if (!_options.Quiet) Console.WriteLine($"{(compare ? "compared" : "wrote")} {name}");
        }
        File.WriteAllText(Path.Combine(root, compare ? "compare.json" : "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(compare
            ? $"P1042_SCREEN_COMPARE combinations={total} differing={failures}"
            : $"P1042_SCREEN_CAPTURE combinations={manifest.Count}");
        return failures == 0 ? 0 : 1;
    }

    private Color[] Render(int w, int h, Action draw)
    {
        using var target = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.Clear(Color.Black);
        draw();
        var pixels = new Color[w * h];
        target.GetData(pixels);
        GraphicsDevice.SetRenderTarget(null);
        return pixels;
    }

    // ── audit ────────────────────────────────────────────────────────────────
    private int RunAudit(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new List<object>();
        int failures = 0;
        foreach (var scene in Scene.All.Where(s => s.Family != "loadout"))
        foreach (var (w, h) in Resolutions)
        foreach (var palette in new[] { JetPaletteIds.Green, JetPaletteIds.Amber })
        {
            var recorder = new ScreenRecorder(_small, _big);
            using var chrome = new JetScreenSkinRenderer(recorder, _hangar, _docked, _loadoutV2,
                new HudSettings { Skin = HudSkinIds.Jet }, null, palette);
            ScreenAudit.Prepared a;
            if (scene.Family == "intro")
                a = ScreenAudit.Intro(recorder, IntroFrame(w, h, scene.State, scene.State == 0));
            else
                a = ScreenAudit.Hangar(recorder, HangarFrame(w, h, scene.State));
            DrawScene(chrome, scene, w, h, HudStyle.Modern);

            var result = ScreenAudit.Evaluate(recorder, w, h, a.HitRects);
            Console.WriteLine($"AUDIT {scene.Name} {palette} {w}x{h}: " +
                $"controls={result.Controls} hit-vs-draw={result.HitVsDraw} stroke-vs-text={result.StrokeVsText} " +
                $"text-outside-plate={result.TextOutsidePlate} text-off-screen={result.TextOffScreen} " +
                $"overlap-cross={result.TextOverlapCross} overlap-same={result.TextOverlapSame}");
            failures += result.Failures;
            report.Add(new
            {
                scene = scene.Name, palette, width = w, height = h,
                result.Controls, result.HitVsDraw, result.StrokeVsText, result.TextOutsidePlate,
                result.TextOffScreen, result.TextOverlapCross, result.TextOverlapSame,
                samples = result.Samples,
            });
        }
        File.WriteAllText(Path.Combine(directory, "audit.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"P1042_AUDIT failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    // ── allocation ───────────────────────────────────────────────────────────
    private int RunAllocation()
    {
        var surface = new NullSurface();
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null, settings, null);
        var intro = IntroFrame(1920, 1080, 3, true);
        var hangar = HangarFrame(1920, 1080, 1);
        for (int i = 0; i < 16; i++) { chrome.DrawIntro(intro); chrome.DrawHangar(hangar); }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 240; i++) { chrome.DrawIntro(intro); chrome.DrawHangar(hangar); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"P1042_ALLOC intro+hangar frames=480 bytes={bytes}");
        return bytes == 0 ? 0 : 1;
    }

    private sealed class NullSurface : IJetVectorSurface
    {
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) { }
        public void Fill(Vector2 min, Vector2 size, Color color) { }
        public void Text(string text, Vector2 position, bool large, float scale, Color color) { }
        public Vector2 Measure(string text, bool large, float scale) => Vector2.Zero;
    }

    /// <summary>The P1014 device-free shop fixture (the same one the renderer tests use).</summary>
    private sealed record ShopScene(LoadoutV2Presentation Presentation)
    {
        public static ShopScene? TryBuild()
        {
            var catalog = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork));
            if (catalog?.Core is not { } core) return null;
            var civs = core.Civilizations.Values.Where(c => c.Name == "Gigacorp").ToList();
            if (civs.Count == 0) return null;
            var civ = civs[0];
            if (!core.StationTypes.TryGetValue(civ.InitialStationTypeId, out var station)) return null;
            var techs = TechTreeBitMask.FromBytes(civ.TtbmBaseTechs);
            var offered = catalog.FilteredFor(station.TtbmLocal, techs);
            if (offered.Hulls.Count == 0) return null;
            var shop = new ShopModel(offered, offered.Hulls[0].HullId, [], 10000);
            shop.ApplyDefaultLoadout();
            shop = new ShopModel(offered, shop.EffectiveHullId, shop.ChosenParts.ToArray(), 10000);
            var model = new LoadoutV2Model(shop, catalog.Hulls, BucketCatalog.ReachableTechs(core, techs),
                station.TtbmLocal, techs);
            ShipStatSheet? Sheet(bool current)
            {
                short wantedHull = current ? shop.CurrentHullId : shop.EffectiveHullId;
                var matches = catalog.Hulls.Where(h => h.HullId == wantedHull).ToList();
                if (matches.Count == 0) return null;
                var hull = matches[0];
                var mounted = new List<MountedStatPart>();
                foreach (var part in current ? shop.CurrentParts : shop.ChosenParts)
                    if (part.MountId < 0x80 && shop.PartById(part.PartId) is { } type)
                        mounted.Add(new(type, part.MountId));
                return ShipStatSheet.Build(hull, mounted);
            }
            return new(LoadoutV2Presentation.Build(model, Sheet(true), Sheet(false)));
        }
    }
}

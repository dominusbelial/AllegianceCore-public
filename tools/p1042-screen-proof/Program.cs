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
        // P1042b — the pre-game sub-view families (fixtures mirror the production
        // frame builders; the real-client goldens are the pixel gate, §2).
        new("view-findserver", "view", 0),
        new("view-directconnect", "view", 1),
        new("view-singleplayer", "view", 2),
        new("view-training", "view", 3),
        new("view-creategame", "view", 4),
        new("view-options", "view", 5),
        new("view-help", "view", 6),
        new("view-trainingsuggest", "view", 7),
        new("career", "career", 0),
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
            _hangar, _docked, _loadoutV2, settings, null, JetPaletteIds.Green, _painter);
        _jetAmber = new JetScreenSkinRenderer(new JetSpriteBatchSurface(_batch, _white, _small, _big),
            _hangar, _docked, _loadoutV2, settings, null, JetPaletteIds.Amber, _painter);
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

    // ── P1042b sub-view fixtures ─────────────────────────────────────────────
    private static readonly Color ViewTitle = new(0.38f, 0.90f, 0.84f);
    private static readonly Color ViewText = new(0.88f, 0.96f, 0.95f);
    private static readonly Color ViewDim = new(0.48f, 0.64f, 0.62f);
    private static readonly Color SelectionFill = new(0.08f, 0.35f, 0.33f, 0.55f);
    private static readonly Color FieldFill = new(0.10f, 0.42f, 0.39f, 0.65f);

    private ScreenRow Row(float w, string label, bool selected, IntroLayoutResult layout, int index)
    {
        string text = (selected ? "> " : "  ") + label;
        var size = _small.MeasureString(text);
        var row = layout.Rows[index];
        return new(label, text, new Vector2((w - size.X) / 2f, row.Y + 4f),
            new RectangleF(row.X, row.Y, row.Width, row.Height), 1f, selected,
            selected ? ScreenInkRole.Title : ScreenInkRole.Body, selected ? ViewTitle : ViewText);
    }

    private ScreenRow Band(float w, string label, bool selected, float x, float y, float bandW,
        float bandH, string text, Color ink)
        => new(label, text, new Vector2(x, y), new RectangleF(x - 24, y - 4, bandW, bandH), 1f,
            selected, selected ? ScreenInkRole.Title : ScreenInkRole.Body, ink);

    private IntroViewFrame ViewFrame(int view, int w, int h)
    {
        var rows = new List<ScreenRow>();
        var lines = new List<ScreenTextRun>();
        var plates = new List<ScreenPlate>();
        string title, subtitle = "";
        switch (view)
        {
            case 0:
                title = "Find Server";
                subtitle = "LAN / Tailscale / VPN subnet / favorites / lobby  -  select to join";
                for (int i = 0; i < 3; i++)
                {
                    bool sel = i == 0;
                    float y = 120 + i * 26;
                    var band = new RectangleF(40, y - 3, w - 80f, 20);
                    rows.Add(new($"Alpha vs Beta  {i + 2}/8", (sel ? "> " : "  ") + $"Alpha vs Beta  {i + 2}/8",
                        new Vector2(50, y), band, 1f, sel, sel ? ScreenInkRole.Title : ScreenInkRole.Body,
                        sel ? ViewTitle : ViewText));
                    if (sel) plates.Add(new(band, ScreenPlateRole.Selection, SelectionFill));
                }
                {
                    string pw = "Password: __   (0 chars)   [ ] remember";
                    var size = _small.MeasureString(pw);
                    plates.Add(new(new RectangleF(44, h - 78, size.X + 20, 24), ScreenPlateRole.Focus, FieldFill));
                    lines.Add(new(pw, new Vector2(50, h - 74), 1f, ScreenInkRole.Title, ViewTitle));
                }
                lines.Add(new("UP/DOWN select   ENTER join   TAB password   ESC back",
                    new Vector2((w - _small.MeasureString("UP/DOWN select   ENTER join   TAB password   ESC back").X) / 2f, h - 26),
                    1f, ScreenInkRole.Dim, ViewDim));
                break;
            case 1:
                title = "Direct Connect";
                subtitle = "Type an address[:port]  -  a hostname, IPv4/IPv6, or a MagicDNS name";
                {
                    string label = "Address: 10.0.0.7:6073_";
                    var lsz = _small.MeasureString(label);
                    float boxW = MathF.Min(w - 40f, MathF.Max(340f, lsz.X + 28f));
                    plates.Add(new(new RectangleF(w / 2f - boxW / 2f, h * 0.42f - 4, boxW, 24),
                        ScreenPlateRole.Focus, FieldFill));
                    lines.Add(new(label, new Vector2((w - lsz.X) / 2f, h * 0.42f), 1f,
                        ScreenInkRole.Title, ViewTitle));
                    plates.Add(new(new RectangleF(w / 2f - 170, h * 0.42f + 14, 340, 2),
                        ScreenPlateRole.UnderlineActive, ViewTitle));
                }
                lines.Add(new("Password: __   (0 chars)", new Vector2(50, h * 0.53f), 1f,
                    ScreenInkRole.Dim, ViewDim));
                lines.Add(new("ENTER connect   TAB password   ESC back",
                    new Vector2((w - _small.MeasureString("ENTER connect   TAB password   ESC back").X) / 2f, h - 26),
                    1f, ScreenInkRole.Dim, ViewDim));
                break;
            case 2:
                title = "Single Player";
                subtitle = "Training or Skirmish";
                {
                    var entries = new[] { "Training", "Skirmish" };
                    var layout = IntroLayout.Compute(w, h, entries.Length);
                    for (int i = 0; i < entries.Length; i++) rows.Add(Row(w, entries[i], i == 0, layout, i));
                    string hint = "UP/DOWN select   ENTER choose   ESC back";
                    lines.Add(new(hint, new Vector2((w - _small.MeasureString(hint).X) / 2f, layout.FooterY),
                        1f, ScreenInkRole.Dim, ViewDim));
                }
                break;
            case 3:
                title = "Training";
                subtitle = "Choose a mission";
                {
                    var entries = new[] { "Basic Flight", "Basic Combat", "Advanced Combat" };
                    var layout = IntroLayout.Compute(w, h, entries.Length);
                    for (int i = 0; i < entries.Length; i++) rows.Add(Row(w, entries[i], i == 0, layout, i));
                    string desc = "Learn to fly: throttle, turn, and dock at a station.";
                    lines.Add(new(desc, new Vector2((w - _small.MeasureString(desc).X) / 2f, layout.PilotY),
                        1f, ScreenInkRole.Warning, Color.Orange));
                    string hint = "UP/DOWN select   ENTER start   ESC back";
                    lines.Add(new(hint, new Vector2((w - _small.MeasureString(hint).X) / 2f, layout.FooterY),
                        1f, ScreenInkRole.Dim, ViewDim));
                }
                break;
            case 4:
                title = "Create Game";
                subtitle = "Server, core, map and game name  -  the lobby creates the mission";
                {
                    var entries = new[]
                    {
                        "SERVER: Lobby alpha", "CORE:   conquest", "MAP:    dm04",
                        "GAME NAME: <type a name>", "CREATE GAME",
                    };
                    float y = h * 0.32f;
                    for (int i = 0; i < entries.Length; i++)
                    {
                        bool sel = i == 0;
                        var band = new RectangleF(50, y - 4, w - 100f, 30);
                        if (sel) plates.Add(new(band, ScreenPlateRole.Selection, SelectionFill));
                        rows.Add(new(entries[i], (sel ? "> " : "  ") + entries[i], new Vector2(64, y), band,
                            1f, sel, sel ? ScreenInkRole.Title : ScreenInkRole.Body,
                            sel ? ViewTitle : ViewText));
                        y += 36f;
                    }
                    lines.Add(new("Waiting for the lobby.", new Vector2(64, y + 12), 1f,
                        ScreenInkRole.Warning, new Color(1f, 0.72f, 0.35f)));
                    string hint = "UP/DOWN row   LEFT/RIGHT choose   TYPE name   ENTER create   ESC back";
                    lines.Add(new(hint, new Vector2((w - _small.MeasureString(hint).X) / 2f, h - 40), 1f,
                        ScreenInkRole.Dim, ViewDim));
                }
                break;
            case 5:
                title = "Options";
                subtitle = "Graphics / Sound / Game / Controls";
                {
                    var entries = new[] { "Graphics Settings", "Sound Options", "Game Options", "Mouse Options", "Visuals", "Benchmark sharing" };
                    var (firstY, rowStep) = OptionsMenuModel.LayoutRows(h, entries.Length);
                    float y = firstY;
                    for (int i = 0; i < entries.Length; i++)
                    {
                        bool sel = i == 0;
                        var band = new RectangleF(60, y - 4, w - 120, 30);
                        if (sel) plates.Add(new(band, ScreenPlateRole.Selection, SelectionFill));
                        rows.Add(new(entries[i], (sel ? "> " : "  ") + entries[i], new Vector2(72, y), band,
                            1f, sel, sel ? ScreenInkRole.Title : ScreenInkRole.Plain,
                            sel ? ViewTitle : Color.White));
                        y += rowStep;
                    }
                    lines.Add(new("UP/DOWN select   ENTER open   ESC back", new Vector2(72, y + 18), 1f,
                        ScreenInkRole.Dim, ViewDim));
                }
                break;
            case 6:
                title = "Help";
                subtitle = "The manual - UP/DOWN topics   BACKSPACE back   PGUP/PGDN scroll   ESC close";
                {
                    float tx = 40;
                    foreach (var topic in new[] { "Getting started", "Flying", "Combat", "Commanding" })
                    {
                        bool cur = topic == "Flying";
                        string label = (cur ? "[" : " ") + topic + (cur ? "]" : " ");
                        lines.Add(new(label, new Vector2(tx, 112), 1f,
                            cur ? ScreenInkRole.Title : ScreenInkRole.Dim, cur ? ViewTitle : ViewDim));
                        tx += _small.MeasureString(topic).X + 34f;
                    }
                    for (int i = 0; i < 6; i++)
                        lines.Add(new($"Line {i + 1}: the manual page text used by the help overlay.",
                            new Vector2(60, 160 + i * 26), 1f, ScreenInkRole.Body, ViewText));
                }
                break;
            default:
                title = "Welcome to Allegiance";
                subtitle = "Training is strongly recommended before going online.";
                {
                    string[] body =
                    {
                        "ALLEGIANCE is a team-based massively-multiplayer space combat game,",
                        "in which your goal is to conquer the universe.",
                        "",
                        "Training is strongly recommended before going online, and will take",
                        "approximately 30 minutes. Press ENTER to begin training now.",
                        "",
                        "[ ] Don't ask me again",
                    };
                    float y = h * 0.36f;
                    foreach (var ln in body)
                    {
                        bool ask = ln.Contains("Don't ask");
                        lines.Add(new(ln, new Vector2((w - _small.MeasureString(ln).X) / 2f, y), 1f,
                            ask ? ScreenInkRole.Dim : ScreenInkRole.Body, ask ? ViewDim : ViewText));
                        y += 30f;
                    }
                    string hint = "ENTER begin training   TAB don't-ask-again   ESC dismiss";
                    lines.Add(new(hint, new Vector2((w - _small.MeasureString(hint).X) / 2f, h - 40), 1f,
                        ScreenInkRole.Dim, ViewDim));
                }
                break;
        }
        return new IntroViewFrame(new Viewport(0, 0, w, h), "view", title, subtitle, rows, lines, plates,
            new ScreenInks(ViewTitle, ViewText, ViewDim, Color.Orange));
    }

    private CareerScreenFrame CareerFrame(int w, int h, HudStyle style)
        => new(new Viewport(0, 0, w, h), null, null, null, null, false, false, false, 0, false,
            "", "Not signed in.", "", style, new ScreenInks(ViewTitle, ViewText, ViewDim, Color.Orange));

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
            case "view":
            {
                var frame = ViewFrame(scene.State, w, h);
                _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
                try { screen.DrawIntroView(frame); }
                finally { _batch.End(); }
                break;
            }
            case "career":
            {
                var frame = CareerFrame(w, h, style);
                _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
                try { screen.DrawCareer(frame); }
                finally { _batch.End(); }
                break;
            }
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
            // P1042b — the audited hangar renderer reports the CONTENT text it draws
            // (port-verb labels, native hover string) to the same recorder, so the
            // doubled-label class is counted.
            var hangar = new HangarScreenRenderer(_batch, _white, _small, nativeFonts: null,
                GraphicsDevice, _assets, recorder);
            using var chrome = new JetScreenSkinRenderer(recorder, hangar, _docked, _loadoutV2,
                new HudSettings { Skin = HudSkinIds.Jet }, null, palette);
            ScreenAudit.Prepared a;
            if (scene.Family == "intro")
                a = ScreenAudit.Intro(recorder, IntroFrame(w, h, scene.State, scene.State == 0));
            else if (scene.Family == "view")
            {
                a = new ScreenAudit.Prepared();
                var frame = ViewFrame(scene.State, w, h);
                for (int i = 0; i < frame.Rows.Count; i++)
                {
                    var row = frame.Rows[i];
                    a.HitRects.Add(new(ScreenActions.ViewBody, ScreenActions.ViewRow(i), row.Rect));
                }
            }
            else if (scene.Family == "career")
            {
                a = new ScreenAudit.Prepared();
                for (int i = 0; i < CareerScreenRenderer.TabLabels.Length; i++)
                {
                    var rect = CareerScreenRenderer.TabRect(
                        _small.MeasureString(CareerScreenRenderer.TabLabels[i]), i, w);
                    a.HitRects.Add(new(ScreenActions.Career, ScreenActions.CareerTab(i),
                        new RectangleF(rect.X, rect.Y, rect.Width, rect.Height)));
                }
            }
            else
                a = ScreenAudit.Hangar(recorder, HangarFrame(w, h, scene.State));
            DrawScene(chrome, scene, w, h, HudStyle.Modern);
            hangar.Dispose();

            var result = ScreenAudit.Evaluate(recorder, w, h, a.HitRects);
            Console.WriteLine($"AUDIT {scene.Name} {palette} {w}x{h}: " +
                $"controls={result.Controls} hit-vs-draw={result.HitVsDraw} stroke-vs-text={result.StrokeVsText} " +
                $"text-outside-plate={result.TextOutsidePlate} text-off-screen={result.TextOffScreen} " +
                $"overlap-cross={result.TextOverlapCross} overlap-same={result.TextOverlapSame} " +
                $"overlap-content={result.ContentOverlap}");
            failures += result.Failures;
            report.Add(new
            {
                scene = scene.Name, palette, width = w, height = h,
                result.Controls, result.HitVsDraw, result.StrokeVsText, result.TextOutsidePlate,
                result.TextOffScreen, result.TextOverlapCross, result.TextOverlapSame,
                result.ContentOverlap,
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
        // P1042b — the pre-game families: a row-heavy view and the career screen (the
        // career content painter is null here, so only the chrome is measured).
        var view = ViewFrame(5, 1920, 1080);
        var career = CareerFrame(1920, 1080, HudStyle.Modern);
        void Cycle() { chrome.DrawIntro(intro); chrome.DrawHangar(hangar);
            chrome.DrawIntroView(view); chrome.DrawCareer(career); }
        for (int i = 0; i < 16; i++) Cycle();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 240; i++) Cycle();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"P1042_ALLOC intro+hangar+view+career frames={240 * 4} bytes={bytes}");
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

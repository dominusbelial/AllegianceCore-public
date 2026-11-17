// P980 (skins phase 1A) — the HUD golden-baseline fixture.
//
// The original HUD-2 harness rendered 16 scenarios × 5 resolutions × 3 sizes × 2
// styles through the PRODUCTION renderers and asserted real font bounds, no
// text overlap, inventory column fit and full Modern element coverage. P980
// keeps every one of those assertions and adds the pieces every later phase-1
// slice needs to prove "zero RGBA delta":
//
//   --capture <dir>          write decoded RGBA (lossless PNG) + SHA-256 of the
//                            DECODED pixels + a semantic snapshot per
//                            combination, plus a deterministic manifest.json
//   --compare <dir>          re-render and compare against a capture; a diff
//                            image for the first N differing combinations, a
//                            changed-pixel count per combination, non-zero exit
//   --diff-captures <a> <b>  compare two captures offline (used for the
//                            double-capture determinism proof)
//   --self-test <dir>        prove the compare mode is honest: a deliberately
//                            changed pixel and a changed semantic snapshot are
//                            both detected, and the untouched baseline passes
//
// Determinism is fixture-owned, never renderer-owned:
//  * a FRESH ModernHudRenderer per combination, so its frame-delta easing
//    (ModernHudRenderer.cs:114-116) and HUD-level toast timer cannot leak from
//    one combination into the next (_dt is exactly 1 on the first draw and the
//    gauge targets start at their model value, so the easing is a no-op);
//  * the low-resource "!" badge is the one wall-clock animated glyph
//    (ModernHudRenderer.cs:166, MathF.Sin(Stopwatch.GetTimestamp()...)). It is
//    recorded as an explicit per-combination animation mask taken from the
//    renderer's own measured text boxes, and pixels inside it are counted and
//    reported separately instead of failing the gate.
//  * every other frame-dependent input (radar pulse, contact set, text, size,
//    colour-blind state) is a constant of the fixture.
//
// Usage (single-process, offscreen SDL — see docs/reports/SKINS_1A_BASELINE_P980.md):
//   SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture <dir>
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using HudProof;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

var options = Options.Parse(args);
using var proof = new Proof(options);
try { proof.Run(); }
catch (Exception exception)
{
    Console.Error.WriteLine("HUD2_FAIL " + exception);
    Environment.Exit(2);
}
return Environment.ExitCode;

sealed record Options
{
    public string Output { get; init; } = Path.Combine(".scratch", "p980", "render");
    public string? Capture { get; init; }
    public string? Compare { get; init; }
    public string? SelfTest { get; init; }
    /// <summary>P1035 — render the Jet skin's story-1 scenes to PNG (see CaptureJet).</summary>
    public string? Jet { get; init; }
    public string[] DiffCaptures { get; init; } = Array.Empty<string>();
    public string? Filter { get; init; }
    public int MaxDiffs { get; init; } = 10;
    public bool Test { get; init; }
    public bool Samples { get; init; }
    public bool NoBaselineVerify { get; init; }

    public static Options Parse(string[] arguments)
    {
        string? capture = null, compare = null, selfTest = null, filter = null, output = null, jet = null;
        var diff = new List<string>();
        int maxDiffs = 10;
        bool test = false, samples = false, noVerify = false;
        var positional = new List<string>();
        for (int i = 0; i < arguments.Length; i++)
        {
            string Next(string flag) => i + 1 < arguments.Length ? arguments[++i]
                : throw new ArgumentException($"{flag} needs a value");
            switch (arguments[i])
            {
                case "--capture": capture = Next("--capture"); break;
                case "--compare": compare = Next("--compare"); break;
                case "--self-test": selfTest = Next("--self-test"); break;
                case "--jet": jet = Next("--jet"); break;
                case "--filter": filter = Next("--filter"); break;
                case "--max-diffs": maxDiffs = int.Parse(Next("--max-diffs")); break;
                case "--diff-captures":
                {
                    var a = Next("--diff-captures");
                    var b = Next("--diff-captures");
                    diff.Add(a); diff.Add(b); break;
                }
                case "--test": test = true; break;
                case "--samples": samples = true; break;
                case "--no-baseline-verify": noVerify = true; break;
                case "--help" or "-h":
                    Console.WriteLine("hud2-proof [<outputDir>] [--test] [--samples] [--capture <dir>] [--compare <dir>] [--self-test <dir>] [--jet <dir>] [--diff-captures <a> <b>] [--filter <text>] [--max-diffs <n>] [--no-baseline-verify]");
                    Environment.Exit(0); break;
                default:
                    if (arguments[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unknown flag " + arguments[i]);
                    positional.Add(arguments[i]); break;
            }
        }
        if (positional.Count > 0) output = positional[0];
        return new Options
        {
            Output = output ?? (capture ?? compare ?? selfTest ?? jet ?? Path.Combine(".scratch", "p980", "render")),
            Capture = capture, Compare = compare, SelfTest = selfTest, Jet = jet, DiffCaptures = diff.ToArray(),
            Filter = filter, MaxDiffs = maxDiffs, Test = test, Samples = samples, NoBaselineVerify = noVerify,
        };
    }
}

/// <summary>One cell of the render matrix.</summary>
sealed record Combo(string Scenario, int Width, int Height, float Size, string Backdrop, string Style)
{
    public string Id => $"{Scenario}/{Width}x{Height}/s{Fmt(Size)}/{Backdrop}/{Style}";
    public string Stem => $"{Scenario}__{Width}x{Height}__s{Fmt(Size)}__{Backdrop}__{Style}";
    public static string Fmt(float size) => size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

readonly record struct RectRecord(int X, int Y, int W, int H)
{
    public static RectRecord Of(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

// ── manifest / snapshot schema (all serialization is deterministic) ───────────
sealed record DriverInfo(
    string Renderer, string AdapterDescription, string GraphicsProfile, string SurfaceFormat,
    string MonoGameVersion, string SdlVideoDriver, string OsDescription, float DisplayScale);

sealed record MatrixInfo(
    IReadOnlyList<string> Scenarios, IReadOnlyList<string> Sizes,
    IReadOnlyList<string> Backdrops, IReadOnlyList<string> Styles,
    IReadOnlyList<IReadOnlyList<int>> Resolutions, int CombinationCount);

sealed record ComboEntry(
    string Id, string Scenario, int Width, int Height, float Size, string Backdrop, string Style,
    string PixelsSha256, string PixelsOutsideAnimationSha256, int AnimationPixels,
    IReadOnlyList<RectRecord> AnimationMask, string Png, string Snapshot, string SnapshotSha256);

sealed record Manifest(
    int Schema, string Tool, string Task, string Commit, bool Dirty,
    DriverInfo Driver, MatrixInfo Matrix, IReadOnlyList<ComboEntry> Combinations);

sealed record ElementSnapshot(
    string Id, bool Available, bool Visible, string? Anchor, float? X, float? Y, bool? YUp,
    float? Value, string? Text, float[]? Color, float? ScreenX, float? ScreenY);

sealed record BoxSnapshot(string Text, int X, int Y, int W, int H, bool FixedAnchor);

sealed record InventoryRowFit(
    string Key, string Name, string? Stats, float NameWidthPx, float NameMaxPx, bool NameFits,
    float StatsWidthPx, float StatsMaxPx, float StatsFitScale, bool StatsFits);

sealed record InventoryFit(bool Visible, float Scale, IReadOnlyList<InventoryRowFit> Rows, bool AllFit);

sealed record Snapshot(
    string Scenario, int Width, int Height, float Size, string Backdrop, string Style,
    bool Visible, bool FlightVisible, bool InstrumentsVisible,
    IReadOnlyList<ElementSnapshot> Elements, IReadOnlyList<BoxSnapshot> TextBoxes,
    InventoryFit? Inventory, IReadOnlyList<string> DrawnElements, float ModernScale,
    IReadOnlyList<string> Findings);

sealed class Proof : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly Options _options;
    private readonly string _out;
    private readonly List<object> _summaries = new();
    private readonly bool _timing = Environment.GetEnvironmentVariable("HUD2_TIMING") == "1";

    private SpriteBatch _batch = null!;
    private Texture2D _white = null!;
    private SpriteFont _small = null!, _big = null!, _modernOutline = null!, _modernSmall = null!;
    private GaugeTextures? _gauges;
    private StatusTextures? _status;
    private IndicatorTextures? _indicators;
    private HudRenderer _classic = null!;
    // P1035 — the Jet story-1 renderer for `--jet` captures (its shared radar pass
    // is the production RadarBracketRenderer; the fixture draws no chat/cargo/tags).
    private JetHudRenderer? _jet;
    private JetHudRenderer? _jetAudit;
    private HudSettings? _jetSettings;
    private RecordingSurface? _jetAuditSurface;
    private HudSkinDefinition? _jetDefinition;
    // P1041 — the amber variant: its own preloaded renderer bound to the real batch
    // (PNG evidence) and a second recorder (geometry/palette audits). Both draw the
    // SAME models; only the palette differs.
    private JetHudRenderer? _jetAmber;
    private JetHudRenderer? _jetAmberAudit;
    private RecordingSurface? _jetAmberSurface;
    private HudSkinDefinition? _amberDefinition;

    private static readonly (string Name, Color Color)[] Backdrops =
    [
        ("dark", new Color(40, 53, 76)),
        ("bright", new Color(225, 215, 205)),
    ];

    // P980 — 800×480 is the small-window floor the P949 real captures use; 4K is
    // the largest HUD the scale budget must survive. Both were missing from the
    // HUD-2 matrix (UI_SKINS_P972 §1.5).
    private static readonly (int Width, int Height)[] Resolutions =
    [
        (1920, 1080), (2560, 1440), (1280, 720), (1600, 900), (1366, 768), (800, 480), (3840, 2160),
    ];

    private static readonly float[] Sizes = [.75f, 1f, 1.5f];

    // The HUD-2 scenarios plus the P980 additions that give an uncovered parity
    // row a fixture (see docs/reports/P980_assets/coverage.json): a training
    // callout, a ghost/death banner with lives+kills, a command recipient, an
    // ore target, a mid-slide pane and both native visibility toggles off.
    private static readonly string[] Scenarios =
    [
        "flight", "chat-stress", "combat", "critical", "chase", "docked", "lifepod", "dead", "command",
        "hidden", "inventory", "systems", "radar-All", "radar-Default", "radar-Target", "radar-Minimal",
        "training", "ghost", "commandee", "target-full", "slide", "radar-overlays",
    ];

    private static readonly string[] Styles = ["Classic", "Modern"];

    public Proof(Options options)
    {
        _options = options;
        _out = Path.GetFullPath(options.Output);
        _graphics = new(this) { PreferredBackBufferWidth = 2560, PreferredBackBufferHeight = 1440 };
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        Directory.CreateDirectory(_out);
        _batch = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });
        _small = Content.Load<SpriteFont>("HUD");
        _big = Content.Load<SpriteFont>("HUDLarge");
        _modernOutline = Content.Load<SpriteFont>("HUDModern");
        _modernSmall = Metrics(_modernOutline, .25f);   // ModernHudRenderer's measured _small
        string artwork = Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork);
        bool hasArtwork = Directory.Exists(artwork);
        if (hasArtwork)
        {
            _gauges = GaugeTextures.Load(GraphicsDevice, artwork);
            _status = StatusTextures.Load(GraphicsDevice, artwork);
            _indicators = IndicatorTextures.Load(GraphicsDevice, artwork);
        }
        _classic = new HudRenderer(_batch, _white, _small, _big, _gauges, statusTextures: _status, indicators: _indicators);
        // P1035 — the built-in Jet definition and its story-1 renderer. The shared
        // radar pass uses the production Classic radar composition.
        _jetDefinition = HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet);
        if (_jetDefinition is not null)
        {
            var radar = new RadarBracketRenderer(_batch, _white, _status);
            var resources = new HudSkinResources(model => radar.Draw(model, GraphicsDevice.Viewport),
                (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });
            _jetSettings = new HudSettings { Skin = HudSkinIds.Jet, Size = 1f };
            _jet = new JetHudRenderer(_batch, _white, _small, _big,
                _jetSettings, resources, _jetDefinition, radarIcons: null);
            // P1037 — a second Jet renderer bound to a recording surface, so `--jet`
            // can audit the exact text/plate boxes of every scene (no screenshot-only
            // claim). It draws the same model through the same production primitives.
            _jetAuditSurface = new RecordingSurface(_jet.Painter.Surface);
            _jetAudit = new JetHudRenderer(_jetAuditSurface,
                _jetSettings, resources, _jetDefinition);
            // P1041 — the amber variant of the same skin (jet.amber.skin.json). It
            // shares every renderer/layout id; only the palette block differs.
            _amberDefinition = HudSkinResolver.Catalog.TryGetVariant(HudSkinIds.Jet, JetPaletteIds.Amber);
            if (_amberDefinition is not null)
            {
                _jetAmber = new JetHudRenderer(_batch, _white, _small, _big,
                    _jetSettings, resources, _amberDefinition, radarIcons: null);
                _jetAmberSurface = new RecordingSurface(_jetAmber.Painter.Surface);
                _jetAmberAudit = new JetHudRenderer(_jetAmberSurface,
                    _jetSettings, resources, _amberDefinition);
            }
        }
        Console.WriteLine(hasArtwork
            ? "HUD2_ARTWORK present (native gauge/status/indicator art loaded)"
            : "HUD2_ARTWORK absent (synthetic gauge/status shapes)");

        if (_options.SelfTest is { } selfTest)
        {
            RunSelfTest(selfTest);
            Console.WriteLine("HUD2_SELF_TEST_PASS");
            Environment.ExitCode = 0;
        }
        else if (_options.DiffCaptures.Length == 2)
        {
            Environment.ExitCode = DiffCaptureDirectories(_options.DiffCaptures[0], _options.DiffCaptures[1]);
        }
        else if (_options.Compare is { } baseline)
        {
            Environment.ExitCode = CompareRendered(baseline);
        }
        else if (_options.Capture is { } capture)
        {
            Environment.ExitCode = Capture(capture);
        }
        else if (_options.Jet is { } jetDirectory)
        {
            Environment.ExitCode = CaptureJet(jetDirectory);
        }
        else
        {
            Environment.ExitCode = RenderLegacy();
        }
        WriteRunStats();
        Exit();
    }

    // ── capture ──────────────────────────────────────────────────────────────
    private int Capture(string directory)
    {
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        string pngDir = Path.Combine(root, "png");
        string snapshotDir = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(pngDir);
        Directory.CreateDirectory(snapshotDir);
        if (_options.Filter is { } partial) Console.WriteLine($"HUD2_CAPTURE filter '{partial}' (partial baseline, debug only)");
        var entries = new List<ComboEntry>(Combos().Count);
        var started = Stopwatch.StartNew();
        int done = 0, total = Combos().Count;
        foreach (var (width, height) in Resolutions)
        {
            using var target = new RenderTarget2D(GraphicsDevice, width, height);
            var pixels = new Color[width * height];
            foreach (float size in Sizes)
            foreach (var (backdropName, backdropColor) in Backdrops)
            foreach (string scenario in Scenarios)
            foreach (string style in Styles)
            {
                var combo = new Combo(scenario, width, height, size, backdropName, style);
                if (!Match(combo.Id)) continue;
                var result = RenderCombo(combo, target, pixels, backdropColor);
                string pngRel = Path.Combine("png", combo.Stem + ".png");
                using (var file = File.Create(Path.Combine(root, pngRel)))
                    target.SaveAsPng(file, width, height);
                string snapRel = Path.Combine("snapshots", combo.Stem + ".json");
                File.WriteAllText(Path.Combine(root, snapRel), result.SnapshotJson);
                entries.Add(new ComboEntry(combo.Id, scenario, width, height, size, backdropName, style,
                    result.PixelsSha256, result.OutsideSha256, result.AnimationPixels, result.Mask,
                    pngRel.Replace('\\', '/'), snapRel.Replace('\\', '/'), result.SnapshotSha256));
                if (++done % 100 == 0)
                    Console.WriteLine($"HUD2_CAPTURE {done}/{total} {combo.Id} {started.Elapsed.TotalSeconds:0}s");
            }
        }
        WriteManifest(root, entries);
        Console.WriteLine($"HUD2_CAPTURE_PASS {entries.Count} combinations in {started.Elapsed.TotalMinutes:0.0} min -> {root}");
        return 0;
    }

    /// <summary>P1035/P1036 — the Jet evidence capture. Renders the Jet skin through
    /// the production JetHudRenderer (its own vector primitives plus the shared
    /// Classic radar pass) at the two required resolutions, on a dark and a bright
    /// backdrop, and writes the drawn-state summary beside the PNGs. Since P1036 the
    /// scenes include the four flight-symbology cases: level, pitched + rolled,
    /// near-vertical (heading unavailable) and reverse throttle. Deliberately
    /// separate from the Classic/Modern matrix so the zero-delta capture and
    /// comparison stay byte-identical.</summary>
    private int CaptureJet(string directory)
    {
        if (_jet is null || _jetDefinition is null)
        {
            Console.Error.WriteLine("HUD2_JET_MISSING the Jet skin definition is not installed");
            return 1;
        }
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var scenes = new (string Scenario, int Width, int Height, string Backdrop)[]
        {
            // Level, heading 045, 260 mps, 80 % throttle.
            ("jet-level", 1920, 1080, "dark"),
            ("jet-level", 1920, 1080, "bright"),
            ("jet-level", 800, 480, "dark"),
            ("jet-level", 800, 480, "bright"),
            // Pitched +18°, banked −25° (left), heading 210.
            ("jet-pitched", 1920, 1080, "dark"),
            ("jet-pitched", 800, 480, "dark"),
            // Near-vertical: 89.9° pitch, heading unavailable (held).
            ("jet-vertical", 1920, 1080, "dark"),
            ("jet-vertical", 800, 480, "dark"),
            // Reverse throttle (−0.6 → 20 %, below the 50 % idle detent).
            ("jet-reverse", 1920, 1080, "dark"),
            ("jet-reverse", 800, 480, "dark"),
            ("jet-reverse", 800, 480, "bright"),
            // P1037 — target designator / lock / lead / order scenes, both required
            // resolutions on dark and bright backdrops.
            ("jet-target", 1920, 1080, "dark"),
            ("jet-target", 1920, 1080, "bright"),
            ("jet-target", 800, 480, "dark"),
            ("jet-target", 800, 480, "bright"),
            ("jet-locked", 1920, 1080, "dark"),
            ("jet-locked", 1920, 1080, "bright"),
            ("jet-locked", 800, 480, "dark"),
            ("jet-locked", 800, 480, "bright"),
            ("jet-lockfull", 1920, 1080, "dark"),
            ("jet-lockfull", 1920, 1080, "bright"),
            ("jet-lockfull", 800, 480, "dark"),
            ("jet-lockfull", 800, 480, "bright"),
            ("jet-offscreen", 1920, 1080, "dark"),
            ("jet-offscreen", 1920, 1080, "bright"),
            ("jet-offscreen", 800, 480, "dark"),
            ("jet-offscreen", 800, 480, "bright"),
            ("jet-lead", 1920, 1080, "dark"),
            ("jet-lead", 1920, 1080, "bright"),
            ("jet-lead", 800, 480, "dark"),
            ("jet-lead", 800, 480, "bright"),
            ("jet-leadout", 1920, 1080, "dark"),
            ("jet-leadout", 1920, 1080, "bright"),
            ("jet-leadout", 800, 480, "dark"),
            ("jet-leadout", 800, 480, "bright"),
            ("jet-orders", 1920, 1080, "dark"),
            ("jet-orders", 1920, 1080, "bright"),
            ("jet-orders", 800, 480, "dark"),
            ("jet-orders", 800, 480, "bright"),
            ("jet-friendly", 1920, 1080, "dark"),
            ("jet-friendly", 1920, 1080, "bright"),
            ("jet-friendly", 800, 480, "dark"),
            ("jet-friendly", 800, 480, "bright"),
            // P1038 — stores (normal / low / critical), pod OXY, cloak/eye/flag, a caution
            // stack of three or more, the ghost banner, the F4 pane with every row type, the
            // F4 pane of empty mounts and the two new lock radii (item 0 follow-up b).
            ("jet-stores", 1920, 1080, "dark"),
            ("jet-stores", 1920, 1080, "bright"),
            ("jet-stores", 800, 480, "dark"),
            ("jet-stores", 800, 480, "bright"),
            ("jet-stores-low", 1920, 1080, "dark"),
            ("jet-stores-low", 800, 480, "dark"),
            ("jet-stores-critical", 1920, 1080, "dark"),
            ("jet-stores-critical", 800, 480, "dark"),
            ("jet-pod", 1920, 1080, "dark"),
            ("jet-pod", 800, 480, "dark"),
            ("jet-flags", 1920, 1080, "dark"),
            ("jet-flags", 800, 480, "dark"),
            ("jet-caution", 1920, 1080, "dark"),
            ("jet-caution", 1920, 1080, "bright"),
            ("jet-caution", 800, 480, "dark"),
            ("jet-caution", 800, 480, "bright"),
            ("jet-ghost", 1920, 1080, "dark"),
            ("jet-ghost", 800, 480, "dark"),
            ("jet-f4", 1920, 1080, "dark"),
            ("jet-f4", 1920, 1080, "bright"),
            ("jet-f4", 800, 480, "dark"),
            // P1038c — the compact pane on the bright backdrop, so the title row, the pane
            // backing and the slot marker have before/after evidence at BOTH resolutions
            // and both backdrops.
            ("jet-f4", 800, 480, "bright"),
            ("jet-f4-empty", 1920, 1080, "dark"),
            ("jet-f4-empty", 800, 480, "dark"),
            ("jet-locksmall", 1920, 1080, "dark"),
            ("jet-locksmall", 800, 480, "dark"),
            ("jet-locklarge", 1920, 1080, "dark"),
            ("jet-locklarge", 800, 480, "dark"),
            // P1039 — the Jet radar: square tactical scope and circular RWR, sparse and
            // dense (64 contacts with labels), all four LOD modes, every contact kind with
            // every overlay, and the null-bearing fallback, at both resolutions and both
            // backdrops.
            ("jet-radar", 1920, 1080, "dark"),
            ("jet-radar", 1920, 1080, "bright"),
            ("jet-radar", 800, 480, "dark"),
            ("jet-radar", 800, 480, "bright"),
            ("jet-radar-round", 1920, 1080, "dark"),
            ("jet-radar-round", 1920, 1080, "bright"),
            ("jet-radar-round", 800, 480, "dark"),
            ("jet-radar-round", 800, 480, "bright"),
            ("jet-radar-dense", 1920, 1080, "dark"),
            ("jet-radar-dense", 1920, 1080, "bright"),
            ("jet-radar-dense", 800, 480, "dark"),
            ("jet-radar-dense", 3840, 2160, "dark"),
            ("jet-radar-dense-round", 1920, 1080, "dark"),
            ("jet-radar-kinds", 1920, 1080, "dark"),
            ("jet-radar-kinds", 800, 480, "dark"),
            ("jet-radar-nobearing", 1920, 1080, "dark"),
            ("jet-radar-nobearing", 800, 480, "dark"),
            ("jet-radar-All", 1920, 1080, "dark"),
            ("jet-radar-Default", 1920, 1080, "dark"),
            ("jet-radar-Target", 1920, 1080, "dark"),
            ("jet-radar-Minimal", 1920, 1080, "dark"),
            // The shared Classic radar pass with every overlay/label flag set.
            ("radar-overlays", 1920, 1080, "dark"),
            // Instrument-hidden states prove the native gate (pod keeps them,
            // docked and command view do not).
            ("lifepod", 800, 480, "dark"),
            ("docked", 800, 480, "dark"),
            ("command", 1920, 1080, "dark"),
            // P1040 — the console/chat/lifecycle/training states at both required
            // resolutions on dark and bright backdrops, plus one 4K CMD frame.
            ("jet-chat-long", 1920, 1080, "dark"),
            ("jet-chat-long", 1920, 1080, "bright"),
            ("jet-chat-long", 800, 480, "dark"),
            ("jet-chat-long", 800, 480, "bright"),
            ("jet-compose-mouse", 1920, 1080, "dark"),
            ("jet-compose-mouse", 800, 480, "dark"),
            ("jet-compose-chat", 1920, 1080, "dark"),
            ("jet-compose-chat", 800, 480, "dark"),
            ("jet-compose-command", 1920, 1080, "dark"),
            ("jet-compose-command", 800, 480, "dark"),
            ("jet-compose-shell", 1920, 1080, "dark"),
            ("jet-compose-shell", 800, 480, "dark"),
            ("jet-compose-quick", 1920, 1080, "dark"),
            ("jet-compose-quick", 800, 480, "dark"),
            ("jet-cmd", 1920, 1080, "dark"),
            ("jet-cmd", 1920, 1080, "bright"),
            ("jet-cmd", 800, 480, "dark"),
            ("jet-cmd", 800, 480, "bright"),
            ("jet-cmd", 3840, 2160, "dark"),
            ("jet-commandview", 1920, 1080, "dark"),
            ("jet-commandview", 800, 480, "dark"),
            ("jet-tips", 1920, 1080, "dark"),
            ("jet-tips", 1920, 1080, "bright"),
            ("jet-tips", 800, 480, "dark"),
            ("jet-tips", 800, 480, "bright"),
            ("jet-orders-text", 1920, 1080, "dark"),
            ("jet-orders-text", 800, 480, "dark"),
            ("jet-podoxy", 1920, 1080, "dark"),
            ("jet-podoxy", 1920, 1080, "bright"),
            ("jet-podoxy", 800, 480, "dark"),
            ("jet-podoxy", 800, 480, "bright"),
            ("jet-ghost", 1920, 1080, "bright"),
            ("jet-ghost", 800, 480, "bright"),
            ("jet-turret", 1920, 1080, "dark"),
            ("jet-turret", 1920, 1080, "bright"),
            ("jet-turret", 800, 480, "dark"),
            ("jet-turret", 800, 480, "bright"),
            ("jet-training", 1920, 1080, "dark"),
            ("jet-training", 1920, 1080, "bright"),
            ("jet-training", 800, 480, "dark"),
            ("jet-training", 800, 480, "bright"),
            ("jet-rollcue", 1920, 1080, "dark"),
            ("jet-rollcue", 800, 480, "dark"),
            // P1040c — the roll lesson on the bright backdrop too, so every training scene
            // (jet-training overlay 17 + the roll lesson) has dark+bright evidence at both
            // required resolutions.
            ("jet-rollcue", 1920, 1080, "bright"),
            ("jet-rollcue", 800, 480, "bright"),
        };
        static object Box(RectangleF box) => new { x = box.X, y = box.Y, w = box.Width, h = box.Height };
        List<object> Furniture()
        {
            var list = new List<object>(_jetAudit!.FurnitureBoxes.Length);
            foreach (RectangleF box in _jetAudit.FurnitureBoxes) list.Add(Box(box));
            return list;
        }
        var summaries = new List<object>(scenes.Length);
        int missingTotal = 0, hintBlocked = 0, cellLeaks = 0;
        // P1041 — the amber counts: off-palette colours per variant, green-vs-amber
        // geometry differences, and the amber run's own audit classes.
        HudSkinPalette greenPalette = _jetDefinition.Palette!;
        HudSkinPalette? amberPalette = _amberDefinition?.Palette;
        int greenOff = 0, amberOff = 0, geometryTotal = 0;
        int amberStroke = 0, amberOutside = 0, amberOffScreen = 0, amberSmall = 0, amberInPlate = 0;
        int amberCue = 0, amberLegible = 0, amberCross = 0, amberSame = 0, amberOverGlyph = 0;
        int amberStrokeText = 0, amberCallout = 0, amberPlates = 0;
        var paletteScenes = new List<string>();
        var geometryScenes = new List<string>();
        // P1041 — the distinct offending colours (a leftover's hex is the evidence).
        var greenOffenders = new SortedSet<string>(StringComparer.Ordinal);
        var amberOffenders = new SortedSet<string>(StringComparer.Ordinal);
        var missingScenes = new List<string>();
        foreach (var (scenario, width, height, backdropName) in scenes)
        {
            using var target = new RenderTarget2D(GraphicsDevice, width, height);
            var backdrop = Backdrops.First(b => b.Name == backdropName).Color;
            var model = Scenario(scenario, width, height);
            // P1039 — the ONE shared round-radar option decides the scope's border: square
            // tactical scope off, circular RWR on (P972 §5.4 row 1).
            _jetSettings!.RoundRadar = scenario is "jet-radar-round" or "jet-radar-dense-round";
            // Each scene starts from no held attitude (the proof's frames are
            // independent; in the live client the hold carries across frames).
            _jet.ResetFlightState();
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(backdrop);
            try
            {
                // Same frame-boundary call Game1 makes, with no target preview, in the
                // production pass order: HUD, then the command composition, then the
                // console composition.
                var drawFrame = new HudDrawFrame(GraphicsDevice.Viewport, null, false);
                _jet.Draw(model, drawFrame);
                _jet.DrawCommand(model);
                _jet.DrawConsole(model);
            }
            finally
            {
                GraphicsDevice.SetRenderTarget(null);
            }
            // P1037 — the layout audit renders the SAME model through a recording
            // surface, so the "no label overlaps a caption or a box" rule is measured
            // from the exact boxes the renderer submits. P1038b extends it to
            // stroke-vs-text, text-outside-plate, text-off-screen and icon glyph size.
            LayoutAudit audit;
            float scale = ModernHudScale.Core(1f, 1f, height);
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(backdrop);
            try
            {
                _jetAuditSurface!.Reset();
                _jetAudit!.ResetFlightState();
                var auditFrame = new HudDrawFrame(GraphicsDevice.Viewport, null, false);
                _jetAudit.Draw(model, auditFrame);
                _jetAudit.DrawCommand(model);
                _jetAudit.DrawConsole(model);
                audit = _jetAuditSurface.Audit(width, height, scale, backdrop);
            }
            finally
            {
                GraphicsDevice.SetRenderTarget(null);
            }
            string stem = $"jet-{scenario.Replace("jet-", "")}-{width}x{height}-{backdropName}";
            using (var file = File.Create(Path.Combine(root, stem + ".png")))
                target.SaveAsPng(file, width, height);
            // ── P1041 — the amber variant of the SAME scene ─────────────────────
            // The amber renderer draws the same model through its own recorder: the
            // PNG comes from its own real-batch pass, the palette/geometry audits from
            // the recorder (which forwards every primitive to that batch).
            LayoutAudit amberAudit = audit;
            int greenOffPalette = 0, amberOffPalette = 0, geometryDiffs = 0;
            string tagGreen = "", tagAmber = "";
            if (_jetAmber is not null && _jetAmberSurface is not null && _jetAmberAudit is not null)
            {
                GraphicsDevice.SetRenderTarget(target);
                GraphicsDevice.Clear(backdrop);
                try
                {
                    var amberFrame = new HudDrawFrame(GraphicsDevice.Viewport, null, false);
                    _jetAmber.ResetFlightState();
                    _jetAmber.Draw(model, amberFrame);
                    _jetAmber.DrawCommand(model);
                    _jetAmber.DrawConsole(model);
                }
                finally
                {
                    GraphicsDevice.SetRenderTarget(null);
                }
                GraphicsDevice.SetRenderTarget(target);
                GraphicsDevice.Clear(backdrop);
                try
                {
                    _jetAmberSurface.Reset();
                    _jetAmberAudit.ResetFlightState();
                    var amberFrame = new HudDrawFrame(GraphicsDevice.Viewport, null, false);
                    _jetAmberAudit.Draw(model, amberFrame);
                    _jetAmberAudit.DrawCommand(model);
                    _jetAmberAudit.DrawConsole(model);
                    amberAudit = _jetAmberSurface.Audit(width, height, scale, backdrop);
                }
                finally
                {
                    GraphicsDevice.SetRenderTarget(null);
                }
                // The scene's only intentional fixed colour: the native side-identity
                // colour the shared console model carries (consoledata.cpp:99-107,
                // dialog.mdl:26,4722), used for tips and its own brightness steps.
                Color? side = model.Context.Console?.SideColor is { } sc
                    ? new Color(sc.R, sc.G, sc.B, sc.A) : null;
                greenOffPalette = _jetAuditSurface!.SubmittedColours.Count(c => !PaletteAllows(greenPalette, c, side));
                amberOffPalette = _jetAmberSurface.SubmittedColours.Count(c => !PaletteAllows(amberPalette!, c, side));
                greenOff += greenOffPalette;
                amberOff += amberOffPalette;
                // Geometry: identical measured boxes, strokes, plates and element boxes;
                // only the palette's own WIP tag (item 5's label) is excluded.
                string[] greenGeometry = _jetAuditSurface.GeometrySignature(excludeTag: true);
                string[] amberGeometry = _jetAmberSurface.GeometrySignature(excludeTag: true);
                geometryDiffs = GeometryDiff(greenGeometry, amberGeometry);
                geometryTotal += geometryDiffs;
                tagGreen = _jetAuditSurface.TagRun();
                tagAmber = _jetAmberSurface.TagRun();
                amberStroke += amberAudit.StrokeHits;
                amberOutside += amberAudit.TextOutsidePlate;
                amberOffScreen += amberAudit.TextOffScreen;
                amberSmall += amberAudit.IconTooSmall;
                amberInPlate += amberAudit.TextOverlapInPlate;
                amberCue += amberAudit.CueReadoutHits;
                amberLegible += amberAudit.LegibleBehindPane;
                amberCross += amberAudit.TextOverlapCross;
                amberSame += amberAudit.TextOverlapSame;
                amberOverGlyph += amberAudit.TextOverGlyph;
                amberStrokeText += amberAudit.StrokeThroughText;
                amberCallout += amberAudit.CalloutCrossed;
                amberPlates += amberAudit.RadarPlates;
                foreach (Color c in _jetAuditSurface.SubmittedColours)
                    if (!PaletteAllows(greenPalette, c, side)) greenOffenders.Add($"{c.R:X2}{c.G:X2}{c.B:X2}");
                foreach (Color c in _jetAmberSurface.SubmittedColours)
                    if (!PaletteAllows(amberPalette!, c, side)) amberOffenders.Add($"{c.R:X2}{c.G:X2}{c.B:X2}");
                if (greenOffPalette > 0 || amberOffPalette > 0)
                    paletteScenes.Add($"{stem}:green={greenOffPalette},amber={amberOffPalette}");
                if (geometryDiffs > 0) geometryScenes.Add($"{stem}:{geometryDiffs}");
            }
            // The amber recorder just drew into `target`: save the amber evidence frame.
            if (_jetAmber is not null)
                using (var file = File.Create(Path.Combine(root, stem + "-amber.png")))
                    target.SaveAsPng(file, width, height);
            var attitude = _jet.Attitude;
            // P1040b — the `ElementMissing` class: the elements this scene's model makes
            // eligible (its gate true, after the panel's own stated line limit) against the
            // elements the renderer reported as DRAWN this frame.
            string[] eligible = EligibleElements(_jetAudit!, model, height);
            string[] missing = eligible.Where(id => !_jetAuditSurface!.DrawnIds.Contains(id)).ToArray();
            missingTotal += missing.Length;
            if (missing.Length > 0) missingScenes.Add(stem + ":" + string.Join("+", missing));
            if (_jetAudit!.HintBox.Height > 0f && !_jetAudit.HintClear) hintBlocked++;
            cellLeaks += CellLeaks(_jetAudit!, _jetAuditSurface!, model.Context.CommandWing);
            summaries.Add(new
            {
                scene = stem, scenario, width, height, backdrop = backdropName,
                coverage = _jet.Coverage!.OrderBy(id => id, StringComparer.Ordinal),
                visible = model.Visible, flightVisible = model.FlightVisible,
                instrumentsVisible = model.InstrumentsVisible,
                reticleVisible = model.ReticleVisible, telemetry = model.Telemetry is not null,
                speed = model.Telemetry?.Speed, throttle = model.Telemetry?.Throttle,
                throttlePercent = model.Telemetry is { } own
                    ? (int)MathF.Round((Math.Clamp(own.Throttle, -1f, 1f) + 1f) * 0.5f * 100f) : (int?)null,
                velocityMarkerVisible = model.VelocityMarkerVisible,
                // P1037 — the shared-model gates the Jet target/lock/lead/order
                // drawing must agree with.
                target = model.Context.Target is { } t
                    ? new { name = t.Name, enemy = t.IsEnemy, distance = t.Distance, closing = t.ClosingSpeed, screenRadius = t.ScreenRadius }
                    : null,
                targetPane = model.Context.TargetPane is { } pane
                    ? new { present = pane.Present, range = pane.Distance, closing = pane.ClosingSpeed, speed = pane.Speed,
                            tti = pane.TimeToIntercept, sector = pane.SectorName, inRange = pane.InRange }
                    : null,
                leadGate = model.LeadMarkerVisible, aimGate = model.AimPredictorVisible,
                orderGate = model.OrderArrowsVisible, chaseGate = model.ChaseAimVisible,
                lockState = model.Context.Lock is { } l ? new { state = l.State.ToString(), fraction = l.Fraction, screenRadius = l.ScreenRadius } : null,
                // P1038 item 0a — the measured lock/designator fit of this frame: the
                // designator's corner distance along the diamond diagonal against the
                // diamond's own boundary there (a positive gap means it lies inside).
                lockFit = LockFit(model, width, height),
                // P1038 — the shared gates the stores / caution / inventory drawing reads.
                storesVisible = model.InstrumentsVisible && model.Telemetry is not null,
                gauges = model.Gauges.Select(g => new { kind = g.Kind.ToString(), label = g.Label, fraction = g.Fraction, visible = g.Visible }),
                caution = CautionSummary(model, height),
                inventoryVisible = model.Context.InventoryVisible, inventoryRows = model.Context.InventoryPane?.Rows.Count ?? 0,
                inventorySlide = model.Context.InventorySlide,
                missileTrack = model.Context.MissileTrack is { } mt ? new { tracked = mt.IsTracked, bestLock = mt.BestLock } : null,
                flashing = model.Telemetry?.Flashing,
                targetPaneVisible = model.TargetPaneVisible,
                offScreenBearing = model.Context.Target is null && model.Context.TargetBearing is not null,
                attitude = new
                {
                    available = attitude.Available, pitch = attitude.PitchDegrees, roll = attitude.RollDegrees,
                    heading = attitude.HeadingDegrees, headingValid = attitude.HeadingValid,
                    rollValid = attitude.RollValid,
                },
                scale, strokePx = MathF.Max(1f, JetVectorPainter.ReferenceStroke * scale),
                // P1038b — the measured cap height the icon/size rules use (the small font's
                // own line height at this frame's scale).
                capHeight = _jetAudit!.Painter.Measure("X", large: false, scale).Y,
                layout = audit,
                // P1039 — the placement the renderer committed this frame (the furniture it
                // avoided, the two readout blocks and the caution/scope rectangles), so the
                // priority order is evidence, not an assertion.
                placement = new
                {
                    targetReadout = Box(_jetAudit!.TargetReadoutBox),
                    // P1039c — the designator readout's own rows (the fallback block prints only
                    // what the pane lacks; the pane's readings are not repeated).
                    targetReadoutTexts = _jetAuditSurface!.TextsInside(_jetAudit.TargetReadoutBox),
                    lockReadout = Box(_jetAudit.LockReadoutBox),
                    targetSlot = _jetAudit.TargetReadoutSlot,
                    lockSlot = _jetAudit.LockReadoutSlot,
                    cautionPanel = Box(_jetAudit.CautionPanel),
                    radarScope = Box(_jetAudit.RadarScope),
                    furniture = Furniture(),
                },
                // P1039b — the radar's placement pass measured for this scene: how many labels
                // were placed, shortened or hidden, the never-hidden overflow (must be 0), the
                // dropped marker words, and the four band values the scope drew.
                radar = _jetAudit.RadarScope.Width > 0f ? new
                {
                    scope = Box(_jetAudit.RadarScope),
                    scopeRange = JetRadar.ScopeRange(model.Context.Contacts),
                    bands = Bands(model.Context.Contacts),
                    placed = _jetAudit.RadarLabelsPlaced,
                    shortened = _jetAudit.RadarLabelsShortened,
                    hidden = _jetAudit.RadarLabelsHidden,
                    overflows = _jetAudit.RadarLabelOverflows,
                    priorityMisses = _jetAudit.RadarPriorityLabelMisses,
                    wordsDropped = _jetAudit.RadarWordsDropped,
                    priorityHidden = _jetAudit.RadarPriorityHidden,
                    // P1039c — the scope's own budget (pixels per label) and whether a
                    // non-priority label may keep its full multi-row form at this scope size.
                    budget = _jetAudit.RadarLabelBudget,
                    fullFormAllowed = _jetAudit.RadarFullFormAllowed,
                    plates = _jetAudit.RadarPlates,
                    labels = Labels(_jetAudit),
                } : null,
                // P1039c — the compact annunciator row's measured ink box and the word gap it
                // was drawn with (the `AUTOVLOCK FLAG` defect's evidence).
                annunciatorRow = Box(_jetAudit!.AnnunciatorRow),
                annunciatorWordGap = _jetAudit!.AnnunciatorGap,
                // P1040b — the elements this scene's model makes eligible and the ones the
                // renderer actually drew (the `ElementMissing` class: an eligible survival/
                // critical element left undrawn is a counted failure).
                elements = new
                {
                    eligible,
                    drawn = _jetAuditSurface!.DrawnIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    missing,
                },
                // P1040 — the console/chat/lifecycle boxes the renderer committed this frame,
                // so the placement is evidence rather than an assertion.
                console = new
                {
                    chatPane = Box(_jetAudit!.ChatPane),
                    consoleColumn = Box(_jetAudit.ConsoleColumn),
                    // P1040b — the console column's own rows (the lifepod block is survived first:
                    // the evidence that it was really drawn, not merely gated).
                    columnRows = _jetAuditSurface!.TextsInside(_jetAudit.ConsoleColumn),
                    vote = Box(_jetAudit.VoteBox),
                    compose = Box(_jetAudit.ComposeBox),
                    commandPanel = Box(_jetAudit.CommandPanel),
                    lifepod = Box(_jetAudit.LifepodBox),
                    lifepodDrawable = _jetAudit.LifepodDrawable,
                    hintSlot = _jetAudit.HintSlotIndex,
                    // P1040b — the CMD panel's cells: every drawn cell word must be INSIDE its own
                    // cell (the native hit rectangle), so the evidence is the leak count per cell.
                    commandCells = CommandCells(_jetAudit!, _jetAuditSurface!, model.Context.CommandWing),
                    // P1040b — the bottom band and the diagnostic rows at this frame's scale.
                    hint = Box(_jetAudit.HintBox),
                    hintClear = _jetAudit.HintClear,
                    // P1040c — the shared training callout's measured text block and the lifepod
                    // block's own rows (the word-gap evidence).
                    callout = Box(_jetAudit.TrainingCalloutBox),
                    podRows = _jetAuditSurface!.RunsInside(_jetAudit.LifepodBox),
                    tips = Box(_jetAudit.TipBlock),
                    tipRows = _jetAuditSurface!.TextsInside(_jetAudit.TipBlock),
                    cargo = Box(_jetAudit.CargoBox),
                    flightParts = Box(_jetAudit.FlightPartsBox),
                    cargoTexts = _jetAuditSurface!.TextsInside(_jetAudit.CargoBox),
                    flightPartsTexts = _jetAuditSurface!.TextsInside(_jetAudit.FlightPartsBox),
                    chatLines = model.Context.Chat?.Lines.Count ?? 0,
                    chatOffset = model.Context.Chat?.Offset ?? 0,
                    composeState = model.Context.Chat?.ComposeState.ToString(),
                    composeProgress = model.Context.Chat?.ComposeProgress ?? 0f,
                    paneLines = model.Context.Chat?.PaneLines ?? 0,
                    missionClock = model.Context.MissionClockSeconds,
                    missionCountdown = model.Context.MissionCountdown,
                    commandSlide = model.Context.CommandPaneSlide,
                    commandView = model.Context.CommandView,
                },
                tag = _jet.TagLabel,
                // P1041 — the amber variant's own evidence for this scene: its PNG,
                // its audit, its off-palette count, the number of geometry differences
                // from green (tag text excluded: the item-5 label) and the two tag runs.
                palette = new
                {
                    green = _jet.Palette,
                    amber = _jetAmber?.Palette,
                    png = $"{stem}-amber.png",
                    greenOffPalette,
                    amberOffPalette,
                    geometryDiffs,
                    tagGreen,
                    tagAmber,
                    layout = amberAudit,
                },
            });
        }
        // P1039 — the Jet radar's draw cost against the Modern radar on the SAME dense scene
        // (66 contacts, labels at LOD active). Both renderers draw the whole frame, so the
        // numbers include the shared shadow of every other element; the radar dominates at
        // this contact count.
        if (_jet is not null)
        {
            HudModel dense = Scenario("jet-radar-dense", 1920, 1080);
            var frame = new HudDrawFrame(GraphicsDevice.Viewport, null, false);
            const int frames = 120;
            _jet.Draw(dense, frame);                     // warm-up (JIT, first allocations)
            var jetWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) _jet.Draw(dense, frame);
            jetWatch.Stop();
            var modernSettings = new HudSettings { Style = HudStyle.Modern, Size = 1f };
            using var modern = new ModernHudRenderer(_batch, _white, _small, _big, modernSettings, _modernOutline);
            modern.Draw(dense, GraphicsDevice.Viewport);
            var modernWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) modern.Draw(dense, GraphicsDevice.Viewport);
            modernWatch.Stop();
            Console.WriteLine($"HUD2_JET_RADAR_COST scene=jet-radar-dense 1920x1080 contacts={dense.Context.Contacts.Count} "
                + $"jet={jetWatch.Elapsed.TotalMilliseconds / frames:0.000} ms/frame "
                + $"modern={modernWatch.Elapsed.TotalMilliseconds / frames:0.000} ms/frame ratio="
                + $"{jetWatch.Elapsed.TotalMilliseconds / Math.Max(1e-6, modernWatch.Elapsed.TotalMilliseconds):0.00}");
        }
        File.WriteAllText(Path.Combine(root, "jet-capture.json"),
            JsonSerializer.Serialize(new { schema = 10, skin = _jetDefinition.Id,
                palette = _jetDefinition.Palette, amberPalette = _amberDefinition?.Palette, scenes = summaries }, PrettyJson));
        // P1038b/c — the layout classes must be 0 in every Jet scene; print the offending
        // counts so a regression is visible in the run log, not only in JSON.
        int strokeHits = 0, outsidePlate = 0, offScreen = 0, smallIcons = 0;
        int inPlate = 0, cueHits = 0, legible = 0, cross = 0, same = 0, overGlyph = 0, strokeText = 0, plates = 0;
        int calloutCrossed = 0;
        var calloutScenes = new List<string>();
        int radarPlaced = 0, radarShortened = 0, radarHidden = 0, radarOverflow = 0, radarMisses = 0, radarWords = 0;
        int radarPriorityHidden = 0;
        foreach (var scene in summaries)
        {
            var layout = (LayoutAudit)scene.GetType().GetProperty("layout")!.GetValue(scene)!;
            strokeHits += layout.StrokeHits;
            outsidePlate += layout.TextOutsidePlate;
            offScreen += layout.TextOffScreen;
            smallIcons += layout.IconTooSmall;
            inPlate += layout.TextOverlapInPlate;
            cueHits += layout.CueReadoutHits;
            legible += layout.LegibleBehindPane;
            cross += layout.TextOverlapCross;
            same += layout.TextOverlapSame;
            overGlyph += layout.TextOverGlyph;
            strokeText += layout.StrokeThroughText;
            plates += layout.RadarPlates;
            calloutCrossed += layout.CalloutCrossed;
            if (layout.CalloutCrossed > 0)
                calloutScenes.Add($"{scene.GetType().GetProperty("scene")!.GetValue(scene)}:{layout.CalloutCrossed}");
            object? radar = scene.GetType().GetProperty("radar")!.GetValue(scene);
            if (radar is null) continue;
            var type = radar.GetType();
            radarPlaced += (int)type.GetProperty("placed")!.GetValue(radar)!;
            radarShortened += (int)type.GetProperty("shortened")!.GetValue(radar)!;
            radarHidden += (int)type.GetProperty("hidden")!.GetValue(radar)!;
            radarOverflow += (int)type.GetProperty("overflows")!.GetValue(radar)!;
            radarMisses += (int)type.GetProperty("priorityMisses")!.GetValue(radar)!;
            radarWords += (int)type.GetProperty("wordsDropped")!.GetValue(radar)!;
            radarPriorityHidden += (int)type.GetProperty("priorityHidden")!.GetValue(radar)!;
        }
        Console.WriteLine($"HUD2_JET_AUDIT stroke-vs-text {strokeHits}, text-outside-plate {outsidePlate}, "
            + $"text-off-screen {offScreen}, icon-too-small {smallIcons}, text-in-plate {inPlate}, "
            + $"cue-vs-readout {cueHits}, legible-behind-pane {legible}, text-overlap-cross {cross}, "
            + $"text-overlap-same {same} over {scenes.Length} scenes");
        Console.WriteLine($"HUD2_JET_RADAR_STROKES text-over-glyph {overGlyph}, stroke-through-text {strokeText}, "
            + $"backing plates {plates} over {scenes.Length} scenes");
        Console.WriteLine($"HUD2_JET_RADAR_LABELS placed {radarPlaced}, shortened {radarShortened}, "
            + $"hidden {radarHidden}, priority hidden {radarPriorityHidden}, never-hidden overflows {radarOverflow}, "
            + $"priority misses {radarMisses}, words dropped {radarWords} over {scenes.Length} scenes");
        // P1040b — the two new classes: an eligible survival/critical element that was not
        // drawn, and a controls hint that could not find a clear slot (both must be 0).
        Console.WriteLine($"HUD2_JET_ELEMENTS element-missing {missingTotal} over {scenes.Length} scenes"
            + (missingScenes.Count > 0 ? " [" + string.Join(", ", missingScenes) + "]" : ""));
        Console.WriteLine($"HUD2_JET_CONSOLE hint-blocked {hintBlocked}, cmd-cell leaks {cellLeaks} over {scenes.Length} scenes");
        // P1040c — Jet furniture drawn across a shared training callout's ink (must be 0).
        Console.WriteLine($"HUD2_JET_CALLOUT callout-crossed {calloutCrossed} over {scenes.Length} scenes"
            + (calloutScenes.Count > 0 ? " [" + string.Join(", ", calloutScenes) + "]" : ""));
        // P1041 — the variant evidence: no colour outside each palette (plus the scene's
        // native side-identity colour), identical geometry green vs amber, and the amber
        // run's own layout classes all 0.
        Console.WriteLine($"HUD2_JET_PALETTES green off-palette {greenOff}, amber off-palette {amberOff} over {scenes.Length} scenes"
            + (greenOffenders.Count > 0 ? " green offenders [" + string.Join(",", greenOffenders) + "]" : "")
            + (amberOffenders.Count > 0 ? " amber offenders [" + string.Join(",", amberOffenders) + "]" : ""));
        Console.WriteLine($"HUD2_JET_GEOMETRY green-vs-amber diffs {geometryTotal} over {scenes.Length} scenes"
            + " (the WIP tag's own text/box excluded: item 5's label)"
            + (geometryScenes.Count > 0 ? " [" + string.Join(", ", geometryScenes) + "]" : ""));
        Console.WriteLine($"HUD2_JET_AMBER_AUDIT stroke-vs-text {amberStroke}, text-outside-plate {amberOutside}, "
            + $"text-off-screen {amberOffScreen}, icon-too-small {amberSmall}, text-in-plate {amberInPlate}, "
            + $"cue-vs-readout {amberCue}, legible-behind-pane {amberLegible}, text-overlap-cross {amberCross}, "
            + $"text-overlap-same {amberSame}, text-over-glyph {amberOverGlyph}, stroke-through-text {amberStrokeText}, "
            + $"radar plates {amberPlates}, callout-crossed {amberCallout} over {scenes.Length} scenes");
        Console.WriteLine($"HUD2_JET_CAPTURE_PASS {scenes.Length} scenes -> {root}");
        bool amberClean = _jetAmber is null
            || (amberOff == 0 && geometryTotal == 0 && amberStroke == 0 && amberOutside == 0 && amberOffScreen == 0
                && amberSmall == 0 && amberInPlate == 0 && amberCue == 0 && amberLegible == 0 && amberCross == 0
                && amberSame == 0 && amberOverGlyph == 0 && amberStrokeText == 0 && amberCallout == 0);
        return missingTotal == 0 && hintBlocked == 0 && cellLeaks == 0 && calloutCrossed == 0
            && greenOff == 0 && amberClean ? 0 : 1;
    }

    /// <summary>
    /// P1041 — the palette audit's allowed set (P972 §4.1). A submitted colour is on
    /// the palette when it is a role's exact colour, a documented alpha shade of a
    /// role (the renderer's own brightness steps: unlit bar 0.35/0.3, ring 0.55,
    /// leader 0.8/label 0.75, plate backing 0.82/0.85/0.97, tip rows 0.75/0.9 over
    /// the side colour), a point on the Dim→Hot ballistic-lead ramp, or the scene's
    /// native side-identity colour (consoledata.cpp:99-107, dialog.mdl:26,4722) with
    /// its two text brightness steps. Anything else is an off-palette leftover.
    /// </summary>
    private static bool PaletteAllows(HudSkinPalette palette, Color c, Color? side)
    {
        foreach (string hex in new[] { palette.Primary, palette.Dim, palette.Hot, palette.Plate, palette.Keyline })
        {
            if (!HudSkinColor.TryParseHex(hex, out var role)) continue;
            if (Near(c, role.R, role.G, role.B)) return true;
            if (ShadeOf(c, role)) return true;
        }
        if (HudSkinColor.TryParseHex(palette.Dim, out var dim) && HudSkinColor.TryParseHex(palette.Hot, out var hot))
            for (int i = 0; i <= 255; i++)
            {
                float t = i / 255f;
                if (Near(c, (int)(dim.R + (hot.R - dim.R) * t), (int)(dim.G + (hot.G - dim.G) * t),
                    (int)(dim.B + (hot.B - dim.B) * t))) return true;
            }
        if (side is { } s)
        {
            if (Near(c, s.R, s.G, s.B)) return true;
            if (ShadeOf(c, new HudSkinColor(s.R, s.G, s.B))) return true;
        }
        return false;
    }

    /// <summary>A brightness step of a palette role: <c>Color * f</c> with one factor in
    /// [0.24, 1.02] (the renderer's plate backings 0.62/0.72/0.82/0.85/0.97, the radar
    /// ring/leader/label steps, the unlit bar 0.3/0.35, the tip rows 0.675/0.75/0.9 and
    /// the continuous radar range-fade factor 0.25…1).</summary>
    private static bool ShadeOf(Color c, HudSkinColor role)
    {
        int max = Math.Max(role.R, Math.Max(role.G, role.B));
        if (max == 0) return c.R == 0 && c.G == 0 && c.B == 0;
        float f = 0f;
        bool any = false;
        int count = 0;
        if (role.R == max) { f += c.R / (float)role.R; count++; any = true; }
        if (role.G == max) { f += c.G / (float)role.G; count++; any = true; }
        if (role.B == max) { f += c.B / (float)role.B; count++; any = true; }
        if (!any) return false;
        f /= count;
        if (f < 0.24f || f > 1.02f) return false;
        return Math.Abs(c.R - role.R * f) <= 2 && Math.Abs(c.G - role.G * f) <= 2
            && Math.Abs(c.B - role.B * f) <= 2;
    }

    /// <summary>Colour equality with one byte of slack for the renderer's own truncation.</summary>
    private static bool Near(Color c, int r, int g, int b) =>
        Math.Abs(c.R - r) <= 1 && Math.Abs(c.G - g) <= 1 && Math.Abs(c.B - b) <= 1;

    /// <summary>P1041 — the number of differing lines between two geometry signatures
    /// (a length change counts the surplus lines).</summary>
    private static int GeometryDiff(string[] green, string[] amber)
    {
        int diffs = Math.Abs(green.Length - amber.Length);
        int shared = Math.Min(green.Length, amber.Length);
        for (int i = 0; i < shared; i++)
            if (!string.Equals(green[i], amber[i], StringComparison.Ordinal)) diffs++;
        return diffs;
    }

    /// <summary>
    /// P1040b — the elements this scene's model makes ELIGIBLE for the `ElementMissing`
    /// class: the survival/critical elements whose shared-model gate is true, restricted to
    /// the master-caution panel's own selected set (priority order + the stated line limit),
    /// so the comparison is "what must be on screen" against "what the renderer reported as
    /// committed", never a claim about an element the panel legitimately does not show.
    /// </summary>
    private static string[] EligibleElements(JetHudRenderer renderer, HudModel model, int height)
    {
        var eligible = new List<string>(6);
        if (model.PodVisible) eligible.Add("lifepod");
        // The caution panel keeps its own native gate (JetHudRenderer.DrawCaution returns
        // unless the cockpit is visible), so nothing is eligible from it when it cannot draw.
        if (model.FlightVisible)
        {
            Span<JetCaution> buffer = new JetCaution[JetCautionModel.MaxCautions];
            int count = JetCautionModel.Build(model, buffer);
            bool podDrawn = renderer.LifepodDrawable;
            int limit = JetCautionModel.LineLimit(JetStoresPanel.Compact(height));
            int selected = 0;
            for (int i = 0; i < count && selected < limit; i++)
            {
                JetCaution caution = buffer[i];
                if (caution.Kind == JetCautionKind.Vote) continue;
                if (caution.Kind == JetCautionKind.PodOxygen && podDrawn) continue;
                selected++;
                if (caution.Kind == JetCautionKind.Ghost) eligible.Add("death");
                else if (caution.Kind == JetCautionKind.MissileTrack) eligible.Add("missile");
            }
            if (selected > 0) eligible.Add("caution");
        }
        if (model.Context.Console?.VoteText is { Length: > 0 } && model.Visible) eligible.Add("vote");
        return eligible.ToArray();
    }

    /// <summary>
    /// P1040b — the CMD panel's drawn cells at panel-absolute coordinates, with the number of
    /// runs that leak past each cell's border and the wing cell's own runs. Every cell is the
    /// native <see cref="CommandBarLayout"/> rectangle the hit test resolves; the fit rule keeps
    /// the words inside them.
    /// </summary>
    /// <summary>P1040b — the last CMD cell-leak detail (evidence for the report).</summary>
    private static string[] _cmdLeakDetail = Array.Empty<string>();

    private static object? CommandCells(JetHudRenderer renderer, RecordingSurface surface, int wingIndex)
    {
        RectangleF panel = renderer.CommandPanel;
        if (panel.Width <= 0f || panel.Height <= 0f) return null;
        RectangleF Abs(RectangleF cell) => new(panel.X + cell.X, panel.Y + cell.Y, cell.Width, cell.Height);
        RectangleF wing = Abs(JetCommandConsole.WingCell);
        return new
        {
            panel = new { x = panel.X, y = panel.Y, w = panel.Width, h = panel.Height },
            wingCell = new { x = wing.X, y = wing.Y, w = wing.Width, h = wing.Height },
            wingRuns = surface.RunsInside(wing),
            leaks = CellLeaks(renderer, surface, wingIndex),
            leakDetail = _cmdLeakDetail,
        };
    }

    /// <summary>P1040b — the number of drawn cell words that leave their cell (must be 0).
    /// Only the runs whose text IS that cell's word and whose anchor lies in the cell are
    /// tested: the native wing combo overlaps the verb bar's band, so a neighbour's run that
    /// merely touches another cell's rectangle is not this cell's word.</summary>
    private static int CellLeaks(JetHudRenderer renderer, RecordingSurface surface, int wingIndex)
    {
        RectangleF panel = renderer.CommandPanel;
        if (panel.Width <= 0f || panel.Height <= 0f) return 0;
        var detail = new List<string>();
        int leaks = 0;

        void Check(string word, string name, RectangleF cell)
        {
            foreach ((float x, float y, float w, float h) in surface.RunsWithText(word))
            {
                if (x < cell.X - 2f || x > cell.X + cell.Width) continue;
                if (y < cell.Y - 2f || y > cell.Y + cell.Height) continue;
                bool inside = x >= cell.X - 1.5f && x + w <= cell.X + cell.Width + 1.5f
                    && y >= cell.Y - 1.5f && y + h <= cell.Y + cell.Height + 1.5f;
                if (inside) continue;
                leaks++;
                detail.Add($"{name}:{word}@{x:0.#},{y:0.#} {w:0.#}x{h:0.#} cell {cell.X:0.#},{cell.Y:0.#} {cell.Width:0.#}x{cell.Height:0.#}");
            }
        }

        RectangleF Abs(RectangleF cell) => new(panel.X + cell.X, panel.Y + cell.Y, cell.Width, cell.Height);
        for (int c = 0; c < JetCommandConsole.RecipientCount; c++)
            Check(JetCommandConsole.RecipientWord(c), "recipient" + c, Abs(JetCommandConsole.RecipientCell(c)));
        {
            RectangleF wing = Abs(JetCommandConsole.WingCell);
            Check("WING", "wing", wing);
            Check(CommandBarLayout.WingNames[Math.Clamp(wingIndex, 0,
                CommandBarLayout.WingNames.Length - 1)].ToUpperInvariant(), "wing", wing);
        }
        for (int c = 0; c < JetCommandConsole.VerbCount; c++)
            Check(JetCommandConsole.VerbWord(c), "verb" + c, Abs(JetCommandConsole.VerbCell(c)));
        _cmdLeakDetail = detail.ToArray();
        return leaks;
    }

    private sealed record RenderResult(
        Color[] Pixels, string PixelsSha256, string OutsideSha256, int AnimationPixels,
        IReadOnlyList<RectRecord> Mask, string SnapshotJson, string SnapshotSha256, Snapshot Snapshot);

    private RenderResult RenderCombo(Combo combo, RenderTarget2D target, Color[] pixels, Color backdrop)
    {
        var settings = new HudSettings { Style = combo.Style == "Modern" ? HudStyle.Modern : HudStyle.Classic, Size = combo.Size };
        settings.ColorBlindSafe = combo.Size == 1.5f;
        var model = Scenario(combo.Scenario, combo.Width, combo.Height);
        var phase = Stopwatch.StartNew();
        void Phase(string label) { if (_timing) Console.WriteLine($"HUD2_TIMING {combo.Id} {label} {phase.Elapsed.TotalMilliseconds:0} ms"); phase.Restart(); }
        // ModernHudRenderer keeps per-frame animation state (the frame delta,
        // the eased gauge values and the level toast — ModernHudRenderer.cs:114-116,
        // :128-129). Reusing one instance would carry wall-clock timing from the
        // previous combination into this draw, so the fixture drops back to that
        // state's initial value before every draw. The renderer itself is
        // untouched; only its private frame cache is reset (see ResetModernFrameState).
        var renderer = Modern(combo.Style, combo.Size);
        if (renderer is ModernHudRenderer reused) ResetModernFrameState(reused);
        // P805 — the renderer reads its resolved option property while the model
        // reports the same gate from the context; the fixture sets both so a
        // combination never claims a row it does not draw.
        renderer.SelectionBracket = combo.Scenario == "target-full";
        Phase("construct");
        try
        {
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(backdrop);
            // P988: exercise the production adapter too. The P980 fixture's
            // external Classic radar/notices below retain their original order
            // (different from Game1); changing that fixture would re-bless pixels.
            Adapter(renderer).Draw(model, new(GraphicsDevice.Viewport, null, combo.Scenario == "target-full"));
            if (model.FlightVisible && renderer == _classic)
            {
                var radar = new RadarBracketRenderer(_batch, _white);
                radar.Draw(model, GraphicsDevice.Viewport);   // Classic radar + round border (production path)
            }
            // The ordinary client routes Classic notices and target-no-match
            // feedback through this same production measured-text method.
            if (model.Visible && renderer == _classic)
            {
                _batch.Begin();
                foreach (var notice in model.Context.Notices)
                    renderer.DrawMeasuredText(_small, notice.Text, new(combo.Width * .35f, combo.Height * .30f), Color.White);
                _batch.End();
            }
            GraphicsDevice.SetRenderTarget(null);
            Phase("draw");
            target.GetData(pixels);
            Phase("read");
            var findings = CheckLayout(model, renderer, combo, settings);
            var mask = AnimationMask(model, renderer, combo);
            Phase("check");
            string hash = HashPixels(pixels, combo.Width, combo.Height, null);
            string outside = HashPixels(pixels, combo.Width, combo.Height, mask);
            Phase("hash");
            int animationPixels = 0;
            foreach (var rect in mask) animationPixels += rect.W * rect.H;
            var snapshot = BuildSnapshot(model, renderer, combo, settings) with { Findings = findings };
            if (findings.Count > 0)
            {
                string key = combo.Scenario + "/" + combo.Width + "x" + combo.Height;
                if (_reportedFindings.Add(key))
                    Console.WriteLine($"HUD2_FINDING {combo.Id}: {findings[0]}" + (findings.Count > 1 ? $" (+{findings.Count - 1} more)" : ""));
            }
            // Indented so a semantic regression reads as a line diff.
            string json = JsonSerializer.Serialize(snapshot, PrettyJson);
            return new RenderResult(pixels, hash, outside, animationPixels, mask, json, Hash(json), snapshot);
        }
        finally
        {
            GraphicsDevice.SetRenderTarget(null);
        }
    }

    private readonly Dictionary<HudRenderer, IHudSkinRenderer> _adapters = new();
    private IHudSkinRenderer Adapter(HudRenderer renderer)
    {
        if (_adapters.TryGetValue(renderer, out var adapter)) return adapter;
        var services = new HudSkinResources(_ => { }, (_, _) => { }, _ => { },
            (_, _) => { }, _ => { }, _ => { }, _ => { });
        adapter = new HudSkinRenderer(renderer, services);
        _adapters.Add(renderer, adapter);
        return adapter;
    }

    // One Modern renderer per user Size: its constructor builds two SDF glyph
    // atlases from the shipped fonts (~9 s on the P980 host), so a renderer per
    // combination is not affordable. The SDF atlases are content, not
    // animation state, and are rebuilt by the renderer itself when the HUD scale
    // changes (ModernHudRenderer.PrepareLayout).
    private readonly Dictionary<float, ModernHudRenderer> _modernBySize = new();

    private HudRenderer Modern(string style, float size)
    {
        if (style != "Modern") return _classic;
        if (_modernBySize.TryGetValue(size, out var renderer)) return renderer;
        var settings = new HudSettings { Style = HudStyle.Modern, Size = size, ColorBlindSafe = size == 1.5f };
        renderer = new ModernHudRenderer(_batch, _white, _small, _big, settings, _modernOutline);
        _modernBySize[size] = renderer;
        Console.WriteLine($"HUD2_MODERN_RENDERER size {Combo.Fmt(size)} constructed");
        return renderer;
    }

    /// <summary>P980 fixture-side determinism: return ModernHudRenderer's private
    /// frame-animation cache to its constructor-time value before every draw.
    /// Fields (ModernHudRenderer.cs:17-27): _animated (eased gauge values),
    /// _previous (Stopwatch timestamp of the last draw), _dt, _levelToastRemain
    /// and _shownLevel. A rename fails loudly here instead of silently letting a
    /// wall-clock delta leak into the next combination's baseline pixels.</summary>
    private static void ResetModernFrameState(ModernHudRenderer renderer)
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(ModernHudRenderer);
        object? Field(string name) => type.GetField(name, Flags)?.GetValue(renderer)
            ?? throw new InvalidOperationException($"hud2-proof: ModernHudRenderer.{name} is gone; the fixture's per-combination animation reset must be updated (renderer unchanged by P980)");
        if (Field("_animated") is not System.Collections.IDictionary animated)
            throw new InvalidOperationException("hud2-proof: ModernHudRenderer._animated is no longer a dictionary");
        animated.Clear();
        void Set(string name, object value)
        {
            var field = type.GetField(name, Flags) ?? throw new InvalidOperationException($"hud2-proof: ModernHudRenderer.{name} is gone");
            field.SetValue(renderer, value);
        }
        Set("_previous", 0L);
        Set("_dt", 1f);                 // what the renderer itself uses on its first draw
        Set("_levelToastRemain", 0f);
        Set("_shownLevel", HudLevel.Tactical);   // the renderer's field initializer
    }

    // ── compare: re-render against a capture ─────────────────────────────────
    private int CompareRendered(string baselineDirectory, bool verifyGoldens = true)
    {
        string root = Path.GetFullPath(baselineDirectory);
        var baseline = ReadManifest(root);
        Directory.CreateDirectory(_out);
        var diffs = new List<string>();
        var animation = new List<string>();
        int compared = 0, pixelDiffs = 0, semanticDiffs = 0, animationPixelsSeen = 0, corruptGoldens = 0;
        int diffImages = 0;
        string diffDir = Path.Combine(_out, "diffs");
        var expected = baseline.Combinations.Where(c => Match(c.Id)).ToList();
        if (expected.Count == 0) { Console.Error.WriteLine("HUD2_COMPARE_MISSING no baseline combination matches the filter"); return 1; }
        Console.WriteLine($"HUD2_COMPARE baseline {baseline.Commit}{(baseline.Dirty ? "+dirty" : "")} at {root}; {expected.Count} combination(s)");
        foreach (var (width, height) in expected.Select(c => (c.Width, c.Height)).Distinct().OrderBy(r => r.Width).ThenBy(r => r.Height))
        {
            using var target = new RenderTarget2D(GraphicsDevice, width, height);
            var pixels = new Color[width * height];
            foreach (var entry in expected.Where(c => c.Width == width && c.Height == height))
            {
                var combo = new Combo(entry.Scenario, entry.Width, entry.Height, entry.Size, entry.Backdrop, entry.Style);
                var backdrop = Backdrops.First(b => b.Name == entry.Backdrop).Color;
                var result = RenderCombo(combo, target, pixels, backdrop);
                compared++;
                // The golden is the DECODED pixels, so the stored PNG must still
                // decode to the hash its manifest recorded. Without this a
                // corrupted or hand-edited golden file would silently pass.
                if (verifyGoldens && !_options.NoBaselineVerify)
                {
                    var golden = LoadPng(Path.Combine(root, entry.Png), width, height);
                    if (golden is null)
                    {
                        corruptGoldens++;
                        diffs.Add($"{entry.Id}: the baseline PNG is missing or unreadable");
                        continue;
                    }
                    if (HashPixels(golden, width, height, null) != entry.PixelsSha256
                        || HashPixels(golden, width, height, entry.AnimationMask) != entry.PixelsOutsideAnimationSha256)
                    {
                        corruptGoldens++;
                        diffs.Add($"{entry.Id}: the baseline PNG does not decode to the manifest hash (corrupt or edited golden)");
                    }
                }
                if (result.OutsideSha256 != entry.PixelsOutsideAnimationSha256)
                {
                    pixelDiffs++;
                    var (changed, maskChanged) = ComparePixelsWith(entry, root, result, width, height);
                    diffs.Add($"{entry.Id}: {changed} changed pixel(s) outside the animation mask" +
                              (maskChanged > 0 ? $", {maskChanged} inside it" : ""));
                    if (diffImages < _options.MaxDiffs)
                        WriteDiffImage(entry, root, result, width, height, diffDir);
                    diffImages++;
                }
                else if (result.PixelsSha256 != entry.PixelsSha256)
                {
                    // Only the wall-clock-animated badge moved: reported, not fatal.
                    animationPixelsSeen++;
                    animation.Add($"{entry.Id}: identical outside the animation mask; the animated badge pixels differ ({entry.AnimationPixels} px region)");
                }
                // The semantic golden is the stored snapshot FILE, checked against
                // the manifest hash: the re-render must reproduce the same visible
                // element set, text, bounds and inventory fit byte for byte.
                string snapshotPath = Path.Combine(root, entry.Snapshot);
                string goldenSnapshot = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath) : "";
                if (goldenSnapshot.Length == 0 || Hash(goldenSnapshot) != entry.SnapshotSha256)
                {
                    corruptGoldens++;
                    diffs.Add($"{entry.Id}: the baseline semantic snapshot is missing or does not match the manifest hash");
                }
                else if (result.SnapshotJson != goldenSnapshot)
                {
                    semanticDiffs++;
                    diffs.Add($"{entry.Id}: semantic snapshot differs\n" + SemanticDiff(root, entry, result.SnapshotJson));
                }
                if (!result.Mask.SequenceEqual(entry.AnimationMask))
                {
                    semanticDiffs++;
                    diffs.Add($"{entry.Id}: animation mask changed (fixture or gauge thresholds moved)");
                }
                if (compared % 100 == 0) Console.WriteLine($"HUD2_COMPARE {compared}/{expected.Count}");
            }
        }
        var report = new
        {
            schema = 1,
            baseline = root,
            baselineCommit = baseline.Commit,
            baselineDirty = baseline.Dirty,
            compared,
            pixelDifferences = pixelDiffs,
            animationOnlyDifferences = animationPixelsSeen,
            semanticDifferences = semanticDiffs,
            corruptGoldens,
            pass = pixelDiffs == 0 && semanticDiffs == 0 && corruptGoldens == 0,
            differences = diffs,
            animationOnly = animation,
        };
        File.WriteAllText(Path.Combine(_out, "compare-report.json"), JsonSerializer.Serialize(report, PrettyJson));
        foreach (string line in diffs.Take(40)) Console.WriteLine("HUD2_DIFF " + line);
        foreach (string line in animation.Take(10)) Console.WriteLine("HUD2_ANIM " + line);
        int exit = pixelDiffs == 0 && semanticDiffs == 0 && corruptGoldens == 0 ? 0 : 1;
        Console.WriteLine($"HUD2_COMPARE_{(exit == 0 ? "PASS" : "FAIL")} {compared} combinations, {pixelDiffs} pixel diff(s), " +
                          $"{semanticDiffs} semantic diff(s), {animationPixelsSeen} animation-only diff(s), {corruptGoldens} corrupt golden(s)");
        return exit;
    }

    /// <summary>Changed pixels of a re-render against a stored baseline PNG,
    /// split into outside/inside the recorded animation mask.</summary>
    private (int Changed, int MaskChanged) ComparePixelsWith(ComboEntry entry, string root, RenderResult result, int width, int height)
    {
        var baseline = LoadPng(Path.Combine(root, entry.Png), width, height);
        if (baseline is null) return (width * height, 0);
        int changed = 0, maskChanged = 0;
        var mask = entry.AnimationMask;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            if (baseline[i] == result.Pixels[i]) continue;
            if (InMask(mask, x, y)) maskChanged++; else changed++;
        }
        return (changed, maskChanged);
    }

    private void WriteDiffImage(ComboEntry entry, string root, RenderResult result, int width, int height, string diffDir)
    {
        var baseline = LoadPng(Path.Combine(root, entry.Png), width, height);
        if (baseline is null) return;
        Directory.CreateDirectory(diffDir);
        var diff = new Color[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            bool inMask = InMask(entry.AnimationMask, x, y);
            if (baseline[i] == result.Pixels[i]) diff[i] = baseline[i] * .25f;
            else diff[i] = inMask ? new Color(0, 200, 255) : new Color(255, 0, 255);
        }
        PngCodec.Encode(Path.Combine(diffDir, entry.Scenario + "__" + entry.Width + "x" + entry.Height +
            "__s" + Combo.Fmt(entry.Size) + "__" + entry.Backdrop + "__" + entry.Style + "__diff.png"), diff, width, height);
    }

    /// <summary>Golden pixels come back through the managed decoder: the golden
    /// contract is decoded RGBA, and routing 3500 4K decodes through the GPU
    /// exhausted the Intel/Mesa execbuf budget (P980 run 1).</summary>
    private static Color[]? LoadPng(string path, int width, int height)
    {
        if (!File.Exists(path)) { Console.Error.WriteLine("HUD2_COMPARE_MISSING golden " + path); return null; }
        try { return PngCodec.Decode(path, width, height); }
        catch (InvalidDataException exception) { Console.Error.WriteLine("HUD2_COMPARE_MISSING " + exception.Message); return null; }
    }

    // ── offline capture-vs-capture determinism proof ──────────────────────────
    private int DiffCaptureDirectories(string aDirectory, string bDirectory)
    {
        string aRoot = Path.GetFullPath(aDirectory), bRoot = Path.GetFullPath(bDirectory);
        string aManifestText = File.ReadAllText(Path.Combine(aRoot, "manifest.json"));
        string bManifestText = File.ReadAllText(Path.Combine(bRoot, "manifest.json"));
        var a = JsonSerializer.Deserialize<Manifest>(aManifestText, Json)!;
        var b = JsonSerializer.Deserialize<Manifest>(bManifestText, Json)!;
        var diffs = new List<string>();
        var animation = new List<string>();
        // The manifest carries both the full decoded-pixel hash and the hash
        // outside the recorded animation mask. Two captures of the same tree can
        // only differ in the masked hash of a low-resource Modern frame, so the
        // manifest comparison normalizes exactly those fields.
        bool manifestEqualRaw = aManifestText == bManifestText;
        bool manifestEqual = CanonicalManifest(a) == CanonicalManifest(b);
        if (!manifestEqualRaw && manifestEqual) Console.WriteLine("HUD2_DETERMINISM manifests identical apart from the masked animation hashes");
        if (!manifestEqual) diffs.Add("manifest.json differs between the two captures beyond the animation mask");
        int pixelDiffs = 0, semanticDiffs = 0, animationDiffs = 0, goldenCorrupt = 0;
        int compared = 0;
        var bById = b.Combinations.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var left in a.Combinations)
        {
            if (!Match(left.Id)) continue;
            if (!bById.TryGetValue(left.Id, out var right)) { pixelDiffs++; diffs.Add($"{left.Id}: missing from the second capture"); continue; }
            compared++;
            var leftPixels = LoadPng(Path.Combine(aRoot, left.Png), left.Width, left.Height);
            var rightPixels = LoadPng(Path.Combine(bRoot, right.Png), right.Width, right.Height);
            if (leftPixels is null || rightPixels is null) { goldenCorrupt++; continue; }
            // Each capture's PNG must still decode to the hash its manifest
            // recorded, so a half-written or hand-edited golden cannot pass.
            if (HashPixels(leftPixels, left.Width, left.Height, null) != left.PixelsSha256
                || HashPixels(rightPixels, right.Width, right.Height, null) != right.PixelsSha256)
            {
                goldenCorrupt++;
                diffs.Add($"{left.Id}: a capture's PNG does not decode to its own manifest hash");
                continue;
            }
            int changed = 0, maskChanged = 0;
            for (int y = 0; y < left.Height; y++)
            for (int x = 0; x < left.Width; x++)
            {
                int i = y * left.Width + x;
                if (leftPixels[i] == rightPixels[i]) continue;
                if (InMask(left.AnimationMask, x, y)) maskChanged++; else changed++;
            }
            if (changed > 0)
            {
                pixelDiffs++;
                diffs.Add($"{left.Id}: {changed} changed pixel(s) outside the animation mask ({maskChanged} inside it)");
            }
            else if (maskChanged > 0)
            {
                animationDiffs++;
                animation.Add($"{left.Id}: {maskChanged} animated badge pixel(s) differ inside the recorded mask (outside: 0)");
            }
            if (left.SnapshotSha256 != right.SnapshotSha256) { semanticDiffs++; diffs.Add($"{left.Id}: semantic snapshot differs"); }
            if (compared % 200 == 0) Console.WriteLine($"HUD2_DETERMINISM {compared}/{a.Combinations.Count}");
        }
        var report = new
        {
            schema = 1,
            first = aRoot,
            second = bRoot,
            manifestEqual,
            compared,
            pixelDifferences = pixelDiffs,
            animationOnlyDifferences = animationDiffs,
            semanticDifferences = semanticDiffs,
            corruptGoldens = goldenCorrupt,
            manifestEqualRaw,
            manifestEqualApartFromAnimation = manifestEqual,
            pass = pixelDiffs == 0 && semanticDiffs == 0 && goldenCorrupt == 0 && manifestEqual,
            differences = diffs,
            animationOnly = animation,
        };
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "determinism-report.json"), JsonSerializer.Serialize(report, PrettyJson));
        foreach (string line in diffs.Take(40)) Console.WriteLine("HUD2_DIFF " + line);
        foreach (string line in animation.Take(10)) Console.WriteLine("HUD2_ANIM " + line);
        int exit = report.pass ? 0 : 1;
        Console.WriteLine($"HUD2_DETERMINISM_{(exit == 0 ? "PASS" : "FAIL")} {compared} combinations: {pixelDiffs} pixel diff(s), " +
                          $"{semanticDiffs} semantic diff(s), {animationDiffs} animation-only diff(s), manifest equal={manifestEqual}");
        return exit;
    }

    /// <summary>The manifest with each masked combination's full-pixel hash
    /// replaced by its outside-mask hash, so two captures differing only in the
    /// documented wall-clock badge compare equal.</summary>
    private static string CanonicalManifest(Manifest manifest)
    {
        var normalized = manifest with
        {
            Combinations = manifest.Combinations.Select(c => c.AnimationMask.Count == 0 ? c
                : c with { PixelsSha256 = c.PixelsOutsideAnimationSha256 }).ToList(),
        };
        return JsonSerializer.Serialize(normalized, Json);
    }

    // ── compare self-test ────────────────────────────────────────────────────
    private void RunSelfTest(string baselineDirectory)
    {
        string source = Path.GetFullPath(baselineDirectory);
        var baseline = ReadManifest(source);
        var entry = baseline.Combinations.FirstOrDefault(c => Match(c.Id) && c.Width <= 1920 && c.Style == "Classic" && c.Backdrop == "dark")
                    ?? throw new InvalidOperationException("no baseline combination available for the self-test");
        string work = Path.Combine(_out, "baseline-copy");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(Path.Combine(work, "png"));
        Directory.CreateDirectory(Path.Combine(work, "snapshots"));
        string manifestPath = Path.Combine(work, "manifest.json");
        string pngPath = Path.Combine(work, entry.Png), snapPath = Path.Combine(work, entry.Snapshot);
        File.Copy(Path.Combine(source, entry.Png), pngPath, true);
        File.Copy(Path.Combine(source, entry.Snapshot), snapPath, true);
        void WriteManifest(ComboEntry current)
            => File.WriteAllText(manifestPath, JsonSerializer.Serialize(baseline with { Combinations = new[] { current } }, Json));
        WriteManifest(entry);
        _optionsFilter = entry.Id;
        Console.WriteLine($"HUD2_SELF_TEST combination {entry.Id}");

        // 1. the untouched copy passes.
        Expect("clean baseline", CompareRendered(work), expectedExit: 0);

        // 2. a hand-edited golden PNG (manifest untouched) is caught by the
        //    decoded-pixel integrity check.
        var pixels = LoadPng(pngPath, entry.Width, entry.Height)!;
        int changedIndex = 2 * entry.Width + 2;
        var original = pixels[changedIndex];
        pixels[changedIndex] = original == new Color(255, 0, 255) ? Color.Lime : new Color(255, 0, 255);
        SavePng(pngPath, pixels, entry.Width, entry.Height);
        Expect("edited golden file", CompareRendered(work), expectedExit: 1);
        RequireReport("corrupt golden", "\"corruptGoldens\": 1");

        // 3. the same one-pixel change, but with the manifest re-signed so the
        //    file is "a self-consistent baseline whose frame changed by 1 px":
        //    exactly like a renderer regression. The pixel comparison must report
        //    exactly one changed pixel outside the animation mask and write a
        //    diff image for it.
        var changed = (ComboEntry)entry with
        {
            PixelsSha256 = HashPixels(pixels, entry.Width, entry.Height, null),
            PixelsOutsideAnimationSha256 = HashPixels(pixels, entry.Width, entry.Height, entry.AnimationMask),
        };
        WriteManifest(changed);
        Expect("one changed pixel", CompareRendered(work), expectedExit: 1);
        RequireReport("one changed pixel", "1 changed pixel(s) outside the animation mask");
        if (!Directory.Exists(Path.Combine(_out, "diffs")) || Directory.GetFiles(Path.Combine(_out, "diffs"), "*__diff.png").Length == 0)
            throw new InvalidOperationException("self-test: no diff image was written for the changed combination");

        // 4. the comparison is deterministic and passes once the golden is restored.
        pixels[changedIndex] = original;
        SavePng(pngPath, pixels, entry.Width, entry.Height);
        WriteManifest(entry);
        Expect("restored golden", CompareRendered(work), expectedExit: 0);

        // 5. a changed semantic snapshot (one text string) is detected even when
        //    every pixel is identical.
        string snapshotText = File.ReadAllText(snapPath);
        string mutatedText = snapshotText.Replace("\"Style\": \"" + entry.Style + "\"",
            "\"Style\": \"" + entry.Style + "-edited\"", StringComparison.Ordinal);
        var mutated = JsonSerializer.Deserialize<Snapshot>(mutatedText, Json)!;
        if (mutated.Style == entry.Style) throw new InvalidOperationException("self-test could not mutate the semantic snapshot");
        File.WriteAllText(snapPath, mutatedText);
        // 5a. the edited snapshot is caught by the manifest hash…
        Expect("edited semantic snapshot", CompareRendered(work), expectedExit: 1);
        RequireReport("edited semantic snapshot", "does not match the manifest hash");
        // 5b. …and with the manifest re-signed it is caught by the content diff.
        WriteManifest(entry with { SnapshotSha256 = Hash(mutatedText) });
        Expect("changed semantic snapshot", CompareRendered(work), expectedExit: 1);
        RequireReport("semantic change", "semantic snapshot differs");
        File.WriteAllText(snapPath, snapshotText);
        WriteManifest(entry);

        // 6. back to the untouched baseline.
        Expect("restored baseline", CompareRendered(work), expectedExit: 0);
        _optionsFilter = null;
        Console.WriteLine("HUD2_SELF_TEST clean pass, edited golden caught, 1 changed pixel counted with a diff image, semantic change detected, restored baseline passes");

        static void Expect(string step, int actual, int expectedExit)
        {
            if (actual != expectedExit)
                throw new InvalidOperationException($"self-test '{step}': compare exited {actual}, expected {expectedExit}");
            Console.WriteLine($"HUD2_SELF_TEST_STEP {step}: exit {actual} as expected");
        }
        void RequireReport(string step, string expected)
        {
            string report = File.ReadAllText(Path.Combine(_out, "compare-report.json"));
            if (!report.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"self-test '{step}': compare report does not contain '{expected}':\n" + report);
        }
    }

    /// <summary>Set by the self-test to pin one combination; otherwise --filter.</summary>
    private string? _optionsFilter;
    private bool Match(string id) => (_optionsFilter ?? _options.Filter) is not { } filter || id.Contains(filter, StringComparison.Ordinal);

    private static void SavePng(string path, Color[] pixels, int width, int height)
        => PngCodec.Encode(path, pixels, width, height);

    // ── legacy render pass (the HUD-2 assertion harness) ─────────────────────
    private int RenderLegacy()
    {
        var started = Stopwatch.StartNew();
        int done = 0, total = Combos().Count;
        string sampleDir = Path.Combine(_out, "samples");
        foreach (var (width, height) in Resolutions)
        {
            using var target = new RenderTarget2D(GraphicsDevice, width, height);
            var pixels = new Color[width * height];
            foreach (float size in Sizes)
            foreach (var (backdropName, backdropColor) in Backdrops)
            foreach (string scenario in Scenarios)
            foreach (string style in Styles)
            {
                var combo = new Combo(scenario, width, height, size, backdropName, style);
                if (!Match(combo.Id)) continue;
                var result = RenderCombo(combo, target, pixels, backdropColor);
                if (_options.Samples && !_options.Test && combo is { Size: 1, Backdrop: "dark" }
                    && (scenario is "critical" or "combat" or "inventory" || scenario.StartsWith("radar-")) && width >= 1920)
                {
                    Directory.CreateDirectory(sampleDir);
                    using var file = File.Create(Path.Combine(sampleDir, combo.Stem + ".png"));
                    target.SaveAsPng(file, width, height);
                }
                _summaries.Add(new
                {
                    id = combo.Id, scenario, width, height, size, colorBlindSafe = size == 1.5f, style,
                    boxes = result.Snapshot.TextBoxes.Count,
                    visible = result.Snapshot.Elements.Count(e => e.Visible),
                });
                if (++done % 200 == 0) Console.WriteLine($"HUD2_RENDER {done}/{total} {started.Elapsed.TotalSeconds:0}s");
            }
        }
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "render-checks.json"), JsonSerializer.Serialize(_summaries, Json));
        Console.WriteLine($"HUD2_RENDER_PASS {_summaries.Count} rendered scenarios; real font bounds, no overlap, all visible elements");
        return 0;
    }

    // ── the shipped HUD-2 assertions (unchanged semantics) ───────────────────
    private readonly HashSet<string> _reportedFindings = new(StringComparer.Ordinal);

    /// <summary>The P980 scenarios added to the matrix (the original HUD-2 set
    /// keeps its strict assertions).</summary>
    private static readonly HashSet<string> P980Scenarios = new(StringComparer.Ordinal)
    {
        "training", "ghost", "commandee", "target-full", "slide", "radar-overlays",
    };

    /// <summary>Every HUD-2 assertion, plus the P980 findings policy: a text
    /// OVERLAP at the newly added 800×480 floor, or in a newly added scenario, is
    /// recorded in the semantic snapshot instead of aborting the capture. The
    /// golden has to hold today's real output and src/ is out of scope for this
    /// slice, so the finding is reported for the slice that fixes layout rather
    /// than hidden or blessed silently. Text outside the viewport, the inventory
    /// column fit and Modern element coverage stay fatal at every resolution.</summary>
    private IReadOnlyList<string> CheckLayout(HudModel model, HudRenderer renderer, Combo combo, HudSettings settings)
    {
        var findings = new List<string>();
        bool overlapIsFatal = combo.Width >= 1280 && !P980Scenarios.Contains(combo.Scenario);
        var boxes = renderer.Layout.TextBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            Require(boxes[i].Bounds.Left >= 0 && boxes[i].Bounds.Right <= combo.Width && boxes[i].Bounds.Top >= 0 && boxes[i].Bounds.Bottom <= combo.Height, "text outside viewport");
            for (int j = i + 1; j < boxes.Count; j++)
            {
                // Native console anchors (label + value 14 px apart) may
                // overlap in the taller port font; that is intentional
                // (HUD-9b fixedAnchor), so only overlap-check them
                // against non-anchored text.
                if (boxes[i].FixedAnchor && boxes[j].FixedAnchor) continue;
                bool overlap = boxes[i].Bounds.Intersects(boxes[j].Bounds);
                if (!overlap) continue;
                string message = $"{combo.Id} overlap: {boxes[i]} / {boxes[j]}";
                if (overlapIsFatal) Require(false, message);
                findings.Add(message);
            }
        }
        // HUD-9b — pin the fixed console-text anchors (top-edge based,
        // not bottom-based) at the reference 1920×1080/scale-1 render.
        if (combo is { Size: 1, Width: 1920 } && combo.Scenario is "combat" or "critical")
        {
            RequireBox(boxes, "Current location:", 37);
            // HUD-11 — the port font line height (22 px), not native's
            // raw 14 px, separates label and value (dialog.mdl:1473-1485).
            RequireBox(boxes, "Alystra", 59);
            if (combo.Scenario == "combat")
            {
                RequireBox(boxes, "Current orders:", 37);
                // FONT-1c fix 4 — the order rows are the native
                // Point(0,-20)/(0,-40) = +20/+40 px below the label
                // (dialog.mdl:1641-1652), not a measured line height.
                RequireBox(boxes, "attack Enemy fighter", 57);
                RequireBox(boxes, "defend Garrison", 77);
            }
            // HUD-11 — critical-tip offset uses configured NumChatLines (7): 50+12*7.
            RequireBox(boxes, "Reloading ammo...", 134);
            if (combo.Scenario == "critical")
                // HUD-11 — configured NumChatLines (7) drives the vote
                // offset even though only two lines are shown.
                RequireBox(boxes, "Vote: Mutiny against the commander? (Y/N)", 169);
            // FONT-1c fix 5 — JustifyCenter inside the 150-wide box
            // (dialog.mdl:5145-5151,5430): top stays 15 px above
            // centre; the measured glyph box is centred on x=960
            // (left = 960 - width/2), not left-aligned at 885.
            RequireCentredBox(boxes, "Ripcord in: 9", 525, combo.Width / 2f);
        }
        // P859 — measure the inventory columns at the SAME scale the
        // renderer applies (density 1.0 on the proof desktop, capped by
        // the window-height budget), not the pre-P833 height heuristic.
        float renderScale = renderer is ModernHudRenderer ? ModernHudScale.Core(1f, settings.Size, combo.Height) : 1f;
        RequireInventoryColumnsFit(model, renderer is ModernHudRenderer ? _modernSmall : _small, combo.Width, combo.Height, renderScale);
        bool inventoryPaneUp = model.Context.InventoryVisible && model.Context.InventoryPane is { Rows.Count: > 0 };
        if (inventoryPaneUp)
            Require(!boxes.Any(b => b.Text == "SYSTEMS"), $"{combo.Id} SYSTEMS list must hide while the inventory pane is up");
        if (combo.Scenario == "systems")
            Require(boxes.Any(b => b.Text == "SYSTEMS"), "SYSTEMS list should draw when the inventory pane is hidden");
        if (renderer is ModernHudRenderer modern)
        {
            var missing = model.Elements.Where(e => e.Visible && !modern.DrawnElements.Contains(e.Id)).Select(e => e.Id).ToArray();
            Require(missing.Length == 0, $"missing {combo.Id}: {string.Join(", ", missing)}");
            if (model.PodVisible) Require(boxes.Any(b => b.Text == model.Context.PodReturnInstruction), "pod return-to-base key instruction missing");
            if (!model.Visible) Require(boxes.Count == 0, "hidden text");
            if (model.Visible) Require(boxes.Count > 0, "blank visible frame");
        }
        return findings;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void RequireBox(IReadOnlyList<HudTextBox> boxes, string text, float top, float? left = null)
    {
        var matches = boxes.Where(b => b.Text == text).ToArray();
        Require(matches.Length > 0, $"missing '{text}'");
        if (left is { } expectedLeft)
            Require(matches.Any(b => Math.Abs(b.Bounds.Top - top) <= 2 && Math.Abs(b.Bounds.Left - expectedLeft) <= 2),
                $"{text} top/left mismatch: " + string.Join(", ", matches.Select(b => $"{b.Bounds.Top}/{b.Bounds.Left}")));
        else
            Require(matches.Any(b => Math.Abs(b.Bounds.Top - top) <= 2),
                $"{text} top mismatch: " + string.Join(", ", matches.Select(b => $"{b.Bounds.Top}")));
    }

    private static void RequireCentredBox(IReadOnlyList<HudTextBox> boxes, string text, float top, float centreX)
    {
        var matches = boxes.Where(b => b.Text == text).ToArray();
        Require(matches.Length > 0, $"missing '{text}'");
        Require(matches.Any(b => Math.Abs(b.Bounds.Top - top) <= 2
                && Math.Abs(b.Bounds.Left - (centreX - b.Bounds.Width / 2f)) <= 2
                && Math.Abs(b.Bounds.Left - (centreX - HudConsoleFormat.RipcordLeft)) > 2),
            $"{text} top/centred-left mismatch: " + string.Join(", ", matches.Select(b => $"{b.Bounds.Top}/{b.Bounds.Left}/{b.Bounds.Width}")));
    }

    /// <summary>The same sprite-font metric scaling ModernHudRenderer applies to
    /// its outline source so the column-fit assertion measures the SAME glyphs.</summary>
    private static SpriteFont Metrics(SpriteFont source, float scale)
    {
        var glyphs = source.GetGlyphs().Values.OrderBy(g => g.Character).ToArray();
        return new SpriteFont(source.Texture, glyphs.Select(g => g.BoundsInTexture).ToList(),
            glyphs.Select(g => new Rectangle((int)MathF.Round(g.Cropping.X * scale), (int)MathF.Round(g.Cropping.Y * scale), (int)MathF.Round(g.Cropping.Width * scale), (int)MathF.Round(g.Cropping.Height * scale))).ToList(),
            glyphs.Select(g => g.Character).ToList(), (int)MathF.Round(source.LineSpacing * scale), source.Spacing * scale,
            glyphs.Select(g => new Vector3(g.LeftSideBearing, g.Width, g.RightSideBearing) * scale).ToList(), source.DefaultCharacter);
    }

    /// <summary>HUD-7 — prove the inventory pane's name/stats columns clip inside
    /// the pane rectangle using the SAME public fit helper + real font metrics,
    /// independent of the renderer's internal call.</summary>
    private static InventoryFit? MeasureInventory(HudModel model, SpriteFont small, int w, int h, float renderScale, bool assert)
    {
        if (model.Context.InventoryPane is not { Rows.Count: > 0 } pane) return null;
        float s = InventoryPaneLayout.Scale(h);
        var rows = new List<InventoryRowFit>(pane.Rows.Count);
        foreach (var row in pane.Rows)
        {
            float nameMax = (InventoryPaneLayout.StatsX - InventoryPaneLayout.NameX - 2f) * s;
            string name = HudFormat.FitToWidth(row.Name, t => small.MeasureString(t).X * renderScale, nameMax);
            float nameWidth = small.MeasureString(name).X * renderScale;
            bool nameFits = nameWidth <= nameMax + .01f;
            if (assert) Require(nameFits, $"inventory name '{row.Name}' exceeds its column at {w}x{h}");
            float statsMax = (InventoryPaneLayout.LineWidth - InventoryPaneLayout.StatsX - 3f) * s;
            float statsWidth = 0, fitScale = 1;
            if (row.StatsText is { } stats)
            {
                // HUD-11 — production scales the stats font into the native
                // Point(130,5) slot instead of clipping the reading
                // (invsweapon.mdl StatsBar); assert the FULL string fits.
                statsWidth = small.MeasureString(stats).X * renderScale;
                fitScale = InventoryPaneLayout.FitFontScale(stats, statsMax, t => small.MeasureString(t).X * renderScale);
                if (assert) Require(statsWidth * fitScale <= statsMax + .01f, $"inventory stats '{stats}' exceeds the pane at {w}x{h}");
            }
            bool statsFits = row.StatsText is null || statsWidth * fitScale <= statsMax + .01f;
            rows.Add(new InventoryRowFit(row.Key, row.Name, row.StatsText, nameWidth, nameMax, nameFits,
                statsWidth, statsMax, fitScale, statsFits));
        }
        return new InventoryFit(model.FlightVisible && model.Context.InventoryVisible, s, rows, rows.All(r => r.NameFits && r.StatsFits));
    }

    private static void RequireInventoryColumnsFit(HudModel model, SpriteFont small, int w, int h, float renderScale)
        => MeasureInventory(model, small, w, h, renderScale, assert: true);

    private Snapshot BuildSnapshot(HudModel model, HudRenderer renderer, Combo combo, HudSettings settings)
    {
        var elements = new List<ElementSnapshot>(model.Elements.Count);
        foreach (var e in model.Elements)
            elements.Add(new ElementSnapshot(e.Id, e.Available, e.Visible,
                e.Placement?.Anchor.ToString(), e.Placement?.X, e.Placement?.Y, e.Placement?.YUp,
                e.Value, e.Text, e.Color is { } c ? [c.R, c.G, c.B] : null, e.ScreenPosition?.X, e.ScreenPosition?.Y));
        var boxes = renderer.Layout.TextBoxes
            .Select(b => new BoxSnapshot(b.Text, b.Bounds.X, b.Bounds.Y, b.Bounds.Width, b.Bounds.Height, b.FixedAnchor))
            .ToList();
        float scale = renderer is ModernHudRenderer ? ModernHudScale.Core(1f, combo.Size, combo.Height) : 1f;
        var inventory = MeasureInventory(model, renderer is ModernHudRenderer ? _modernSmall : _small,
            combo.Width, combo.Height, scale, assert: false);
        var drawn = renderer is ModernHudRenderer modern
            ? modern.DrawnElements.OrderBy(s => s, StringComparer.Ordinal).ToList()
            : new List<string>();
        return new Snapshot(combo.Scenario, combo.Width, combo.Height, combo.Size, combo.Backdrop, combo.Style,
            model.Visible, model.FlightVisible, model.InstrumentsVisible, elements, boxes, inventory, drawn, scale,
            Array.Empty<string>());
    }

    /// <summary>P980 — every pixel the RENDERER drives from a wall clock. The one
    /// site in the HUD renderers is ModernHudRenderer's low-resource "!" badge,
    /// whose alpha is <c>Color * (.55 + .45*sin(Stopwatch...))</c> (:166). The
    /// badge is placed through <see cref="HudRenderer.DrawMeasuredText"/>, so its
    /// measured text box (plus the translucent background fill) is exactly the
    /// animated region. Recorded rather than guessed from gauge geometry.</summary>
    private static IReadOnlyList<RectRecord> AnimationMask(HudModel model, HudRenderer renderer, Combo combo)
    {
        if (renderer is not ModernHudRenderer) return Array.Empty<RectRecord>();
        var rects = new List<RectRecord>();
        foreach (var box in renderer.Layout.TextBoxes)
        {
            if (box.Text != "!") continue;
            var r = box.Bounds;
            r.Inflate(4, 2);
            r = Rectangle.Intersect(r, new Rectangle(0, 0, combo.Width, combo.Height));
            if (r.Width > 0 && r.Height > 0) rects.Add(RectRecord.Of(r));
        }
        return rects;
    }

    // ── hashing / helpers ────────────────────────────────────────────────────
    private static bool InMask(IReadOnlyList<RectRecord> mask, int x, int y)
    {
        for (int i = 0; i < mask.Count; i++) if (mask[i].Contains(x, y)) return true;
        return false;
    }

    private static string HashPixels(Color[] pixels, int width, int height, IReadOnlyList<RectRecord>? mask)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = pixels[y * width + x];
                row[x * 4] = c.R; row[x * 4 + 1] = c.G; row[x * 4 + 2] = c.B; row[x * 4 + 3] = c.A;
            }
            if (mask is not { Count: > 0 })
            {
                hash.AppendData(row);
                continue;
            }
            // Zero the masked spans so the hash is exactly "outside the animation".
            for (int i = 0; i < mask.Count; i++)
            {
                var r = mask[i];
                if (y < r.Y || y >= r.Y + r.H) continue;
                row.Slice(r.X * 4, r.W * 4).Clear();
            }
            hash.AppendData(row);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private List<Combo> Combos()
    {
        var combos = new List<Combo>(Scenarios.Length * Resolutions.Length * Sizes.Length * Backdrops.Length * Styles.Length);
        foreach (var (width, height) in Resolutions)
        foreach (float size in Sizes)
        foreach (var (backdrop, _) in Backdrops)
        foreach (string scenario in Scenarios)
        foreach (string style in Styles)
            combos.Add(new Combo(scenario, width, height, size, backdrop, style));
        // --filter is a debug/partial-capture aid; a published baseline is
        // always captured unfiltered (the manifest records the full matrix).
        return _options.Filter is null ? combos : combos.Where(c => Match(c.Id)).ToList();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private void WriteManifest(string root, List<ComboEntry> entries)
    {
        var adapter = GraphicsDevice.Adapter;
        string monoGame = typeof(Game).Assembly.GetName().Version?.ToString() ?? "unknown";
        // The DesktopGL adapter's Description is the GL renderer string (on Mesa
        // it names the GPU + driver); MonoGame exposes no public GL version call.
        var driver = new DriverInfo(
            "MonoGame.Framework.DesktopGL", adapter.Description, GraphicsDevice.GraphicsProfile.ToString(),
            GraphicsDevice.PresentationParameters.BackBufferFormat + "/" + GraphicsDevice.PresentationParameters.DepthStencilFormat,
            monoGame, Environment.GetEnvironmentVariable("SDL_VIDEODRIVER") ?? "(default)",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, 1f);
        var matrix = new MatrixInfo(Scenarios, Sizes.Select(Combo.Fmt).ToArray(),
            Backdrops.Select(b => b.Name).ToArray(), Styles,
            Resolutions.Select(r => (IReadOnlyList<int>)new[] { r.Width, r.Height }).ToArray(), entries.Count);
        var (commit, dirty) = GitState();
        var manifest = new Manifest(1, "tools/hud2-proof", "P980", commit, dirty, driver, matrix, entries);
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest, PrettyJson));
        Console.WriteLine($"HUD2_MANIFEST {entries.Count} combinations, commit {commit}{(dirty ? "+dirty" : "")}, driver {adapter.Description}");
    }

    private Manifest ReadManifest(string root)
    {
        string path = Path.Combine(root, "manifest.json");
        if (!File.Exists(path)) throw new InvalidOperationException("no manifest.json in " + root);
        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json)
               ?? throw new InvalidOperationException("unreadable manifest.json in " + root);
    }

    private string SemanticDiff(string root, ComboEntry entry, string currentJson)
    {
        string path = Path.Combine(root, entry.Snapshot);
        if (!File.Exists(path)) return "  baseline semantic snapshot missing: " + path;
        var expected = File.ReadAllText(path).Split('\n');
        var actual = currentJson.Split('\n');
        var lines = new List<string>();
        for (int i = 0; i < Math.Max(expected.Length, actual.Length) && lines.Count < 8; i++)
        {
            string a = i < expected.Length ? expected[i].TrimEnd('\r') : "<missing>";
            string b = i < actual.Length ? actual[i].TrimEnd('\r') : "<missing>";
            if (a == b) continue;
            lines.Add($"  line {i + 1}: baseline {Trim(a)} | now {Trim(b)}");
        }
        return string.Join('\n', lines);
        static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
    }

    private void WriteRunStats()
    {
        try
        {
            Directory.CreateDirectory(_out);
            var stats = new
            {
                schema = 1,
                generatedUtc = DateTime.UtcNow.ToString("O"),
                elapsedSeconds = Math.Round(_runElapsed.Elapsed.TotalSeconds, 1),
                mode = _options.SelfTest is not null ? "self-test" : _options.DiffCaptures.Length == 2 ? "diff-captures"
                    : _options.Compare is not null ? "compare" : _options.Capture is not null ? "capture" : "render",
                output = _out,
            };
            File.WriteAllText(Path.Combine(_out, "run.json"), JsonSerializer.Serialize(stats, PrettyJson));
        }
        catch (IOException) { /* stats are diagnostics only */ }
    }

    private readonly Stopwatch _runElapsed = Stopwatch.StartNew();

    private static (string Commit, bool Dirty) GitState()
    {
        if (Environment.GetEnvironmentVariable("HUD2_COMMIT") is { Length: > 0 } forced) return (forced, false);
        try
        {
            string root = RepoRoot();
            string commit = Run("git", root, "rev-parse", "HEAD").Trim();
            string status = Run("git", root, "status", "--porcelain");
            return (commit.Length >= 12 ? commit[..12] : commit, status.Length > 0);
        }
        catch (Exception) { return ("unknown", false); }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AllegianceCore.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("AllegianceCore.slnx");
    }

    private static string Run(string file, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start " + file);
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    // ── scenarios (shared HudModel fixture; no renderer code) ────────────────
    private static HudModel Scenario(string scenario, int w, int h,
        HudFlightReference? flightReference = null, float? speed = null, float? throttle = null,
        bool afterburner = true)
    {
        // P1036 — the four Jet flight-symbology scenes: level, pitched + rolled,
        // near-vertical (heading held unavailable) and reverse throttle.
        if (scenario.StartsWith("jet-", StringComparison.Ordinal))
        {
            (flightReference, speed, throttle, afterburner) = scenario switch
            {
                "jet-level" => (FlightReference(0f, 0f, 45f), 260f, .6f, false),
                "jet-pitched" => (FlightReference(18f, -25f, 210f), 320f, 1f, false),
                "jet-vertical" => (FlightReference(89.9f, 0f, 0f), 180f, .3f, false),
                "jet-reverse" => (FlightReference(-10f, 12f, 90f), 40f, -.6f, false),
                // P1037 — target / lock / lead / order scenes (level flight).
                "jet-target" or "jet-locked" or "jet-lockfull" or "jet-offscreen"
                    or "jet-lead" or "jet-leadout" or "jet-orders" or "jet-friendly"
                    or "jet-locksmall" or "jet-locklarge"
                    => (FlightReference(0f, 0f, 45f), 260f, .6f, false),
                // P1038 — stores / caution / inventory scenes (level flight).
                "jet-stores" or "jet-stores-low" or "jet-stores-critical" or "jet-pod"
                    or "jet-flags" or "jet-caution" or "jet-ghost" or "jet-f4" or "jet-f4-empty"
                    // P1039 — the radar scenes fly level, heading 045.
                    or "jet-radar" or "jet-radar-round" or "jet-radar-dense"
                    or "jet-radar-dense-round" or "jet-radar-kinds" or "jet-radar-nobearing"
                    or "jet-radar-All" or "jet-radar-Default" or "jet-radar-Target" or "jet-radar-Minimal"
                    // P1040 — the console/chat/lifecycle/training scenes fly level, heading 045.
                    or "jet-chat-long" or "jet-compose-mouse" or "jet-compose-chat"
                    or "jet-compose-command" or "jet-compose-shell" or "jet-compose-quick"
                    or "jet-cmd" or "jet-commandview" or "jet-tips" or "jet-orders-text"
                    or "jet-podoxy" or "jet-turret" or "jet-training"
                    => (FlightReference(0f, 0f, 45f), 260f, .6f, false),
                // P1040 — the roll pointer scene carries a real bank so the mark is off-centre.
                "jet-rollcue" => (FlightReference(4f, -28f, 45f), 260f, .6f, false),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "unknown Jet scene"),
            };
        }
        var frame = new HudFrame(.82f, .7f, .8f, .9f, 1, 1, 260, .6f, true, true, true, 0, false, 0, true, true, 15, 24,
            new WeaponStatusRow[] { new(0, "Rapid fire plasma cannon", true, 0, 200, WeaponStatus.NotReady), new(1, "Interceptor missile", false, 0, 0, WeaponStatus.Ready) }, false, 0, 2500,
            new(.2f, -.3f, .1f, false), 85, ShipWarningMask.OutOfBounds,
            SignaturePercent: 42f, Detected: scenario == "target-full", AutopilotEngaged: true, VectorLockEngaged: true,
            CarriedFlag: true, CarriedArtifact: scenario == "target-full");
        if (scenario == "critical") frame = frame with { HullFraction = .08f, ShieldFraction = 0, FuelFraction = .10f, EnergyFraction = .02f, Flashing = true };
        if (scenario == "lifepod") frame = frame with { IsAdriftLifepod = true, OxygenSecondsRemaining = 95 };
        // P1036 — the Jet flight scenes drive the frame with the attitude/readout
        // values under test (and can release the fixture's latched afterburner so
        // a reverse throttle actually reaches the shared model).
        if (!afterburner || speed is not null || throttle is not null)
            frame = frame with
            {
                Speed = speed ?? frame.Speed,
                Throttle = throttle ?? frame.Throttle,
                AfterburnerActive = afterburner && frame.AfterburnerActive,
            };
        // P1038 — the Jet stores/caution scenes drive only shared-model values: the six
        // gauges, the weapon/missile status rows, the own-status flags and the alert mask.
        bool jetScene = scenario.StartsWith("jet-", StringComparison.Ordinal);
        if (jetScene)
        {
            // The launcher row carries the shared IsLauncher flag (the stores block shows a
            // launcher's count and arming state); the weapon row's own ammo reading.
            frame = frame with
            {
                WarningMask = ShipWarningMask.None,
                WeaponSystems = new WeaponStatusRow[]
                {
                    new(0, "Rapid fire plasma cannon", true, 160, 200, WeaponStatus.Ready),
                    new(5, "SRM Dumbfire", false, 2, 6, WeaponStatus.Ready, IsLauncher: true),
                },
                Detected = scenario == "jet-flags",
                Cloaked = scenario == "jet-flags",
                CloakStrengthPercent = scenario == "jet-flags" ? 70f : null,
                CarriedArtifact = scenario == "jet-flags",
            };
            switch (scenario)
            {
                case "jet-stores-low":
                    frame = frame with { HullFraction = .45f, ShieldFraction = .40f, FuelFraction = .35f };
                    break;
                case "jet-stores-critical":
                    frame = frame with
                    {
                        HullFraction = .08f, ShieldFraction = .05f, FuelFraction = .10f,
                        EnergyFraction = .02f, AmmoFraction = 0f, Flashing = true,
                    };
                    break;
                case "jet-pod":
                case "jet-podoxy":
                    frame = frame with
                    {
                        IsAdriftLifepod = true, OxygenSecondsRemaining = 95,
                        AmmoFraction = 0f, EnergyFraction = 0f, FuelFraction = .63f,
                    };
                    break;
                case "jet-caution":
                    frame = frame with
                    {
                        HullFraction = .05f, EnergyFraction = .03f, Flashing = true,
                        WarningMask = ShipWarningMask.OutOfBounds | ShipWarningMask.CrowdedSector,
                    };
                    break;
                case "jet-f4":
                case "jet-f4-empty":
                    frame = frame with { HullFraction = .82f, ShieldFraction = .70f };
                    break;
            }
        }
        float spread = Math.Clamp(h / 680f, 1.15f, 2.1f);
        bool target = scenario is "combat" or "critical" or "target-full" || scenario.StartsWith("radar-");
        // P980 — one scenario carries every radar overlay flag and one carries every
        // target-pane field, so the rows HUD-2 never showed have a baseline frame
        // without changing what the original scenarios present.
        bool overlays = scenario == "radar-overlays";
        bool full = scenario == "target-full";
        bool chase = scenario == "chase";
        // P1037 — the Jet target/lock/lead/order scenes drive the same shared model
        // fields the later tests pin; each scene differs only in which of them is set.
        bool jetTargetScene = scenario is "jet-target" or "jet-locked" or "jet-lockfull" or "jet-orders"
            or "jet-friendly" or "jet-locksmall" or "jet-locklarge";
        bool jetOffScreen = scenario == "jet-offscreen";
        bool jetLeadScene = scenario is "jet-lead" or "jet-leadout";
        bool jetLockScene = scenario is "jet-locked" or "jet-lockfull" or "jet-locksmall" or "jet-locklarge";
        bool jetFriendly = scenario == "jet-friendly";
        bool anyTarget = target || jetTargetScene || jetOffScreen;
        bool flightCockpit = scenario is "flight" or "chat-stress" or "combat" or "critical" or "lifepod" or "training"
            || scenario.StartsWith("radar-") || scenario.StartsWith("jet-");
        var mode = scenario == "radar-overlays" ? RadarMode.All
            : scenario.StartsWith("radar-") ? Enum.Parse<RadarMode>(scenario[6..])
            : scenario.StartsWith("jet-radar-", StringComparison.Ordinal) ? JetRadarMode(scenario)
            : RadarMode.Default;
        var contacts = new List<HudContact>();
        for (int i = 0; i < 18; i++)
        {
            var kind = (HudContactKind)(i % 9);
            bool isEnemy = i % 8 == 0, isSelf = i == 17, isSel = i == 0;
            float separation = 500f + i * 40f;
            bool shown = kind switch
            {
                HudContactKind.Ship => RadarLod.ShowShip(mode, !isEnemy, isSelf, isSel),
                HudContactKind.Station => RadarLod.ShowStation(mode, pedestal: false, ownOrAllied: !isEnemy, onScreen: i % 2 == 0, separation, isSel),
                HudContactKind.Warp => RadarLod.ShowWarp(mode, isSel),
                HudContactKind.Asteroid => RadarLod.ShowAsteroid(mode, namedSpecial: i % 2 == 0, rangeZero: false, separation, isSel),
                HudContactKind.Missile => RadarLod.ShowMissile(mode, aimsAtOwnSide: isEnemy, isSel),
                HudContactKind.Probe or HudContactKind.Mine => RadarLod.ShowProbeOrMine(mode, ownOrAllied: !isEnemy, separation, isSel),
                _ => RadarLod.ShowTreasure(mode, useful: true, separation, isSel),
            };
            if (!shown) continue;
            var position = new HudPoint(w * .2f + i * 70, h * .48f);
            // HUD-5 — a small number of labelled/overlaid contacts exercise the
            // radar overlay + label block in the overlap sweep without crowding.
            if (i == 0)
                contacts.Add(new(kind, position, i % 3 != 0, .9f, new(.1f, .8f, .9f), true, false,
                    "Enemy fighter with a long pilot name", 1250, .3f, .6f, null, true, false, false,
                    AcceptedOrder: true, CommandId: 2, CommandAccepted: true, NearestEnemy: true));
            else if (i == 1 && kind == HudContactKind.Station)
                contacts.Add(new(kind, position, i % 3 != 0, .9f, new(.9f, .7f, .3f), false, false,
                    "Garrison", 800, .5f, .8f, overlays ? .35f : null,
                    false, false, true, QueuedOrder: overlays));
            else if (i == 2 && kind == HudContactKind.Warp)
                contacts.Add(new(kind, position, i % 3 != 0, .9f, null, false, false,
                    "A-4 Aleph", 600));
            else
                contacts.Add(new(kind, position, i % 3 != 0, .9f,
                    kind is HudContactKind.Treasure or HudContactKind.Tech ? null : new HudColor(.1f, .8f, .9f),
                    isSel, i == 3, IsSelf: isSelf, IsTech: kind == HudContactKind.Tech,
                    Highlight: i == 4, RipcordSeconds: i == 5 ? 7 : null,
                    QueuedOrder: overlays && i == 6, CommandId: overlays && i == 6 ? 3 : null,
                    Threat: overlays && i == 7, ThreatFlash: overlays && i == 8,
                    Subject: overlays && i == 9, CarriedFlag: overlays && i == 10, CarriedArtifact: overlays && i == 11,
                    Ripcord: overlays && i == 12, DetectionEye: overlays && i == 13,
                    Fill: overlays && kind == HudContactKind.Asteroid ? .6f : null));
        }
        if (scenario.StartsWith("jet-radar", StringComparison.Ordinal))
            contacts = JetRadarContacts(mode, scenario, w, h);
        // P1040 — the console/chat/lifecycle/training scenes. The chat window, the compose
        // state and the console text are the SAME shared-model values the live client builds
        // (BuildChatModel/BuildConsoleText); only which of them is present changes per scene.
        bool jetChatLong = scenario == "jet-chat-long";
        NativeComposeState composeState = scenario switch
        {
            "jet-compose-mouse" => NativeComposeState.MouseCommand,
            "jet-compose-chat" => NativeComposeState.Chat,
            "jet-compose-command" => NativeComposeState.Command,
            "jet-compose-shell" => NativeComposeState.Shell,
            "jet-compose-quick" => NativeComposeState.Quick,
            _ => NativeComposeState.NotComposing,
        };
        bool composing = composeState != NativeComposeState.NotComposing;
        string[] chatLines = jetChatLong
            ? new[]
            {
                "[TEAM] Pilot Alpha: defend the base",
                "[ALL] Pilot Bravo: incoming enemy fighter at the north gate, two of them closing fast",
                "[SECTOR] Pilot Charlie: " + new string('W', 210),
                "[TEAM] Pilot Delta: acknowledged, moving to the north gate now",
                "[ADMIN] Pilot Echo: quiet sector",
                "[ALLIES] Pilot Foxtrot: mining the far rock",
                "[TEAM] Pilot Golf: on my way, eta 30 seconds",
            }
            : new[] { "[TEAM] Pilot: defend the base", "[ALL] Enemy: incoming" };
        bool[] chatLeaders = jetChatLong
            ? new[] { true, false, false, false, false, false, true }
            : new[] { true, false };
        HudColor?[] chatColors = jetChatLong
            ? new HudColor?[]
            {
                new(.07f, .95f, .84f), new(.90f, .40f, .30f), new(.07f, .95f, .84f),
                new(.07f, .95f, .84f), new(.70f, .70f, .70f), new(.30f, .60f, .90f),
                new(.07f, .95f, .84f),
            }
            : new HudColor?[] { new(.07f, .95f, .84f), new(.90f, .40f, .30f) };
        string chatInput = scenario switch
        {
            "jet-compose-command" => "attack Enemy fighter with a long pilot name",
            "jet-compose-shell" => "!mute Pilot Alpha",
            _ => "returning to base with a long typed line so the field must fit",
        };
        HudChat chatModel = new(chatLines,
            jetChatLong ? 3 : 0, chatInput, composing, 1,
            scenario == "jet-compose-chat" ? "TEAM - Rixian" : "[TEAM] ",
            jetChatLong ? "TEAM" : null, composeState, composing ? .55f : 0f,
            new HudColor(.07f, .95f, .84f), chatColors, chatLeaders,
            scenario == "jet-chat-long" ? 9 : ChatPaneLayout.DefaultLines);
        bool tipsScene = scenario == "jet-tips";
        bool orderScene = scenario == "jet-orders-text";
        bool turretScene = scenario == "jet-turret";
        bool trainingScene = scenario == "jet-training";
        bool cmdScene = scenario is "jet-cmd" or "jet-commandview";
        string? voteText = tipsScene ? "Vote: Mutiny against the commander? (Y/N)" : null;
        return HudModel.Create(frame, new HudContext
        {
            Docked = scenario == "docked", Dead = scenario == "dead",
            CommandView = scenario == "command" || cmdScene, Hidden = scenario == "hidden",
            Camera = scenario == "command" || cmdScene ? CameraMode.Command : chase ? CameraMode.Chase : CameraMode.Cockpit,
            Turret = turretScene,
            VelocityMarker = flightCockpit ? new(w / 2f - 60, h / 2f + 20) : null,
            FlightReference = flightReference,
            // P1037 — the order-steering scene points the accepted and current arrows
            // in clearly different directions so both chevrons and both words read.
            OrderArrows = flightCockpit
                ? scenario == "jet-orders" ? new(new(.6f, -.8f), new(-.7f, .7f), false)
                : new(new(1f, 0f), new(0f, 1f), false)
                : null,
            ChaseAim = chase ? new(w / 2f + 40, h / 2f - 30) : null,
            // P1038 follow-up — the designator and the lock bracket come from the SAME
            // projected world radius in a real frame (Game1.Camera.cs derives both from the
            // target's world radius), so the Jet lock scenes pin both to one value.
            Target = anyTarget && !jetOffScreen ? new(new(w / 2 + 126.5f * spread, h / 2 - 112.5f * spread), true,
                jetFriendly ? "Friendly pilot" : "Enemy fighter with a long pilot name", .6f, .3f,
                !jetFriendly, full ? .42f : null, 1250, 43, new(1, 0, 0),
                ScreenRadius: jetScene ? LockRadius(scenario) : null) : null,
            TargetPane = anyTarget ? new(true, jetFriendly ? "Friendly pilot" : "Enemy fighter with a long pilot name",
                "Fighter", jetFriendly ? "Rixian" : "Rixian", new(1, 0, 0), .6f, .3f, 1250f, 43f,
                full ? .42f : null, full || jetTargetScene ? 15f : null, true, true,
                TimeToIntercept: full || jetTargetScene ? TargetTti.Text(1250f, 260f, 43f) : null,
                ClosingSpeed: anyTarget ? 43f : null) : null,
            // Native draws the ballistic lead only after the hull/relay capability
            // gate succeeds (indicator.cpp:283-330).
            SelectionBracket = full || jetTargetScene,
            Lead = target ? new(new(w / 2 + 100, h / 2 - 50), new(w / 2 + 200, h / 2 - 80), true, .8f, 1, Granted: full, Visible: true)
                : jetLeadScene ? new(new(w / 2 + 140, h / 2 - 90), new(w / 2 + 200, h / 2 - 120), scenario == "jet-lead",
                    .8f, 1, Granted: true, Visible: true) : null,
            Lock = target ? new(new(w / 2 + 200, h / 2 - 80), MissileLockFeedback.LockState.Partial, 1)
                : jetLockScene ? new(new(w / 2 + 126.5f * spread, h / 2 - 112.5f * spread),
                    scenario == "jet-lockfull" || scenario == "jet-locklarge" ? MissileLockFeedback.LockState.Full
                        : MissileLockFeedback.LockState.Partial,
                    1, scenario == "jet-lockfull" || scenario == "jet-locklarge" ? 1f : .62f,
                    LockRadius(scenario)) : null,
            // P1038 — the world's incoming-missile snapshot for the caution panel.
            MissileTrack = scenario == "jet-caution" ? new MissileTrackState(true, .72f) : null,
            HitConfirmation = target, PodReturnInstruction = "Shift+Z / G AUTOPILOT TO BASE",
            TargetBearing = anyTarget ? new(.8f, -.2f) : null,
            Notices = new[] { new HudNotice("NO ENEMY FIGHTER IN SECTOR", false, 1), new HudNotice("Reloading ammo...", true, 1) },
            Chat = jetScene ? chatModel
                : new(scenario == "chat-stress" ? Enumerable.Range(0, 8).Select(i => $"[TEAM] Pilot {i}: " + new string('W', 250)).ToArray() : new[] { "[TEAM] Pilot: defend the base", "[ALL] Enemy: incoming" }, 0, scenario == "chat-stress" ? new string('W', 250) : "returning", true, 1, "[TEAM] "),
            FlightPartsLine = "WEAPONS: GROUP  BOOST: ON  [1:Plasma]", CargoLine = "CARGO 1/5  Ammo pack x3",
            // P980 — P785 training callout (native RenderTrainingOverlay case 17,
            // indicator.cpp:634-641) and P949's one-time control hint.
            TrainingOverlayId = scenario == "training" ? 17 : trainingScene ? 17 : 0,
            ControlHint = scenario == "training" ? "Shift+F1 opens the controls"
                : trainingScene ? "Shift+F1 opens the controls" : null,
            // P1040 — the Jet console/lifecycle presentation values (Classic/Modern never
            // read them): the command-pane slide + the handler's wing/recipient/verb, and
            // the shared mission clock.
            CommandPaneSlide = cmdScene ? 1f : 0f,
            CommandWing = cmdScene ? 8 : 0,
            CommandRecipientColumn = cmdScene ? 3 : 0,
            CommandVerb = cmdScene ? 4 : 0,
            MissionClockSeconds = cmdScene ? 143 : jetScene ? 125 : null,
            MissionCountdown = cmdScene,
            // P980 — the native command recipient/verb row (HUD-10).
            Commandee = scenario == "commandee" ? "attack Enemy fighter" : null,
            // P980 — a mid-slide pane (native .5 s slide, dialog.mdl:5387,5404).
            // P1038 — the two Jet F4 scenes open the pane fully (slide progress 1).
            InventorySlide = scenario is "jet-f4" or "jet-f4-empty" ? 1f : scenario == "slide" ? .5f : 0f,
            TargetPaneSlide = scenario == "slide" ? .5f : 0f,
            InventoryPane = scenario == "jet-f4" ? JetInventoryPane()
                : scenario == "jet-f4-empty" ? JetEmptyInventoryPane()
                : new HudInventoryPane("Fig Adv", new HudInventoryRow[]
            {
                new(HudInventoryRowKind.Weapon, "1.", "Gatling Gun", true, true, false, "800m / 15 X 8", 3, 1f, null, null),
                new(HudInventoryRowKind.Weapon, "2.", "Plasma Cannon", false, false, false, "1200m / 20 X 5", 3, 1f, null, null),
                new(HudInventoryRowKind.Weapon, "Y.", "Turret weapon", false, false, false, null, 1, 0.45f, null, null),
                new(HudInventoryRowKind.Magazine, "5.", "SRM Dumbfire 3", false, false, false, "5 / 1125m / 150", 2, 1f, .6f, 5),
                new(HudInventoryRowKind.Shield, "7.", "Energy Shield", false, false, false, "500 / 3 sec", 3, 1f, null, 1),
                new(HudInventoryRowKind.Afterburner, "9.", "Booster", false, false, false, "220mps / 8", 3, 1f, null, 1),
                new(HudInventoryRowKind.Cloak, "8.", "Cloaking Device", false, false, false, "12 sec", 3, 1f, null, 1),
                new(HudInventoryRowKind.Cargo, "S.", "Ammo Pack", false, false, false, "3", 0, 0f, null, 3),
                new(HudInventoryRowKind.EmptyWeapon, "3.", "<small weapon mount>", false, false, false, null, 0, 0f, null, null),
            }),
            Contacts = contacts,
            // P1038 — the Jet scenes default the F4 pane CLOSED (the pane is its own scene);
            // the Classic/Modern matrix keeps the fixture's original rule.
            InventoryVisible = jetScene ? scenario is "jet-f4" or "jet-f4-empty" : scenario != "systems",
            CommandLines = scenario == "command" ? new[] { "COMMAND SHIP: Scout", "Hull 82% Shield 70%" }
                : cmdScene ? new[]
                {
                    "COMMAND SHIP: Fig Adv",
                    "Hull 82%  Shield 70%  Speed 260 mps",
                    "RECIPIENTS: 9 wings + 3 ships | LEFT SELECT / DRAG | RIGHT ORDER",
                }
                : Array.Empty<string>(),
            RadarMode = mode,
            RoundRadar = scenario.StartsWith("radar-") || scenario == "jet-radar-round"
                || scenario == "jet-radar-dense-round",
            Ripcord = jetScene ? (scenario is "jet-caution" or "jet-ghost" ? "Ripcord in: 9" : null)
                : "Ripcord in: 9",
            Ghost = scenario is "ghost" or "jet-ghost",
            Console = new HudConsoleText
            {
                SectorName = "Alystra",
                CarrierName = scenario is "flight" or "chat-stress" ? "Pilot Alpha" : turretScene ? "Pilot Alpha" : null,
                BoardState = scenario is "flight" or "chat-stress" ? 2 : turretScene ? 2 : 0,
                AcceptedOrder = scenario is "flight" or "combat" ? "defend Garrison" : orderScene ? "defend Garrison" : null,
                QueuedOrder = scenario is "flight" or "combat" ? "attack Enemy fighter" : orderScene ? "attack Enemy fighter" : null,
                VoteText = scenario is "critical" or "jet-caution" ? "Vote: Mutiny against the commander? (Y/N)" : voteText,
                OrdinaryTips = tipsScene
                    ? new[] { "Welcome to the sector", "Alystra: 3 miners on duty", "Payday: $500 deposited into your account balance" }
                    : jetScene ? Array.Empty<string>() : new[] { "Welcome to the sector", "Alystra: 3 miners on duty", "Payday: $500" },
                CriticalTips = tipsScene
                    ? new[] { new HudNotice("Reloading ammo...", true, 1), new HudNotice("Enemy carrier spotted in sector seven", true, 1) }
                    : jetScene && scenario != "jet-caution" ? Array.Empty<HudNotice>() : new[] { new HudNotice("Reloading ammo...", true, 1) },
                SideColor = new(0.07f, 0.95f, 0.84f),
                // P980 — lives/kills badges (native console.cpp:812-821): the
                // lives badge is only drawn below 10, the kills badge in a deathmatch.
                Lives = scenario is "ghost" or "jet-ghost" or "jet-caution" ? 3 : null,
                MissionKills = scenario is "ghost" or "jet-ghost" or "jet-caution" ? 7 : null,
                Deathmatch = scenario is "ghost" or "jet-ghost" or "jet-caution",
            }
        });
    }

    /// <summary>P1038c — the measured compact lock cue of a frame: the native-size
    /// designator half, the corner distance the cue is drawn at (√2·half + clearance +
    /// 40·(1−lock)), its axis extent (cos 15°) and the gap it keeps outside the
    /// designator's corner.</summary>
    private static object? LockFit(HudModel model, int width, int height)
    {
        if (model.Context.Lock is not { } lockState || lockState.State == MissileLockFeedback.LockState.None) return null;
        float scale = ModernHudScale.Core(1f, 1f, height);
        float cx = lockState.Center.X, cy = lockState.Center.Y;
        float toEdge = MathF.Min(MathF.Min(cx, cy), MathF.Min(width - cx, height - cy));
        // P1038c — the cue hugs the designator drawn with the SAME native half-span.
        float designatorHalf = JetTargetGeometry.DesignatorHalf(
            model.Context.Target?.ScreenRadius ?? lockState.ScreenRadius, scale, toEdge);
        float corner = designatorHalf * JetTargetGeometry.Sqrt2;
        float distance = JetTargetGeometry.LockCueCornerDistance(designatorHalf, lockState.Fraction, scale);
        float extent = JetTargetGeometry.LockCueExtent(distance);
        float clearance = JetTargetGeometry.LockClearance * (scale > 0f ? scale : 1f);
        float closing = distance - corner - clearance;
        return new
        {
            designatorHalf, cornerDistance = corner, cueDistance = distance, cueExtent = extent,
            gap = distance - corner, clearance, closingTerm = closing,
            inside = distance - corner >= clearance - 0.5f,
            sweepDegrees = JetTargetGeometry.LockCornerSweep * 180f / MathF.PI,
            dashesPerCorner = JetTargetGeometry.LockCornerDashes,
        };
    }

    /// <summary>P1039b — the planned block of every contact of this scene (form 0 = hidden), so
    /// the placement is evidence in the JSON, not an assertion.</summary>
    private static object?[] Labels(JetHudRenderer renderer)
    {
        var boxes = renderer.RadarLabelBoxes;
        var forms = renderer.RadarLabelForms;
        var list = new object?[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            if (forms[i] == 0) { list[i] = null; continue; }
            RectangleF box = boxes[i];
            list[i] = new { form = (int)forms[i], x = MathF.Round(box.X, 1), y = MathF.Round(box.Y, 1),
                w = MathF.Round(box.Width, 1), h = MathF.Round(box.Height, 1) };
        }
        return list;
    }

    /// <summary>P1039b — the four band values the scope draws for this scene's contact set.</summary>
    private static int[] Bands(IReadOnlyList<HudContact> contacts)
    {
        float range = JetRadar.ScopeRange(contacts);
        var bands = new int[JetRadar.RingCount];
        for (int ring = 1; ring <= JetRadar.RingCount; ring++) bands[ring - 1] = JetRadar.RingRange(ring, range);
        return bands;
    }

    /// <summary>P1039 — the radar mode of a Jet radar scene: the four LOD names are parsed
    /// verbatim, the shape variants stay on Default/All.</summary>
    private static RadarMode JetRadarMode(string scenario)
    {
        string suffix = scenario["jet-radar-".Length..];
        return suffix switch
        {
            "round" or "nobearing" => RadarMode.Default,
            "dense" or "dense-round" or "kinds" => RadarMode.All,
            _ => Enum.Parse<RadarMode>(suffix),
        };
    }

    /// <summary>
    /// P1039 — the Jet radar scenes' contact set. Every contact is a normal
    /// <see cref="HudContact"/> the snapshot builder could produce: the same per-kind LOD
    /// filter (<see cref="RadarLod"/>) decides presence, the bearing is the presentation-only
    /// own-relative value (−π…π, computed by <see cref="RadarBearing"/>) and the range/label
    /// payload is the same native one. `kinds` puts one of every kind plus every overlay in
    /// the frame; `dense` is 66 contacts (the brief's ≥ 60) with labels at LOD active;
    /// `nobearing` leaves every bearing null to prove the projected-scope fallback.
    /// </summary>
    private static List<HudContact> JetRadarContacts(RadarMode mode, string scenario, float w, float h)
    {
        bool dense = scenario.Contains("dense", StringComparison.Ordinal);
        bool kinds = scenario.Contains("kinds", StringComparison.Ordinal);
        bool noBearing = scenario.Contains("nobearing", StringComparison.Ordinal);
        int count = dense ? 66 : kinds ? 12 : 10;
        var list = new List<HudContact>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = (HudContactKind)(i % 9);
            // Bearings spread around the circle; the dense frame fills every quadrant.
            float bearing = (i * 2.399963f) % (2f * MathF.PI) - MathF.PI;
            int range = dense ? 240 + (i * 53) % 3400 : 400 + i * 260;
            int separation = range;
            bool enemy = i % 4 == 0 && kind == HudContactKind.Ship;
            bool selected = i == 0;
            bool shown = kind switch
            {
                HudContactKind.Ship => RadarLod.ShowShip(mode, !enemy, isSelf: false, isSelected: selected),
                HudContactKind.Station => RadarLod.ShowStation(mode, pedestal: false, ownOrAllied: i % 3 == 0,
                    onScreen: true, separation, selected),
                HudContactKind.Warp => RadarLod.ShowWarp(mode, selected),
                HudContactKind.Asteroid => RadarLod.ShowAsteroid(mode, namedSpecial: i % 2 == 0,
                    rangeZero: false, separation, selected),
                HudContactKind.Missile => RadarLod.ShowMissile(mode, aimsAtOwnSide: true, selected),
                HudContactKind.Probe or HudContactKind.Mine => RadarLod.ShowProbeOrMine(mode,
                    ownOrAllied: i % 3 == 0, separation, selected),
                _ => RadarLod.ShowTreasure(mode, useful: true, separation, selected),
            };
            if (!shown) continue;
            float? relative = noBearing ? null : (i == 0 ? 0f : bearing);
            list.Add(new HudContact(
                kind,
                // A projected point too: the null-bearing fallback maps it into the scope.
                new HudPoint(w * 0.5f + MathF.Cos(bearing) * w * 0.3f, h * 0.5f + MathF.Sin(bearing) * h * 0.3f),
                true, 1f, new HudColor(.1f, .8f, .9f), selected, i == 5,
                Name: i % 3 == 0 ? "Contact " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                Range: range,
                Shield: kind is HudContactKind.Ship or HudContactKind.Station ? .55f : null,
                Hull: kind is HudContactKind.Ship or HudContactKind.Station ? .82f : null,
                Fill: kind == HudContactKind.Asteroid && i % 4 == 0 ? .5f : null,
                IsEnemy: enemy, IsSelf: i == 9 && !dense, IsSameSide: !enemy && kind == HudContactKind.Ship,
                AcceptedOrder: i == 1, QueuedOrder: i == 2,
                Threat: i == 4, ThreatFlash: i == 4,
                Subject: i == 6, CarriedFlag: i == 7, CarriedArtifact: i == 8,
                Ripcord: i == 5, Highlight: i == 3, NearestEnemy: i == 0,
                CommandId: i is 1 or 2 ? 2 : null, CommandAccepted: i == 1,
                RipcordSeconds: i == 5 ? 7 : null, DetectionEye: i == 1 && kind == HudContactKind.Ship,
                IsTech: kind == HudContactKind.Tech,
                ClipSeparation: separation, MissileTargetsOwnSide: kind == HudContactKind.Missile,
                TreasureUseful: kind is HudContactKind.Treasure or HudContactKind.Tech,
                CameraDistance: range,
                RelativeBearingRadians: i == 9 && !dense ? 0f : relative));
        }
        return list;
    }

    /// <summary>P1038 — the projected target radius the Jet lock scenes pin on BOTH the
    /// designator and the lock bracket (a small one, the P1037 default, and one near the
    /// 150 px designator ceiling).</summary>
    private static float LockRadius(string scenario) => scenario switch
    {
        "jet-locksmall" => 22f,
        "jet-locklarge" => 145f,
        _ => 120f,
    };

    /// <summary>P1038 — the caution lines the master-caution panel resolves for this scene
    /// (kind + priority + value), so the capture JSON records what the panel actually drew.</summary>
    private static object CautionSummary(HudModel model, int height)
    {
        var buffer = new JetCaution[JetCautionModel.MaxCautions];
        int count = JetCautionModel.Build(model, buffer);
        var lines = new List<object>(count);
        for (int i = 0; i < count; i++)
            lines.Add(new { kind = buffer[i].Kind.ToString(), priority = buffer[i].Priority, hot = buffer[i].Hot,
                fraction = buffer[i].Fraction, seconds = buffer[i].Seconds, count = buffer[i].Count });
        return new { count, limit = JetCautionModel.LineLimit(JetStoresPanel.Compact(height)), compact = JetStoresPanel.Compact(height), lines };
    }

    /// <summary>P1038 — one row of every native inventory kind, for the F4 scene: weapons,
    /// a turret occupant, observers, a mounting weapon, an arming launcher, shield,
    /// afterburner, cloak, chaff, and two cargo rows.</summary>
    private static HudInventoryPane JetInventoryPane() => new("Fig Adv", new HudInventoryRow[]
    {
        new(HudInventoryRowKind.Weapon, "1.", "Gatling Gun", true, true, false, "800m / 15 X 8", 3, 1f, null, null),
        new(HudInventoryRowKind.Weapon, "2.", "Plasma Cannon", false, false, false, "1200m / 20 X 5", 3, 1f, null, null),
        new(HudInventoryRowKind.Turret, "", "Pilot Beta", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.Observers, "", "Observers: 2", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.Weapon, "Y.", "Turret weapon", false, false, false, null, 1, .45f, null, null),
        new(HudInventoryRowKind.Magazine, "5.", "SRM Dumbfire 3", false, false, false, "5 / 1125m / 150", 2, 1f, .6f, 5),
        new(HudInventoryRowKind.Shield, "7.", "Energy Shield", false, false, false, "500 / 3 sec", 3, 1f, null, 1),
        new(HudInventoryRowKind.Afterburner, "9.", "Booster", false, false, false, "220mps / 8", 3, 1f, null, 1),
        new(HudInventoryRowKind.Cloak, "8.", "Cloaking Device", false, false, false, "82% / 12 sec", 3, 1f, null, 1),
        new(HudInventoryRowKind.Chaff, "6.", "Chaff Dispenser", false, false, false, "40", 3, 1f, null, 40),
        new(HudInventoryRowKind.Cargo, "S.", "Ammo Pack", false, false, false, "3", 0, 0f, null, 3),
        new(HudInventoryRowKind.Cargo, "", "Fuel Pod", false, false, false, "1", 0, 0f, null, 1),
    });

    /// <summary>P1038 — the F4 pane of empty mounts (empty weapon/equipment/cargo slots plus
    /// one out-of-ammo ready weapon, whose row must keep its <c>A</c> cue).</summary>
    private static HudInventoryPane JetEmptyInventoryPane() => new("Fig Adv", new HudInventoryRow[]
    {
        new(HudInventoryRowKind.EmptyWeapon, "1.", "<small weapon mount>", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.EmptyWeapon, "2.", "<small weapon mount>", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.Weapon, "3.", "Gatling Gun", false, false, true, "800m / 15 X 8", 3, 1f, null, null),
        new(HudInventoryRowKind.EmptyEquipment, "5.", "<missile mount>", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.EmptyEquipment, "7.", "<shield mount>", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.EmptyEquipment, "8.", "<cloak mount>", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.EmptyEquipment, "9.", "<afterburner mount>", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.EmptyCargo, "S.", "<cargo mount>", false, false, false, null, 0, 0f, null, null),
    });

    /// <summary>
    /// P1036 — a deterministic own-ship attitude for the Jet flight scenes: the
    /// forward is placed at <paramref name="headingDegrees"/> in the sector plane
    /// (0° = world +X), pitched <paramref name="pitchDegrees"/> above it, then rolled
    /// <paramref name="rollDegrees"/> about the forward axis with the sim's own
    /// <see cref="Orientation.PostRotate"/> (the same left-handed convention the
    /// flight code uses), converted through <see cref="HudFlightReference.FromSim"/>.
    /// No world access — this is the same shape the model adapter builds.
    /// </summary>
    private static HudFlightReference FlightReference(float pitchDegrees, float rollDegrees, float headingDegrees)
    {
        float heading = headingDegrees * MathF.PI / 180f;
        float pitch = pitchDegrees * MathF.PI / 180f;
        float roll = rollDegrees * MathF.PI / 180f;
        var forward = new Allegiance.Core.Collision.Vector3(
            MathF.Cos(heading) * MathF.Cos(pitch),
            MathF.Sin(heading) * MathF.Cos(pitch),
            MathF.Sin(pitch));
        var levelUp = new Allegiance.Core.Collision.Vector3(
            -MathF.Cos(heading) * MathF.Sin(pitch),
            -MathF.Sin(heading) * MathF.Sin(pitch),
            MathF.Cos(pitch));
        var orientation = new Orientation();
        orientation.Set(forward, levelUp);
        if (roll != 0f) orientation.PostRotate(forward, roll);
        return HudFlightReference.FromSim(orientation, forward * 260f, HudFlightReference.SectorUp);
    }

    /// <summary>
    /// P1037 — a pass-through <see cref="IJetVectorSurface"/> that records every text
    /// run, fill, stroke and icon glyph it submits (delegating the actual drawing to the
    /// production surface). The renderer's keyline pass submits the same string eight
    /// times at ±1 px, so a run within 1.6 px of an already-recorded one replaces it: the
    /// recorded box is the ink run, which is what a pilot sees. A stroke is recorded the
    /// same way (the painter emits a wider keyline quad and then the ink quad for the same
    /// endpoints; the narrowest one is the ink).
    /// <para>P1038b — the audit measured from these submissions is the recording surface
    /// the brief requires: text-vs-text, caption and plate overlaps, <b>stroke-vs-text</b>,
    /// <b>text-outside-plate</b>, <b>text-off-screen</b> and the <b>minimum icon glyph
    /// size</b>.</para>
    /// </summary>
    private sealed class RecordingSurface : IJetVectorSurface, IJetGlyphAudit, IJetCueAudit, IJetElementAudit,
        IJetRadarAudit
    {
        /// <summary>P1039 — the element the runs that follow belong to (the renderer
        /// announces each block once; a run with no announcement carries null).</summary>
        private string? _element;

        /// <summary>P1040b — the elements whose ink the renderer COMMITTED this frame
        /// (reported through <see cref="IJetElementAudit.Drawn"/>); the proof compares this
        /// set with the scene model's gates for the `ElementMissing` class.</summary>
        private readonly HashSet<string> _drawn = new(StringComparer.Ordinal);

        public IReadOnlySet<string> DrawnIds => _drawn;

        /// <summary>P1039c — the radar contact whose marks and runs follow (-1 outside the
        /// radar or for its captions).</summary>
        private int _contact = -1;

        /// <summary>P1039 — name the block whose runs follow.</summary>
        public void Element(string id) => _element = id;

        /// <summary>P1040b — an element's ink was committed this frame.</summary>
        public void Drawn(string id) => _drawn.Add(id);

        /// <summary>P1039c — name the radar contact whose runs follow.</summary>
        public void Contact(int index) => _contact = index;

        /// <summary>A cursor-invisible sub-pixel tolerance: anything inside this is the same
        /// geometric run submitted twice (keyline then ink).</summary>
        private const float SameRun = 0.35f;

        private readonly IJetVectorSurface _inner;
        private readonly List<LayoutBox> _texts = new();
        private readonly List<LayoutBox> _fills = new();
        private readonly List<LayoutFill> _plates = new();
        private readonly List<LayoutStroke> _strokes = new();
        private readonly List<LayoutGlyph> _glyphs = new();
        private readonly List<LayoutCue> _cues = new();
        private readonly List<LayoutBox> _cueReadouts = new();
        /// <summary>P1039c — the radar geometry: each contact's glyph/overlay box, the four band
        /// strokes and the leader strokes of the frame.</summary>
        private readonly List<LayoutContactGlyph> _contactGlyphs = new();
        private readonly List<LayoutRing> _rings = new();
        private readonly List<LayoutStroke> _leaders = new();
        /// <summary>P1039c — the backing plates drawn under radar readings (they interrupt the
        /// band stroke, so the audit counts them instead of a band-through-text hit).</summary>
        private readonly List<LayoutFill> _radarPlates = new();
        private readonly Dictionary<int, Color> _ink = new();
        /// <summary>P1041 — every colour the frame submitted (line, fill, text),
        /// so the palette audit can prove no colour outside the variant palette
        /// (plus the stated fixed set) is used.</summary>
        private readonly List<Color> _submitted = new();
        private int _seq;

        public RecordingSurface(IJetVectorSurface inner) => _inner = inner;

        public void Begin() => _inner.Begin();
        public void End() => _inner.End();
        public Vector2 Measure(string text, bool large, float scale) => _inner.Measure(text, large, scale);

        public void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            RecordStroke(a, b, width, _seq++);
            _submitted.Add(color);
            _inner.Line(a, b, width, color);
        }

        public void Fill(Vector2 min, Vector2 size, Color color)
        {
            int seq = _seq++;
            _submitted.Add(color);
            float w = MathF.Max(0f, size.X), h = MathF.Max(0f, size.Y);
            _fills.Add(new("#", min.X, min.Y, w, h, seq));
            // P1038b — a fill thin in BOTH axes is a bar/mark (a segmented store bar, a
            // progress bar, a state marker): record it as a centred stroke so a bar drawn
            // through a glyph is measured as one. A fill thin in only ONE axis is a plate
            // keyline (the frame of a readout box or pane), which is furniture, not a bar,
            // and is not counted against the text a plate legitimately holds.
            if (w > 0f && h > 0f && w < 8f && h < 8f)
            {
                if (w >= h)
                    RecordStroke(new(min.X, min.Y + h * 0.5f), new(min.X + w, min.Y + h * 0.5f), h, seq);
                else
                    RecordStroke(new(min.X + w * 0.5f, min.Y), new(min.X + w * 0.5f, min.Y + h), w, seq);
            }
            else if (w >= 8f && h >= 8f)
            {
                // P1038c — keep the submitted tint (its alpha is the plate's opacity, which
                // the legible-behind-pane check composites).
                _plates.Add(new(min.X, min.Y, w, h, seq, color));
            }
            _inner.Fill(min, size, color);
        }

        public void Text(string text, Vector2 position, bool large, float scale, Color color)
        {
            Vector2 size = _inner.Measure(text, large, scale);
            int seq = _seq++;
            Record(_texts, text, position.X, position.Y, size.X, size.Y, seq);
            _ink[seq] = color;
            _submitted.Add(color);
            _inner.Text(text, position, large, scale, color);
        }

        /// <summary>P1038b — one icon glyph the renderer composed (annunciator, caution or
        /// inventory state mark) with the cap height its word needs.</summary>
        public void Glyph(string id, RectangleF box, float minimumHeight)
            => _glyphs.Add(new(id, box.X, box.Y, box.Width, box.Height, minimumHeight, _seq++));

        /// <summary>P1038c — one lock-cue arc, for the cue-vs-own-readout check.</summary>
        public void CueArc(Vector2 center, float radius, float start, float sweep)
            => _cues.Add(new(center, radius, start, sweep, _seq++));

        /// <summary>P1038c — the lock readout block the cue belongs to.</summary>
        public void CueReadout(RectangleF box)
            => _cueReadouts.Add(new("LOCK", box.X, box.Y, box.Width, box.Height, _seq++));

        /// <summary>P1039c — the glyph/overlay box of the contact announced last.</summary>
        public void ContactGlyph(RectangleF box)
            => _contactGlyphs.Add(new(_contact, box.X, box.Y, box.Width, box.Height, _seq++));

        /// <summary>P1039c — one range-band stroke of the scope.</summary>
        public void RadarRing(Vector2 centre, float radius, bool round)
            => _rings.Add(new(centre, radius, round, _seq++));

        /// <summary>P1039c — one leader stroke of a placed label block.</summary>
        public void RadarLeader(Vector2 from, Vector2 to, float width)
            => _leaders.Add(new(from, to, width, _seq++, _element));

        /// <summary>P1039c — one backing plate drawn under a radar reading.</summary>
        public void RadarPlate(RectangleF box)
            => _radarPlates.Add(new(box.X, box.Y, box.Width, box.Height, _seq++, Color.White));

        private void Record(List<LayoutBox> list, string text, float x, float y, float w, float h, int seq)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                LayoutBox run = list[i];
                if (string.Equals(run.Text, text, StringComparison.Ordinal)
                    && MathF.Abs(run.X - x) <= 2.5f && MathF.Abs(run.Y - y) <= 2.5f)
                {
                    list[i] = new LayoutBox(text, x, y, w, h, seq, _element, _contact);   // keep the ink run (submitted last)
                    return;
                }
            }
            list.Add(new LayoutBox(text, x, y, w, h, seq, _element, _contact));
        }

        /// <summary>P1038b — record one stroke, keeping the INK width when the painter
        /// submits the same segment twice (keyline quad, then ink quad).</summary>
        private void RecordStroke(Vector2 a, Vector2 b, float width, int seq)
        {
            if (width <= 0f) return;
            for (int i = _strokes.Count - 1; i >= 0; i--)
            {
                LayoutStroke run = _strokes[i];
                if (Near(run.A, a) && Near(run.B, b) || Near(run.A, b) && Near(run.B, a))
                {
                    if (width < run.Width) _strokes[i] = new LayoutStroke(a, b, width, seq, _element);
                    return;
                }
            }
            _strokes.Add(new LayoutStroke(a, b, width, seq, _element));
        }

        private static bool Near(Vector2 a, Vector2 b)
            => MathF.Abs(a.X - b.X) <= SameRun && MathF.Abs(a.Y - b.Y) <= SameRun;

        public void Reset()
        {
            _texts.Clear();
            _fills.Clear();
            _plates.Clear();
            _strokes.Clear();
            _glyphs.Clear();
            _cues.Clear();
            _cueReadouts.Clear();
            _contactGlyphs.Clear();
            _rings.Clear();
            _leaders.Clear();
            _radarPlates.Clear();
            _ink.Clear();
            _submitted.Clear();
            _element = null;
            _contact = -1;
            _drawn.Clear();
        }

        /// <summary>P1041 — every colour submitted this frame.</summary>
        public IReadOnlyList<Color> SubmittedColours => _submitted;

        /// <summary>
        /// P1041 — the frame's geometry as ordered, colour-free lines: every text run and
        /// its measured box, every stroke and width, every fill/plate rectangle, every
        /// icon glyph, lock cue and cue readout, radar contact glyph, band ring, leader and
        /// radar plate. Two palettes drawing the same model must produce identical lines;
        /// only the palette's own WIP tag text (item 5's label) may differ and is excluded
        /// when <paramref name="excludeTag"/> is set.
        /// </summary>
        public string[] GeometrySignature(bool excludeTag)
        {
            static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var lines = new List<string>(_texts.Count + _strokes.Count + _fills.Count + 96);
            foreach (LayoutBox t in _texts)
            {
                if (excludeTag && (t.Text == JetHudRenderer.TagText || t.Text == JetHudRenderer.TagTextAmber)) continue;
                lines.Add($"T|{t.Text}|{F(t.X)},{F(t.Y)},{F(t.W)},{F(t.H)}");
            }
            foreach (LayoutStroke s in _strokes)
                lines.Add($"S|{F(s.A.X)},{F(s.A.Y)},{F(s.B.X)},{F(s.B.Y)},{F(s.Width)}");
            foreach (LayoutBox f in _fills) lines.Add($"F|{F(f.X)},{F(f.Y)},{F(f.W)},{F(f.H)}");
            foreach (LayoutGlyph g in _glyphs) lines.Add($"G|{g.Id}|{F(g.X)},{F(g.Y)},{F(g.W)},{F(g.H)},{F(g.MinimumHeight)}");
            foreach (LayoutCue c in _cues) lines.Add($"C|{F(c.Center.X)},{F(c.Center.Y)},{F(c.Radius)},{F(c.Start)},{F(c.Sweep)}");
            foreach (LayoutBox r in _cueReadouts) lines.Add($"Q|{F(r.X)},{F(r.Y)},{F(r.W)},{F(r.H)}");
            foreach (LayoutContactGlyph g in _contactGlyphs) lines.Add($"K|{g.Index}|{F(g.X)},{F(g.Y)},{F(g.W)},{F(g.H)}");
            foreach (LayoutRing n in _rings) lines.Add($"N|{F(n.Centre.X)},{F(n.Centre.Y)},{F(n.Radius)},{n.Round}");
            foreach (LayoutStroke l in _leaders) lines.Add($"L|{F(l.A.X)},{F(l.A.Y)},{F(l.B.X)},{F(l.B.Y)},{F(l.Width)}");
            foreach (LayoutFill p in _radarPlates) lines.Add($"P|{F(p.X)},{F(p.Y)},{F(p.W)},{F(p.H)}");
            return lines.ToArray();
        }

        /// <summary>The one tag run's measured box (item 5's label, reported separately).</summary>
        public string TagRun()
        {
            foreach (LayoutBox t in _texts)
                if (t.Text == JetHudRenderer.TagText || t.Text == JetHudRenderer.TagTextAmber)
                    return $"{t.Text}|{t.X:0.###},{t.Y:0.###},{t.W:0.###},{t.H:0.###}";
            return "";
        }

        /// <summary>
        /// P1038b — the extended layout audit of the frame just drawn.
        /// <list type="bullet">
        /// <item><b>text-vs-text / caption</b> (P1036): informational; a caption hit is a
        /// failure because a tape caption is HUD furniture.</item>
        /// <item><b>stroke-vs-text</b>: any line or bar segment whose ink box crosses a text
        /// glyph box — including the stroke a label owns (a bar through its own word reads
        /// as strikethrough).</item>
        /// <item><b>text-outside-plate</b>: a text run that overlaps a plate (a readout box
        /// or a pane) but is not fully inside it.</item>
        /// <item><b>text-off-screen</b>: a text box beyond the viewport minus the edge
        /// gutter (<see cref="EdgeGutter"/> standard-stroke units × scale).</item>
        /// <item><b>minimum glyph</b>: an icon glyph shorter than the cap height of the word
        /// it introduces.</item>
        /// <item><b>text-vs-text inside one plate</b> (P1038c): two runs owned by the same
        /// plate that intersect — the F4 title on the caption band, reported per plate.</item>
        /// <item><b>cue-vs-own-readout</b> (P1038c): a lock-cue arc crossing the
        /// <c>LOCK nn%</c> readout block it is drawn with.</item>
        /// <item><b>legible-behind-pane</b> (P1038c): a text/icon drawn BEFORE the F4 pane
        /// whose box intersects the pane and whose composited ghost keeps at least
        /// <see cref="GhostLegibleChannel"/> of 255 channel separation from the plate.</item>
        /// </list>
        /// </summary>
        public LayoutAudit Audit(int viewportWidth, int viewportHeight, float scale, Color backdrop)
        {
            // The P1036 review rule: no label may overlap a tape caption ("SPD / MPS",
            // "THR") or a readout box (plate); a readout's own ink inside its plate is
            // the one allowed case. Text-vs-text is reported as information only, since
            // independently positioned markers (ladder numerals, order words) can
            // legitimately share a row of sky.
            var captionOverlaps = new List<string>();
            var textOverlaps = new List<string>();
            for (int i = 0; i < _texts.Count; i++)
                for (int j = i + 1; j < _texts.Count; j++)
                {
                    LayoutBox a = _texts[i], b = _texts[j];
                    if (!Intersects(a, b)) continue;
                    textOverlaps.Add($"{a.Text}@{a.X:0.#},{a.Y:0.#} {a.W:0.#}x{a.H:0.#} x {b.Text}@{b.X:0.#},{b.Y:0.#}");
                    if (IsCaption(a.Text) || IsCaption(b.Text))
                        captionOverlaps.Add($"{a.Text}@{a.X:0.#},{a.Y:0.#} x {b.Text}@{b.X:0.#},{b.Y:0.#}");
                }
            var boxOverlaps = new List<string>();
            // P1038 — a PANE (the F4 inventory panel, the caution stack) owns its content:
            // its own rows may sit over the plates underneath, exactly like the native pane
            // that covers the same side of the HUD.
            var panes = new List<LayoutBox>();
            foreach (LayoutBox pane in _fills) if (pane.H > 64f) panes.Add(pane);
            foreach (LayoutBox fill in _fills)
                foreach (LayoutBox text in _texts)
                {
                    // A "box" is a readout plate; the 3 px fraction bars are strokes.
                    if (fill.H < 8f || fill.H > 64f) continue;
                    if (InAnyPane(text, panes)) continue;
                    if (!Intersects(fill, text)) continue;
                    if (Contains(fill, text)) continue;      // the readout ink on its own plate
                    boxOverlaps.Add($"{text.Text}@{text.X:0.#},{text.Y:0.#} {text.W:0.#}x{text.H:0.#} in fill@{fill.X:0.#},{fill.Y:0.#} {fill.W:0.#}x{fill.H:0.#}");
                }

            // ── P1038b: stroke-vs-text ─────────────────────────────────────
            // A stroke drawn AFTER the text sits on top of it (the strikethrough the brief
            // reports); a stroke drawn BEFORE it is the background furniture the text's own
            // keyline separates it from (the P1036 ladder rungs, a plate's tick). The glyph
            // ink band is the middle of the recorded run box: SpriteFont's MeasureString
            // height is the LINE SPACING (22 px at scale 1), of which about ten pixels are
            // capital ink starting a few pixels below the run's top (InkTop…InkBottom).
            var strokeHits = new List<string>();
            foreach (LayoutStroke stroke in _strokes)
                foreach (LayoutBox text in _texts)
                {
                    if (stroke.Seq < text.Seq)
                    {
                        // P1040b — a FIXED INSTRUMENT's stroke drawn before the run is not
                        // "background under the ink" when the run is somebody else's label:
                        // the ladder's rungs, the tape rails and the radar frame are painted
                        // first, so a hint laid across the +20 rung used to be invisible here.
                        // The instrument's own marks (same element) and world-anchored markers
                        // (order arrows over the ladder, native by design) stay exempt.
                        if (!IsFixedInstrumentStroke(stroke.Element)) continue;
                        // P1040c — the own-element exemption covers an instrument's OWN mark (a
                        // rung under the numeral it labels). The CMD panel draws its grid FRAMES
                        // and its words from one element (`cmd`), so that exemption hid the verb
                        // bar's top edge running through `DELTA` (the P1040b `DEL`/`TA`): a frame
                        // stroke is the grid the word sits in, never the word's own mark.
                        if (string.Equals(stroke.Element, text.Element, StringComparison.Ordinal)
                            && !IsGridElement(stroke.Element)) continue;
                        if (IsInstrumentFurniture(text.Element ?? "") || IsWorldMarker(text.Element)) continue;
                        // A near-opaque plate drawn AFTER the stroke and BEFORE the run hides
                        // the instrument behind the run (the F4 pane's 0.97 backing): the rung
                        // is not visible under the pane's own rows, so it is not a hit.
                        if (OpaquePlateBetween(text, stroke.Seq)) continue;
                    }
                    if (BehindLaterPlate(text)) continue;     // a pane drawn on top hides it
                    if (!HasLetter(text.Text)) continue;      // a scale graduation, not a label
                    // P1039 — the radar scope is FIXED furniture and world-anchored symbology
                    // (the target designator/lock bracket, the ballistic lead, the order
                    // chevrons) follows the target: a bracket may cross the scope's frame, its
                    // mode caption or a contact label, and neither mark may move. Stated
                    // exception (the brief's own example: world-anchored markers crossing
                    // fixed HUD furniture); the label keeps its full text.
                    if (IsRadarVsWorldMarker(stroke.Element, text.Element)) continue;
                    float pad = stroke.Width * 0.5f;
                    float inkTop = text.Y + text.H * InkTopFraction + 1f;
                    float inkBottom = text.Y + text.H * InkBottomFraction - 1f;
                    if (inkBottom <= inkTop) continue;
                    if (!SegmentHitsBox(stroke.A, stroke.B,
                        text.X - pad, inkTop - pad, text.X + text.W + pad, inkBottom + pad)) continue;
                    strokeHits.Add($"{text.Element}:{text.Text}@{text.X:0.#},{text.Y:0.#} x "
                        + $"stroke[{stroke.Element}] {stroke.A.X:0.#},{stroke.A.Y:0.#}->{stroke.B.X:0.#},{stroke.B.Y:0.#} w{stroke.Width:0.#}");
                }

            // ── P1040c: callout-crossed ─────────────────────────────────────
            // The shared training callout's POSITION is native (HudTrainingCallout,
            // indicator.cpp:461-701) and may not move, so it keeps its exemption in the
            // text-overlap classes. Jet's own furniture, however, must YIELD: this class counts
            // every Jet instrument stroke or run that is drawn across a callout's ink box. P1040b
            // read 0 for the `-20` rung numerals crossed at 1920x1080 because the pair
            // callout-vs-ladder was exempt from both sides ("fixed instrument furniture"), and
            // the rungs are drawn before the callout's run.
            var calloutHits = new List<string>();
            foreach (LayoutBox callout in _texts)
            {
                if (!string.Equals(callout.Element, "training", StringComparison.Ordinal)) continue;
                float inkTop = callout.Y + callout.H * InkTopFraction + 1f;
                float inkBottom = callout.Y + callout.H * InkBottomFraction - 1f;
                if (inkBottom <= inkTop) continue;
                foreach (LayoutStroke stroke in _strokes)
                {
                    if (!IsJetFurniture(stroke.Element)) continue;
                    // A near-opaque plate drawn after the stroke and before the callout's run
                    // hides the stroke under the callout (the same rule stroke-vs-text uses).
                    if (OpaquePlateBetween(callout, stroke.Seq)) continue;
                    float pad = stroke.Width * 0.5f;
                    if (!SegmentHitsBox(stroke.A, stroke.B, callout.X - pad, inkTop - pad,
                        callout.X + callout.W + pad, inkBottom + pad)) continue;
                    calloutHits.Add($"callout:{callout.Text}@{callout.X:0.#},{callout.Y:0.#} x "
                        + $"stroke[{stroke.Element}] {stroke.A.X:0.#},{stroke.A.Y:0.#}->{stroke.B.X:0.#},{stroke.B.Y:0.#}");
                }
                foreach (LayoutBox other in _texts)
                {
                    if (!IsJetFurniture(other.Element)) continue;
                    if (!Intersects(callout, other)) continue;
                    calloutHits.Add($"callout:{callout.Text}@{callout.X:0.#},{callout.Y:0.#} x "
                        + $"{other.Element}:{other.Text}@{other.X:0.#},{other.Y:0.#} {other.W:0.#}x{other.H:0.#}");
                }
            }

            // ── P1038b: text-outside-plate ─────────────────────────────────
            // Only a plate drawn BEFORE the run can be the plate that run belongs to (a
            // label drawn before the F4 pane is behind it, not on it).
            var outsidePlate = new List<string>();
            foreach (LayoutBox text in _texts)
            {
                LayoutFill? owner = null;
                foreach (LayoutFill plate in _plates)
                {
                    if (plate.Seq > text.Seq) break;
                    if (Intersects(new LayoutBox("#", plate.X, plate.Y, plate.W, plate.H, plate.Seq), text))
                        owner = plate;
                }
                if (owner is not { } fill) continue;
                if (Contains(new LayoutBox("#", fill.X, fill.Y, fill.W, fill.H, fill.Seq), text)) continue;
                outsidePlate.Add($"{text.Text}@{text.X:0.#},{text.Y:0.#} {text.W:0.#}x{text.H:0.#} leaves plate@{fill.X:0.#},{fill.Y:0.#} {fill.W:0.#}x{fill.H:0.#}");
            }

            // ── P1038b: text-off-screen ────────────────────────────────────
            // A pane that is sliding out (the native 0.5 s F4 slide) is intentionally
            // outside the viewport while its own content travels with it.
            var offScreen = new List<string>();
            float gutter = EdgeGutter * (scale > 0f ? scale : 1f);
            float right = viewportWidth - gutter, bottom = viewportHeight - gutter;
            foreach (LayoutBox text in _texts)
            {
                if (text.X >= gutter - 0.5f && text.Y >= gutter - 0.5f
                    && text.X + text.W <= right + 0.5f && text.Y + text.H <= bottom + 0.5f) continue;
                if (InSlidingPane(text, viewportWidth, viewportHeight)) continue;
                offScreen.Add($"{text.Text}@{text.X:0.#},{text.Y:0.#} {text.W:0.#}x{text.H:0.#} (viewport {viewportWidth}x{viewportHeight}, gutter {gutter:0.#})");
            }

            // ── P1038b: minimum glyph size ─────────────────────────────────
            var smallGlyphs = new List<string>();
            foreach (LayoutGlyph glyph in _glyphs)
            {
                if (glyph.H >= glyph.MinimumHeight - 0.5f) continue;
                smallGlyphs.Add($"{glyph.Id}@{glyph.X:0.#},{glyph.Y:0.#} {glyph.W:0.#}x{glyph.H:0.#} < cap {glyph.MinimumHeight:0.#}");
            }

            // ── P1038c: text-vs-text inside ONE plate ──────────────────────
            // Two runs that belong to the same plate (the last plate drawn before the run
            // that its box crosses) and intersect each other: the F4 title sitting on the
            // caption band was exactly this. Cross-plate text-vs-text stays informational
            // (TextOverlaps), because independent markers can legitimately share sky.
            var inPlateOverlaps = new List<string>();
            float panePad = JetInventoryPresentation.Pad * JetInventoryPresentation.Scale(viewportHeight);
            for (int i = 0; i < _texts.Count; i++)
                for (int j = i + 1; j < _texts.Count; j++)
                {
                    LayoutBox a = _texts[i], b = _texts[j];
                    if (!Intersects(a, b)) continue;
                    int owner = OwnerPlate(a, panePad);
                    if (owner < 0 || owner != OwnerPlate(b, panePad)) continue;
                    inPlateOverlaps.Add($"{a.Text}@{a.X:0.#},{a.Y:0.#} x {b.Text}@{b.X:0.#},{b.Y:0.#} in plate seq {owner}");
                }

            // ── P1039: text-vs-text across ELEMENTS ────────────────────────
            // Every run is stamped with the block that drew it (the renderer announces an
            // element before its runs). Two runs from DIFFERENT elements that intersect is
            // the count the brief requires: at 800×480 the designator readout landed on the
            // ladder `-10`, on `CUR` and on `PITCH 00`, and `LOCK 62%` landed on the
            // `HOSTILE` name line. Same-element rows (a two-line block) are its own layout,
            // already covered by `TextOverlapInPlate`.
            var crossOverlaps = new List<string>();
            for (int i = 0; i < _texts.Count; i++)
                for (int j = i + 1; j < _texts.Count; j++)
                {
                    LayoutBox a = _texts[i], b = _texts[j];
                    if (!Intersects(a, b)) continue;
                    if (a.Element is null || b.Element is null) continue;
                    if (string.Equals(a.Element, b.Element, StringComparison.Ordinal)) continue;
                    // Exception 1 — two FIXED INSTRUMENT FURNITURE marks (the ladder's
                    // numerals, the ACC/CUR order words, the heading/tape graduations).
                    // Native draws these over each other by design (the order arrows are
                    // drawn over the same ladder, indicator.cpp:786-791), so this pair stays
                    // informational in `TextOverlaps` and is not a readout crossing another
                    // element's reading.
                    if (IsInstrumentFurniture(a.Element) && IsInstrumentFurniture(b.Element)) continue;
                    // Exception 2 — an overlap HIDDEN behind a later near-opaque plate (the F4
                    // pane, alpha ≥ 240 of 255, the same threshold `LegibleBehindPane` uses):
                    // when the EARLIER run's share of the intersecting area is under the pane,
                    // the pilot sees one run there, not two.
                    if (OverlapHiddenByOpaquePlate(a, b)) continue;
                    crossOverlaps.Add($"{a.Element}:{a.Text}@{a.X:0.#},{a.Y:0.#} {a.W:0.#}x{a.H:0.#} "
                        + $"x {b.Element}:{b.Text}@{b.X:0.#},{b.Y:0.#} {b.W:0.#}x{b.H:0.#}");
                }

            // ── P1039b: text-vs-text inside ONE element ────────────────────
            // Any two runs the SAME block drew that intersect: the radar's contact labels and
            // ring captions (P1039 drew them all at fixed offsets, a solid mass of text), a
            // plate's rows, the stores block's rows. After the Jet radar's placement pass no
            // two reads of one element may share ink; a label the pass HID is never drawn, so
            // it cannot be counted here.
            var sameOverlaps = new List<string>();
            for (int i = 0; i < _texts.Count; i++)
                for (int j = i + 1; j < _texts.Count; j++)
                {
                    LayoutBox a = _texts[i], b = _texts[j];
                    if (!Intersects(a, b)) continue;
                    if (a.Element is null || b.Element is null) continue;
                    if (!string.Equals(a.Element, b.Element, StringComparison.Ordinal)) continue;
                    sameOverlaps.Add($"{a.Element}:{a.Text}@{a.X:0.#},{a.Y:0.#} {a.W:0.#}x{a.H:0.#} "
                        + $"x {b.Text}@{b.X:0.#},{b.Y:0.#} {b.W:0.#}x{b.H:0.#}");
                }

            // ── P1039c: text over ANOTHER contact's glyph (radar) ──────────
            // Every contact glyph/overlay box the radar reported, against every radar text
            // run: a reading drawn on a different contact's glyph (or on a caption's row) is
            // the defect the review saw. A contact's OWN glyph is its own mark (the marker
            // words M/P/T/SUB/ACC sit beside it by design), so it is excluded by index.
            var overGlyph = new List<string>();
            foreach (LayoutBox text in _texts)
            {
                if (!string.Equals(text.Element, "radar", StringComparison.Ordinal)) continue;
                foreach (LayoutContactGlyph glyph in _contactGlyphs)
                {
                    if (glyph.Index == text.Contact) continue;
                    var box = new LayoutBox("#", glyph.X, glyph.Y, glyph.W, glyph.H, glyph.Seq);
                    if (!Intersects(box, text)) continue;
                    overGlyph.Add($"{text.Text}@{text.X:0.#},{text.Y:0.#} {text.W:0.#}x{text.H:0.#} "
                        + $"over glyph of contact {glyph.Index}@{glyph.X:0.#},{glyph.Y:0.#} {glyph.W:0.#}x{glyph.H:0.#}");
                }
            }

            // ── P1039c: a band or leader stroke through a radar reading ────
            // The stated rule for defect 1 is the SLOT TEST (no label is placed across a band),
            // not a backing plate, so any ring or leader stroke crossing a radar run's ink band
            // is a hit; there is no plate exemption to apply. The ink band is the same
            // InkTopFraction…InkBottomFraction band the stroke-vs-text rule uses.
            var strokeText = new List<string>();
            foreach (LayoutBox text in _texts)
            {
                if (!string.Equals(text.Element, "radar", StringComparison.Ordinal)) continue;
                float inkTop = text.Y + text.H * InkTopFraction + 1f;
                float inkBottom = text.Y + text.H * InkBottomFraction - 1f;
                if (inkBottom <= inkTop) continue;
                foreach (LayoutRing ring in _rings)
                {
                    if (!RingHits(ring, text.X, inkTop, text.X + text.W, inkBottom)) continue;
                    if (RadarPlateCovers(text, ring.Seq)) continue;   // the plate interrupts the band
                    strokeText.Add($"{text.Text}@{text.X:0.#},{text.Y:0.#} x band r{ring.Radius:0.#} "
                        + $"{(ring.Round ? "circle" : "square")} c{ring.Centre.X:0.#},{ring.Centre.Y:0.#}");
                }
                foreach (LayoutStroke leader in _leaders)
                {
                    float pad = JetRadarLabels.RingPad;
                    if (!SegmentHitsBox(leader.A, leader.B, text.X - pad, inkTop - pad,
                        text.X + text.W + pad, inkBottom + pad)) continue;
                    if (RadarPlateCovers(text, leader.Seq)) continue;  // never used by the pass, kept honest
                    strokeText.Add($"{text.Text}@{text.X:0.#},{text.Y:0.#} x leader "
                        + $"{leader.A.X:0.#},{leader.A.Y:0.#}->{leader.B.X:0.#},{leader.B.Y:0.#} w{leader.Width:0.#}");
                }
            }

            // ── P1038c: cue-vs-own-readout ─────────────────────────────────
            // The cue is drawn BEFORE its caption, so the generic stroke-vs-text rule
            // (background strokes are exempt) cannot see this: the renderer reports each cue
            // arc and the readout block, and every arc chord is tested against that block.
            var cueHits = new List<string>();
            float cuePad = 2f * (scale > 0f ? scale : 1f);
            foreach (LayoutBox readout in _cueReadouts)
                foreach (LayoutCue cue in _cues)
                {
                    if (!ArcHitsBox(cue, readout, cuePad)) continue;
                    cueHits.Add($"LOCK readout@{readout.X:0.#},{readout.Y:0.#} {readout.W:0.#}x{readout.H:0.#} "
                        + $"x cue arc r{cue.Radius:0.#} {cue.Start * 180f / MathF.PI:0.#}+{cue.Sweep * 180f / MathF.PI:0.#}deg");
                }

            // ── P1038c: legible-behind-pane ────────────────────────────────
            // The F4 pane is the near-opaque plate (alpha ≥ 240 of 255) taller than four
            // rows. A run/icon drawn BEFORE it keeps (1−alpha) of its ink-vs-backdrop
            // channel difference; ≥ 8 of 255 (3.1 %) reads as legible through the backing.
            var legibleBehindPane = new List<string>();
            foreach (LayoutFill pane in _plates)
            {
                if (pane.H <= 64f || pane.Tint.A < 240) continue;
                float residual = 1f - pane.Tint.A / 255f;
                var paneBox = new LayoutBox("#", pane.X, pane.Y, pane.W, pane.H, pane.Seq);
                foreach (LayoutBox text in _texts)
                {
                    if (text.Seq >= pane.Seq || !Intersects(paneBox, text)) continue;
                    Color ink = _ink.TryGetValue(text.Seq, out Color recorded) ? recorded : backdrop;
                    float ghost = ChannelDelta(ink, backdrop) * residual;
                    if (ghost < GhostLegibleChannel) continue;
                    legibleBehindPane.Add($"{text.Text}@{text.X:0.#},{text.Y:0.#} ghost {ghost:0.#}/255 "
                        + $"behind plate seq {pane.Seq} alpha {pane.Tint.A}");
                }
                foreach (LayoutGlyph glyph in _glyphs)
                {
                    if (glyph.Seq >= pane.Seq) continue;
                    var glyphBox = new LayoutBox(glyph.Id, glyph.X, glyph.Y, glyph.W, glyph.H, glyph.Seq);
                    if (!Intersects(paneBox, glyphBox)) continue;
                    // Worst case: the brightest ink the palette carries.
                    float ghost = ChannelDelta(new Color(255, 255, 255), backdrop) * residual;
                    if (ghost < GhostLegibleChannel) continue;
                    legibleBehindPane.Add($"{glyph.Id}@{glyph.X:0.#},{glyph.Y:0.#} ghost {ghost:0.#}/255 "
                        + $"behind plate seq {pane.Seq} alpha {pane.Tint.A}");
                }
            }

            return new LayoutAudit(_texts.Count, _fills.Count, _strokes.Count, textOverlaps.Count,
                captionOverlaps.Count, boxOverlaps.Count, strokeHits.Count, outsidePlate.Count, offScreen.Count,
                _glyphs.Count, smallGlyphs.Count, inPlateOverlaps.Count, cueHits.Count, legibleBehindPane.Count,
                crossOverlaps.Count, sameOverlaps.Count, overGlyph.Count, strokeText.Count, _radarPlates.Count,
                captionOverlaps.Take(4).ToArray(), boxOverlaps.Take(4).ToArray(),
                strokeHits.Take(4).ToArray(), outsidePlate.Take(4).ToArray(),
                offScreen.Take(4).ToArray(), smallGlyphs.Take(4).ToArray(),
                inPlateOverlaps.Take(4).ToArray(), cueHits.Take(4).ToArray(), legibleBehindPane.Take(4).ToArray(),
                crossOverlaps.Take(4).ToArray(), sameOverlaps.Take(4).ToArray(),
                overGlyph.Take(4).ToArray(), strokeText.Take(4).ToArray(), calloutHits.Count,
                calloutHits.Take(4).ToArray());
        }

        /// <summary>P1039 — the one stated stroke-vs-text exception: the Jet radar's fixed
        /// scope (frame, bands, mode caption, contact labels) against a WORLD-ANCHORED
        /// marker (target designator, lock cue, ballistic lead, order chevrons, target data
        /// block). The radar cannot move and the marker is anchored to the projected target,
        /// so the two may share sky; no reading is truncated or hidden by it.</summary>
        private static bool IsRadarVsWorldMarker(string? a, string? b)
            => (a == "radar" && IsWorldMarker(b)) || (b == "radar" && IsWorldMarker(a));

        private static bool IsWorldMarker(string? element)
            => element is "target" or "lock" or "lead" or "order" or "target-pane";

        /// <summary>P1039 — the fixed instrument furniture whose marks may share sky with
        /// each other (the ladder numerals, the ACC/CUR order words, the heading and tape
        /// graduations). They are the same navigation instrument and native draws them over
        /// one another; the exception is stated in the report. The TRAINING CALLOUT stays here
        /// because its anchors are the shared native table (<c>indicator.cpp:461-701</c>) — a
        /// skin cannot move it without diverging from native — while the controls HINT is no
        /// longer exempt: its position is the skin's own, and that exemption is exactly why
        /// P1040's audit read 0 for a hint drawn across the ladder.
        /// <para><b>P1040b — the second half of the gap.</b> The stroke rule ignores every
        /// stroke drawn before the text (the ladder's rungs are drawn first), so a label over
        /// them was never a `StrokeHit` either. <see cref="IsFixedInstrumentStroke"/> closes
        /// that: a fixed instrument's stroke drawn before a run that is NOT that instrument's
        /// own mark, NOT shared tutorial geometry and NOT a world-anchored marker counts.</para>
        /// </summary>
        private static bool IsInstrumentFurniture(string element)
            => element is "ladder" or "order" or "hdg" or "spd" or "thr" or "training";

        /// <summary>P1040c — the elements that ARE fixed instrument furniture even though they
        /// are not exempt from the callout rule: Jet's own navigation furniture may not be drawn
        /// across a shared training callout (the callout's position is native and may not move,
        /// so Jet yields).</summary>
        private static bool IsJetFurniture(string? element)
            => element is "ladder" or "hdg" or "spd" or "thr" or "radar";

        /// <summary>P1040c — the elements whose STROKES are a grid, not a run's own mark: the
        /// CMD panel's cell frames. A frame stroke crossing a word is a strikethrough.</summary>
        private static bool IsGridElement(string? element) => element is "cmd";

        /// <summary>P1040b — the fixed instrument whose STROKES (rungs, rails, graduations,
        /// ring frame) are drawn before every label: a run drawn over one is the hint-over-the-
        /// ladder class the P1040 audit could not see. P1040c adds the CMD panel: its cell frames
        /// are drawn before its words, and the native wing combo overlaps the verb bar's band, so
        /// the verb bar's top edge ran through `DELTA`.</summary>
        private static bool IsFixedInstrumentStroke(string? element)
            => element is "ladder" or "hdg" or "spd" or "thr" or "radar" or "cmd";

        /// <summary>P1040b — is <paramref name="text"/> INSIDE a near-opaque plate (alpha
        /// ≥ 240/255, the F4 pane's backing) drawn after <paramref name="strokeSeq"/> and
        /// before the run? Then the plate hides whatever was painted under that region.</summary>
        private bool OpaquePlateBetween(LayoutBox text, int strokeSeq)
        {
            foreach (LayoutFill plate in _plates)
            {
                if (plate.Seq <= strokeSeq || plate.Seq >= text.Seq || plate.Tint.A < 240) continue;
                var box = new LayoutBox("#", plate.X, plate.Y, plate.W, plate.H, plate.Seq);
                if (Contains(box, text)) return true;
            }
            return false;
        }

        /// <summary>P1039 — is the INTERSECTION of two runs covered by a plate that was drawn
        /// after the earlier of them (alpha ≥ 240/255: the F4 pane's 0.97 backing)? Then the
        /// overlap region shows one run over the plate, never two.</summary>
        private bool OverlapHiddenByOpaquePlate(LayoutBox a, LayoutBox b)
        {
            // The plate must be drawn after the EARLIER run — that is what hides it. (The
            // later run may be the plate's own content, drawn after the plate.)
            int earlier = Math.Min(a.Seq, b.Seq);
            float x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
            float x1 = Math.Min(a.X + a.W, b.X + b.W), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
            if (x1 <= x0 || y1 <= y0) return false;
            foreach (LayoutFill plate in _plates)
            {
                if (plate.Seq <= earlier || plate.Tint.A < 240) continue;
                if (plate.X <= x0 + 0.01f && plate.Y <= y0 + 0.01f
                    && plate.X + plate.W >= x1 - 0.01f && plate.Y + plate.H >= y1 - 0.01f)
                    return true;
            }
            return false;
        }

        /// <summary>P1038c — the plate a run belongs to: the last plate drawn BEFORE the run
        /// that CONTAINS it. A pane's content keeps its native inner padding, so its owned
        /// box is inset horizontally by <c>Pad × pane scale</c>; the Jet WIP tag drawn at a
        /// pane's very edge (a debug overlay, not pane content) is therefore not owned by
        /// it. Readout plates own any run they contain.</summary>
        private int OwnerPlate(LayoutBox text, float panePad)
        {
            int owner = -1;
            foreach (LayoutFill plate in _plates)
            {
                if (plate.Seq > text.Seq) break;
                float inset = plate.H > 64f ? panePad : 0f;
                var box = new LayoutBox("#", plate.X + inset, plate.Y,
                    MathF.Max(0f, plate.W - 2f * inset), plate.H, plate.Seq);
                if (Contains(box, text)) owner = plate.Seq;
            }
            return owner;
        }

        /// <summary>P1038c — does an arc's chorded ink cross <paramref name="box"/>? The arc
        /// is sampled at 12 chords (a cue arc is at most 30°), each tested with the same
        /// Liang–Barsky slab clip the stroke rule uses.</summary>
        private static bool ArcHitsBox(LayoutCue cue, LayoutBox box, float pad)
        {
            const int samples = 12;
            float x0 = box.X - pad, y0 = box.Y - pad;
            float x1 = box.X + box.W + pad, y1 = box.Y + box.H + pad;
            Vector2 previous = ArcPoint(cue, 0f);
            for (int i = 1; i <= samples; i++)
            {
                Vector2 point = ArcPoint(cue, i / (float)samples);
                if (SegmentHitsBox(previous, point, x0, y0, x1, y1)) return true;
                previous = point;
            }
            return false;
        }

        private static Vector2 ArcPoint(LayoutCue cue, float t)
        {
            float angle = cue.Start + cue.Sweep * t;
            return new(cue.Center.X + MathF.Cos(angle) * cue.Radius,
                cue.Center.Y + MathF.Sin(angle) * cue.Radius);
        }

        /// <summary>P1039c — the distinct texts recorded inside <paramref name="box"/> (the
        /// designator readout's own runs: the fallback block's rows are evidence, not prose).</summary>
        public string[] TextsInside(RectangleF box)
        {
            if (box.Width <= 0f || box.Height <= 0f) return Array.Empty<string>();
            var outer = new LayoutBox("#", box.X, box.Y, box.Width, box.Height, int.MaxValue);
            var list = new List<string>();
            foreach (LayoutBox text in _texts)
            {
                if (!Contains(outer, text)) continue;
                if (!list.Contains(text.Text)) list.Add(text.Text);
            }
            return list.ToArray();
        }

        /// <summary>P1040b — every text run recorded inside <paramref name="box"/>, with its
        /// ink box: the CMD cell evidence (a cell word must be inside its cell).</summary>
        public object[] RunsInside(RectangleF box)
        {
            if (box.Width <= 0f || box.Height <= 0f) return Array.Empty<object>();
            var outer = new LayoutBox("#", box.X, box.Y, box.Width, box.Height, int.MaxValue);
            var list = new List<object>();
            foreach (LayoutBox text in _texts)
            {
                if (!Intersects(outer, text)) continue;
                bool inside = Contains(outer, text);
                list.Add(new { text = text.Text, x = MathF.Round(text.X, 1), y = MathF.Round(text.Y, 1),
                    w = MathF.Round(text.W, 1), h = MathF.Round(text.H, 1), inside });
            }
            return list.ToArray();
        }

        /// <summary>P1040b — every recorded run of <paramref name="text"/> with its ink box.</summary>
        public (float X, float Y, float W, float H)[] RunsWithText(string text)
        {
            var list = new List<(float X, float Y, float W, float H)>();
            foreach (LayoutBox run in _texts)
            {
                if (!string.Equals(run.Text, text, StringComparison.Ordinal)) continue;
                list.Add((run.X, run.Y, run.W, run.H));
            }
            return list.ToArray();
        }

        /// <summary>P1039c — true when a radar backing plate drawn AFTER the stroke and BEFORE
        /// the run contains it: the plate interrupts the stroke, so the run is not struck through
        /// (the plate itself is counted instead).</summary>
        private bool RadarPlateCovers(LayoutBox text, int strokeSeq)
        {
            foreach (LayoutFill plate in _radarPlates)
            {
                if (plate.Seq <= strokeSeq || plate.Seq >= text.Seq) continue;
                var box = new LayoutBox("#", plate.X, plate.Y, plate.W, plate.H, plate.Seq);
                if (Contains(box, text)) return true;
            }
            return false;
        }

        /// <summary>P1039c — does a band stroke pass through the box (x0,y0)–(x1,y1)? A circle</summary>
        /// is tested exactly (nearest point inside the radius, farthest corner outside it); a
        /// square band is four segments. The stroke's half width plus half a pixel is added.</summary>
        private static bool RingHits(LayoutRing ring, float x0, float y0, float x1, float y1)
        {
            if (x1 < x0 || y1 < y0 || ring.Radius <= 0f) return false;
            float pad = JetRadarLabels.RingPad;
            Vector2 c = ring.Centre;
            if (ring.Round)
            {
                float nx = Math.Clamp(c.X, x0, x1), ny = Math.Clamp(c.Y, y0, y1);
                float near = MathF.Sqrt((nx - c.X) * (nx - c.X) + (ny - c.Y) * (ny - c.Y));
                float far = MathF.Sqrt(MathF.Max(
                    (x0 - c.X) * (x0 - c.X) + (y0 - c.Y) * (y0 - c.Y),
                    MathF.Max((x1 - c.X) * (x1 - c.X) + (y0 - c.Y) * (y0 - c.Y),
                        MathF.Max((x0 - c.X) * (x0 - c.X) + (y1 - c.Y) * (y1 - c.Y),
                            (x1 - c.X) * (x1 - c.X) + (y1 - c.Y) * (y1 - c.Y)))));
                return near <= ring.Radius + pad && far >= ring.Radius - pad;
            }
            float r = ring.Radius;
            return SegmentHitsBox(new(c.X - r, c.Y - r), new(c.X + r, c.Y - r), x0 - pad, y0 - pad, x1 + pad, y1 + pad)
                || SegmentHitsBox(new(c.X + r, c.Y - r), new(c.X + r, c.Y + r), x0 - pad, y0 - pad, x1 + pad, y1 + pad)
                || SegmentHitsBox(new(c.X + r, c.Y + r), new(c.X - r, c.Y + r), x0 - pad, y0 - pad, x1 + pad, y1 + pad)
                || SegmentHitsBox(new(c.X - r, c.Y + r), new(c.X - r, c.Y - r), x0 - pad, y0 - pad, x1 + pad, y1 + pad);
        }

        /// <summary>P1038c — the largest per-channel difference between two colours (0…255).</summary>
        private static float ChannelDelta(Color a, Color b)
            => MathF.Max(MathF.Abs(a.R - b.R), MathF.Max(MathF.Abs(a.G - b.G), MathF.Abs(a.B - b.B)));

        /// <summary>P1038c — the legibility threshold of a ghost through the pane backing:
        /// 8 of 255 channel separation (3.1 %) from the plate. Stated once so the plate
        /// opacity and the audit can never drift apart.</summary>
        public const float GhostLegibleChannel = 8f;

        /// <summary>The audit's edge gutter, in standard-stroke units (× scale): the same
        /// 4 px inset every Jet plate keeps from a viewport edge.</summary>
        public const float EdgeGutter = 4f;

        /// <summary>P1038b — where the capital ink starts/ends inside a recorded run box:
        /// SpriteFont's MeasureString height is the line spacing (22 px at scale 1 for the
        /// 8 pt HUD font), while the capitals are about ten pixels starting four pixels
        /// below the run's top (measured on the proof's own frames).</summary>
        public const float InkTopFraction = 0.18f;
        public const float InkBottomFraction = 0.64f;

        /// <summary>P1038b — true when the run is the content of a pane that is not fully
        /// on screen: the F4 pane slides out by design (native 0.5 s slide), so its rows
        /// travel outside the viewport with it.</summary>
        private bool InSlidingPane(LayoutBox text, int viewportWidth, int viewportHeight)
        {
            foreach (LayoutFill pane in _plates)
            {
                if (pane.Seq > text.Seq) break;
                if (pane.H <= 64f) continue;
                var box = new LayoutBox("#", pane.X, pane.Y, pane.W, pane.H, pane.Seq);
                if (!Contains(box, text)) continue;
                if (pane.X < 0f || pane.Y < 0f
                    || pane.X + pane.W > viewportWidth || pane.Y + pane.H > viewportHeight) return true;
            }
            return false;
        }

        /// <summary>P1038b — true when a plate (readout box or pane) drawn AFTER this run
        /// covers the run's centre: the HUD element behind an opaque plate is background,
        /// so a bar or frame drawn on the plate cannot strike through it.</summary>
        private bool BehindLaterPlate(LayoutBox text)
        {
            float cx = text.X + text.W * 0.5f, cy = text.Y + text.H * 0.5f;
            foreach (LayoutFill plate in _plates)
                if (plate.Seq > text.Seq
                    && cx >= plate.X && cx <= plate.X + plate.W
                    && cy >= plate.Y && cy <= plate.Y + plate.H) return true;
            return false;
        }

        /// <summary>P1038b — true when a run carries at least one letter. A run of bare
        /// digits is a tape/ladder graduation mark (or a bare numeral readout): it is
        /// furniture every overlay crosses, not a word label, and the stroke check is about
        /// words being struck through.</summary>
        private static bool HasLetter(string text)
        {
            foreach (char c in text) if (char.IsAsciiLetter(c)) return true;
            return false;
        }

        private static bool IsCaption(string text)
            => text == JetHudRenderer.SpeedCaptionText || text == JetHudRenderer.ThrottleCaptionText;

        private static bool Intersects(LayoutBox a, LayoutBox b)
            => a.X < b.X + b.W - 1f && b.X < a.X + a.W - 1f
            && a.Y < b.Y + b.H - 1f && b.Y < a.Y + a.H - 1f;

        private static bool InAnyPane(LayoutBox text, List<LayoutBox> panes)
        {
            foreach (LayoutBox pane in panes) if (Contains(pane, text)) return true;
            return false;
        }

        private static bool Contains(LayoutBox outer, LayoutBox inner)
            => inner.X >= outer.X - 1.5f && inner.X + inner.W <= outer.X + outer.W + 1.5f
            && inner.Y >= outer.Y - 1.5f && inner.Y + inner.H <= outer.Y + outer.H + 1.5f;

        /// <summary>True when the segment (a→b) crosses the axis-aligned box (Liang–Barsky
        /// slab clip). The box is already widened by half the stroke width.</summary>
        private static bool SegmentHitsBox(Vector2 a, Vector2 b, float x0, float y0, float x1, float y1)
        {
            if (x1 < x0 || y1 < y0) return false;
            // The degenerate (dot) stroke: a point inside the box.
            float dx = b.X - a.X, dy = b.Y - a.Y;
            if (MathF.Abs(dx) < 1e-5f && MathF.Abs(dy) < 1e-5f)
                return a.X >= x0 && a.X <= x1 && a.Y >= y0 && a.Y <= y1;
            float t0 = 0f, t1 = 1f;
            if (!Clip(-dx, a.X - x0, ref t0, ref t1)) return false;
            if (!Clip(dx, x1 - a.X, ref t0, ref t1)) return false;
            if (!Clip(-dy, a.Y - y0, ref t0, ref t1)) return false;
            if (!Clip(dy, y1 - a.Y, ref t0, ref t1)) return false;
            return t0 <= t1;
        }

        private static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (MathF.Abs(p) < 1e-6f) return q >= 0f;
            float r = q / p;
            if (p < 0f)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
    }

    /// <summary>P1039 — a recorded text run: its ink box, draw order and the ELEMENT that
    /// drew it (`TextOverlapCross` compares runs from different elements). P1039c — the radar
    /// CONTACT the run belongs to (-1 for a caption or for any run outside the radar), so
    /// `TextOverGlyph` can exclude a contact's own glyph.</summary>
    private readonly record struct LayoutBox(string Text, float X, float Y, float W, float H, int Seq,
        string? Element = null, int Contact = -1);

    /// <summary>P1039c — one radar contact's glyph/overlay box (the obstacle and the
    /// text-over-glyph reference), with the contact it belongs to.</summary>
    private readonly record struct LayoutContactGlyph(int Index, float X, float Y, float W, float H, int Seq);

    /// <summary>P1039c — one range-band stroke: a circle or a square outline of one radius.</summary>
    private readonly record struct LayoutRing(Vector2 Centre, float Radius, bool Round, int Seq);

    /// <summary>P1038b — one recorded stroke: its endpoints, ink width (px) and draw order.</summary>
    private readonly record struct LayoutStroke(Vector2 A, Vector2 B, float Width, int Seq,
        string? Element = null);

    /// <summary>P1038c — a recorded plate (a readout box or a pane), its draw order and the
    /// tint it was filled with (its alpha is the plate's opacity).</summary>
    private readonly record struct LayoutFill(float X, float Y, float W, float H, int Seq, Color Tint);

    /// <summary>P1038b — one recorded icon glyph, its minimum height and draw order.</summary>
    private readonly record struct LayoutGlyph(string Id, float X, float Y, float W, float H,
        float MinimumHeight, int Seq);

    /// <summary>P1038c — one recorded lock-cue arc.</summary>
    private readonly record struct LayoutCue(Vector2 Center, float Radius, float Start, float Sweep, int Seq);

    private sealed record LayoutAudit(int Texts, int Fills, int Strokes, int TextOverlaps, int CaptionOverlaps,
        int BoxOverlaps, int StrokeHits, int TextOutsidePlate, int TextOffScreen, int Icons, int IconTooSmall,
        int TextOverlapInPlate, int CueReadoutHits, int LegibleBehindPane, int TextOverlapCross,
        int TextOverlapSame, int TextOverGlyph, int StrokeThroughText, int RadarPlates,
        string[] CaptionOverlapSamples, string[] BoxOverlapSamples, string[] StrokeHitSamples,
        string[] TextOutsidePlateSamples, string[] TextOffScreenSamples, string[] IconTooSmallSamples,
        string[] TextOverlapInPlateSamples, string[] CueReadoutHitSamples, string[] LegibleBehindPaneSamples,
        string[] TextOverlapCrossSamples, string[] TextOverlapSameSamples, string[] TextOverGlyphSamples,
        string[] StrokeThroughTextSamples, int CalloutCrossed, string[] CalloutCrossedSamples);
}

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
    public string[] DiffCaptures { get; init; } = Array.Empty<string>();
    public string? Filter { get; init; }
    public int MaxDiffs { get; init; } = 10;
    public bool Test { get; init; }
    public bool Samples { get; init; }
    public bool NoBaselineVerify { get; init; }

    public static Options Parse(string[] arguments)
    {
        string? capture = null, compare = null, selfTest = null, filter = null, output = null;
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
                    Console.WriteLine("hud2-proof [<outputDir>] [--test] [--samples] [--capture <dir>] [--compare <dir>] [--self-test <dir>] [--diff-captures <a> <b>] [--filter <text>] [--max-diffs <n>] [--no-baseline-verify]");
                    Environment.Exit(0); break;
                default:
                    if (arguments[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unknown flag " + arguments[i]);
                    positional.Add(arguments[i]); break;
            }
        }
        if (positional.Count > 0) output = positional[0];
        return new Options
        {
            Output = output ?? (capture ?? compare ?? selfTest ?? Path.Combine(".scratch", "p980", "render")),
            Capture = capture, Compare = compare, SelfTest = selfTest, DiffCaptures = diff.ToArray(),
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
            renderer.Draw(model, GraphicsDevice.Viewport);
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
    private static HudModel Scenario(string scenario, int w, int h)
    {
        var frame = new HudFrame(.82f, .7f, .8f, .9f, 1, 1, 260, .6f, true, true, true, 0, false, 0, true, true, 15, 24,
            new WeaponStatusRow[] { new(0, "Rapid fire plasma cannon", true, 0, 200, WeaponStatus.NotReady), new(1, "Interceptor missile", false, 0, 0, WeaponStatus.Ready) }, false, 0, 2500,
            new(.2f, -.3f, .1f, false), 85, ShipWarningMask.OutOfBounds,
            SignaturePercent: 42f, Detected: scenario == "target-full", AutopilotEngaged: true, VectorLockEngaged: true,
            CarriedFlag: true, CarriedArtifact: scenario == "target-full");
        if (scenario == "critical") frame = frame with { HullFraction = .08f, ShieldFraction = 0, FuelFraction = .10f, EnergyFraction = .02f, Flashing = true };
        if (scenario == "lifepod") frame = frame with { IsAdriftLifepod = true, OxygenSecondsRemaining = 95 };
        float spread = Math.Clamp(h / 680f, 1.15f, 2.1f);
        bool target = scenario is "combat" or "critical" or "target-full" || scenario.StartsWith("radar-");
        // P980 — one scenario carries every radar overlay flag and one carries every
        // target-pane field, so the rows HUD-2 never showed have a baseline frame
        // without changing what the original scenarios present.
        bool overlays = scenario == "radar-overlays";
        bool full = scenario == "target-full";
        bool chase = scenario == "chase";
        bool flightCockpit = scenario is "flight" or "chat-stress" or "combat" or "critical" or "lifepod" or "training"
            || scenario.StartsWith("radar-");
        var mode = scenario == "radar-overlays" ? RadarMode.All
            : scenario.StartsWith("radar-") ? Enum.Parse<RadarMode>(scenario[6..]) : RadarMode.Default;
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
        return HudModel.Create(frame, new HudContext
        {
            Docked = scenario == "docked", Dead = scenario == "dead",
            CommandView = scenario == "command", Hidden = scenario == "hidden",
            Camera = scenario == "command" ? CameraMode.Command : chase ? CameraMode.Chase : CameraMode.Cockpit,
            VelocityMarker = flightCockpit ? new(w / 2f - 60, h / 2f + 20) : null,
            OrderArrows = flightCockpit ? new(new(1f, 0f), new(0f, 1f), false) : null,
            ChaseAim = chase ? new(w / 2f + 40, h / 2f - 30) : null,
            Target = target ? new(new(w / 2 + 126.5f * spread, h / 2 - 112.5f * spread), true, "Enemy fighter with a long pilot name", .6f, .3f, true, full ? .42f : null, 1250, 43, new(1, 0, 0)) : null,
            TargetPane = target ? new(true, "Enemy fighter with a long pilot name", "Fighter", "Rixian", new(1, 0, 0), .6f, .3f, 1250f, 43f,
                full ? .42f : null, full ? 15f : null, true, true,
                TimeToIntercept: full ? TargetTti.Text(1250f, 260f, 43f) : null) : null,
            // Native draws the ballistic lead only after the hull/relay capability
            // gate succeeds (indicator.cpp:283-330).
            SelectionBracket = full,
            Lead = target ? new(new(w / 2 + 100, h / 2 - 50), new(w / 2 + 200, h / 2 - 80), true, .8f, 1, Granted: full, Visible: true) : null,
            Lock = target ? new(new(w / 2 + 200, h / 2 - 80), MissileLockFeedback.LockState.Partial, 1) : null,
            HitConfirmation = target, PodReturnInstruction = "Shift+Z / G AUTOPILOT TO BASE",
            TargetBearing = target ? new(.8f, -.2f) : null,
            Notices = new[] { new HudNotice("NO ENEMY FIGHTER IN SECTOR", false, 1), new HudNotice("Reloading ammo...", true, 1) },
            Chat = new(scenario == "chat-stress" ? Enumerable.Range(0, 8).Select(i => $"[TEAM] Pilot {i}: " + new string('W', 250)).ToArray() : new[] { "[TEAM] Pilot: defend the base", "[ALL] Enemy: incoming" }, 0, scenario == "chat-stress" ? new string('W', 250) : "returning", true, 1, "[TEAM] "),
            FlightPartsLine = "WEAPONS: GROUP  BOOST: ON  [1:Plasma]", CargoLine = "CARGO 1/5  Ammo pack x3",
            // P980 — P785 training callout (native RenderTrainingOverlay case 17,
            // indicator.cpp:634-641) and P949's one-time control hint.
            TrainingOverlayId = scenario == "training" ? 17 : 0,
            ControlHint = scenario == "training" ? "Shift+F1 opens the controls" : null,
            // P980 — the native command recipient/verb row (HUD-10).
            Commandee = scenario == "commandee" ? "attack Enemy fighter" : null,
            // P980 — a mid-slide pane (native .5 s slide, dialog.mdl:5387,5404).
            InventorySlide = scenario == "slide" ? .5f : 0f,
            TargetPaneSlide = scenario == "slide" ? .5f : 0f,
            InventoryPane = new HudInventoryPane("Fig Adv", new HudInventoryRow[]
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
            Contacts = contacts, InventoryVisible = scenario != "systems",
            CommandLines = scenario == "command" ? new[] { "COMMAND SHIP: Scout", "Hull 82% Shield 70%" } : Array.Empty<string>(),
            RadarMode = mode,
            RoundRadar = scenario.StartsWith("radar-"),
            Ripcord = "Ripcord in: 9",
            Ghost = scenario == "ghost",
            Console = new HudConsoleText
            {
                SectorName = "Alystra",
                CarrierName = scenario is "flight" or "chat-stress" ? "Pilot Alpha" : null,
                BoardState = scenario is "flight" or "chat-stress" ? 2 : 0,
                AcceptedOrder = scenario is "flight" or "combat" ? "defend Garrison" : null,
                QueuedOrder = scenario is "flight" or "combat" ? "attack Enemy fighter" : null,
                VoteText = scenario == "critical" ? "Vote: Mutiny against the commander? (Y/N)" : null,
                OrdinaryTips = new[] { "Welcome to the sector", "Alystra: 3 miners on duty", "Payday: $500" },
                CriticalTips = new[] { new HudNotice("Reloading ammo...", true, 1) },
                SideColor = new(0.07f, 0.95f, 0.84f),
                // P980 — lives/kills badges (native console.cpp:812-821): the
                // lives badge is only drawn below 10, the kills badge in a deathmatch.
                Lives = scenario == "ghost" ? 3 : null,
                MissionKills = scenario == "ghost" ? 7 : null,
                Deathmatch = scenario == "ghost",
            }
        });
    }
}

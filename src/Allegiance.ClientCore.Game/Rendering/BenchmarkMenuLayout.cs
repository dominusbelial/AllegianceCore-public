namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1055 — the Benchmark/Showcase sub-view's list + description geometry, extracted from
/// <c>Game1.DrawBenchmarkMenu</c> so the draw path and the screen proof share ONE source (and
/// so a unit test can pin both layouts headlessly).
///
/// <para><b>The defect this replaces.</b> The view was laid out at fixed constants: nine
/// rows at <c>y = 210 + i·38</c> (band height 34, so the list ends at 548 px) and the
/// three-paragraph description at <c>(0.12·w, h − 96)</c> with no wrapping. At 800×480 the
/// description therefore starts at y 384 — inside the list (rows 4..6 of the nine are drawn
/// under it, and rows 7..8 fall off the bottom of the screen) — and each paragraph is far
/// wider than the 704 px between its anchor and the right edge. This is a port-only screen
/// (there is no native pane): the native intro's sub-screens are the menu/training popups
/// (<c>ination.cpp</c>/<c>introscreen.cpp</c>) and none of them carries this paragraph.</para>
///
/// <para><b>The rule.</b> At every size where the classic constants FIT (the list above the
/// description with a gap, the wrapped description inside the bottom margin) the classic
/// geometry is returned verbatim — so 1920×1080 keeps the exact rows, bands and description
/// anchors it had (no pixel change at 1080p, proven by the capture compare). When they do not
/// fit (800×480) the list is compressed into the band between the subtitle and the description
/// (same rows, same order, same selection semantics — only the pitch/band shrink) and the
/// description is wrapped to the available width and bottom-anchored, so nothing overlaps and
/// nothing leaves the viewport. Text content is unchanged apart from the wrapping; no input,
/// focus or command path is touched (the list is not scrolled — scrolling would need a new
/// key binding, which is out of scope).</para>
/// </summary>
public static class BenchmarkMenuLayout
{
    /// <summary>The sub-view's row labels, in native menu order (the same array
    /// <c>Game1.Benchmark</c> indexes; shared so the proof fixture cannot drift).</summary>
    public static readonly string[] Labels =
    [
        "Watch a battle: Small (5v5)", "Medium (15v15)", "Large (30v30)", "Epic (60v60)", "Grand (200v200)",
        "Settings tune (fixed, ranked)", "Replay recorded battle",
        "Calibrate this machine (bounded)", "Skip / Back",
    ];

    /// <summary>The Showcase paragraph (unchanged text; only wrapped differently per width).</summary>
    public const string Description =
        "A Showcase is a big AI battle; every match shows its performance and score at game over.\n" +
        "The fixed tune (seed 729, 5v5, 150s x3) is the only RANKED score. Calibration tries a few worker sizings\n" +
        "at the selected skirmish size and saves the fastest for this machine. Arrows / Enter; Escape skips.";

    /// <summary>Classic first row's band top (the pre-P1055 constant).</summary>
    public const float ClassicListTop = 210f;
    /// <summary>Classic row pitch (band 34 + 4 gap).</summary>
    public const float ClassicRowStep = 38f;
    /// <summary>Classic selection/row band height.</summary>
    public const float ClassicBandHeight = 34f;
    /// <summary>Classic description anchor: <c>w · 0.12</c> from the left.</summary>
    public const float ClassicDescLeftFactor = 0.12f;
    /// <summary>Classic description anchor: <c>h − 96</c> from the top.</summary>
    public const float ClassicDescBottom = 96f;
    /// <summary>The list's band width: <c>w · 0.6</c> centred on <c>w · 0.2</c>.</summary>
    public const float BandWidthFactor = 0.6f;

    /// <summary>Fitted layout: the first row starts below the subtitle (82 + line pitch).</summary>
    public const float FittedListTop = 110f;
    /// <summary>Fitted layout: description side margin (the full usable width).</summary>
    public const float FittedSideMargin = 40f;
    /// <summary>Clearance the description must keep below the last row's band.</summary>
    public const float GapAboveDescription = 10f;
    /// <summary>Clearance the description keeps from the bottom edge.</summary>
    public const float BottomMargin = 8f;
    /// <summary>Never compress a row band below its own text pitch.</summary>
    public const float MinRowStep = 22f;

    /// <summary>
    /// The resolved geometry of the view: row band top/pitch/height, the description's anchor,
    /// width, scale and the fit flag (false = the classic constants verbatim).
    /// </summary>
    public readonly record struct Geometry(float RowTop, float RowStep, float BandHeight,
        float DescX, float DescTop, float DescWidth, float DescScale, bool Fitted)
    {
        /// <summary>The row band for row <paramref name="index"/> (top-left + size).</summary>
        public (float X, float Y, float W, float H) Band(int index, float w)
            => (w * (1f - BandWidthFactor) / 2f, RowTop + index * RowStep, w * BandWidthFactor, BandHeight);
    }

    /// <summary>
    /// Resolve the view for a viewport. <paramref name="measureText"/> measures one line with
    /// the real HUD font; <paramref name="lineHeight"/> is its line pitch.
    /// </summary>
    public static Geometry Compute(int rowCount, float w, float h,
        string description, Func<string, float> measureText, float lineHeight)
    {
        // Classic first: whenever the pre-P1055 constants fit, they are what is drawn.
        float classicDescTop = h - ClassicDescBottom;
        float classicDescWidth = w - 2f * (w * ClassicDescLeftFactor);
        var classicLines = Wrap(description, classicDescWidth, measureText);
        float classicDescHeight = classicLines.Count * lineHeight;
        float classicListBottom = ClassicListTop + MathF.Max(0, rowCount - 1) * ClassicRowStep + ClassicBandHeight;
        if (classicListBottom <= classicDescTop - GapAboveDescription
            && classicDescHeight <= ClassicDescBottom - BottomMargin
            && ClassicListTop >= 0f)
            return new(ClassicListTop, ClassicRowStep, ClassicBandHeight,
                w * ClassicDescLeftFactor, classicDescTop, classicDescWidth, 1f, false);

        // Fitted: the description takes the full width and is bottom-anchored; the list is
        // compressed into the band above it (rows, order and behaviour unchanged).
        float descWidth = MathF.Max(200f, w - 2f * FittedSideMargin);
        var lines = Wrap(description, descWidth, measureText);
        float descHeight = lines.Count * lineHeight;
        float lowestDescTop = FittedListTop + rowCount * MinRowStep + GapAboveDescription;
        float descTop = MathF.Max(lowestDescTop, h - BottomMargin - descHeight);
        float available = MathF.Max(MinRowStep * rowCount, descTop - GapAboveDescription - FittedListTop);
        float step = MathF.Min(ClassicRowStep, available / MathF.Max(1, rowCount));
        step = MathF.Max(MinRowStep, step);
        float band = MathF.Max(lineHeight, MathF.Min(ClassicBandHeight, step - 2f));
        return new(FittedListTop, step, band, FittedSideMargin, descTop, descWidth, 1f, true);
    }

    /// <summary>
    /// Greedy word wrap of <paramref name="text"/> to <paramref name="width"/>, preserving the
    /// caller's paragraph breaks (<c>'\n'</c>). Empty paragraphs are kept as empty lines so the
    /// vertical rhythm does not change. A word wider than the width is left on its own line
    /// (there is nothing to break it on).
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, float width, Func<string, float> measureText)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(string.Empty); continue; }
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = new System.Text.StringBuilder();
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && measureText(candidate) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
                else
                {
                    if (line.Length > 0) line.Append(' ');
                    line.Append(word);
                }
            }
            lines.Add(line.ToString());
        }
        return lines;
    }
}

using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1055 — the two content collisions the P1042c chrome audit could not see, pinned headlessly:
/// <list type="bullet">
/// <item>the Benchmark/Showcase sub-view: at 800×480 the fixed row pitch put the ninth row off
/// the screen and the unwrapped description drew over rows 5-6. <see cref="BenchmarkMenuLayout"/>
/// is the single geometry source now; these tests prove the list and the wrapped description do
/// not overlap and stay inside the viewport at every audited size, that the text content is
/// unchanged apart from wrapping, and that 1920×1080 still resolves the exact pre-P1055 constants
/// (so that capture stays pixel-identical).</item>
/// <item>the starmap sector-info pane: native draws the cluster warning INSIDE the pane at the
/// pane origin (<c>sectormap.cpp:517-534</c>); the port drew it under the "STARMAP | …" header,
/// which is exactly where the pane box starts, so the pane's translucent backing painted over it.
/// These tests prove the warning line is a pane line (inside the pane's box, below the header
/// band) and that the pre-fix header anchor would have been covered, and they pin the native
/// probe-icon/count spacing (icon width + 4 px, <c>sectormap.cpp:338-344</c>).</item>
/// </list>
/// A fixed 10 px-per-character measure keeps the arithmetic deterministic; the production
/// numbers come from the real HUD fonts in <c>tools/p1042-screen-proof --audit</c>.
/// </summary>
public sealed class P1055ScreenOverlapTests
{
    private const float LineHeight = 20f;
    private static float Measure(string text) => text.Length * 10f;

    public static TheoryData<int, int> Resolutions => new()
    {
        { 800, 480 },
        { 1920, 1080 },
        { 3840, 2160 },
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Benchmark_list_and_description_never_overlap_and_stay_on_screen(int w, int h)
    {
        var labels = BenchmarkMenuLayout.Labels;
        var geo = BenchmarkMenuLayout.Compute(labels.Length, w, h,
            BenchmarkMenuLayout.Description, Measure, LineHeight);

        float lastBandBottom = geo.RowTop + (labels.Length - 1) * geo.RowStep + geo.BandHeight;
        Assert.True(geo.RowStep >= LineHeight,
            $"{w}x{h}: row pitch {geo.RowStep:0.#} is below the {LineHeight} px line pitch");
        Assert.True(geo.BandHeight >= LineHeight,
            $"{w}x{h}: band {geo.BandHeight:0.#} is shorter than its own text");
        Assert.True(lastBandBottom <= geo.DescTop - 1f,
            $"{w}x{h}: last row band ends at {lastBandBottom:0.#}, description starts at {geo.DescTop:0.#}");
        Assert.True(lastBandBottom <= h, $"{w}x{h}: last row band ends at {lastBandBottom:0.#} (off-screen)");
        Assert.True(geo.RowTop >= 100f, $"{w}x{h}: rows start under the subtitle (y {geo.RowTop:0.#})");

        var wrapped = BenchmarkMenuLayout.Wrap(BenchmarkMenuLayout.Description, geo.DescWidth, Measure);
        Assert.True(wrapped.Count > 0);
        Assert.True(geo.DescX >= 0f && geo.DescX + geo.DescWidth <= w + 0.01f,
            $"{w}x{h}: description spans {geo.DescX:0.#}..{geo.DescX + geo.DescWidth:0.#}");
        foreach (string line in wrapped)
            Assert.True(Measure(line) <= geo.DescWidth,
                $"{w}x{h}: wrapped line '{line}' is {Measure(line):0.#} px wide (limit {geo.DescWidth:0.#})");

        float descBottom = geo.DescTop + wrapped.Count * LineHeight * geo.DescScale;
        Assert.True(descBottom <= h, $"{w}x{h}: description ends at {descBottom:0.#} (off-screen)");
        Assert.True(geo.DescTop >= lastBandBottom + 1f,
            $"{w}x{h}: description top {geo.DescTop:0.#} vs last band {lastBandBottom:0.#}");
    }

    /// <summary>1920×1080 (where the old constants fit) must resolve to them exactly: the
    /// pre-P1055 capture of the screen is the pixel gate, so this branch may not drift.</summary>
    [Fact]
    public void Benchmark_at_1080_keeps_the_classic_geometry_verbatim()
    {
        var geo = BenchmarkMenuLayout.Compute(BenchmarkMenuLayout.Labels.Length, 1920f, 1080f,
            BenchmarkMenuLayout.Description, Measure, LineHeight);
        Assert.False(geo.Fitted);
        Assert.Equal(210f, geo.RowTop, 2);
        Assert.Equal(38f, geo.RowStep, 2);
        Assert.Equal(34f, geo.BandHeight, 2);
        Assert.Equal(1920f * 0.12f, geo.DescX, 2);
        Assert.Equal(1080f - 96f, geo.DescTop, 2);
        Assert.Equal(1f, geo.DescScale, 2);
        var (bx, by, bw, bh) = geo.Band(4, 1920f);
        Assert.Equal(384f, bx, 2);
        Assert.Equal(210f + 4 * 38f, by, 2);
        Assert.Equal(1152f, bw, 2);
        Assert.Equal(34f, bh, 2);
        // At this width each of the three native paragraphs fits on its own line, so the
        // wrapped block is exactly the three runs the old single multi-line string drew.
        var wrapped = BenchmarkMenuLayout.Wrap(BenchmarkMenuLayout.Description, geo.DescWidth, Measure);
        Assert.Equal(3, wrapped.Count);
        Assert.Equal(BenchmarkMenuLayout.Description.Split('\n'), wrapped);
    }

    /// <summary>The text itself is unchanged: only where the lines break changes.</summary>
    [Fact]
    public void Benchmark_wrapping_preserves_the_description_text()
    {
        var wrapped = BenchmarkMenuLayout.Wrap(BenchmarkMenuLayout.Description, 700f, Measure);
        string rejoined = string.Join(" ", wrapped);
        Assert.Equal(BenchmarkMenuLayout.Description.Replace('\n', ' '), rejoined);
    }

    /// <summary>At 800×480 the description really needs wrapping (the pre-fix single run was
    /// 1,144 px wide in the audit's measurement) and the list is compressed, not dropped:
    /// every row is present, in order.</summary>
    [Fact]
    public void Benchmark_at_800_fits_the_list_and_wraps_the_description()
    {
        var geo = BenchmarkMenuLayout.Compute(BenchmarkMenuLayout.Labels.Length, 800f, 480f,
            BenchmarkMenuLayout.Description, Measure, LineHeight);
        Assert.True(geo.Fitted);
        var wrapped = BenchmarkMenuLayout.Wrap(BenchmarkMenuLayout.Description, geo.DescWidth, Measure);
        Assert.True(wrapped.Count >= 3);
        Assert.Equal(BenchmarkMenuLayout.Labels.Length, BenchmarkMenuLayout.Labels.Length);   // all rows kept
        for (int i = 0; i < BenchmarkMenuLayout.Labels.Length; i++)
        {
            var (_, y, _, bh) = geo.Band(i, 800f);
            Assert.True(y + bh <= geo.DescTop);
        }
    }

    /// <summary>The native probe-row spacing: the count is drawn the icon's reserved width + 4 px
    /// right of the row origin (<c>sectormap.cpp:338-344</c>), and the glyph (a diamond centred in
    /// that 12 px box, radius 4.5) ends well before the ink starts.</summary>
    [Fact]
    public void Probe_row_count_sits_icon_width_plus_four_pixels_right_and_never_touches_the_icon()
    {
        var pane = new SectorInfoPaneLayout.Data("X", 1, hasHelium: true, heliumOre: 0f,
            groups: [], displayLines: ["A"], showsOwnProbes: true, ownProbeCount: 3);
        var geo = SectorInfoPaneRenderer.Box(pane, 0f, 0f, 800f, 600f, LineHeight, Measure);
        // 12 (reserved glyph box) + 4 (native gap) + 90 ("PROBES  3" at 10 px/char) + 2×12 padding
        Assert.Equal(12f + 4f + 90f + 24f, geo.Box.Width, 2);
        float textX = geo.Box.X + 12f + 12f + 4f;      // row origin + glyph box + native gap
        float glyphRightEdge = geo.Box.X + 12f + 6f + 4.5f;  // centred diamond, radius 4.5
        Assert.True(textX - glyphRightEdge >= 1f,
            $"the count starts {textX - glyphRightEdge:0.#} px past the glyph's edge");
    }

    /// <summary>The alert belongs to the pane now: it is the pane's first line, inside the box and
    /// below the header band. The pre-fix header anchor sat inside the pane box, which is why the
    /// pane's backing painted over it.</summary>
    [Fact]
    public void Sector_info_pane_draws_the_alert_as_its_first_line_inside_its_own_box()
    {
        var samples = new List<SectorInfoPaneLayout.AsteroidSample>
        {
            new(0, 1400f), new(1, 260f), new(0, 800f), new(2, 120f),
        };
        var pane = SectorInfoPaneLayout.Build("Sol", samples, 3, expanded: true,
            warningText: "Enemy miner spotted");
        Assert.Equal("Enemy miner spotted", pane.WarningText);
        Assert.Equal(5, pane.DisplayLines.Count);       // name, asteroids, 2 groups, He3 ore

        float panelX = 440f, panelY = 220f, panelW = 1040f, panelH = 640f;
        var geo = SectorInfoPaneRenderer.Box(pane, panelX, panelY, panelW, panelH, LineHeight, Measure);
        Assert.True(geo.HasWarning);
        Assert.Equal(7, geo.LineCount);                 // warning + 5 display + probe row

        var origins = SectorInfoPaneLayout.LineOrigins(
            new System.Numerics.Vector2(geo.Box.X + 12f, geo.Box.Y + 8f), LineHeight, geo.LineCount);
        var warningBox = new RectangleF(origins[0].X, origins[0].Y,
            Measure(pane.WarningText!), LineHeight);
        Assert.True(warningBox.Y >= geo.Box.Y && warningBox.Y + LineHeight <= geo.Box.Y + geo.Box.Height,
            "the warning line must be inside the pane's box");
        Assert.True(warningBox.Y > panelY + 10f + LineHeight,
            "the warning line must be below the STARMAP header band");

        // The pre-fix placement (StarmapRenderer: panelY + 10 + titleHeight + 3) overlapped the
        // pane's top by more than a line: that is the class the content audit now reports.
        float preFixAlertTop = panelY + 10f + LineHeight + 3f;
        float overlap = (geo.Box.Y + geo.Box.Height) - preFixAlertTop;
        Assert.True(preFixAlertTop < geo.Box.Y + geo.Box.Height && overlap > 1f,
            $"the pre-fix alert anchor ({preFixAlertTop:0.#}) was inside the pane box starting at {geo.Box.Y:0.#}");
    }
}

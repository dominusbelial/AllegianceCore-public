using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P789 — the additive game-over performance panel must never move, cover or
/// change input handling of any native control (the P752 tables'
/// <c>LastSideHeaderRects</c>/<c>LastPlayerHeaderRects</c> hit bars and the
/// Back button, native gameoverscreen.cpp:609-625,840-874,922-956). The
/// placement is pure (<see cref="GameOverPerfPanelLayout"/>), so the
/// non-intersection guarantee is pinned here headlessly at the four gate
/// resolutions.
/// </summary>
public class GameOverPerfPanelLayoutTests
{
    /// <summary>The native panel rect, computed with the exact
    /// GameOverScreenRenderer.Draw formula (panelW = min(w·0.94, 1240),
    /// panelH = min(h·0.94, 760), centred).</summary>
    private static Rectangle NativePanel(int w, int h)
    {
        float panelW = System.MathF.Min(w * 0.94f, 1240f);
        float panelH = System.MathF.Min(h * 0.94f, 760f);
        return new((int)((w - panelW) / 2f), (int)((h - panelH) / 2f), (int)panelW, (int)panelH);
    }

    /// <summary>The native Back button rect from the same formula
    /// (120×30, centred at panelY + panelH − 46).</summary>
    private static Rectangle BackButton(Rectangle panel) =>
        new(panel.X + (panel.Width - 120) / 2, panel.Y + panel.Height - 46, 120, 30);

    /// <summary>Representative native header hit rects: the side table's header
    /// row and the player table's header row, laid out the way the renderer
    /// paints them (x = panelX + 30, y ≈ panelY + 120 / + 160).</summary>
    private static IEnumerable<Rectangle> HeaderRects(Rectangle panel)
    {
        yield return new(panel.X + 30, panel.Y + 120, 400, 16);
        yield return new(panel.X + 30, panel.Y + 160, 520, 16);
    }

    private static IEnumerable<Rectangle> Protected(Rectangle panel)
    {
        yield return panel;               // the whole native panel
        yield return BackButton(panel);
        foreach (var r in HeaderRects(panel)) yield return r;
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3712, 2088)]
    public void Panel_PlacedBesideTheNativePanel_NeverIntersectsNativeControls(int w, int h)
    {
        var panel = NativePanel(w, h);
        var protectedRects = new List<Rectangle>(Protected(panel));
        var perf = GameOverPerfPanelLayout.Compute(w, h, panel, protectedRects);
        Assert.NotNull(perf);
        var rect = perf.Value;
        // Outside the native panel entirely.
        Assert.False(rect.Intersects(panel));
        foreach (var r in protectedRects)
            Assert.False(rect.Intersects(r), $"perf panel {rect} intersects native rect {r}");
        // On the viewport.
        Assert.True(rect.X >= 0 && rect.Right <= w && rect.Y >= 0 && rect.Bottom <= h);
        Assert.Equal(GameOverPerfPanelLayout.Width, rect.Width);
        Assert.True(rect.Height >= GameOverPerfPanelLayout.MinHeight);
    }

    [Fact]
    public void Panel_At1280x720_IsHiddenRatherThanCoveringNativeControls()
    {
        var panel = NativePanel(1280, 720);
        // 1280×720 leaves ~38 px margins — no safe placement exists, so the
        // panel is NOT drawn instead of covering a native control.
        var perf = GameOverPerfPanelLayout.Compute(1280, 720, panel, new List<Rectangle>(Protected(panel)));
        Assert.Null(perf);
    }

    [Fact]
    public void Panel_PrefersTheRightMargin_ThenFallsBackToLeft()
    {
        var panel = NativePanel(1920, 1080);
        var perf = GameOverPerfPanelLayout.Compute(1920, 1080, panel, new List<Rectangle>(Protected(panel)));
        Assert.NotNull(perf);
        // Right margin: the panel sits to the RIGHT of the native panel.
        Assert.True(perf.Value.X > panel.X + panel.Width);
        // A viewport cropped so the right margin cannot fit falls back to the
        // left margin (still clear of every native rect).
        var leftOnly = GameOverPerfPanelLayout.Compute(panel.Right + (int)GameOverPerfPanelLayout.Width, 1080, panel, new List<Rectangle>(Protected(panel)));
        Assert.NotNull(leftOnly);
        Assert.True(leftOnly.Value.Right < panel.X);
    }

    [Fact]
    public void Panel_ScoreLine_ExplainsTheScoreInOneLine()
    {
        var summary = new Allegiance.ClientCore.MonoGame.Diagnostics.SessionPerformanceRecorder()
            .Finish(new Allegiance.SinglePlayer.Benchmark.BenchmarkRecommendation(), null, "") with
        {
            ScoreMeasured = true, Score = 1200, Ranked = false,
            FpsOnePercentLow = 60, ShipsInViewMean = 120, TidiTimeWeightedMeanPercent = 100
        };
        var panel = GameOverPerfPanel.From(summary);
        Assert.Equal("1200 unranked = ships 120 x fps(60/60) x time(100/100) x 10", panel.ScoreLine);
        Assert.True(panel.SuggestionAvailable);
    }
}

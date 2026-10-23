using Allegiance.ClientCore.MonoGame.Diagnostics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P789 — the performance panel beside the native game-over tables: the
/// session recorder's numbers, the bottleneck verdict, the suggested settings
/// with an Apply key, and the Showcase score (ranked only for the fixed-seed
/// tune Showcase). A modern, port-only addition — native had no such panel
/// (era constraint, per the P789 footer rule) — so it is strictly ADDITIVE:
/// it never moves, covers or changes input handling of any native control.
/// All layout is decided by <see cref="GameOverPerfPanelLayout"/> so the
/// non-intersection guarantee is unit-testable headlessly.
/// </summary>
public sealed record GameOverPerfPanel(
    int Score,
    bool Ranked,
    bool ScoreMeasured,
    double FpsAverage,
    double FpsOnePercentLow,
    double FrameP50Ms,
    double FrameP95Ms,
    double FrameP99Ms,
    double FrameWorstMs,
    double? CpuMs,
    double? GpuMs,
    double TidiMinPercent,
    double TidiMeanPercent,
    double ShipsInViewMean,
    int ShipsInViewPeak,
    int TotalPlayersPeak,
    double MatchSeconds,
    double? ServerTickP95Ms,
    string Bottleneck,
    bool SuggestionAvailable)
{
    /// <summary>Resolve the panel's numbers from the session recorder summary.
    /// One line explains the score; "ranked" only for the fixed-seed tune
    /// Showcase (same seed/size/duration everywhere), everything else is
    /// "unranked" so numbers are never compared across different battles.</summary>
    public static GameOverPerfPanel From(SessionPerformanceSummary s)
    {
        string bottleneck = s.Bottleneck;
        if (bottleneck.Length == 0 && s.Settings is { } settings)
            bottleneck = Allegiance.SinglePlayer.Benchmark.BenchmarkPolicy.Bottleneck(new()
            {
                Rendered = true, FpsOnePercentLow = s.FpsOnePercentLow,
                CpuMs = s.CpuMs, GpuMs = s.GpuMs, FrameMs = s.FrameMs,
            });
        return new(
            s.Score, s.Ranked, s.ScoreMeasured,
            s.FpsAverage, s.FpsOnePercentLow,
            s.FrameMs?.P50 ?? 0, s.FrameMs?.P95 ?? 0, s.FrameMs?.P99 ?? 0, s.FrameMs?.Worst ?? 0,
            s.CpuMs, s.GpuMs,
            s.TidiMinPercent, s.TidiTimeWeightedMeanPercent,
            s.ShipsInViewMean, s.ShipsInViewPeak, s.TotalPlayersPeak,
            s.MatchSeconds, s.ServerTickP95Ms,
            bottleneck, s.Settings is not null);
    }

    /// <summary>The score line shown on the panel — one line, as required.
    /// ASCII only: the HUD fonts' repertoire is ASCII (see ChatLogFormat.AsciiSafe).</summary>
    public string ScoreLine => !ScoreMeasured ? "score unmeasured"
        : $"{Score} {(Ranked ? "RANKED" : "unranked")} = ships {ShipsInViewMean:F0} x fps({FpsOnePercentLow:F0}/60) x time({TidiMeanPercent:F0}/100) x 10";
}

/// <summary>P789 — pure placement of the additive performance panel. The rule:
/// it must never intersect any native control — the native game-over panel
/// itself, its two sortable-table header rects
/// (<c>LastSideHeaderRects</c>/<c>LastPlayerHeaderRects</c>, the port of
/// native's OnButtonBarStats hit bars, gameoverscreen.cpp:840-874,922-956) or
/// the Back button (gameoverscreen.cpp:609-625). Priority: the margin right
/// of the native panel, then the left margin; when neither fits (e.g.
/// 1280×720) the panel is NOT drawn rather than covering native controls —
/// the toggle key can still reveal it later on a bigger window.</summary>
public static class GameOverPerfPanelLayout
{
    public const float Width = 320f;
    public const float MinHeight = 150f;
    public const float Gap = 8f;

    /// <summary>Compute the panel rect for the current viewport, or null when
    /// no safe placement exists. <paramref name="protectedRects"/> are the
    /// native hit rects (table headers + Back button); the returned rect never
    /// intersects the native panel or any of them.</summary>
    public static Microsoft.Xna.Framework.Rectangle? Compute(
        int viewportWidth, int viewportHeight, Microsoft.Xna.Framework.Rectangle nativePanel,
        System.Collections.Generic.IEnumerable<Microsoft.Xna.Framework.Rectangle> protectedRects)
    {
        int width = (int)Width;
        // Right margin of the native panel.
        int right = viewportWidth - (nativePanel.X + nativePanel.Width);
        if (right >= width + (int)Gap)
        {
            var rect = Fit(viewportHeight, nativePanel.X + nativePanel.Width + (int)Gap, width, nativePanel.Height);
            if (rect is { } r && ClearOf(r, protectedRects)) return r;
        }
        // Left margin.
        int left = nativePanel.X;
        if (left >= width + (int)Gap)
        {
            var rect = Fit(viewportHeight, nativePanel.X - width - (int)Gap, width, nativePanel.Height);
            if (rect is { } r && ClearOf(r, protectedRects)) return r;
        }
        return null;
    }

    private static Microsoft.Xna.Framework.Rectangle Fit(int viewportHeight, int x, int width, int nativeHeight)
    {
        int height = Math.Min(Math.Max((int)MinHeight, Math.Min(nativeHeight, viewportHeight - 32)), viewportHeight - 32);
        if (height < (int)MinHeight) return default;
        int y = Math.Max(16, (viewportHeight - height) / 2);
        return new(x, y, width, height);
    }

    private static bool ClearOf(Microsoft.Xna.Framework.Rectangle rect,
        System.Collections.Generic.IEnumerable<Microsoft.Xna.Framework.Rectangle> protectedRects)
    {
        foreach (var r in protectedRects)
            if (rect.Intersects(r)) return false;
        return true;
    }
}

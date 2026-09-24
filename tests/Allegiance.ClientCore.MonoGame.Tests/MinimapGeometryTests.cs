using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="MinimapGeometry"/> — the P360 persistent
/// corner mini-map placement. No graphics device (same discipline as
/// <see cref="StarmapLayoutTests"/>). They pin the contract that matters to the
/// pilot: the compact panel is anchored in the bottom-right corner, stays
/// inside the viewport (honouring the screen margin) at every window size, and
/// respects its size caps so it is always a compact widget, never a wall.
/// </summary>
public class MinimapGeometryTests
{
    [Fact]
    public void NormalViewport_AnchorsBottomRight_WithinMargins()
    {
        var (x, y, w, h) = MinimapGeometry.CornerPanel(1920f, 1080f);

        // Scaled up but capped by the hard maxima.
        Assert.Equal(MinimapGeometry.MaxWidth, w);
        Assert.Equal(MinimapGeometry.MaxHeight, h);

        // Anchored Margin px from the bottom/right edges, hence fully in bounds.
        Assert.Equal(1920f - MinimapGeometry.Margin - w, x);
        Assert.Equal(1080f - MinimapGeometry.Margin - h, y);
        Assert.True(x >= 0f && y >= 0f, "panel must not run off the top/left");
        Assert.True(x + w <= 1920f - MinimapGeometry.Margin, "panel overflows the right margin");
        Assert.True(y + h <= 1080f - MinimapGeometry.Margin, "panel overflows the bottom margin");
    }

    [Fact]
    public void LargerViewport_StillRespectsTheSizeCaps()
    {
        // Even on a big screen the widget stays a compact corner widget.
        var (x, y, w, h) = MinimapGeometry.CornerPanel(2560f, 1440f);
        Assert.Equal(MinimapGeometry.MaxWidth, w);
        Assert.Equal(MinimapGeometry.MaxHeight, h);
        Assert.Equal(2560f - MinimapGeometry.Margin - w, x);
        Assert.Equal(1440f - MinimapGeometry.Margin - h, y);
    }

    [Fact]
    public void TinyViewport_IsFloored_AndStaysOnScreen()
    {
        // A small window must still get a usable widget that never runs off-screen.
        var (x, y, w, h) = MinimapGeometry.CornerPanel(300f, 200f);

        Assert.True(w >= 80f && h >= 60f, "widget must not collapse on a tiny window");
        Assert.True(x >= 0f && y >= 0f, "panel must not run off the top/left");
        Assert.True(x + w <= 300f, "panel overflows the viewport width");
        Assert.True(y + h <= 200f, "panel overflows the viewport height");
    }

    [Fact]
    public void EvenSmallestViewport_NeverOverflows()
    {
        // Smaller than even the floors: clamp so the right/bottom edge stays at
        // most at the margin line (never past the viewport edge).
        var (x, y, w, h) = MinimapGeometry.CornerPanel(100f, 80f);
        Assert.True(x + w <= 100f, "overflows the width");
        Assert.True(y + h <= 80f, "overflows the height");
        Assert.True(x >= 0f && y >= 0f, "negative origin");
        Assert.True(w > 0f && h > 0f, "degenerate zero size");
    }
}

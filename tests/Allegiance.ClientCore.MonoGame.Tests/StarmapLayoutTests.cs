using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="StarmapLayout"/> — the P249 whole-mission
/// starmap coordinate mapping. No graphics device (same discipline as
/// <see cref="LockBracketStyleTests"/>). They pin the contract native's
/// <c>sectormap.cpp</c> <c>CalculateScreenMinAndMax</c>/<c>GetClusterPoint</c>
/// establish: authored ScreenX/ScreenY get min/max-fitted into the panel with a
/// border fudge and aspect-ratio preservation, so the layout reads as a map
/// (spread, ordered, in-bounds), never a corner pile.
/// </summary>
public class StarmapLayoutTests
{
    private static StarmapLayout.ClusterSite Site(short id, float x, float y) =>
        new(id, "S" + id, x, y);

    [Fact]
    public void EmptyInput_ProducesEmptyLayout()
    {
        var layout = StarmapLayout.Fit([], 100f, 50f, 400f, 200f, 20f);
        Assert.Equal(0, layout.Count);
        Assert.False(layout.TryGetPoint(1, out _));
    }

    [Fact]
    public void Points_LieWithinTheUsablePanelArea()
    {
        // Panel 100,50,400,200 with border 20 → usable inner rect
        // [120,120,360,160] in x and [70,70,160] in y.
        var layout = StarmapLayout.Fit(
            [Site(1, 0f, 0f), Site(2, 100f, 50f), Site(3, 50f, 25f)],
            100f, 50f, 400f, 200f, 20f);

        Assert.Equal(3, layout.Count);
        Assert.True(layout.TryGetPoint(1, out var p1));
        Assert.True(layout.TryGetPoint(2, out var p2));
        Assert.True(layout.TryGetPoint(3, out _));

        foreach (var p in new[] { p1, p2 })
        {
            Assert.InRange(p.X, 120f, 480f);   // panelX+border .. panelX+panelW-border
            Assert.InRange(p.Y, 70f, 230f);    // panelY+border .. panelY+panelH-border
        }
    }

    [Fact]
    public void Mapping_IsMonotonic_AndOrdered()
    {
        var layout = StarmapLayout.Fit(
            [Site(1, 0f, 0f), Site(2, 100f, 50f), Site(3, 50f, 25f)],
            100f, 50f, 400f, 200f, 20f);

        layout.TryGetPoint(1, out var lo);
        layout.TryGetPoint(2, out var hi);
        // Higher authored ScreenX → further right; higher ScreenY → further down.
        Assert.True(hi.X > lo.X, "larger ScreenX must map further right");
        Assert.True(hi.Y > lo.Y, "larger ScreenY must map further down");
    }

    [Fact]
    public void Bounds_AreAspectRatioCorrected_ToThePanel()
    {
        var layout = StarmapLayout.Fit(
            [Site(1, 0f, 0f), Site(2, 100f, 50f), Site(3, 50f, 25f)],
            100f, 50f, 400f, 200f, 20f);

        float xRange = layout.MaxX - layout.MinX;
        float yRange = layout.MaxY - layout.MinY;
        float aspect = xRange / yRange;
        float desired = (400f - 2 * 20f) / (200f - 2 * 20f);   // 360/160 = 2.25
        Assert.True(Math.Abs(aspect - desired) < 1e-3f, $"bounds aspect {aspect} != panel {desired}");
    }

    [Fact]
    public void UnknownId_HasNoPoint()
    {
        var layout = StarmapLayout.Fit([Site(7, 0f, 0f)], 0f, 0f, 100f, 100f, 10f);
        Assert.False(layout.TryGetPoint(999, out _));
    }

    [Fact]
    public void SingleCluster_IsCentered()
    {
        var layout = StarmapLayout.Fit([Site(5, 3f, -2f)], 100f, 50f, 400f, 200f, 20f);
        Assert.True(layout.TryGetPoint(5, out var p));
        Assert.Equal(300f, p.X, 1);   // 100 + 400/2
        Assert.Equal(150f, p.Y, 1);   // 50 + 200/2
    }

    [Fact]
    public void FarApartClusters_SpreadAcrossThePanel_NotPiled()
    {
        // Two authored coords very far apart must land a good distance apart on
        // screen — the anti-"jumbled corner pile" guarantee.
        var layout = StarmapLayout.Fit(
            [Site(1, -5000f, 3000f), Site(2, 5000f, 8900f)],
            100f, 50f, 400f, 200f, 20f);

        Assert.True(layout.TryGetPoint(1, out var a));
        Assert.True(layout.TryGetPoint(2, out var b));
        float dx = Math.Abs(b.X - a.X);
        float dy = Math.Abs(b.Y - a.Y);
        // Occupies at least ~half the usable panel span on one axis.
        Assert.True(dx >= 180f || dy >= 80f,
            $"points barely separated (dx={dx}, dy={dy}) — layout collapsed");
    }
}

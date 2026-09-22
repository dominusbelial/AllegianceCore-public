using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P333 — ship render-scale normalization. The helper's geometry measurement
/// (<see cref="ShipRenderScale.RenderRadius"/>) needs a compiled content
/// <c>Model</c> (GPU-bound), so it is exercised by the live before/after
/// capture proof; the pure length↔radius mapping and its guards are fully
/// unit-tested here, including a proportional-class assertion built from the
/// real PCore001 hull lengths and the real measured per-model render radii
/// (docs/P305_roster/roster_variety.txt).
/// </summary>
public class ShipRenderScaleTests
{
    [Theory]
    // (real HullType.Length, real measured rendered radius from roster_variety.txt)
    [InlineData(300.0f, 102.3f)]   // bios-carrier (cap502)
    [InlineData(17.1f, 157.5f)]    // bios-fighter (wc_gcfigx)
    [InlineData(11.4f, 126.3f)]    // scout (fig05ops1)
    [InlineData(75.0f, 106.7f)]    // corvette (utl18a)
    [InlineData(4.0f, 136.8f)]     // eject pod (utl100)
    public void Maps_render_radius_to_half_hull_length(float length, float renderRadius)
    {
        // Invariant: after applying the scale the visual merged-sphere radius
        // equals the hull's gameplay collision radius 0.5 × Length. If the 0.5
        // coefficient or the division is ever "fixed" wrongly, this reds.
        float scale = ShipRenderScale.MapLengthAndRadius(length, renderRadius);
        Assert.Equal(0.5f * length, scale * renderRadius, precision: 3);
    }

    [Fact]
    public void Nonpositive_length_or_radius_returns_no_scale()
    {
        Assert.Equal(1f, ShipRenderScale.MapLengthAndRadius(0f, 100f));
        Assert.Equal(1f, ShipRenderScale.MapLengthAndRadius(50f, 0f));
        Assert.Equal(1f, ShipRenderScale.MapLengthAndRadius(-5f, 100f));
        Assert.Equal(1f, ShipRenderScale.MapLengthAndRadius(0f, 0f));
    }

    [Fact]
    public void Null_model_returns_no_scale_even_with_valid_length()
    {
        // The null-model guard short-circuits before any GPU-bound RenderRadius
        // measurement, so this is exercisable headless.
        Assert.Equal(1f, ShipRenderScale.ScaleForLength(300f, null));
        Assert.Equal(1f, ShipRenderScale.ScaleForLength(0f, null));
    }

    [Fact]
    public void Normalization_makes_classes_proportional_not_equal()
    {
        // The P333 bug: raw rendered radii are all ~95-160 REGARDLESS of class
        // (a fighter and a carrier read at nearly the same on-screen size).
        // After normalization the post-scale visual radius equals 0.5×Length, so
        // the carrier (real len 300) is an order of magnitude bigger than the
        // fighter (real len ~17) — proportional, matching gameplay collision.
        float carrierScale = ShipRenderScale.MapLengthAndRadius(300.0f, 102.3f);  // bios-carrier
        float fighterScale = ShipRenderScale.MapLengthAndRadius(17.1f, 157.5f);   // bios-fighter
        float carrierVisual = carrierScale * 102.3f;   // 150 = 0.5*300
        float fighterVisual = fighterScale * 157.5f;   // 8.55 = 0.5*17.1

        Assert.True(carrierVisual > fighterVisual * 10f,
            $"carrier visual radius {carrierVisual:F1} should be >10x the fighter's {fighterVisual:F1}");
    }
}

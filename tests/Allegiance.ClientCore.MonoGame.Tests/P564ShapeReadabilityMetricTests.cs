using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P564 — unit tests for the pure <see cref="ShapeReadabilityMetric"/>: the pixel
/// luminance stats and the mesh-based normal-vs-brightness correlation. These pin the
/// metric the P564 readability table is built from, so the numbers in the report are
/// reproducible.
/// </summary>
public class P564ShapeReadabilityMetricTests
{
    [Fact]
    public void PixelStats_report_shading_spread_and_mean_over_object_pixels()
    {
        // 2x2 RGBA: two bright pixels (key-lit), two dim (back/alt-lit), no background.
        var buf = new byte[]
        {
            200, 200, 200, 255,  40, 40, 40, 255,
            200, 200, 200, 255,  40, 40, 40, 255,
        };
        var s = ShapeReadabilityMetric.ComputePixelStats(buf, 2, 2, bgR: 0, bgG: 0, bgB: 0);

        // Mean luminance = average of 200 and 40 (grey) ≈ 120.
        Assert.Equal(120.0, s.MeanLuminance, 6);
        // Spread (P90-P10) is dominated by the bright/dim gap, well above zero.
        Assert.True(s.Spread10_90 > 80.0, $"expected a real bright/dim spread, got {s.Spread10_90}");
        Assert.True(s.LuminanceStdDev > 70.0, $"expected real shading contrast, got {s.LuminanceStdDev}");
        Assert.Equal(0.0, s.NearBlackFraction, 6);   // dim side (40) is not < 25
    }

    [Fact]
    public void PixelStats_mask_background_and_flag_near_black()
    {
        // 2x2: one near-black object pixel (10), three background pixels equal to bg.
        var buf = new byte[]
        {
            8, 12, 20, 255,   0, 0, 0, 255,
            0, 0, 0, 255,     0, 0, 0, 255,
        };
        var s = ShapeReadabilityMetric.ComputePixelStats(buf, 2, 2, bgR: 0, bgG: 0, bgB: 0);

        Assert.Equal(1.0, s.NearBlackFraction, 6);        // the one object pixel is near-black
        Assert.True(s.MeanLuminance < 25.0);
        Assert.Equal(0.0, s.LuminanceStdDev, 6);          // only one object pixel -> no spread
    }

    [Fact]
    public void PixelStats_uniform_brightness_has_zero_contrast()
    {
        var buf = new byte[4 * 4];                         // all pixels 120 grey
        for (int i = 0; i < 4; i++) { buf[i * 4] = 120; buf[i * 4 + 1] = 120; buf[i * 4 + 2] = 120; buf[i * 4 + 3] = 255; }
        var s = ShapeReadabilityMetric.ComputePixelStats(buf, 2, 2, 0, 0, 0);

        Assert.Equal(0.0, s.LuminanceStdDev, 6);
        Assert.Equal(0.0, s.Spread10_90, 6);
    }

    [Fact]
    public void NormalBrightness_correlation_is_strong_when_light_reveals_geometry()
    {
        // Six degenerate faces (positions at origin -> equal weight) with normals
        // ±Z, ±X, ±Y. Key light toward +Z (KeyDirection = -Z). The +Z face is far
        // brighter than the side faces, so brightness tracks the normal -> a high
        // positive correlation.
        var norms = new float[]
        {
            0,0,1, 0,0,1, 0,0,1,   0,0,-1, 0,0,-1, 0,0,-1,
            1,0,0, 1,0,0, 1,0,0,   -1,0,0, -1,0,0, -1,0,0,
            0,1,0, 0,1,0, 0,1,0,   0,-1,0, 0,-1,0, 0,-1,0,
        };
        var pos = new float[54];   // 18 vertices, all at origin (degenerate)
        var idx = new int[] { 0,1,2, 3,4,5, 6,7,8, 9,10,11, 12,13,14, 15,16,17 };
        var light = new SectorLighting(
            KeyDirection: new(0f, 0f, -1f), KeyColor: new(1f, 1f, 1f),
            AltDirection: new(0f, 0f, 1f), AltColor: new(0f, 0.5f, 1f),
            Ambient: new(0.25f, 0.25f, 0.25f));

        var (r, variation) = ShapeReadabilityMetric.ComputeNormalBrightness(pos, norms, idx, light);

        Assert.True(r > 0.8, $"light revealing geometry should give a strong positive correlation, got {r}");
        Assert.True(variation > 0.2, $"varied faces should produce real Lambertian shading contrast, got {variation}");
    }

    [Fact]
    public void NormalBrightness_correlation_tracks_toward_the_light_only()
    {
        // Flip the key light to -Z (toward = -Z): now the -Z face is the bright one,
        // still a strong positive correlation with lamKey = max(dot(N, toward),0).
        var norms = new float[]
        {
            0,0,1, 0,0,1, 0,0,1,   0,0,-1, 0,0,-1, 0,0,-1,
            1,0,0, 1,0,0, 1,0,0,   -1,0,0, -1,0,0, -1,0,0,
            0,1,0, 0,1,0, 0,1,0,   0,-1,0, 0,-1,0, 0,-1,0,
        };
        var pos = new float[54];   // 18 vertices, all at origin (degenerate)
        var idx = new int[] { 0,1,2, 3,4,5, 6,7,8, 9,10,11, 12,13,14, 15,16,17 };
        var light = new SectorLighting(
            KeyDirection: new(0f, 0f, 1f), KeyColor: new(1f, 1f, 1f),
            AltDirection: new(0f, 0f, -1f), AltColor: new(0f, 0.5f, 1f),
            Ambient: new(0.25f, 0.25f, 0.25f));

        var (r, _) = ShapeReadabilityMetric.ComputeNormalBrightness(pos, norms, idx, light);

        Assert.True(r > 0.8, $"brightness must track the face turning toward the key light, got {r}");
    }
}

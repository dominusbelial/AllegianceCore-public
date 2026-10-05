using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P545 — headless tests of the native particle colour/size curves
/// (<see cref="NativeParticleCurves"/>), a literal transcription of
/// <c>effect/particlegeo.cpp</c> (SmokeBehavior / FireBehavior / SparkBehavior).
/// These pin the exact native tint math so stacked smoke stays dim grey — the
/// fix for the reported white blowout.
/// </summary>
public class NativeParticleCurvesTests
{
    // ── SmokeBehaviour (particlegeo.cpp:45-54) ────────────────────────────
    [Fact]
    public void Smoke_color_at_full_countdown_is_dim_grey_025()
    {
        // c = 0.25·countdown; RGBA = (c, c·0.8, c, c) — max alpha 0.25.
        var (r, g, b, a) = NativeParticleCurves.SmokeColor(1f);
        Assert.Equal(0.25f, r, 5);
        Assert.Equal(0.20f, g, 5);
        Assert.Equal(0.25f, b, 5);
        Assert.Equal(0.25f, a, 5);
    }

    [Fact]
    public void Smoke_fades_to_zero_as_it_expires()
    {
        var (r, g, b, a) = NativeParticleCurves.SmokeColor(0f);
        Assert.Equal(0f, r, 5);
        Assert.Equal(0f, g, 5);
        Assert.Equal(0f, b, 5);
        Assert.Equal(0f, a, 5);
        var halfway = NativeParticleCurves.SmokeColor(0.5f);
        Assert.Equal(0.125f, halfway.R, 5);
        Assert.Equal(0.125f, halfway.A, 5);   // 0.25·0.5
    }

    [Fact]
    public void Smoke_size_grows_from_075x_to_30x()
    {
        // fSize = m_fSize · (0.75 + 2.25·(1 − countdown)).
        Assert.Equal(0.75f, NativeParticleCurves.SmokeSizeScale(1f), 5);
        Assert.Equal(3.00f, NativeParticleCurves.SmokeSizeScale(0f), 5);
        Assert.Equal(1.875f, NativeParticleCurves.SmokeSizeScale(0.5f), 5);
    }

    // ── FireBehaviour (particlegeo.cpp:73-129) ────────────────────────────
    [Fact]
    public void Fire_ramps_up_white_growing_half_to_full()
    {
        // lifeFrac=0 → countdown clamped at 1: colour (1,1,1,0.5); size 0.5→1.0
        // over the first 10% of life (exponential 0.666).
        var start = NativeParticleCurves.ComputeFire(0f);
        Assert.Equal(1f, start.R, 5);
        Assert.Equal(1f, start.G, 5);
        Assert.Equal(1f, start.B, 5);
        Assert.Equal(0.5f, start.A, 5);
        Assert.Equal(0.5f, start.SizeScale, 5);

        var midRamp = NativeParticleCurves.ComputeFire(0.050f);   // half of the 10% ramp
        Assert.InRange(midRamp.SizeScale, 0.814f, 0.817f); // 0.5+0.5·pow(0.5,⅔)
        Assert.Equal(1f, midRamp.R, 5);
    }

    [Fact]
    public void Fire_ramps_down_yellow_to_red_shrinking_full_to_half()
    {
        // Just past the ramp: colour (1, c, 0.25c, 0.5c²) with c=(1−life)/(0.9).
        var startDown = NativeParticleCurves.ComputeFire(0.099f + 1e-3f);
        Assert.Equal(1f, startDown.R, 5);
        Assert.InRange(startDown.SizeScale, 0.998f, 1.002f);

        // Near the end: c → 0, colour → (1, 0, 0, 0), size → 0.5.
        var end = NativeParticleCurves.ComputeFire(1f);
        Assert.Equal(1f, end.R, 5);
        Assert.Equal(0f, end.G, 5);
        Assert.Equal(0f, end.B, 5);
        Assert.Equal(0f, end.A, 5);
        Assert.Equal(0.5f, end.SizeScale, 5);

        // Mid life: red-dominant (yellow → red), still warm.
        var mid = NativeParticleCurves.ComputeFire(0.55f);
        Assert.Equal(1f, mid.R, 5);
        Assert.Equal(0.5f, mid.G, 4);          // c = (1−0.55)/0.9 = 0.5
        Assert.Equal(0.125f, mid.B, 4);        // 0.25c
        Assert.Equal(0.125f, mid.A, 4);        // 0.5c² = 0.5·0.25
    }

    // ── SparkBehaviour (particlegeo.cpp:230-236) ──────────────────────────
    [Fact]
    public void Spark_is_hot_blue_fading_out()
    {
        // c = countdown, k = ½c²; RGBA = (1.5k, 2k, c, k).
        var (r, g, b, a) = NativeParticleCurves.SparkColor(1f);
        Assert.Equal(0.750f, r, 5);
        Assert.Equal(1.000f, g, 5);
        Assert.Equal(1.000f, b, 5);
        Assert.Equal(0.500f, a, 5);

        var dead = NativeParticleCurves.SparkColor(0f);
        Assert.Equal(0f, dead.R, 5);
        Assert.Equal(0f, dead.G, 5);
        Assert.Equal(0f, dead.B, 5);
        Assert.Equal(0f, dead.A, 5);
    }
}

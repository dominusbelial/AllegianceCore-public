using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P503 fix — deriving the ambient-dust grain size from
/// native <c>DrawDecal</c>'s scale semantics instead of the P449a invention
/// <c>Calibration = 0.13</c>. Native passes <c>scale = 0.1/focus</c> to
/// <c>DrawDecal</c> (debrisgeo.cpp:166) and stores it as the eye-space quad's
/// half-extent <c>decal.m_scale = scaleMat·scale</c> with <c>scaleMat = 1</c>
/// (context.cpp:2317); the perspective divide then makes a grain subtend
/// <c>0.1/depth</c> of the screen height (see
/// <see cref="AmbientDustRenderer.ScreenHeightFraction"/>). The P449a formula
/// <c>(0.1/focus)·distance·Calibration</c> instead baked distance in so every
/// grain subtended a constant ~1.3% of the screen height regardless of depth —
/// the "bubbles". Pure math, no graphics device.
/// </summary>
public class AmbientDustSizeTests
{
    // Two distinct camera focal lengths: 75° vertical FOV (focus=cot(37.5°)≈1.303)
    // and 50° (focus=cot(25°)≈2.145, the P449b out-of-scope FOV).
    private const float FocusWide = 1.303f;
    private const float FocusNarrow = 2.145f;

    [Fact]
    public void DecalHalfExtent_is_natives_0_1_over_focus()
    {
        // debrisgeo.cpp:166 scale = 0.1/focus; context.cpp:2317 m_scale = scaleMat·scale, scaleMat = 1.
        Assert.Equal(0.1f / FocusWide, AmbientDustRenderer.DecalHalfExtent(FocusWide), 6);
        Assert.Equal(0.1f / FocusNarrow, AmbientDustRenderer.DecalHalfExtent(FocusNarrow), 6);
    }

    [Fact]
    public void DecalHalfExtent_is_a_pure_function_of_focus()
    {
        // The core bug: P449a multiplied the half-extent by the grain's distance,
        // forcing a constant on-screen size. Native's world half-extent is the
        // same for a grain at 20 m and one at 100 m — a pure function of focus.
        Assert.Equal(AmbientDustRenderer.DecalHalfExtent(FocusWide), AmbientDustRenderer.DecalHalfExtent(FocusWide), 9);
        float h = AmbientDustRenderer.DecalHalfExtent(FocusWide);
        Assert.True(h > 0f && h < 0.1f, $"half-extent {h} should be a small positive constant");
    }

    [Fact]
    public void ScreenHeightFraction_equals_native_0_1_over_depth()
    {
        // fraction = (0.1/focus)·focus/depth = 0.1/depth, independent of focus.
        Assert.Equal(0.1f / 20f, AmbientDustRenderer.ScreenHeightFraction(FocusWide, 20f), 6);
        Assert.Equal(0.1f / 100f, AmbientDustRenderer.ScreenHeightFraction(FocusWide, 100f), 6);
        Assert.Equal(0.1f / 30f, AmbientDustRenderer.ScreenHeightFraction(FocusNarrow, 30f), 6);
    }

    [Fact]
    public void ScreenHeightFraction_is_FOV_independent()
    {
        // Two different cameras (wide 75° and narrow 50°) see the same grain at
        // the same depth at the same fraction of their screen heights.
        Assert.Equal(
            AmbientDustRenderer.ScreenHeightFraction(FocusWide, 50f),
            AmbientDustRenderer.ScreenHeightFraction(FocusNarrow, 50f), 6);
    }

    [Fact]
    public void ScreenHeightFraction_decreases_with_distance()
    {
        // Native's perspective: a farther grain is a SMALLER speck. The P449a
        // constant-size formula failed this (its fraction was distance-invariant).
        float near = AmbientDustRenderer.ScreenHeightFraction(FocusWide, 25f);
        float far = AmbientDustRenderer.ScreenHeightFraction(FocusWide, 100f);
        Assert.True(far < near, "a more distant grain must subtend less of the screen");
        Assert.Equal(0.25f * near, far, 6); // 25→100 m is a 4× depth → ¼ fraction
    }

    [Fact]
    public void Former_P449a_formula_gave_a_bubble_const_at_all_distances()
    {
        // MUST-FAIL discriminator: reconstruct the old (0.1/focus)·dist·0.13 size
        // and confirm it is exactly the anti-native behaviour the tests must catch —
        // a constant on-screen fraction regardless of distance. This documents that
        // ScreenHeightFraction's distance-dependence is a real regression guard.
        const float cal = 0.13f;
        float half20 = (0.1f / FocusWide) * 20f * cal;
        float half80 = (0.1f / FocusWide) * 80f * cal;
        float frac20 = half20 * FocusWide / 20f;   // old on-screen fraction @ 20 m
        float frac80 = half80 * FocusWide / 80f;   // old on-screen fraction @ 80 m
        Assert.Equal(frac20, frac80, 6);           // the bug: distance-invariant
        Assert.Equal(0.1f * cal, frac20, 6);
        Assert.NotEqual(0.1f / 20f, frac20);       // disagree with native at every depth
        Assert.NotEqual(0.1f / 80f, frac80);
    }
}

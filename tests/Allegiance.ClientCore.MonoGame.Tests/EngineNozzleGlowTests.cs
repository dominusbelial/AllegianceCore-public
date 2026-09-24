using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Tests for the P365 engine-nozzle glow colour/size math
/// (<see cref="EngineNozzleGlow"/>) — a faithful port of native
/// <c>ThingGeoImpl::UpdateGlow</c> (thinggeo.cpp:440-460) plus <c>SetHSBA</c> /
/// <c>HueComponent</c> (color.cpp:12-33). Verifies the exact HSB→RGB tint and the
/// decal scale at representative thrust values, the native "no render at value 0"
/// gate (scaleGlow 0), the monotonic brightness rise, and the value clamp. Purely
/// arithmetic — no graphics device.
/// </summary>
public class EngineNozzleGlowTests
{
    private const float Tol = 1e-3f;

    private static void AssertGlow(float value, float er, float eg, float eb, float escale)
    {
        var (r, g, b, scale) = EngineNozzleGlow.Compute(value);
        Assert.InRange(r, er - Tol, er + Tol);
        Assert.InRange(g, eg - Tol, eg + Tol);
        Assert.InRange(b, eb - Tol, eb + Tol);
        Assert.InRange(scale, escale - Tol, escale + Tol);
    }

    [Fact]
    public void Full_thrust_is_bright_yellow_white_at_full_size()
    {
        // value 1 (afterburner / full throttle): hue 1/6, sat 0.5, bri 1, scale 1.
        // HSBA(1/6, 0.5, 1) → (1, 1, 0.5) — the bright yellow-white full-burn glow.
        AssertGlow(1f, 1f, 1f, 0.5f, 1f);
    }

    [Fact]
    public void Zero_thrust_is_black_and_not_rendered()
    {
        // value 0: bri = 1-(1)^3 = 0 → black; scaleGlow = 1-(1)^2 = 0 → the native
        // RenderGlow gate (m_scaleGlow > 0) suppresses the draw entirely.
        AssertGlow(0f, 0f, 0f, 0f, 0f);
    }

    [Fact]
    public void Half_thrust_shifts_to_red_orange()
    {
        // value 0.5: hue 0.0052 (≈red), sat ≈1, bri 0.875, scale 0.75.
        // HSBA → (0.875, 0.0273, 0) — dimmer, smaller, red-orange.
        AssertGlow(0.5f, 0.875f, 0.0273f, 0f, 0.75f);
    }

    [Fact]
    public void Quarter_thrust_is_dim_dark_red()
    {
        // value 0.25: bri 0.578, scale 0.4375 — a dim, small, near-red glow.
        AssertGlow(0.25f, 0.5781f, 0.0006f, 0f, 0.4375f);
    }

    [Theory]
    [InlineData(0.0625f)]
    [InlineData(0.125f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(0.9f)]
    public void Brightness_rises_monotonically_with_thrust(float v)
    {
        var (r1, _, _, s1) = EngineNozzleGlow.Compute(v);
        var (r2, _, _, s2) = EngineNozzleGlow.Compute(Math.Min(1f, v + 0.049f));
        Assert.True(r2 >= r1, $"red component should rise with thrust at {v}");
        Assert.True(s2 > s1, $"scale should rise with thrust at {v}");
    }

    [Fact]
    public void Value_is_clamped_to_unit_range()
    {
        // Native clamps m_thrust to [0, 100] defensively; the port clamps value to
        // [0,1] so out-of-range throttle/afterburner inputs can't produce NaN/HUGE.
        var hi = EngineNozzleGlow.Compute(2f);
        var lo = EngineNozzleGlow.Compute(-1f);
        Assert.Equal(1f, hi.ScaleGlow);
        Assert.Equal(1f, hi.R);
        Assert.Equal(0f, lo.ScaleGlow);
        Assert.Equal(0f, lo.R);
    }

    [Fact]
    public void Hsba_red_green_blue_channels_match_reference_wheel()
    {
        // Sanity on the triangular HueComponent: pure red = HSBA(0,1,1) → (1,0,0);
        // one-third around the wheel (h=1/3) → green (0,1,0); 2/3 → blue (0,0,1).
        var red = EngineNozzleGlow.Hsba(0f, 1f, 1f);
        Assert.InRange(red.R, 1f - Tol, 1f + Tol);
        Assert.InRange(red.G, 0f - Tol, Tol);
        Assert.InRange(red.B, 0f - Tol, Tol);

        var green = EngineNozzleGlow.Hsba(1f / 3f, 1f, 1f);
        Assert.InRange(green.R, 0f - Tol, Tol);
        Assert.InRange(green.G, 1f - Tol, 1f + Tol);
        Assert.InRange(green.B, 0f - Tol, Tol);

        var blue = EngineNozzleGlow.Hsba(2f / 3f, 1f, 1f);
        Assert.InRange(blue.R, 0f - Tol, Tol);
        Assert.InRange(blue.G, 0f - Tol, Tol);
        Assert.InRange(blue.B, 1f - Tol, 1f + Tol);
    }
}

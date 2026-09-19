using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P270 — the client-side cloak presentation helper
/// (<see cref="CloakVisual"/>): a purely-defined, unit-testable opacity with a
/// subtle shimmer. These are the render-tuning knobs that make a cloaked ship
/// visibly translucent yet still recognisable to a teammate; the server (not
/// this helper) is what withholds an undetectable enemy's data entirely.
/// </summary>
public class CloakVisualTests
{
    [Fact]
    public void Base_opacity_is_clearly_translucent()
    {
        // A cloaked ship must read as translucent — comfortably below solid
        // (1.0) so the transparency is unmistakable — yet not so faint it
        // can't be recognised as a ship at a glance.
        Assert.InRange(CloakVisual.BaseOpacity, 0.15f, 0.6f);
    }

    [Fact]
    public void Opacity_oscillates_about_base_as_a_shimmer()
    {
        // Sample one full second of shimmer at fine resolution; the value must
        // swing both ABOVE and BELOW BaseOpacity (a living ripple, not a
        // constant fade), staying inside the [Min, Max] clamp the whole time.
        var seenAbove = false;
        var seenBelow = false;
        for (int i = 0; i <= 1000; i++)
        {
            float op = CloakVisual.Opacity(i / 1000.0);
            Assert.InRange(op, CloakVisual.MinOpacity, CloakVisual.MaxOpacity);
            if (op > CloakVisual.BaseOpacity + 0.001f) seenAbove = true;
            if (op < CloakVisual.BaseOpacity - 0.001f) seenBelow = true;
        }
        Assert.True(seenAbove, "the shimmer must rise above the base opacity");
        Assert.True(seenBelow, "the shimmer must dip below the base opacity");
    }

    [Fact]
    public void Opacity_is_repeatable_deterministic_function_of_time()
    {
        // Pure math of elapsed seconds — same input, same output, every call
        // (so tests and captures are reproducible).
        Assert.Equal(CloakVisual.Opacity(1.234), CloakVisual.Opacity(1.234));
    }

    [Fact]
    public void IsCloaked_matches_the_wire_cloak_bit()
    {
        // The single client surface is the CloakActive stateM bit (0x800, P269).
        const int cloakBit = 0x800;
        Assert.True(CloakVisual.IsCloaked(cloakBit));
        Assert.True(CloakVisual.IsCloaked(cloakBit | 0x400 | 0x2000)); // alongside afterburner/mining
        Assert.False(CloakVisual.IsCloaked(0));
        Assert.False(CloakVisual.IsCloaked(0x7FF));                    // every bit except the cloak
        Assert.False(CloakVisual.IsCloaked(~cloakBit & 0xFFFF));       // complement — no cloak bit
    }

    [Fact]
    public void Shimmer_is_a_slow_ripple_not_a_strobe()
    {
        // Over a short 50ms slice the opacity should barely move — a calm
        // ~0.8 Hz shimmer, not a blink (so consecutive offscreen captures of a
        // cloaked teammate look consistent).
        float a = CloakVisual.Opacity(0.0);
        float b = CloakVisual.Opacity(0.05);
        Assert.InRange(MathF.Abs(a - b), 0f, 0.035f);
    }
}

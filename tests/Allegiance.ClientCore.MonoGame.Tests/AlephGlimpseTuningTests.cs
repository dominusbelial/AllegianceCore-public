using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P585 — pure tests for the resting aleph-glimpse tuning: the approach
/// fade-in curve (<see cref="AlephGlimpseRenderer.ApproachFade"/>) and the
/// resting→pulse mix (<see cref="AlephGlimpseRenderer.Mix"/>). Failing-first:
/// there was no approach fade before (the resting glimpse was a constant faint
/// opacity), so these fail on today's code.
/// </summary>
public class AlephGlimpseTuningTests
{
    [Fact]
    public void ApproachFade_IsFullAtShortRange_AndInvisiblePastFarRange()
    {
        // "full at ~400 u" — at/below FadeFullDistance it is 1.
        Assert.Equal(1f, AlephGlimpseRenderer.ApproachFade(0f));
        Assert.Equal(1f, AlephGlimpseRenderer.ApproachFade(AlephGlimpseRenderer.FadeFullDistance));
        // "invisible past ~3000 u" — at/above FadeStartDistance it is 0.
        Assert.Equal(0f, AlephGlimpseRenderer.ApproachFade(AlephGlimpseRenderer.FadeStartDistance));
        Assert.Equal(0f, AlephGlimpseRenderer.ApproachFade(100000f));
    }

    [Fact]
    public void ApproachFade_MonotonicallyDeclinesBetweenTheKnees()
    {
        float a = AlephGlimpseRenderer.ApproachFade(AlephGlimpseRenderer.FadeFullDistance + 10f);
        float b = AlephGlimpseRenderer.ApproachFade(AlephGlimpseRenderer.FadeFullDistance + 350f);
        float c = AlephGlimpseRenderer.ApproachFade(AlephGlimpseRenderer.FadeStartDistance - 100f);
        Assert.True(a > b && b > c, "the glimpse should fade in smoothly with approach (declining with distance)");
        Assert.InRange(a, 0f, 1f);
        Assert.InRange(c, 0f, 1f);
    }

    [Fact]
    public void ApproachFade_StartsVisibleByTheTaskApproachDistances()
    {
        // The task's spot-checks: clearly fade-in by 2000 and 800, full by 400.
        Assert.True(AlephGlimpseRenderer.ApproachFade(2000f) > 0.05f, "2000 u must show a (faint) glimpse");
        Assert.True(AlephGlimpseRenderer.ApproachFade(800f) > 0.3f, "800 u must be clearly visible");
        Assert.True(AlephGlimpseRenderer.ApproachFade(400f) >= 1f, "400 u is full strength");
    }

    [Fact]
    public void Mix_ReturnsRestingAtZero_AndPulseAtOne()
    {
        Assert.Equal(0.5f, AlephGlimpseRenderer.Mix(0.5f, 0.9f, 0f));
        Assert.Equal(0.9f, AlephGlimpseRenderer.Mix(0.5f, 0.9f, 1f));
        Assert.Equal(0.7f, AlephGlimpseRenderer.Mix(0.5f, 0.9f, 0.5f));
    }

    [Fact]
    public void PulseOpensWiderAndBrighterThanResting()
    {
        // "opens the glimpse wide": the pulse disc is larger and more opaque.
        Assert.True(AlephGlimpseRenderer.PulseDiscRadiusFactor > AlephGlimpseRenderer.RestingDiscRadiusFactor);
        Assert.True(AlephGlimpseRenderer.PulseCentreOpacity > AlephGlimpseRenderer.RestingCentreOpacity);
    }
}

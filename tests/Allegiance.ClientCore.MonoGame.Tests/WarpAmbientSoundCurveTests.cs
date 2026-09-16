using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpAmbientSoundCurve"/> — the distance →
/// volume falloff behind P237's warp ambient sound (the distance-scaled
/// approximation of native's positioned <c>alephSound</c>). No graphics/audio
/// device involved; the curve is deliberately free of MonoGame types.
/// </summary>
public class WarpAmbientSoundCurveTests
{
    [Fact]
    public void VolumeForDistance_IsFullAtZero_SilentAtAudibleRadius()
    {
        Assert.Equal(WarpAmbientSoundCurve.MaxVolume, WarpAmbientSoundCurve.VolumeForDistance(0f), 5);
        Assert.Equal(0f, WarpAmbientSoundCurve.VolumeForDistance(WarpAmbientSoundCurve.AudibleRadius), 5);
        Assert.Equal(0f, WarpAmbientSoundCurve.VolumeForDistance(WarpAmbientSoundCurve.AudibleRadius + 1f), 5);
    }

    [Fact]
    public void VolumeForDistance_FallsLinearlyThroughMidpoint()
    {
        // Half the audible radius → half of the max volume (linear falloff).
        float mid = WarpAmbientSoundCurve.AudibleRadius / 2f;
        Assert.Equal(
            WarpAmbientSoundCurve.MaxVolume * 0.5f,
            WarpAmbientSoundCurve.VolumeForDistance(mid), 5);
    }

    [Fact]
    public void VolumeForDistance_IsMonotonicNonIncreasingInDistance()
    {
        float prev = WarpAmbientSoundCurve.MaxVolume + 1f;
        for (float d = 0f; d <= WarpAmbientSoundCurve.AudibleRadius + 10f; d += 677f)
        {
            float v = WarpAmbientSoundCurve.VolumeForDistance(d);
            Assert.True(v <= prev + 1e-6f, $"distance {d}: volume {v} rose above prev {prev}");
            Assert.InRange(v, 0f, WarpAmbientSoundCurve.MaxVolume);
            prev = v;
        }
    }

    [Fact]
    public void VolumeForDistance_NegativeOrPastRadius_Clamps()
    {
        // Negative distance (listener "inside" the warp) clamps to full, never
        // above MaxVolume.
        Assert.Equal(WarpAmbientSoundCurve.MaxVolume, WarpAmbientSoundCurve.VolumeForDistance(-100f), 5);
        // Just past the radius is fully silent, not negative.
        Assert.Equal(0f, WarpAmbientSoundCurve.VolumeForDistance(WarpAmbientSoundCurve.AudibleRadius * 2f), 5);
    }
}

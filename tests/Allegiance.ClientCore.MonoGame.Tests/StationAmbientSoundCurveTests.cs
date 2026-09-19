using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="StationAmbientSoundCurve"/> — the distance →
/// volume falloff behind P267's station exterior ambient sound (the
/// distance-scaled approximation of native's positioned station
/// <c>AmbientSoundSite</c>, trekigc.cpp:1465-1468). No audio device involved.
/// </summary>
public class StationAmbientSoundCurveTests
{
    [Fact]
    public void VolumeForDistance_IsFullAtZero_SilentAtAudibleRadius()
    {
        Assert.Equal(StationAmbientSoundCurve.MaxVolume, StationAmbientSoundCurve.VolumeForDistance(0f), 5);
        Assert.Equal(0f, StationAmbientSoundCurve.VolumeForDistance(StationAmbientSoundCurve.AudibleRadius), 5);
        Assert.Equal(0f, StationAmbientSoundCurve.VolumeForDistance(StationAmbientSoundCurve.AudibleRadius + 1f), 5);
    }

    [Fact]
    public void VolumeForDistance_FallsLinearlyThroughMidpoint()
    {
        // Half the audible radius → half of the max volume (linear falloff).
        float mid = StationAmbientSoundCurve.AudibleRadius / 2f;
        Assert.Equal(
            StationAmbientSoundCurve.MaxVolume * 0.5f,
            StationAmbientSoundCurve.VolumeForDistance(mid), 5);
    }

    [Fact]
    public void VolumeForDistance_IsMonotonicNonIncreasingInDistance()
    {
        float prev = StationAmbientSoundCurve.MaxVolume + 1f;
        for (float d = 0f; d <= StationAmbientSoundCurve.AudibleRadius + 10f; d += 587f)
        {
            float v = StationAmbientSoundCurve.VolumeForDistance(d);
            Assert.True(v <= prev + 1e-6f, $"distance {d}: volume {v} rose above prev {prev}");
            Assert.InRange(v, 0f, StationAmbientSoundCurve.MaxVolume);
            prev = v;
        }
    }

    [Fact]
    public void VolumeForDistance_NegativeOrPastRadius_Clamps()
    {
        // Negative distance (listener "inside" the station) clamps to full.
        Assert.Equal(StationAmbientSoundCurve.MaxVolume, StationAmbientSoundCurve.VolumeForDistance(-100f), 5);
        Assert.Equal(0f, StationAmbientSoundCurve.VolumeForDistance(StationAmbientSoundCurve.AudibleRadius * 2f), 5);
    }
}

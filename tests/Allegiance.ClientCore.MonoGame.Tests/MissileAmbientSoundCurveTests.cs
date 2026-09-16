using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P236 missile-sound distance curves
/// (<see cref="MissileAmbientSoundCurve"/>) and the native explosion-tier
/// thresholds (<see cref="ExplosionSoundTierMapper"/>). No audio device: these
/// are pure numeric mappings.
/// </summary>
public class MissileAmbientSoundCurveTests
{
    [Fact]
    public void AmbientVolume_FullAtZeroDistance()
    {
        Assert.Equal(MissileAmbientSoundCurve.MaxAmbientVolume,
            MissileAmbientSoundCurve.VolumeForDistance(0f), 5);
    }

    [Fact]
    public void AmbientVolume_LinearFalloff_HalfAtHalfRadius()
    {
        float halfway = MissileAmbientSoundCurve.AudibleRadius / 2f;
        Assert.Equal(MissileAmbientSoundCurve.MaxAmbientVolume / 2f,
            MissileAmbientSoundCurve.VolumeForDistance(halfway), 4);
    }

    [Fact]
    public void AmbientVolume_SilenceAtAudibleRadius()
    {
        Assert.Equal(0f, MissileAmbientSoundCurve.VolumeForDistance(MissileAmbientSoundCurve.AudibleRadius), 5);
    }

    [Fact]
    public void AmbientVolume_BeyondRadius_ClampsToSilence()
    {
        Assert.Equal(0f, MissileAmbientSoundCurve.VolumeForDistance(MissileAmbientSoundCurve.AudibleRadius * 3f), 5);
    }

    [Fact]
    public void Falloff_NegativeDistance_ClampsToFull()
    {
        Assert.Equal(1f, MissileAmbientSoundCurve.Falloff(-1f), 5);
    }

    [Fact]
    public void ExplosionVolume_UsesExplosionPeakNotAmbientPeak()
    {
        // The explosion one-shot peaks higher than the ambient bed.
        Assert.Equal(MissileAmbientSoundCurve.MaxExplosionVolume,
            MissileAmbientSoundCurve.ExplosionVolumeForDistance(0f), 5);
        Assert.True(MissileAmbientSoundCurve.MaxExplosionVolume > MissileAmbientSoundCurve.MaxAmbientVolume);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(MissileAmbientSoundCurve.LaunchOriginRadius, true)]
    [InlineData(MissileAmbientSoundCurve.LaunchOriginRadius + 1f, false)]
    [InlineData(1000f, false)]
    public void IsLocalLaunch_Threshold(float distance, bool expected)
    {
        Assert.Equal(expected, MissileAmbientSoundCurve.IsLocalLaunch(distance));
    }

    [Theory]
    [InlineData(150f, ExplosionSoundTier.Large)]   // > 100 → large
    [InlineData(101f, ExplosionSoundTier.Large)]
    [InlineData(100f, ExplosionSoundTier.Medium)]  // > 10, ≤ 100 → medium
    [InlineData(50f, ExplosionSoundTier.Medium)]
    [InlineData(11f, ExplosionSoundTier.Medium)]
    [InlineData(10f, ExplosionSoundTier.Small)]    // ≤ 10 → small
    [InlineData(5f, ExplosionSoundTier.Small)]
    [InlineData(0f, ExplosionSoundTier.Small)]
    public void ExplosionTierForScale_NativeThresholds(float scale, ExplosionSoundTier expected)
    {
        Assert.Equal(expected, ExplosionSoundTierMapper.ForScale(scale));
    }

    [Fact]
    public void DefaultExplosionScale_SelectsMediumTier()
    {
        // The port's documented fallback scale lands in the medium band (the
        // same tier native uses for its generic ship explosion).
        Assert.Equal(ExplosionSoundTier.Medium,
            ExplosionSoundTierMapper.ForScale(MissileAmbientSoundCurve.DefaultExplosionScale));
    }
}

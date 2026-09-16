using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ProjectileAmbientSoundCurve"/> — the
/// distance → volume falloff and the local-fire spawn-distance predicate
/// behind P232's projectile in-flight ambient sound. No graphics/audio device
/// involved; the curve is deliberately free of MonoGame types.
/// </summary>
public class ProjectileAmbientSoundCurveTests
{
    [Fact]
    public void VolumeForDistance_IsFullAtZero_SilentAtAudibleRadius()
    {
        Assert.Equal(ProjectileAmbientSoundCurve.MaxVolume, ProjectileAmbientSoundCurve.VolumeForDistance(0f), 5);
        Assert.Equal(0f, ProjectileAmbientSoundCurve.VolumeForDistance(ProjectileAmbientSoundCurve.AudibleRadius), 5);
        Assert.Equal(0f, ProjectileAmbientSoundCurve.VolumeForDistance(ProjectileAmbientSoundCurve.AudibleRadius + 1f), 5);
    }

    [Fact]
    public void VolumeForDistance_FallsLinearlyThroughMidpoint()
    {
        // Half the audible radius → half of the max volume (linear falloff).
        float mid = ProjectileAmbientSoundCurve.AudibleRadius / 2f;
        Assert.Equal(
            ProjectileAmbientSoundCurve.MaxVolume * 0.5f,
            ProjectileAmbientSoundCurve.VolumeForDistance(mid), 5);
    }

    [Fact]
    public void VolumeForDistance_IsMonotonicNonIncreasingInDistance()
    {
        float prev = ProjectileAmbientSoundCurve.MaxVolume + 1f;
        for (float d = 0f; d <= ProjectileAmbientSoundCurve.AudibleRadius + 10f; d += 471f)
        {
            float v = ProjectileAmbientSoundCurve.VolumeForDistance(d);
            Assert.True(v <= prev + 1e-6f, $"distance {d}: volume {v} rose above prev {prev}");
            Assert.InRange(v, 0f, ProjectileAmbientSoundCurve.MaxVolume);
            prev = v;
        }
    }

    [Fact]
    public void VolumeForDistance_NegativeOrPastRadius_Clamps()
    {
        // Negative distance (projectile "inside" the listener) clamps to full,
        // never above MaxVolume.
        Assert.Equal(ProjectileAmbientSoundCurve.MaxVolume, ProjectileAmbientSoundCurve.VolumeForDistance(-100f), 5);
        // Just past the radius is fully silent, not negative.
        Assert.Equal(0f, ProjectileAmbientSoundCurve.VolumeForDistance(ProjectileAmbientSoundCurve.AudibleRadius * 2f), 5);
    }

    [Fact]
    public void IsLocalFire_TreatsMuzzleProximityAsOurOwnShot()
    {
        Assert.True(ProjectileAmbientSoundCurve.IsLocalFire(0f));
        Assert.True(ProjectileAmbientSoundCurve.IsLocalFire(ProjectileAmbientSoundCurve.FireOriginRadius));
        // A distant (enemy) bolt is never misread as our fire.
        Assert.False(ProjectileAmbientSoundCurve.IsLocalFire(ProjectileAmbientSoundCurve.FireOriginRadius + 1f));
        Assert.False(ProjectileAmbientSoundCurve.IsLocalFire(ProjectileAmbientSoundCurve.AudibleRadius));
    }
}

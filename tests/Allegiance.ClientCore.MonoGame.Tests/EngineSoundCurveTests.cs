using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="EngineSoundCurve"/> — the throttle/speed →
/// volume/pitch mapping behind P213's engine loop. No graphics/audio device
/// involved; the curve is deliberately free of MonoGame types.
/// </summary>
public class EngineSoundCurveTests
{
    [Theory]
    [InlineData(-1f, 0f, 0.00f)]  // idle throttle, still → load 0
    [InlineData(-1f, 999f, 0.60f)]// idle throttle but hauling along at speed → speed term only
    [InlineData(0f, 0f, 0.20f)]   // mid throttle, still → throttle term only
    [InlineData(1f, 960f, 1.00f)] // full throttle, well past max speed → clamp 1
    [InlineData(1f, 0f, 0.40f)]   // full throttle, still → throttle term only
    public void EngineLoad_BlendsAndClamps(float throttle, float speed, float expected)
    {
        float load = EngineSoundCurve.EngineLoad(throttle, speed, EngineSoundCurve.MaxSpeedDefault);
        Assert.InRange(load, 0f, 1f);
        Assert.Equal(expected, load, 2);
    }

    [Fact]
    public void EngineLoad_NonPositiveMaxSpeed_ContributesZeroSpeedTerm()
    {
        // A zero/garbage maxSpeed must not divide by zero; only the throttle
        // term survives (0.4 * throttleNorm).
        Assert.Equal(0.4f * 0.5f, EngineSoundCurve.EngineLoad(0f, 987654f, 0f), 5);
        Assert.Equal(0f, EngineSoundCurve.EngineLoad(-1f, 987654f, -1f), 5);
    }

    [Fact]
    public void EngineLoad_IdleAndStill_IsZero()
    {
        Assert.Equal(0f, EngineSoundCurve.EngineLoad(-1f, 0f, 120f), 5);
    }

    [Fact]
    public void EngineLoad_MonotonicIncreasingInThrottle()
    {
        float prev = -1f;
        for (float t = -1f; t <= 1f; t += 0.25f)
        {
            float cur = EngineSoundCurve.EngineLoad(t, 30f, 120f);
            Assert.True(cur >= prev - 1e-6f, $"throttle {t}: load {cur} < prev {prev}");
            prev = cur;
        }
    }

    [Fact]
    public void Volume_HasIdleFloorAndClippedTop()
    {
        // Floor: idle load keeps a faint engine presence, never zero.
        Assert.Equal(EngineSoundCurve.BaseVolume, EngineSoundCurve.Volume(0f), 5);
        // Peak: ramp saturates at BaseVolume + VolumeRamp.
        Assert.Equal(EngineSoundCurve.BaseVolume + EngineSoundCurve.VolumeRamp, EngineSoundCurve.Volume(1f), 5);
        // Out-of-range loads are clamped, never exceed [0,1]; saturates at the
        // BaseVolume + VolumeRamp peak (0.65), not a full-scale 1.0.
        Assert.InRange(EngineSoundCurve.Volume(-5f), 0f, 1f);
        Assert.Equal(EngineSoundCurve.BaseVolume + EngineSoundCurve.VolumeRamp, EngineSoundCurve.Volume(5f), 5);
        Assert.True(EngineSoundCurve.Volume(0f) <= EngineSoundCurve.Volume(1f));
    }

    [Fact]
    public void Pitch_SweepsNarrowBandUpwardWithLoad()
    {
        Assert.Equal(EngineSoundCurve.IdlePitch, EngineSoundCurve.Pitch(0f), 5);
        Assert.Equal(EngineSoundCurve.FullPitch, EngineSoundCurve.Pitch(1f), 5);
        Assert.True(EngineSoundCurve.Pitch(1f) > EngineSoundCurve.Pitch(0f));
        // Always within MonoGame's legal pitch range −1..1.
        Assert.InRange(EngineSoundCurve.Pitch(-3f), -1f, 1f);
        Assert.InRange(EngineSoundCurve.Pitch(3f), -1f, 1f);
    }

    [Fact]
    public void Mix_FullThrustRaisesBothVolumeAndPitchVsIdle()
    {
        var idle = EngineSoundCurve.Mix(-1f, 0f, EngineSoundCurve.MaxSpeedDefault);
        var full = EngineSoundCurve.Mix(1f, 860f, EngineSoundCurve.MaxSpeedDefault);
        Assert.True(full.Volume > idle.Volume);
        Assert.True(full.Pitch > idle.Pitch);
        Assert.InRange(full.Volume, 0f, 1f);
        Assert.InRange(full.Pitch, -1f, 1f);
    }
}

using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="DamageDebrisTracker"/> — the P297 port of
/// native's damage-debris stream gating condition (AddDamage/RemoveDamage's
/// <c>(int)((1 − fraction)·MAX_DAMAGE_FRAMES)</c>, thinggeo.cpp:1360, 1386,
/// with <c>MAX_DAMAGE_FRAMES = 10</c>). Verifies the 0.9 threshold, the
/// 1…10 vent-count scaling, and that a healthy/dead ship streams nothing.
/// </summary>
public class DamageDebrisTrackerTests
{
    [Theory]
    // Fractions well above native's 0.9 threshold: stream off (0 vents).
    [InlineData(1.00f, 0)]
    [InlineData(0.983f, 0)]
    [InlineData(0.969f, 0)]
    [InlineData(0.954f, 0)]
    [InlineData(0.907f, 0)]
    // Mid-band fractions: exactly N = (int)((1-frac)*10) vents (1..9) —
    // native's damage-frame scaling, verified far from float tenth-boundaries.
    [InlineData(0.85f, 1)]
    [InlineData(0.75f, 2)]
    [InlineData(0.65f, 3)]
    [InlineData(0.55f, 4)]
    [InlineData(0.45f, 5)]
    [InlineData(0.35f, 6)]
    [InlineData(0.25f, 7)]
    [InlineData(0.15f, 8)]
    [InlineData(0.05f, 9)]
    // Destroyed: no intact hull to vent from.
    [InlineData(0.00f, 0)]
    public void DamageFrameCount_matches_natives_index_map(float fraction, int expected)
    {
        Assert.Equal(expected, DamageDebrisTracker.DamageFrameCount(fraction));
    }

    [Fact]
    public void Threshold_turns_the_stream_on_at_09_and_off_above_it()
    {
        // Native turns the stream on the instant (1-frac)*10-1 >= 0, i.e.
        // frac <= 0.9, and off again above 0.9 (bidirectional via
        // RemoveDamage). Verified against float32 reality: 0.90f -> 1 vent,
        // 0.91f -> 0.
        Assert.Equal(1, DamageDebrisTracker.DamageFrameCount(0.90f));
        Assert.Equal(0, DamageDebrisTracker.DamageFrameCount(0.91f));
    }

    [Fact]
    public void DamageFrameCount_stays_within_native_frame_cap()
    {
        // The count must never exceed MAX_DAMAGE_FRAMES (native indices 0..9).
        for (int f = 0; f <= 100; f++)
        {
            int vents = DamageDebrisTracker.DamageFrameCount(f / 100f);
            Assert.InRange(vents, 0, DamageDebrisTracker.MaxDamageFrames);
        }
    }

    [Fact]
    public void More_damage_never_yields_fewer_vents()
    {
        // Native opens damage frames monotonically as the hull drops. Stepping
        // fraction downward (f: 100 -> 0 = more damage) must never reduce the
        // vent count.
        int prev = 0;
        for (int f = 100; f >= 1; f--)   // frac 1.0 .. 0.01 (excludes the destroyed 0.0 sentinel)
        {
            int vents = DamageDebrisTracker.DamageFrameCount(f / 100f);
            Assert.True(vents >= prev, $"vents fell while damage increased: {vents} < {prev}");
            prev = vents;
        }
    }

    [Fact]
    public void Constant_is_the_documented_native_constants()
    {
        // Guard the port against silently changing the native contract.
        Assert.Equal(10, DamageDebrisTracker.MaxDamageFrames);
        Assert.Equal(0.9f, DamageDebrisTracker.DamageThreshold, 3);
    }
}

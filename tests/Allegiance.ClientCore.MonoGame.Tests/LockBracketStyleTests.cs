using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="LockBracketStyle"/> — the per-state sizing
/// (and its pulse animation) behind the P230/P428 missile-lock indicator. No
/// graphics device (same discipline as <see cref="RockMeshFactoryTests"/>).
/// As of P428 the indicator's SHAPE is the real original sprite (see
/// <see cref="LockTextures"/> / <see cref="LockTextureSourceTests"/>), so the
/// style only contributes the on-screen scale and its gentle pulse; the old
/// per-state tint (ColorFor) and acquisition spin (RotationFor) were removed
/// because the real sprites carry their own fixed colours and are 4-fold
/// symmetric (a spin would read as jitter, and native never rotates them).
/// </summary>
public class LockBracketStyleTests
{
    [Fact]
    public void NoLock_HalfSize_IsStatic_AcrossTime()
    {
        float t1 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.None, 0.0);
        float t2 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.None, 3.7);
        Assert.Equal(t1, t2);
        Assert.Equal(LockBracketStyle.BaseHalfSize, t1);   // no pulse for no-lock
    }

    [Fact]
    public void PartialAndFull_HalfSize_Pulse_AndDiffer()
    {
        float p0 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Partial, 0.0);
        float p1 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Partial, 0.5);
        // Pulses about the base within the ±5px amplitude band.
        Assert.InRange(p0, LockBracketStyle.BaseHalfSize - 5f, LockBracketStyle.BaseHalfSize + 5f);
        Assert.InRange(p1, LockBracketStyle.BaseHalfSize - 5f, LockBracketStyle.BaseHalfSize + 5f);
        Assert.NotEqual(p0, p1);   // it is actually animating

        float f0 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Full, 0.0);
        float f1 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Full, 0.5);
        Assert.InRange(f0, LockBracketStyle.BaseHalfSize - 9f, LockBracketStyle.BaseHalfSize + 9f);
        Assert.NotEqual(f0, f1);
    }

    [Fact]
    public void Full_PulseIsStrongerThanPartial()
    {
        // A full lock pulses harder (larger amplitude) than a partial one, so an
        // acquired lock reads as hotter on the real sprite. Compare the PEAK
        // deviations reached over a dense sample (a point-by-point comparison is
        // unreliable — Full's sine dips through zero).
        float MaxAbsDev(MissileLockFeedback.LockState st)
        {
            float max = 0f;
            for (int i = 0; i < 400; i++)
            {
                double now = i * 0.007;
                float dev = Math.Abs(LockBracketStyle.HalfSizeFor(st, now) - LockBracketStyle.BaseHalfSize);
                if (dev > max) max = dev;
            }
            return max;
        }

        float partialPeak = MaxAbsDev(MissileLockFeedback.LockState.Partial);
        float fullPeak = MaxAbsDev(MissileLockFeedback.LockState.Full);

        // Partial peaks at ±5, Full at ±9.
        Assert.InRange(partialPeak, 4.5f, 5.5f);
        Assert.InRange(fullPeak, 8.5f, 9.5f);
        Assert.True(fullPeak > partialPeak);
    }

    [Fact]
    public void AllStates_StayCloseToBaseHalfSize()
    {
        // The on-screen span never collapses or explodes — always a sane,
        // readable indicator size around the target (native scales these blips
        // by radiusBracket; this port fixes a readable 68px span + pulse).
        for (int i = 0; i < 100; i++)
        {
            double now = i * 0.031;
            foreach (var state in new[]
            {
                MissileLockFeedback.LockState.None,
                MissileLockFeedback.LockState.Partial,
                MissileLockFeedback.LockState.Full,
            })
            {
                float h = LockBracketStyle.HalfSizeFor(state, now);
                Assert.InRange(h, LockBracketStyle.BaseHalfSize - 9.5f, LockBracketStyle.BaseHalfSize + 9.5f);
            }
        }
    }
}

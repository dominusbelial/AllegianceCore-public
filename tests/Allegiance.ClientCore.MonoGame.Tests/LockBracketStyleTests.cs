using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="LockBracketStyle"/> — the per-state tint and
/// animation parameters behind the P230 missile-lock target box. No graphics
/// device (same discipline as <see cref="RockMeshFactoryTests"/>). They pin the
/// visual contract: no-lock is dim + static, partial is amber + gentle scale
/// pulse, full is hot red + stronger pulse + an acquisition spin.
/// </summary>
public class LockBracketStyleTests
{
    [Fact]
    public void ColorFor_IsDistinct_PerState()
    {
        var none = LockBracketStyle.ColorFor(MissileLockFeedback.LockState.None);
        var partial = LockBracketStyle.ColorFor(MissileLockFeedback.LockState.Partial);
        var full = LockBracketStyle.ColorFor(MissileLockFeedback.LockState.Full);
        Assert.NotEqual(none, partial);
        Assert.NotEqual(partial, full);
        Assert.NotEqual(none, full);
        Assert.Equal(LockBracketStyle.NoLock, none);
        Assert.Equal(LockBracketStyle.Partial, partial);
        Assert.Equal(LockBracketStyle.Full, full);
    }

    [Fact]
    public void NoLock_HalfSize_IsStatic_AcrossTime()
    {
        float t1 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.None, 0.0);
        float t2 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.None, 3.7);
        Assert.Equal(t1, t2);
        Assert.Equal(34f, t1);   // base half-size, no pulse
    }

    [Fact]
    public void PartialAndFull_HalfSize_Pulse_AndDiffer()
    {
        float p0 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Partial, 0.0);
        float p1 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Partial, 0.5);
        // Pulses about the 34px base within the ±5px amplitude band.
        Assert.InRange(p0, 34f - 5f, 34f + 5f);
        Assert.InRange(p1, 34f - 5f, 34f + 5f);
        Assert.NotEqual(p0, p1);   // it is actually animating

        float f0 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Full, 0.0);
        float f1 = LockBracketStyle.HalfSizeFor(MissileLockFeedback.LockState.Full, 0.5);
        Assert.InRange(f0, 34f - 9f, 34f + 9f);
        Assert.NotEqual(f0, f1);
    }

    [Fact]
    public void FullLock_Spins_OthersDoNot()
    {
        Assert.Equal(0f, LockBracketStyle.RotationFor(MissileLockFeedback.LockState.None, 5.0));
        Assert.Equal(0f, LockBracketStyle.RotationFor(MissileLockFeedback.LockState.Partial, 5.0));
        Assert.NotEqual(0f, LockBracketStyle.RotationFor(MissileLockFeedback.LockState.Full, 5.0));
        // Rotation advances with wall-clock time (acquisition spin).
        Assert.True(LockBracketStyle.RotationFor(MissileLockFeedback.LockState.Full, 10.0)
                  > LockBracketStyle.RotationFor(MissileLockFeedback.LockState.Full, 1.0));
    }
}

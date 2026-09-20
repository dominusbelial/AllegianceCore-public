using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ChaffAnimation"/> (P292) — no graphics device
/// involved. They pin the native chaff size curve (<c>max(1, radius·(1 −
/// dtLeft/lifespan))</c>, <c>src/Igc/chaffigc.cpp:82-83</c>) and the native spin
/// rate (<c>Rotation(Vector(0,0,1), 2.75f)</c>, <c>chaffigc.cpp:55</c>).
/// </summary>
public class ChaffAnimationTests
{
    [Fact]
    public void CurrentRadius_IsOneFloor_AtBirth()
    {
        Assert.Equal(1.0f, ChaffAnimation.CurrentRadius(0f, 2f, 5f), 3);
    }

    [Fact]
    public void CurrentRadius_GrowsLinearlyTowardsCeiling()
    {
        // radius 5, lifespan 2: at age 1 → 5 · (1/2) = 2.5 (above the 1 floor).
        Assert.Equal(2.5f, ChaffAnimation.CurrentRadius(1f, 2f, 5f), 3);
    }

    [Fact]
    public void CurrentRadius_ReachesCeiling_AtFullAge()
    {
        Assert.Equal(5f, ChaffAnimation.CurrentRadius(2f, 2f, 5f), 3);
    }

    [Fact]
    public void CurrentRadius_IsOneFloor_ForNegativeOrDegenerateAge()
    {
        // Pre-receipt (negative age) and a degenerate (non-positive) lifespan
        // both hold the 1.0 floor rather than producing nonsense.
        Assert.Equal(1.0f, ChaffAnimation.CurrentRadius(-3f, 2f, 5f), 3);
        Assert.Equal(1.0f, ChaffAnimation.CurrentRadius(1f, 0f, 5f), 3);
        Assert.Equal(1.0f, ChaffAnimation.CurrentRadius(1f, -1f, 5f), 3);
    }

    [Fact]
    public void RollAngle_ScalesByNativeSpinRate()
    {
        // Native Rotation(Vector(0,0,1), 2.75) (chaffigc.cpp:55) is 2.75 rad/s.
        Assert.Equal(0f, ChaffAnimation.RollAngle(0f), 3);
        Assert.Equal(2.75f, ChaffAnimation.RollAngle(1f), 3);
        Assert.Equal(5.5f, ChaffAnimation.RollAngle(2f), 3);
    }
}

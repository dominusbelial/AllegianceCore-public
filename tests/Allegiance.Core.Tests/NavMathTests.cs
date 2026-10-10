using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P594 — the port of native <c>solveForImpact</c> (<c>Igc/common.cpp:59-115</c>),
/// used for the native "mmf pod pickup" intercept (common.cpp:1440-1445). Every
/// expectation is derived analytically from the native quadratic
/// <c>0 = deltaP² − radius² + 2t(deltaV·deltaP − radius·speed) + t²(deltaV² − speed²)</c>
/// so the port is validated against the native formula, not against itself.
/// </summary>
public class NavMathTests
{
    /// <summary>Stationary target dead ahead: pod at (0,0,−500), dv=0, speed=100.
    /// c=250000, a=−10000, b=0, b24ac=1e10, t = (0+√1e10)/(−2·−10000) = 100000/20000 = 5 s.
    /// Direction = (−z)/500 = (0,0,−1).</summary>
    [Fact]
    public void StationaryTargetDeadAhead_ReturnsClosingTimeAndDirection()
    {
        float t = NavMath.SolveForImpact(
            new Vector3(0f, 0f, -500f), Vector3.Zero, 100f, 0f, out var dir);
        Assert.Equal(5f, t, 3);
        AssertNear(new Vector3(0f, 0f, -1f), dir);
    }

    /// <summary>Receding pod: pod at (0,0,−300) moving away along −z at 40, pursuer
    /// at 100. Closes at 60/s, so t=5. a = 1600−10000 = −8400, b = 2·12000 = 24000,
    /// b24ac = 3.6e9 (√=60000), t = (24000+60000)/(−2·−8400) = 84000/16800 = 5.
    /// Intercept = (0,0,−300)+5·(0,0,−40) = (0,0,−500).</summary>
    [Fact]
    public void RecedingPod_LeadsToFutureInterceptPosition()
    {
        float t = NavMath.SolveForImpact(
            new Vector3(0f, 0f, -300f), new Vector3(0f, 0f, -40f), 100f, 0f, out var dir);
        Assert.Equal(5f, t, 3);
        // Direction to the intercept: (0,0,−500)/500 = (0,0,−1).
        AssertNear(new Vector3(0f, 0f, -1f), dir);
    }

    /// <summary>Tangential moving pod that CAN be caught: pod at (0,0,−300) moving
    /// +x at 50, pursuer 100. a=2500−10000=−7500, b=0, b24ac=2.7e9 (√=51961.5),
    /// t = 51961.5/15000 ≈ 3.464. Intercept = (0,0,−300)+3.464·(50,0,0) ≈
    /// (173.2,0,−300) — displaced ahead of the pod's current position, proving the
    /// lead.</summary>
    [Fact]
    public void TangentialMovingPod_InterceptLeadsTheTarget()
    {
        float t = NavMath.SolveForImpact(
            new Vector3(0f, 0f, -300f), new Vector3(50f, 0f, 0f), 100f, 0f, out _);
        Assert.Equal(3.464f, t, 2);
    }

    /// <summary>No-solution branch: pod receding faster than the pursuer can close
    /// (dv = −200 along the line of approach, speed 100). a = 40000−10000 = 30000 &gt; 0
    /// and b = 2·(−200·−100) = 40000 &gt; 0 → native returns the huge sentinel time
    /// (FLT_MAX/500000000) and leaves <c>direction</c> UNSET (default).</summary>
    [Fact]
    public void UncatchableTarget_ReturnsHugeSentinel_LeavesDirectionUnset()
    {
        float t = NavMath.SolveForImpact(
            new Vector3(0f, 0f, -100f), new Vector3(0f, 0f, -200f), 100f, 0f, out var dir);
        Assert.True(t > 1e15f, $"expected the huge no-solution sentinel, got {t}");
        Assert.Equal(default, dir);   // native leaves *direction unset on this branch
    }

    /// <summary>Within-contact branch (radius &gt; 0): target closer than the contact
    /// radius → c ≤ 0 → t=0 and direction straight at the target.</summary>
    [Fact]
    public void WithinContact_ReturnsZero_AndStraightDirection()
    {
        float t = NavMath.SolveForImpact(
            new Vector3(0f, 0f, -50f), Vector3.Zero, 100f, 100f, out var dir);
        Assert.Equal(0f, t);
        AssertNear(new Vector3(0f, 0f, -1f), dir);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, int places = 3)
    {
        Assert.Equal(expected.X, actual.X, places);
        Assert.Equal(expected.Y, actual.Y, places);
        Assert.Equal(expected.Z, actual.Z, places);
    }
}

using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P489 — PURE unit tests for the extracted shared dodge law
/// <see cref="ShipDodgeLaw"/> (native <c>Dodge</c>, common.cpp:1692-1790),
/// mirroring the P448a <see cref="ShipKinematics"/> extraction precedent: the
/// law is now testable WITHOUT a live server, and the same module the server's
/// <see cref="Ship.TryComputeDodgeGoal"/> calls byte-identically is what these
/// tests exercise directly. The server-side P168/P170 live tests
/// (<c>NpcDodgeLiveTests</c>) and <c>MultiHopDroneLiveTests</c> remain the
/// byte-identical integration check; these assert the LAW's geometry in
/// isolation.
/// </summary>
public sealed class ShipDodgeLawTests
{
    private const float FallbackTMax = 3f;

    private static ShipDodgeLaw.Mover Mover(Vector3 pos, Vector3 vel, float accel = 20f, float radiusPlusOne = 5f)
        => new(pos, vel, radiusPlusOne, accel);

    private static ShipDodgeLaw.Obstacle Rock(Vector3 pos, float radius, bool ignore = false)
        => new(pos, Vector3.Zero, radius, ignore);

    private static ShipDodgeLaw.Obstacle Moving(Vector3 pos, Vector3 vel, float radius, bool ignore = false)
        => new(pos, vel, radius, ignore);

    /// <summary>A rock dead ahead on a stationary-heading course is an imminent
    /// collision; the law returns a dodge goal whose DIRECTION is perpendicular
    /// to the travel line (the native dead-centre orthogonal fallback,
    /// common.cpp:1777-1782) and whose magnitude is the dodge goal distance.</summary>
    [Fact]
    public void Mover_heading_into_a_static_rock_gets_a_perpendicular_dodge_goal()
    {
        var mover = Mover(new Vector3(0, 0, 0), new Vector3(100, 0, 0));
        var rock = Rock(new Vector3(100, 0, 0), radius: 30f);   // r = 30 + 5 = 35; t = (100-35)/100 = 0.65s → imminent

        Assert.True(ShipDodgeLaw.TryComputeDodgeGoal(mover, new[] { rock }, 520f, FallbackTMax, out var goal));
        Assert.True(MathF.Abs(goal.X) < 1e-3f, $"perpendicular away from the +X line: goal={goal}");
        Assert.True(goal.Z < 0f, $"points away from the threat (native away vector): goal={goal}");
        Assert.Equal(520f, (goal - mover.Position).Length(), 0.01f);
    }

    /// <summary>A rock well off the travel line is never an imminent collision —
    /// the law returns false (no dodge), exactly the P168 "no-obstacle
    /// unaffected" server behaviour.</summary>
    [Fact]
    public void Rock_off_the_path_yields_no_dodge_goal()
    {
        var mover = Mover(new Vector3(0, 0, 0), new Vector3(100, 0, 0));
        var rock = Rock(new Vector3(100, 500, 0), radius: 30f);   // closest approach ~500 units off the line → moving apart

        Assert.False(ShipDodgeLaw.TryComputeDodgeGoal(mover, new[] { rock }, 520f, FallbackTMax, out _));
    }

    /// <summary>The navigation-goal / excluded-category skip (native pmodelSkip /
    /// Ignore) is honoured: a geometrically imminent rock marked Ignore does not
    /// produce a dodge.</summary>
    [Fact]
    public void Ignored_obstacle_is_skipped_even_when_imminent()
    {
        var mover = Mover(new Vector3(0, 0, 0), new Vector3(100, 0, 0));
        var rock = Rock(new Vector3(100, 0, 0), radius: 30f, ignore: true);

        Assert.False(ShipDodgeLaw.TryComputeDodgeGoal(mover, new[] { rock }, 520f, FallbackTMax, out _));
    }

    /// <summary>Among several imminent obstacles the one with the STRICTLY
    /// smallest time-to-impact wins (native's t &lt; tCollide scan), and the
    /// dodge points away from IT, not from a nearer-but-later neighbour.</summary>
    [Fact]
    public void Nearest_time_to_impact_wins()
    {
        var mover = Mover(new Vector3(0, 0, 0), new Vector3(100, 0, 0));
        var far = Rock(new Vector3(200, 0, 0), radius: 30f);    // t = (200-35)/100 = 1.65s
        var near = Rock(new Vector3(100, 0, 0), radius: 30f);   // t = 0.65s → wins regardless of order

        Assert.True(ShipDodgeLaw.TryComputeDodgeGoal(mover, new[] { far, near }, 520f, FallbackTMax, out var goal));
        Assert.True(MathF.Abs(goal.X) < 1e-3f && goal.Z < 0f, $"away vector derived from the NEAREST impact: goal={goal}");
    }

    /// <summary>A MOVING obstacle is avoided using its relative velocity (native's
    /// closest-approach offset, common.cpp:1774-1776): a ship approaching head-on
    /// is dodged perpendicular to the relative line.</summary>
    [Fact]
    public void Moving_obstacle_avoided_via_relative_velocity()
    {
        var mover = Mover(new Vector3(0, 0, 0), Vector3.Zero);
        var inbound = Moving(new Vector3(60, 0, 0), new Vector3(-50, 0, 0), radius: 10f);   // closing at 50 u/s; t = 0.9s

        Assert.True(ShipDodgeLaw.TryComputeDodgeGoal(mover, new[] { inbound }, 520f, FallbackTMax, out var goal));
        Assert.True(MathF.Abs(goal.X) < 1e-3f && goal.Z < 0f, $"dodges the moving ship laterally: goal={goal}");
    }

    /// <summary>Already inside the obstacle → t = 0 → the away vector is STRAIGHT
    /// OUT OF THE CENTRE (native common.cpp:1767-1768), not the closest-approach
    /// offset.</summary>
    [Fact]
    public void Already_inside_obstacle_dodges_straight_away_from_centre()
    {
        var mover = Mover(new Vector3(0, 0, 0), Vector3.Zero);
        var rock = Rock(new Vector3(5, 0, 0), radius: 30f);   // mover inside combined radius 35 → t = 0

        Assert.True(ShipDodgeLaw.TryComputeDodgeGoal(mover, new[] { rock }, 520f, FallbackTMax, out var goal));
        Assert.True(MathF.Abs(goal.Y) < 1e-3f && MathF.Abs(goal.Z) < 1e-3f && goal.X < 0f,
            $"straight away from the centre along −X: goal={goal}");
        Assert.Equal(520f, (goal - mover.Position).Length(), 0.01f);
    }

    /// <summary>Degenerate (non-positive) acceleration falls back to the fixed
    /// tMax horizon (the server's DodgeFallbackTMax=3): a soon collision is
    /// dodged, a late one is not.</summary>
    [Fact]
    public void NonPositive_acceleration_uses_fallback_horizon()
    {
        var moverZero = Mover(new Vector3(0, 0, 0), new Vector3(100, 0, 0), accel: 0f);
        var soon = Rock(new Vector3(100, 0, 0), radius: 30f);   // t = 0.65 ≤ 3 → dodge
        Assert.True(ShipDodgeLaw.TryComputeDodgeGoal(moverZero, new[] { soon }, 520f, FallbackTMax, out _));

        var late = Rock(new Vector3(1000, 0, 0), radius: 30f);  // t = 9.65 > 3 → no dodge
        Assert.False(ShipDodgeLaw.TryComputeDodgeGoal(moverZero, new[] { late }, 520f, FallbackTMax, out _));
    }

    /// <summary>Sanity check on the moved <c>solveForImpact</c> primitive (now on
    /// the module): a head-on interception at known geometry yields the expected
    /// positive time.</summary>
    [Fact]
    public void SolveForImpact_reports_the_expected_intercept_time()
    {
        float t = ShipDodgeLaw.SolveForImpact(
            new Vector3(100, 0, 0), new Vector3(-100, 0, 0), 0f, 35f, out var dir);
        Assert.Equal(0.65f, t, 1e-4f);
        Assert.True(dir.X > 0f, $"aim direction points along the separation: dir={dir}");
    }
}

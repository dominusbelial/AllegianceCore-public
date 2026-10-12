using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P618 — unit tests for the EVE-style load-driven time-dilation controller
/// (<see cref="TimeDilationController"/>). These are pure-logic tests of the
/// controller's contract: convergence under steady overload, no oscillation,
/// hysteresis, recovery, and manual-override-wins. The complementary
/// server-integration timer audit lives in
/// <see cref="TimeDilationTimerAuditTests"/>.
/// </summary>
public class TimeDilationControllerTests
{
    private const double Nominal = 0.05;   // 20 updates/s default server period

    private static TimeDilationParams Params(
        double? engage = null, double? release = null, double? deadband = null,
        double? maxChange = null, double? controlInterval = null, double? alpha = null)
        => new(
            nominalPeriod: Nominal,
            alpha: alpha ?? 0.07,
            engageRatio: engage ?? 1.005,
            releaseRatio: release ?? 0.965,
            deadband: deadband ?? 0.010,
            maxChangePerSecond: maxChange ?? 0.080,
            controlIntervalSeconds: controlInterval ?? 0.0625);

    /// <summary>Feed a steady cost for many observations (enough wall seconds for
    /// the controller to reach its steady-state target).</summary>
    private static void RunSteady(TimeDilationController c, double cost, int iterations = 6000,
        double wallDelta = 0.0039)
    {
        for (int i = 0; i < iterations; i++) c.Observe(cost, wallDelta);
    }

    [Fact]
    public void HealthyTick_NeverEngages_StaysAtFullSpeed()
    {
        // cost = nominal (ratio 1.0, below engage 1.005) → must never slow down.
        var c = new TimeDilationController(Params());
        RunSteady(c, Nominal);
        Assert.Equal(1.0, c.Scale, 3);
        Assert.False(c.Engaged);
    }

    [Fact]
    public void SteadyOverload_ConvergesToNominalOverCost()
    {
        // cost 0.086 → ratio 1.719 → target = 0.05/0.086 = 0.5814.
        double cost = 0.060; // 1.2× nominal → target = 0.8333
        var c = new TimeDilationController(Params());
        RunSteady(c, cost);
        double expected = Nominal / cost;
        Assert.True(c.Engaged, "controller should be engaged under overload");
        // Converged within the deadband of the expected target (allow a little
        // extra for EMA + stepping).
        Assert.InRange(c.Scale, expected - 0.072, expected + 0.064);
        Assert.InRange(c.Scale, 0.778, 0.928);
    }

    [Fact]
    public void SteadyOverload_MonotonicNonIncreasing_NoOscillation()
    {
        var c = new TimeDilationController(Params());
        double prev = c.Scale;
        for (int i = 0; i < 9000; i++)
        {
            c.Observe(0.070, 0.0039);
            Assert.True(c.Scale <= prev + 1e-9,
                $"scale rose (oscillation) at iter {i}: {prev:R} -> {c.Scale:R}");
            prev = c.Scale;
        }
    }

    [Fact]
    public void AfterConvergence_FurtherObservationsLeaveScaleUntouched()
    {
        var c = new TimeDilationController(Params());
        RunSteady(c, 0.068);                       // converge toward 0.735
        double settled = c.Scale;
        Assert.True(settled < 1.0);
        for (int i = 0; i < 5000; i++)
            c.Observe(0.066, 0.0039);              // same ballpark load
        Assert.InRange(c.Scale, settled - 0.021, settled + 0.021);   // no drift/oscillation
    }

    [Fact]
    public void Recovery_LowLoad_EasesBackTowardFullSpeed()
    {
        var c = new TimeDilationController(Params());
        RunSteady(c, 0.090);                       // deep overload → slow motion
        Assert.True(c.Scale < 0.6);
        RunSteady(c, 0.019);                       // cost ≪ nominal → release toward 1.0
        Assert.InRange(c.Scale, 0.985, 1.0);
        Assert.False(c.Engaged);
    }

    [Fact]
    public void ManualOverride_WinsAndSuspendsAuto()
    {
        var c = new TimeDilationController(Params());
        c.SetManual(0.5);
        Assert.Equal(0.5, c.Scale, 6);
        for (int i = 0; i < 9999; i++)
            Assert.False(c.Observe(0.099, 0.039));  // heavy overload must NOT move it
        Assert.Equal(0.5, c.Scale, 6);
        Assert.True(c.ManualOverride);
    }

    [Fact]
    public void ClearManual_ResumesAuto()
    {
        var c = new TimeDilationController(Params());
        c.SetManual(0.4);
        Assert.True(c.ClearManual());
        Assert.False(c.ManualOverride);
        // Auto now governs again: heavy load pulls the scale down from 0.4 floor.
        RunSteady(c, 0.095);
        Assert.True(c.Scale < 0.955);   // not pinned at the manual value any more
    }

    [Fact]
    public void ScaleNeverLeavesClampedRange()
    {
        var c = new TimeDilationController(Params());
        // Extreme overload (cost >> nominal) can never push below MinScale.
        RunSteady(c, 0.997);
        Assert.InRange(c.Scale, 0.097, 1.0);
        // Manual values outside range are clamped.
        c.SetManual(0.0);
        Assert.InRange(c.Scale, 0.097, 1.0);
        c.SetManual(5.0);
        Assert.Equal(1.0, c.Scale, 6);
    }
}

using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P464 — pure tests for <see cref="ServerClockEstimator"/>, the client-side
/// reconstruction of the server's sim clock that supplies the
/// <c>ACTIVE_TURRET_UPDATE</c> <c>time</c>. The server drops any turret update
/// whose <c>time</c> is more than ±1.5 s from the gunner's <c>LastUpdate</c>
/// (<c>shipIGC.cpp:3353-3354</c>), so the central requirement is that the
/// estimate stay inside that gate even when pose samples arrive under network
/// latency jitter.
/// </summary>
public sealed class ServerClockEstimatorTests
{
    [Fact]
    public void Estimate_stays_within_the_1_5s_gate_under_simulated_latency()
    {
        // Model: the server's sim clock advances 1:1 with real seconds (20 sim
        // updates/s ≈ 0.05 s each). It streams a pose every 0.075 s; the client
        // observes each one after a latency drawn uniformly from [0.05, 0.30] s.
        var rng = new Random(1234);
        var est = new ServerClockEstimator();
        double serverTick = 0.075;
        double latencyMin = 0.050, latencyMax = 0.300;

        // Local clock and the true server clock share an origin; the client
        // merely sees each server sample delayed by its latency.
        double localNow = 0.0;
        for (double serverTime = 0.0; serverTime < 30.0; serverTime += serverTick)
        {
            double latency = latencyMin + rng.NextDouble() * (latencyMax - latencyMin);
            double localRecv = serverTime + latency;                 // when the sample actually lands
            est.AddSample((uint)Math.Floor(serverTime), (long)(localRecv * 1000.0));
        }

        // Probe estimates at many instants across the whole span (between and
        // just past samples) and require every one to sit within the gate.
        double worst = 0.0;
        for (localNow = 0.0; localNow < 30.0; localNow += 0.019)
        {
            double estimate = est.Estimate((long)(localNow * 1000.0));
            double trueServerTime = localNow;                        // server clock == real seconds here
            double err = Math.Abs(estimate - trueServerTime);
            worst = Math.Max(worst, err);
            Assert.True(err < 1.5,
                $"estimate {estimate:F3} vs true {trueServerTime:F3} (err {err:F3}) escaped the ±1.5 s gate");
        }
        Assert.True(worst < 0.5, $"worst error under jitter unexpectedly large: {worst:F3}s");
    }

    [Fact]
    public void Without_samples_there_is_no_estimate_and_it_reports_zero()
    {
        var est = new ServerClockEstimator();
        Assert.False(est.HasEstimate);
        Assert.Equal(0.0, est.Estimate(100_000));
    }

    [Fact]
    public void Non_monotonic_server_samples_are_ignored()
    {
        var est = new ServerClockEstimator();
        est.AddSample(100u, 10_000);
        Assert.True(est.HasEstimate);
        // A duplicate / earlier server time must not extend the window.
        est.AddSample(99u, 10_500);
        est.AddSample(100u, 10_600);
        // Single retained sample → slope-1 extrapolation from (100, 10s).
        double estNow = est.Estimate(12_000);                        // 12s local
        Assert.Equal(102.0, estNow, 6);                              // 100 + (12 - 10)
    }

    [Fact]
    public void Advances_with_the_server_clock_when_latency_is_stable()
    {
        var est = new ServerClockEstimator();
        // Five samples, each received exactly 0.1 s after its server time.
        for (int i = 0; i < 5; i++)
        {
            uint serverTime = (uint)(i * 10u);
            long localMs = i * 10_000L + 100L;                       // server + 0.1 s latency
            est.AddSample(serverTime, localMs);
        }
        // At local 70.1 s the estimate should be server 70 s (slope 1, the
        // latency bias cancels once multiple samples align the fit).
        double e = est.Estimate(70_100);
        Assert.Equal(70.0, e, 1);
    }
}

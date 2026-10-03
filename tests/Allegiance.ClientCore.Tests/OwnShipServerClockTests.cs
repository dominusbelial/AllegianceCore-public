using Allegiance.ClientCore.Flight;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P508 — pure tests for <see cref="OwnShipServerClock"/>, the client's server-time
/// (milliseconds) conversion for the native <c>SHIP_UPDATE</c>, fed from
/// <c>FM_CS_PING(35)</c> echoes (server stamps <c>timeServer = SimNow·1000</c>). The
/// server drops any native update whose <c>timeUpdate/1000</c> is outside ±1.5 s of its
/// <c>SimNow</c>, so the central requirement is that the stamped milliseconds stay
/// inside that gate when ping echoes arrive under network latency jitter.
/// </summary>
public sealed class OwnShipServerClockTests
{
    [Fact]
    public void ServerTimeMs_stays_within_the_gate_under_jittered_rtt()
    {
        var rng = new Random(1234);
        var clock = new OwnShipServerClock();
        double firstSimMs = 30_000;                   // mission already 30 s in
        double rttMin = 0.004, rttMax = 0.070;        // 4–70 ms RTT (LAN-ish)

        double worst = 0.0;
        for (double simMs = firstSimMs; simMs < 62_000; simMs += 55.0)   // ~18 Hz ping cadence
        {
            double rtt = rttMin + rng.NextDouble() * (rttMax - rttMin);
            double localRecvMs = simMs + rtt;         // local epoch ~ sim epoch
            clock.AddSample((uint)simMs, (long)localRecvMs);

            // A native SHIP_UPDATE sent shortly after this echo.
            double probeLocalMs = localRecvMs + 12.0;
            double estMs = clock.ServerTimeMs((long)probeLocalMs);
            double trueSimNowMs = probeLocalMs;       // server time == real ms at probe local instant
            double err = Math.Abs(estMs - trueSimNowMs);
            worst = Math.Max(worst, err);
            Assert.True(err < 1500.0,
                $"est {estMs:F0} ms vs true {trueSimNowMs:F0} ms (err {err:F0} ms) escaped the ±1500 ms gate");
        }
        Assert.True(worst < 500.0, $"worst error {worst:F0} ms unexpectedly large");
    }

    [Fact]
    public void Ready_false_until_first_echo_and_zero_time_before()
    {
        var clock = new OwnShipServerClock();
        Assert.False(clock.Ready);
        Assert.Equal(0u, clock.ServerTimeMs(100_000));
    }

    [Fact]
    public void Single_echo_extrapolates_the_offset_at_slope_one()
    {
        var clock = new OwnShipServerClock();
        // Server processed a ping at sim 42 000 ms; the echo arrived at local 42 063 ms
        // (63 ms RTT). Extrapolation at slope ~1 keeps the 63 ms offset, so the server
        // time at local 42 083 ms is 42 083 − 63 = 42 020 ms.
        clock.AddSample(42_000u, 42_063);
        Assert.True(clock.Ready);
        double est = clock.ServerTimeMs(42_083);      // 20 ms later
        Assert.True(Math.Abs(est - 42_020) < 5.0, $"expected ~42020 ms, got {est:F0} ms");
    }

    [Fact]
    public void Multiple_echoes_absorb_a_consistent_rtt_offset()
    {
        var clock = new OwnShipServerClock();
        // Constant 60 ms RTT; server time advances 1:1 with local (epoch-aligned).
        for (long t = 10_000; t < 40_000; t += 250)
            clock.AddSample((uint)t, t + 60);
        double est = clock.ServerTimeMs(40_042);      // near the last echo's local time
        // The fit returns the server time at this local instant = 40_042 - 60 = 39_982.
        Assert.True(Math.Abs(est - 39_982) < 5.0, $"expected ~39982 ms, got {est:F0} ms");
    }
}

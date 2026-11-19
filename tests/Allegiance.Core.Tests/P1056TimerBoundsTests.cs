using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1056 — deterministic, no-server reproductions of the P739 one-ULP flake and
/// of the lateness the fixed bound must still reject. See
/// <c>docs/reports/TIMER_BOUNDS_P1056.md</c>.
/// </summary>
public sealed class P1056TimerBoundsTests
{
    // The live mission clock is the accumulated 1/20 s tick grid: SimPass adds
    // EffectiveSimPeriod = 0.05 s to _simNow on every tick (Server.Broadcast.cs:138),
    // so armedAt is never "round". These two values are the exact clocks the live
    // P739 theory rows armed the bomb at when they failed in this lane
    // (delay 5 and delay 7 rows, observed as 5.0000000000000009 and
    // 7.0000000000000009): both make the production sum round UP by one ULP, so
    // re-deriving the delay as (deadline - clock) returns delay + 8.88e-16.
    [Theory]
    [InlineData(6.299999999999986, 5f)]
    [InlineData(6.8999999999999835, 7f)]
    public void Re_deriving_the_delay_from_the_deadline_is_one_ulp_over_the_loaded_delay(double clock, float delay)
    {
        double deadline = TimerBounds.WarpBombDeadline(clock, delay);
        // Guard the premise: this clock really is a round-up case, not a lucky one.
        Assert.Equal(delay + (Math.BitIncrement((double)delay) - delay), deadline - clock);
        Assert.True(deadline - clock > delay);
        // The deadline itself is the production expression, so the bound accepts it.
        Assert.True(TimerBounds.WarpBombDeadlineMatches(deadline, clock, delay));
    }

    [Theory]
    [InlineData(6.299999999999986, 5f)]
    [InlineData(6.8999999999999835, 7f)]
    public void A_bomb_armed_one_tick_late_is_rejected(double clock, float delay)
    {
        double late = TimerBounds.WarpBombDeadline(clock, delay) + 0.05;   // next 20 Hz tick
        Assert.False(TimerBounds.WarpBombDeadlineMatches(late, clock, delay));
    }

    [Theory]
    [InlineData(6.299999999999986, 5f)]
    [InlineData(6.8999999999999835, 7f)]
    public void A_bomb_armed_one_tick_early_is_rejected(double clock, float delay)
    {
        double early = TimerBounds.WarpBombDeadline(clock, delay) - 0.05;
        Assert.False(TimerBounds.WarpBombDeadlineMatches(early, clock, delay));
    }
}

using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1056 — shared bounds for the timer-boundary flake family. Kept in the test
/// project (never production): the live warp-bomb test and the deterministic
/// no-server regression both ask the same question, so they must answer it the
/// same way.
/// </summary>
internal static class TimerBounds
{
    /// <summary>The production warp-bomb deadline: the collision chunk's mission
    /// clock plus the loaded delay, as one double addition
    /// (<c>Sector.cs:4590</c> <c>now + EffectiveWarpBombDelay</c>; the loaded
    /// value is the float constant slot 32, <c>Igc/igc.h:98</c>).</summary>
    internal static double WarpBombDeadline(double clock, float delay) => clock + (double)delay;

    /// <summary>Is <paramref name="captured"/> the production deadline of a bomb
    /// armed at <paramref name="clock"/>, i.e. the loaded delay after it?
    /// <para><b>Why this is an equality and not a range.</b> IEEE 754 does not
    /// round-trip the expression: for the live 20 Hz mission clock
    /// (5–7 s after boot, so the sum is 10–17 s) <c>(clock + delay) - clock</c>
    /// comes back one ULP above <c>delay</c> whenever the sum rounded up — the
    /// exact 5.0000000000000009 / 7.0000000000000009 the live theory rows
    /// recorded. That is arithmetic in the harness, not a late bomb: production
    /// stores <c>clock + delay</c> as one double addition. The error is bounded
    /// by the ULP of the SUM, not of the delay (it grows with the mission
    /// clock), so no fixed epsilon is justified. Comparing the production
    /// expression itself is exact and still rejects a bomb armed one tick
    /// (0.05 s) late or early — see P1056TimerBoundsTests.</para></summary>
    internal static bool WarpBombDeadlineMatches(double captured, double clock, float delay) =>
        captured == WarpBombDeadline(clock, delay);
}

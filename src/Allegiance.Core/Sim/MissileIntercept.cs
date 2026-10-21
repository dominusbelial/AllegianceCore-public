using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

// Igc/missileigc.cpp:171-352. Solve |dp + t*dv| = acceleration*t²/2.
// Preserve native's early-root search, initial guess, 0.1 residual tolerance,
// and 20-iteration Newton bound. Steering retains the prior estimate per missile.
internal static class MissileIntercept
{
    private static double Newton(double a, double b, double c, double d,
        double guess, double? inflection = null)
    {
        double t = guess;
        for (int cycle = 0; cycle < 20; cycle++)
        {
            double t2 = t * t, t3 = t2 * t;
            double f = a + b * t + c * t2 - d * t3 * t;
            if (Math.Abs(f) < .1) return t;
            double slope = b + 2 * c * t - 4 * d * t3;
            // NewtonB abandons a near root if it crosses the slope/inflection
            // boundary; NewtonA searches the monotonically decreasing tail.
            if (inflection.HasValue && slope >= 0) return -1;
            double next = t - f / slope;
            if (inflection.HasValue && next > inflection.Value) return -1;
            if (cycle == 19) return t;
            t = next;
        }
        return t;
    }

    internal static float Solve(Vector3 dp, Vector3 dv, float acceleration, float guess)
    {
        double a = dp * dp, c = dv * dv;
        double d = acceleration * acceleration / 4.0;
        if (c == 0) return (float)Math.Pow(a / d, .25);
        double b = 2.0 * (dp * dv);
        double inflection = Math.Sqrt(c / (6 * d));
        const double epsilon = -.001;
        double Slope(double t) => b + 2 * c * t - 4 * d * t * t * t;
        if (b + (4.0 / 3.0) * c * inflection <= epsilon)
            return (float)Newton(a, b, c, d, guess);
        if (b < 0)
        {
            double early = guess;
            if (early < inflection && Slope(early) > epsilon) early = 0;
            early = Newton(a, b, c, d, early, inflection);
            if (early >= 0) return (float)early;
        }
        double late = guess;
        if (late < inflection) late = inflection + 1;
        // Native missileigc.cpp:339-341 steps `t += 1.0` until the slope is
        // sufficiently negative — uncapped, it runs ~|dv|*sqrt(2)/acceleration
        // times and never terminates once t passes 2^53 (the MSL-1 / batch-16
        // 482 s sector stall). MSL-1 NON-NATIVE bound: keep the literal unit
        // stepping for the first LateStepCap steps (so every normal-case result
        // stays bit-identical), then jump straight to the slope root.
        int steps = 0;
        while (Slope(late) > epsilon)
        {
            if (++steps > LateStepCap) { late = LateSlopeRoot(b, c, d, epsilon, late); break; }
            late += 1;
        }
        float t = (float)Newton(a, b, c, d, late);
        // MSL-1 non-native: at absurd |dv| the bounded Newton can overflow
        // t^4 to NaN (or past float range); fall back to the (finite) late start instead of
        // propagating NaN into the heading. Never taken for finite results.
        return float.IsFinite(t) ? t : (float)late;
    }

    /// <summary>MSL-1 — unit steps of the native late-root search taken before
    /// the analytic jump (<see cref="LateSlopeRoot"/>).</summary>
    internal const int LateStepCap = 10_000;

    /// <summary>
    /// MSL-1 (non-native) — the first <c>late + k</c> (integer k) at which
    /// <c>S(t) = b + 2ct - 4dt³ &lt;= epsilon</c>, i.e. where native's unit
    /// stepping would stop, found in O(1) iterations. Beyond the slope maximum
    /// (<c>t &gt; sqrt(c/6d)</c>, guaranteed here since late &gt; inflection)
    /// S is concave and decreasing, so Newton started right of the root
    /// descends monotonically onto it.
    /// </summary>
    private static double LateSlopeRoot(double b, double c, double d, double epsilon, double late)
    {
        double S(double t) => b + 2 * c * t - 4 * d * t * t * t - epsilon;
        double hi = Math.Max(late, 1);
        for (int i = 0; i < 2100 && S(hi) > 0; i++) hi *= 2;   // bracket from the right
        double t = hi;
        for (int i = 0; i < 100; i++)
        {
            double slope = 2 * c - 12 * d * t * t;
            if (!(slope < 0)) break;
            double next = t - S(t) / slope;
            if (!(next < t) || next < late) break;
            t = next;
        }
        if (!double.IsFinite(t) || t <= late) return late;
        double k = Math.Ceiling(t - late);
        double result = late + k;
        // Round-off at the root: step at most a few units, as native would.
        for (int i = 0; i < 4 && S(result) > 0; i++) result += 1;
        return result;
    }
}

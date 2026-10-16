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
        while (Slope(late) > epsilon) late += 1;
        return (float)Newton(a, b, c, d, late);
    }
}

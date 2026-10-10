using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// P594 — shared navigation math, ported verbatim from native. Currently hosts
/// <see cref="SolveForImpact"/> (native <c>Igc/common.cpp:59-115</c>), used by
/// both the server NPC autopilot (pod pickup) and the client player autopilot
/// (pod pickup intercept) so the two never drift apart. Pure and unit-testable
/// (no sim/network dependencies), matching the <see cref="ShipDodgeLaw"/>
/// extraction discipline.
/// </summary>
public static class NavMath
{
    /// <summary>
    /// Port of native <c>solveForImpact</c> (<c>Igc/common.cpp:59-115</c>) —
    /// the time <c>t</c> at which a pursuer moving at <paramref name="speed"/>
    /// (plus <paramref name="radius"/>) intercepts a target whose relative
    /// position is <paramref name="deltaP"/> and relative velocity
    /// <paramref name="deltaV"/>. Native docstring:
    /// <c>distance(t) = radius + speed·t = |deltaP + t·deltaV|</c>, solved with
    /// the quadratic
    /// <c>0 = deltaP² − radius² + 2t(deltaV·deltaP − radius·speed) + t²(deltaV² − speed²)</c>.
    /// Returns the smallest positive root (or <c>0</c> when already within
    /// contact), <paramref name="direction"/> is the unit vector to intercept
    /// at (left unset on the no-solution branch, exactly as native leaves it).
    /// <paramref name="radius"/> is passed <c>0</c> by the pod-pickup caller
    /// (<c>common.cpp:1443-1444</c>).
    /// </summary>
    /// <param name="deltaP">Target position minus pursuer position.</param>
    /// <param name="deltaV">Target velocity minus pursuer velocity (native
    /// passes just the target's velocity; the pursuer is treated as moving at
    /// <paramref name="speed"/> toward the intercept).</param>
    /// <param name="speed">The pursuer's max speed (<c>GetMaxSpeed()</c>).</param>
    /// <param name="radius">Contact radius (0 for pod pickup).</param>
    /// <param name="direction">Out: unit direction to intercept (unset on the
    /// native no-solution branch).</param>
    public static float SolveForImpact(
        in Vector3 deltaP, in Vector3 deltaV, float speed, float radius, out Vector3 direction)
    {
        direction = default;
        float c = deltaP * deltaP - radius * radius;
        if (c <= 0f)
        {
            // Already within contact: t=0, direction straight at the target.
            direction = deltaP.Normalize();
            return 0f;
        }

        float a = deltaV * deltaV - (speed * speed);
        float b = 2.0f * (deltaV * deltaP - radius * speed);
        float b24ac = b * b - 4.0f * a * c;

        if ((a == 0.0f) || ((a > 0.0f) && (b > 0.0f)) || (b24ac < 0.0f))
        {
            // No valid solution (native leaves *direction UNSET here — mmf
            // podpickup: returns a huge sentinel time).
            return float.MaxValue / 500000000.0f;
        }

        if (b24ac < 0.0f)
            b24ac = 0.1f;   // native clamps a sub-epsilon negative before sqrt (common.cpp:91-95)

        // Quadratic formula: smallest positive root.
        float t = (b + MathF.Sqrt(b24ac)) / (-2.0f * a);
        if (t > 0.0f)
        {
            direction = (deltaP + t * deltaV) / (t * speed + radius);
        }
        else
        {
            t = 0.0f;
            direction = deltaP.Normalize();
        }
        return t;
    }
}

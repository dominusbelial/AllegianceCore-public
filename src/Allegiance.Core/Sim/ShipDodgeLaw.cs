using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// P489 — a <b>pure</b> port of native's universal collision-avoidance law,
/// <c>Dodge</c> (<c>src/Igc/common.cpp:1692-1790</c>), extracted from
/// <see cref="Ship.TryComputeDodgeGoal"/> following the P448a
/// <see cref="ShipKinematics"/> precedent: same math, testable without a live
/// server, and shared by every consumer that must steer around obstacles.
///
/// <para><b>Consumers.</b> The server's <see cref="Ship.TryComputeDodgeGoal"/>
/// (P168/P170 NPC autopilot) calls it with <b>byte-identical</b> behaviour —
/// the existing P168/P170 dodge tests and <c>MultiHopDroneLiveTests</c> stay
/// green unchanged. The MCP / vizfight client bots' flight helpers (P489) feed
/// it from a peer's <c>ClientWorld</c> (asteroids, stations, known ships) so a
/// bot crossing a dense transit-hub asteroid field steers around the rocks
/// with the <b>same law the server NPC autopilot uses</b>, instead of flying an
/// obstacle-blind straight line and being bounced/destroyed by the server's
/// native impact damage (P484's documented "park at the aleph" root cause).</para>
///
/// <para><b>Pure inputs/outputs.</b> The mover's position, velocity and
/// combined radius, the nearby obstacles (position, radius, velocity), and the
/// <em>navigation goal</em> to ignore (replicated per-obstacle as
/// <see cref="Obstacle.Ignore"/> — native's <c>pmodelSkip</c>,
/// <c>common.cpp:2003</c>). The output is the dodge <em>goal position</em>
/// (<c>mover.Position + away·DodgeGoalDistance</c>) or none. Radius semantics
/// mirror native exactly: <c>r = obstacle.Radius + mover.RadiusPlusOne</c>
/// (native <c>pmodel-&gt;GetRadius() + myRadius</c>, where the mover's own
/// <c>myRadius = GetRadius() + 1.0f</c> — <c>common.cpp:1705-1706</c>), and the
/// imminence gate is native's default <c>t² ≤ 4·r/acceleration</c>
/// (<c>common.cpp:1749-1751</c>).</para>
/// </summary>
public static class ShipDodgeLaw
{
    /// <summary>The mover (the ship doing the dodging) and its acceleration for
    /// the imminence gate. <see cref="Mover.RadiusPlusOne"/> is the ship's own
    /// <c>GetRadius()+1</c> (<c>0.5·HullType.Length + 1</c> in this port);
    /// <see cref="Mover.Acceleration"/> is <c>Thrust/Mass</c>.</summary>
    public readonly record struct Mover(Vector3 Position, Vector3 Velocity, float RadiusPlusOne, float Acceleration);

    /// <summary>A single candidate the mover may have to avoid. Radius is the
    /// obstacle's OWN radius (combined radius is computed inside the law as
    /// <c>Radius + mover.RadiusPlusOne</c>, native <c>common.cpp:1731</c>).
    /// <see cref="Ignore"/> replicates native's <c>Ignore()</c> /
    /// <c>pmodelSkip</c> (skipping self / the navigation goal / excluded
    /// categories) so the CALLER decides what counts as an obstacle, keeping
    /// the law pure and byte-identical across consumers.</summary>
    public readonly record struct Obstacle(Vector3 Position, Vector3 Velocity, float Radius, bool Ignore);

    /// <summary>
    /// Compute the dodge goal for <paramref name="mover"/> against
    /// <paramref name="obstacles"/> (evaluated in the given order — the winner
    /// is the FIRST obstacle with the strictly-smallest time-to-impact passing
    /// the imminence gate, exactly native's <c>t &lt; tCollide</c> scan at
    /// <c>common.cpp:1740-1751</c>), or <c>false</c> when nothing is imminent.
    /// On success <paramref name="goal"/> is <c>mover.Position + away ·
    /// dodgeGoalDistance</c>, where <c>away</c> is native's away vector
    /// (<c>common.cpp:1761-1783</c>): straight away from the centre when
    /// already inside, else the closest-approach offset, orthogonalised when
    /// aimed dead-centre. The goal MAGNITUDE (<c>dodgeGoalDistance</c>, the
    /// server uses 520) only sets how far the escape point is — the direction
    /// is what matters. <paramref name="fallbackTMax"/> is the imminence horizon
    /// used only when <see cref="Mover.Acceleration"/> is non-positive (native's
    /// acceleration-based gate would divide by zero; the server passes 3.0).
    /// </summary>
    public static bool TryComputeDodgeGoal(
        in Mover mover, IEnumerable<Obstacle> obstacles,
        float dodgeGoalDistance, float fallbackTMax, out Vector3 goal)
    {
        goal = default;
        float tCollide = float.MaxValue;
        bool have = false;
        Vector3 winPos = default;
        Vector3 winVel = default;

        foreach (var o in obstacles)
        {
            if (o.Ignore) continue;
            float r = o.Radius + mover.RadiusPlusOne;
            Vector3 rel = o.Position - mover.Position;
            float d2 = rel.LengthSquared();
            float t = d2 > r * r ? SolveForImpact(rel, o.Velocity - mover.Velocity, 0f, r, out _) : 0f;
            if (t < tCollide && IsImminent(t, r, mover.Acceleration, fallbackTMax))
            {
                tCollide = t;
                winPos = o.Position;
                winVel = o.Velocity;
                have = true;
            }
        }

        if (!have) return false;

        // Away vector: native's ca (common.cpp:1761-1783).
        Vector3 dp = mover.Position - winPos;
        Vector3 ca;
        if (tCollide == 0f)
            ca = dp;                                                // already inside → straight away from center (:1767-1768)
        else
        {
            Vector3 dv = mover.Velocity - winVel;
            float ldv2 = dv.LengthSquared();
            ca = ldv2 > 0.1f ? dp - dv * ((dp * dv) / ldv2) : dp;   // closest-approach offset (:1774-1776)
            if (ca.LengthSquared() < 0.1f)
                ca = OrthogonalVector(dp);                          // dead-centre aim → orthogonal (:1777-1782)
        }

        float caLen = ca.Length();
        if (caLen < 1e-3f) return false;                            // defensive: degenerate away vector
        goal = mover.Position + ca * (dodgeGoalDistance / caLen);
        return true;
    }

    /// <summary>
    /// The imminence gate for <see cref="TryComputeDodgeGoal"/>: native's
    /// default (<c>tMax &lt; 0</c>) threshold <c>t² ≤ 4·r/acceleration</c>
    /// (<c>common.cpp:1749-1751</c>), the form used at the idle/generic Dodge
    /// call sites this universal check mirrors (<c>common.cpp:1920,1932</c>;
    /// <c>shipIGC.cpp:2334</c>). <paramref name="r"/> is the combined radius
    /// (obstacle radius + ship radius), <paramref name="acceleration"/> the
    /// ship's <c>Thrust/Mass</c>. Falls back to <paramref name="fallbackTMax"/>
    /// only for a degenerate non-positive acceleration (never divides by zero).
    /// </summary>
    public static bool IsImminent(float t, float r, float acceleration, float fallbackTMax)
        => acceleration > 0f ? t * t <= 4f * r / acceleration : t <= fallbackTMax;

    /// <summary>
    /// Faithful port of the native quadratic solve for time-to-closest-
    /// approach, <c>solveForImpact</c> (<c>src/Igc/common.cpp:59-119</c>). Returns
    /// the time <c>t ≥ 0</c> at which the projectile/separation
    /// <c>deltaP + t·deltaV</c> first reaches distance <c>radius</c> from the
    /// target, or a huge value when no such future encounter exists (moving
    /// apart / already separating). <paramref name="direction"/> receives the
    /// aiming direction when a solution exists (native sets it only in the
    /// solved branches); <c>ShipDodgeLaw</c>'s own dodge scan discards it, but
    /// the utility is general — the same math native's <c>solveForLead</c> reuses
    /// for shot prediction (<c>common.cpp:121-149</c>).
    /// <para><b>Formula</b> (<c>common.cpp:82-108</c>):
    /// <c>c = deltaP² − radius²</c> (≤ 0 ⇒ already overlapping ⇒ <c>t = 0</c>);
    /// <c>a = deltaV² − speed²</c>; <c>b = 2(deltaV·deltaP − radius·speed)</c>;
    /// the smallest positive root of <c>a·t² + b·t + c = 0</c> is
    /// <c>t = (b + √(b²−4ac)) / (−2a)</c>. The no-solution branch
    /// (<c>common.cpp:87-92</c>) returns the same huge sentinel (the mmf
    /// <c>FLT_MAX / 500000000</c> divisor). Native's inner <c>b24ac&lt;0 → 0.1f</c>
    /// clamp (<c>common.cpp:97-100</c>) is unreachable — the negative-discriminant
    /// case is already diverted by the outer guard — and is reproduced as dead
    /// code for fidelity.</para>
    /// <para><b>Tiny C# deviation.</b> C# <c>out</c> requires <c>direction</c> be
    /// assigned on every path, whereas native leaves <c>*direction</c> unset in
    /// the no-solution branch; here it is initialized to <see cref="Vector3.Zero"/>
    /// and simply not overwritten there — no behaviour difference for any caller.</para>
    /// </summary>
    public static float SolveForImpact(Vector3 deltaP, Vector3 deltaV, float speed, float radius, out Vector3 direction)
    {
        direction = default;
        float c = deltaP * deltaP - radius * radius;
        if (c <= 0.0f)                              // already overlapping → t=0 (common.cpp:68-73)
        {
            direction = deltaP.Normalize();
            return 0.0f;
        }

        float a = deltaV * deltaV - speed * speed;                 // :82
        float b = 2.0f * (deltaV * deltaP - radius * speed);       // :83
        float b24ac = b * b - 4.0f * a * c;                        // :85

        if (a == 0.0f || (a > 0.0f && b > 0.0f) || b24ac < 0.0f)   // no valid solution (:87-92)
            return float.MaxValue / 500000000.0f;                  // mmf podpickup divisor

        // b24ac >= 0 here (the negative case is diverted above; native's :97-100
        // clamp is unreachable). Smallest positive root (common.cpp:103).
        float t = (b + MathF.Sqrt(b24ac)) / (-2.0f * a);
        if (t > 0.0f)
        {
            direction = (deltaP + t * deltaV) / (t * speed + radius);   // :108
            return t;
        }

        direction = deltaP.Normalize();                // round-off guard (common.cpp:110-114)
        return 0.0f;
    }

    /// <summary>
    /// Port of <c>Vector::GetOrthogonalVector</c>
    /// (<c>src/zlib/vector.cpp:6-35</c>): a vector perpendicular to <paramref name="v"/>,
    /// picking the largest-magnitude component axis as the pivot exactly as
    /// native does (so it never divides by the zero axis). Used only as the
    /// dead-centre fallback for the dodge away-vector (<c>common.cpp:1777-1782</c>).
    /// </summary>
    public static Vector3 OrthogonalVector(Vector3 v)
    {
        float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
        if (ax > ay)
        {
            if (ax > az) return new Vector3(-v.Z - v.Y * v.Z / v.X, v.Z, v.X);   // x biggest (vector.cpp:18)
        }
        else
        {
            if (ay > az) return new Vector3(v.Y, -v.X - v.Z * v.X / v.Y, v.X);   // y biggest (vector.cpp:26)
        }
        return new Vector3(v.Y, v.Z, -v.Y - v.X * v.Y / v.Z);                    // z biggest (vector.cpp:34)
    }
}

using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// P804 — the shared, pure port of native's <c>solveForLead</c>
/// (<c>src/Igc/common.cpp:121-149</c>), the ballistic lead solve that turns a
/// shooter, a target and a weapon's projectile into "where must I point to
/// intercept". Native keeps it in shared IGC code and calls it from BOTH the
/// server-side AI (<c>Igc/shipIGC.cpp:2464,2617</c>, the port's
/// <see cref="Ship.SolveForLead"/>) and the client's HUD
/// (<c>WinTrek/indicator.cpp:221</c>, the port's client lead solve).
///
/// <para><b>Formula</b> (<c>common.cpp:132-148</c>):
/// <c>myPosition = shooter-&gt;GetPosition() + weapon-&gt;GetPosition() *
/// myOrientation</c> (the hardpoint muzzle in world space);
/// <c>hisVelocity = target-&gt;GetVelocity() * skill</c> (the AI accuracy scalar;
/// the client and a player-pilot AI both pass 1.0);
/// <c>speed = pt-&gt;GetSpeed() * (ammoPerShot ? c_gaSpeedAmmo : 1)</c> and
/// <c>deltaV = AbsoluteF ? hisVelocity : hisVelocity - myVelocity</c>;
/// the intercept is <c>solveForImpact(hisPosition - myPosition, deltaV, speed,
/// target-&gt;GetRadius(), direction)</c> (<c>common.cpp:59-119</c>, the port's
/// <see cref="ShipDodgeLaw.SolveForImpact"/>). The returned time is native's
/// <c>t</c>; <paramref name="direction"/> receives the unit aim direction, the
/// exact vector native projects with <c>TransformDirectionToImage</c>
/// (<c>indicator.cpp:283</c>).</para>
///
/// <para><b>Why a shared helper.</b> P804's parity rule is that the HUD must
/// compute the lead on the client from the IGC state it holds, not read a
/// server stand-in. Extracting the arithmetic here (the P489
/// <see cref="ShipDodgeLaw"/> / P594 <see cref="NavMath"/> precedent) keeps the
/// server AI and the client HUD on ONE implementation, so they cannot drift.</para>
///
/// <para><b>Caller responsibilities (kept out of the pure solve).</b> The
/// <c>c_gaSpeedAmmo</c> factor is applied by the caller exactly as native does
/// with the shooter's side global attributes; <c>skill</c> is applied to the
/// target velocity by the caller; and the muzzle offset is passed already
/// rotated into world space (native <c>weapon-&gt;GetPosition() *
/// myOrientation</c>). A caller without hull geometry (the port's server
/// <see cref="Ship"/>) passes a zero offset — its documented P169 omission.</para>
/// </summary>
public static class LeadSolver
{
    /// <summary>Native's no-solution sentinel: <c>FLT_MAX /
    /// 500000000</c> (<c>common.cpp:86</c>, <c>mmf</c> podpickup divisor). Far
    /// above any real intercept time or weapon lifespan, so it reads as
    /// "never" exactly like native.</summary>
    public const float NoSolutionTime = float.MaxValue / 500000000.0f;

    /// <summary>The pure inputs of <c>solveForLead</c> with the caller-side
    /// scaling already applied (see the type doc).</summary>
    public readonly record struct Input(
        Vector3 ShooterPosition,
        Vector3 ShooterVelocity,
        Vector3 TargetPosition,
        Vector3 TargetVelocity,
        Vector3 MuzzleOffsetWorld,
        float ProjectileSpeed,
        bool Absolute,
        float TargetRadius);

    /// <summary>The solved lead: intercept time (or
    /// <see cref="NoSolutionTime"/>) and the unit aim direction (native leaves
    /// it unset on the no-solution branch; this port's
    /// <see cref="ShipDodgeLaw.SolveForImpact"/> returns zero).</summary>
    public readonly record struct Solution(float Time, Vector3 Direction)
    {
        /// <summary>Whether the solve produced a real intercept (no sentinel /
        /// no zero direction).</summary>
        public bool Solved => Time < NoSolutionTime && Direction.LengthSquared() > 1e-9f;
    }

    /// <summary>
    /// The faithful <c>solveForLead</c> body. Returns the intercept time
    /// <c>t ≥ 0</c> and fills <paramref name="direction"/> with the unit aim
    /// vector; <paramref name="muzzleOffsetWorld"/> is the weapon hardpoint
    /// offset already rotated into world space (zero when no hull geometry).
    /// </summary>
    public static float SolveForLead(in Input input, out Vector3 direction)
    {
        // common.cpp:134-136 — shooter position plus the muzzle hardpoint.
        Vector3 myPosition = input.ShooterPosition + input.MuzzleOffsetWorld;
        // common.cpp:144-146 — an absolute projectile ignores the shooter's
        // own motion in the relative-velocity term.
        Vector3 deltaV = input.Absolute
            ? input.TargetVelocity
            : input.TargetVelocity - input.ShooterVelocity;
        // common.cpp:143-148 — solveForImpact(deltaP, deltaV, speed, radius).
        return ShipDodgeLaw.SolveForImpact(
            input.TargetPosition - myPosition, deltaV,
            input.ProjectileSpeed, input.TargetRadius, out direction);
    }

    /// <summary>Allocation-free convenience wrapper over the
    /// <c>out</c> primitive.</summary>
    public static Solution Solve(in Input input)
    {
        float t = SolveForLead(input, out Vector3 direction);
        return new Solution(t, direction);
    }
}

using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// The player autopilot's per-tick steering law — a pure, standalone port of
/// <see cref="Allegiance.Core.Sim.Ship.SteerToward"/> (P155/P162,
/// <c>src/Allegiance.Core/Sim/Ship.cs:3720</c>), the exact algorithm the
/// port's own bot/NPC AI uses to "face and thrust" a target. The player
/// autopilot is a <b>pure client-side</b> feature: native's
/// <c>CshipIGC::SetAutopilot</c> (<c>src/Igc/shipIGC.cpp:3974</c>) is a plain
/// field write on the client's own local copy of the player ship, and
/// <c>BaseClient::SetAutoPilot</c> (<c>src/clintlib/clintlib.cpp:1592</c>) —
/// what <c>trekClient.SetAutoPilot(true)</c> really calls — just flips that
/// flag so the client runs the identical <c>PlotShipMove</c>/<c>GotoPlan</c>
/// steering locally, producing the same yaw/pitch/throttle a human joystick
/// would and feeding it out over the ordinary controls wire message. This
/// class reproduces that local steering (in the simplified two-zone form this
/// port already chose for <c>SteerToward</c>) so <see cref="Game1"/> can drive
/// the ship with nothing but the same <c>ControlData</c> it already sends.
/// </summary>
/// <remarks>
/// <para><b>Deliberately free of MonoGame types.</b> It operates on the repo's
/// pure sim types (<see cref="Vector3"/>, <see cref="Orientation"/>,
/// <see cref="ControlData"/> — <c>Allegiance.Core</c>) so it is unit-testable
/// without a graphics device, same discipline as <see cref="FlightInput"/>.</para>
/// <para><b>Semantics match <c>SteerToward</c> exactly.</b> The "face" turn
/// uses the ship-local cross-product signal <c>forward × directionToTarget</c>
/// — continuous through the target-astern case (no atan2 discontinuity), with
/// yaw/pitch signs matched to how <c>ExecuteMove</c> applies the turn axes.
/// The "thrust" is a two-zone distance ramp scaled by nose alignment
/// (<c>forward·dir</c>), so misaligned the ship cuts power and turns tightly
/// ("turn then burn") instead of orbiting a target it cannot track at speed.
/// Roll is never commanded (<c>Roll = 0f</c>), matching <c>SteerToward</c>'s
/// own <c>Roll = 0f</c>. The dead-centre guard holds <c>Throttle = -1</c>
/// without dividing by zero, matching <c>SteerToward</c>'s
/// <c>distance &lt;= 1e-3f</c> branch.</para>
/// </remarks>
public static class AutopilotSteering
{
    /// <summary>Default "close enough to stop" radius — <c>Ship.AutopilotArrivalRadius</c>
    /// (<c>Ship.cs:3064</c>).</summary>
    public const float AutopilotArrivalRadius = 60f;

    /// <summary>
    /// P540b — steer the ship's nose onto a target and hold FULL forward throttle
    /// with no braking: a pass-through pursuit, for a ROUTED cross-sector leg whose
    /// goal is the mouth of the next warp toward the target sector. A warp is a
    /// transit point, not a destination — braking to a stop there (as
    /// <see cref="SteerToward"/> would) makes a small hull creep into the aleph,
    /// exit slowly, and be unable to escape the SAME warp's collision radius
    /// before its <c>WarpCooldownSeconds</c> debounce expires, so it re-crosses
    /// and oscillates between the two sectors forever (observed live). Flying at
    /// full throttle through the mouth transits the ship at cruise speed, so it
    /// emerges on the far side with enough speed to clear the next aleph and carry
    /// on. Same nose-follow turn as <see cref="SteerToward"/>; only the throttle
    /// differs (pinned to +1, no distance/decel ramp).
    /// </summary>
    public static ControlData Pursue(in Vector3 position, Orientation orientation, in Vector3 targetPosition)
    {
        Vector3 toTarget = targetPosition - position;
        float distance = toTarget.Length();
        if (distance <= 1e-3f)
            return new ControlData(0f, 0f, 0f, +1f);   // already centred: keep pushing through

        Vector3 dir = toTarget / distance;
        Vector3 body = orientation.TimesInverse(Vector3.CrossProduct(orientation.GetForward(), dir));
        float yaw = Math.Clamp(AutopilotTurnGain * body.Y, -1f, 1f);
        float pitch = Math.Clamp(AutopilotTurnGain * -body.X, -1f, 1f);
        return new ControlData(yaw, pitch, 0f, +1f);   // full forward — fly through the aleph
    }

    /// <summary>Default distance at which the two-zone braking ramp begins —
    /// <c>Ship.AutopilotBrakeStartDistance</c> (<c>Ship.cs:3074</c>).</summary>
    public const float AutopilotBrakeStartDistance = 420f;

    /// <summary>Default turn signal → axis gain — <c>Ship.AutopilotTurnGain</c>
    /// (<c>Ship.cs:3104</c>).</summary>
    public const float AutopilotTurnGain = 2.0f;

    /// <summary>Maximum braking deceleration (units/s²) used to size the
    /// velocity-aware brake ramp (P343). The port's <c>ExecuteMove</c>
    /// throttle-cruise law sheds at most <c>Thrust / Mass</c> per second
    /// (<c>Ship.cs:3409,3475-3493</c> — the per-tick Δv is bounded by
    /// <c>dT·Thrust/Mass</c>), so that is the grounded stopping capability the
    /// braking-distance term assumes. Drag only adds to deceleration, so using
    /// <c>Thrust/Mass</c> (drag-free) is a conservative underestimate that
    /// brakes slightly early — never dangerously late. The client passes the
    /// own ship's real <c>HullType.Thrust / HullType.Mass</c> when the hull is
    /// resolvable; this is the fallback when it is not (empty/missing shop
    /// catalogue).</summary>
    public const float AutopilotBrakeDeceleration = 55f;

    /// <summary>
    /// Compute the control inputs that steer the ship's nose onto
    /// <paramref name="targetPosition"/> and throttle accordingly — a
    /// standalone pure function on plain inputs (the same "face and thrust"
    /// law as <see cref="Allegiance.Core.Sim.Ship.SteerToward"/>,
    /// <c>Ship.cs:3720</c>, plus P343's velocity-aware braking). Callers (e.g.
    /// <see cref="Game1"/>) feed the current own-ship
    /// <see cref="Orientation"/>/position/velocity and the target's (possibly
    /// moving) position each frame; the result is a <see cref="ControlData"/>
    /// to route through the ordinary controls wire.
    /// </summary>
    /// <remarks>
    /// <para><b>Face.</b> Unchanged from the original law — forward ×
    /// directionToTarget expressed in ship-local space, signed to match
    /// <c>ExecuteMove</c>'s turn application.</para>
    /// <para><b>Thrust (P343).</b> The two-zone distance ramp is kept, but the
    /// brake ramp's onset is now velocity-aware. The ship's real velocity is
    /// projected onto the line of sight to get a true <em>closing speed</em>,
    /// and the ramp begins no later than one <em>stopping distance</em> out —
    /// <c>closing² / brakeDeceleration</c>, the distance it would take to shed
    /// that closing speed at the port's real max deceleration
    /// (<c>Thrust/Mass</c>, <c>ExecuteMove</c>) — plus the arrival radius. The
    /// effective brake start is therefore
    /// <c>max(brakeStartDistance, closing²/decel + arrivalRadius)</c>: at
    /// range the ship keeps full forward throttle (the naive distance ramp),
    /// but a fast approach widens the ramp enough that the ship is always
    /// within its own stopping capability, eliminating the momentum overshoot
    /// that made the purely distance-based ramp oscillate. A slow/rest approach
    /// has a negligible stopping distance, so the effective brake start stays
    /// at <c>brakeStartDistance</c> — the original, known-good slow-approach
    /// behaviour is preserved exactly.</para>
    /// </remarks>
    /// <param name="position">The ship's own current world position.</param>
    /// <param name="orientation">The ship's own current attitude.</param>
    /// <param name="targetPosition">The target's current world position
    /// (re-read every frame — the target may be moving).</param>
    /// <param name="velocity">The ship's own current world velocity
    /// (usually <c>ServerShipUpdate.Velocity</c>) — used to project a real
    /// closing speed so braking can begin within stopping distance.</param>
    /// <param name="arrivalRadius">"Close enough to stop" distance; defaults to
    /// <see cref="AutopilotArrivalRadius"/>.</param>
    /// <param name="brakeStartDistance">Where the braking ramp begins; defaults
    /// to <see cref="AutopilotBrakeStartDistance"/>. Must exceed
    /// <paramref name="arrivalRadius"/> for the ramp to be smooth (guarded
    /// against an invalid ratio below).</param>
    /// <param name="turnGain">Turn-signal → yaw/pitch gain; defaults to
    /// <see cref="AutopilotTurnGain"/>.</param>
    /// <param name="brakeDeceleration">Assumed max braking deceleration
    /// (units/s²) sizing the stopping-distance term; defaults to
    /// <see cref="AutopilotBrakeDeceleration"/>.</param>
    public static ControlData SteerToward(
        in Vector3 position,
        Orientation orientation,
        in Vector3 targetPosition,
        in Vector3 velocity,
        float arrivalRadius = AutopilotArrivalRadius,
        float brakeStartDistance = AutopilotBrakeStartDistance,
        float turnGain = AutopilotTurnGain,
        float brakeDeceleration = AutopilotBrakeDeceleration)
    {
        Vector3 toTarget = targetPosition - position;
        float distance = toTarget.Length();

        // Dead centre: hold the kill-throttle and do not divide by zero —
        // mirrors SteerToward's `distance <= 1e-3f` early return (Ship.cs:3724).
        if (distance <= 1e-3f)
            return ControlData.Neutral;   // (0, 0, 0, -1)

        // Face: forward × directionToTarget, expressed in ship-local space
        // (TimesInverse). The up-component drives yaw, the right-component
        // drives pitch; signs match ExecuteMove's Yaw(+rate·dT)/Pitch(−rate·dT).
        Vector3 dir = toTarget / distance;
        Vector3 body = orientation.TimesInverse(Vector3.CrossProduct(orientation.GetForward(), dir));
        float yaw = Math.Clamp(turnGain * body.Y, -1f, 1f);
        float pitch = Math.Clamp(turnGain * -body.X, -1f, 1f);

        // P343 — velocity-aware brake start. Real closing speed toward the
        // target (project the ship's velocity on the line of sight; only a
        // positive closing speed matters — receding ships don't extend the
        // ramp). Its stopping distance closing²/decel is added to the arrival
        // radius and the fixed threshold, so the ramp never begins inside the
        // distance the ship actually needs to shed its current closing speed —
        // the momentum-overshoot root cause of the old purely-distance ramp.
        float brake = brakeStartDistance > arrivalRadius ? brakeStartDistance : 0f;
        if (brake > 0f)
        {
            float closing = Math.Max(0f,
                velocity.X * dir.X + velocity.Y * dir.Y + velocity.Z * dir.Z);
            float stopDist = brakeDeceleration > 0f
                ? (closing * closing) / brakeDeceleration
                : 0f;
            brake = Math.Max(brakeStartDistance, stopDist + arrivalRadius);
        }

        // Thrust: two-zone distance ramp scaled by nose alignment
        // (forward·dir = −local.Z). The ratio guard keeps a caller-supplied
        // brakeStartDistance ≤ arrivalRadius from dividing by zero.
        float distFactor;
        if (brakeStartDistance > arrivalRadius)
            distFactor = distance >= brake
                ? 1f
                : Math.Clamp((distance - arrivalRadius) / (brake - arrivalRadius), 0f, 1f);
        else
            distFactor = distance >= brakeStartDistance ? 1f : 0f;
        float alignment = Math.Clamp(-orientation.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * alignment - 1f, -1f, 1f);

        return new ControlData(yaw, pitch, 0f, throttle);   // roll stays 0 — autopilot never rolls
    }
}

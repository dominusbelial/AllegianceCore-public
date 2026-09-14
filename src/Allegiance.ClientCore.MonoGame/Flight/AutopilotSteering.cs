using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Flight;

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
/// without a graphics device, same discipline as <see cref="FlightInput"/> /
/// <see cref="RemoteShipPredictor"/>.</para>
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

    /// <summary>Default distance at which the two-zone braking ramp begins —
    /// <c>Ship.AutopilotBrakeStartDistance</c> (<c>Ship.cs:3074</c>).</summary>
    public const float AutopilotBrakeStartDistance = 420f;

    /// <summary>Default turn signal → axis gain — <c>Ship.AutopilotTurnGain</c>
    /// (<c>Ship.cs:3104</c>).</summary>
    public const float AutopilotTurnGain = 2.0f;

    /// <summary>
    /// Compute the control inputs that steer the ship's nose onto
    /// <paramref name="targetPosition"/> and throttle accordingly — the exact
    /// body of <see cref="Allegiance.Core.Sim.Ship.SteerToward"/>
    /// (<c>Ship.cs:3720</c>), as a standalone pure function on plain inputs.
    /// Callers (e.g. <see cref="Game1"/>) feed the current own-ship
    /// <see cref="Orientation"/>/position and the target's (possibly moving)
    /// position each frame; the result is a <see cref="ControlData"/> to route
    /// through the ordinary controls wire.
    /// </summary>
    /// <param name="position">The ship's own current world position.</param>
    /// <param name="orientation">The ship's own current attitude.</param>
    /// <param name="targetPosition">The target's current world position
    /// (re-read every frame — the target may be moving).</param>
    /// <param name="arrivalRadius">"Close enough to stop" distance; defaults to
    /// <see cref="AutopilotArrivalRadius"/>.</param>
    /// <param name="brakeStartDistance">Where the braking ramp begins; defaults
    /// to <see cref="AutopilotBrakeStartDistance"/>. Must exceed
    /// <paramref name="arrivalRadius"/> for the ramp to be smooth (guarded
    /// against an invalid ratio below).</param>
    /// <param name="turnGain">Turn-signal → yaw/pitch gain; defaults to
    /// <see cref="AutopilotTurnGain"/>.</param>
    public static ControlData SteerToward(
        in Vector3 position,
        Orientation orientation,
        in Vector3 targetPosition,
        float arrivalRadius = AutopilotArrivalRadius,
        float brakeStartDistance = AutopilotBrakeStartDistance,
        float turnGain = AutopilotTurnGain)
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

        // Thrust: two-zone distance ramp scaled by nose alignment
        // (forward·dir = −local.Z). The ratio guard keeps a caller-supplied
        // brakeStartDistance ≤ arrivalRadius from dividing by zero.
        float distFactor;
        if (brakeStartDistance > arrivalRadius)
            distFactor = distance >= brakeStartDistance
                ? 1f
                : Math.Clamp((distance - arrivalRadius) / (brakeStartDistance - arrivalRadius), 0f, 1f);
        else
            distFactor = distance >= brakeStartDistance ? 1f : 0f;
        float alignment = Math.Clamp(-orientation.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * alignment - 1f, -1f, 1f);

        return new ControlData(yaw, pitch, 0f, throttle);   // roll stays 0 — autopilot never rolls
    }
}

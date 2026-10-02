using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P464 — the pure, client-side extraction of the server's turret-gunner
/// facing integration. A boarded turret gunner must see where they aim and
/// drive the server's turret weapon along that facing, so the client keeps a
/// locally-integrated turret orientation that it both renders (turret view +
/// reticle) and reports to the server each control frame
/// (<c>ACTIVE_TURRET_UPDATE</c>). The server is authoritative on the final
/// gunner orientation (its <c>HandleActiveTurretUpdate</c> snapshot-catches it
/// up via <see cref="Allegiance.Core.Sim.Ship.ExecuteTurretMove"/>,
/// <c>shipIGC.cpp:3349-3392</c>), so this local integrator MUST reproduce that
/// exact math — same turn-rate/arc limits — or the client's aim prediction and
/// the server's applied facing would diverge every frame.
/// </summary>
/// <remarks>
/// This is a deliberate line-for-line extraction of
/// <see cref="Allegiance.Core.Sim.Ship.ExecuteTurretMove"/>
/// (<c>CshipIGC::ExecuteTurretMove</c>, <c>shipIGC.cpp:1618-1702</c>): same
/// spherical constraint on the controls, same throttle-scaled slew velocity
/// between <c>c_minRate</c>/<c>c_maxRate</c>, same yaw/pitch/roll application
/// and re-orthonormalization. The <see cref="ShipTests.MatchesServerExecuteTurretMove"/>-
/// style unit test proves the two agree numerically over a grid of
/// controls/deltas. The port deliberately extracts it rather than instantiating
/// a heavyweight <see cref="Ship"/> just to run one method, but the math is
/// identical (only the floating-point host differs).
/// </remarks>
public static class TurretLook
{
    /// <summary>Minimum turret slew rate, rad/s (<c>c_minRate</c>,
    /// <c>shipIGC.cpp:1640</c>).</summary>
    public const float MinSlewRate = 7.5f * MathF.PI / 180.0f;

    /// <summary>Maximum turret slew rate, rad/s (<c>c_maxRate</c>,
    /// <c>shipIGC.cpp:1641</c>).</summary>
    public const float MaxSlewRate = 75.0f * MathF.PI / 180.0f;

    /// <summary>
    /// Integrate <paramref name="current"/> forward by <paramref name="dt"/>
    /// under the commanded <paramref name="controls"/>, in place, exactly as the
    /// server's <see cref="Allegiance.Core.Sim.Ship.ExecuteTurretMove"/> does for
    /// the gunner's own ship. Returns the same (mutated) instance for fluency.
    /// The gunner never translates, only turns.
    /// </summary>
    /// <param name="current">The gunner's current facing (world frame) —
    /// mutated in place, like the native <c>&amp;pOrientation</c>.</param>
    /// <param name="controls">The commanded yaw/pitch/roll/throttle axes.</param>
    /// <param name="dt">Integration step, seconds.</param>
    public static Orientation Integrate(Orientation current, ControlData controls, float dt)
    {
        // Constrain desired yaw/pitch/roll to a circle, not a box
        // (shipIGC.cpp:1627-1634).
        float l = controls.Yaw * controls.Yaw
                + controls.Pitch * controls.Pitch
                + controls.Roll * controls.Roll;
        l = l > 1f ? 1f / MathF.Sqrt(l) : 1f;

        // Slew velocity scales with throttle (shipIGC.cpp:1640-1643).
        float slewVelocity = (MinSlewRate + 0.5f * (MaxSlewRate - MinSlewRate))
                           + (0.5f * (MaxSlewRate - MinSlewRate)) * controls.Throttle;

        float yawRate = controls.TurnAxis(ControlData.AxisYaw) * l * slewVelocity;
        float pitchRate = controls.TurnAxis(ControlData.AxisPitch) * l * slewVelocity;
        float rollRate = controls.TurnAxis(ControlData.AxisRoll) * l * slewVelocity;

        current.Yaw(yawRate * dt);
        current.Pitch(-pitchRate * dt);
        current.Roll(rollRate * dt);
        current.Renormalize();
        return current;
    }
}

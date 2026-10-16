using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// A <b>pure</b> ship-kinematics integrator extracted from
/// <see cref="Ship.ExecuteMove"/> (the port of <c>CshipIGC::ExecuteShipMove</c>,
/// <c>shipIGC.cpp:2852-3054</c>). It owns no hull, no core, no side — it
/// integrates a single step from an explicit set of <em>already-effective</em>
/// hull statistics plus the ship's current controls and mutable kinematic
/// state. The server's <see cref="Ship.ExecuteMove"/> delegates to it
/// byte-identically; the client's <see cref="Allegiance.ClientCore.Flight.ShipDeadReckoner"/>
/// (P448a) reuses it to replay the real flight model forward from a received
/// pose, so remote ships dead-reckon with the <b>same physics the server will
/// simulate</b> instead of being lerped between stale poses.
/// </summary>
/// <remarks>
/// <para><b>Why pure:</b> <c>ExecuteShipMove</c> reads only the hull's stats,
/// the afterburner's max thrust + current power, the ship's controls/stateM and
/// its mutable velocity/orientation/turn-rates — nothing else (the GA scaling
/// in the port's <c>Effective*</c> methods is applied <em>before</em> calling
/// in the pure form, so the caller hands over already-effective numbers; a
/// side-less client ship resolves identity GAs exactly as the server does for a
/// sideless ship). Extracting this means the exact same math serves the server
/// tick, the dead-reckoner, and (P448b) rendering — with one source of truth
/// for the flight model.</para>
/// <para><b>Mutability contract:</b> <paramref name="velocity"/> is passed by
/// <c>ref</c> and is both read and written (the C++ takes it by pointer);
/// <paramref name="orientation"/> is a reference-typed <see cref="Orientation"/>
/// and is mutated in place (<c>Yaw/Pitch/Roll</c> + <c>Renormalize</c>);
/// <paramref name="turnRates"/> is a 3-element array mutated in place (the C++
/// member <c>m_turnRates[]</c>). Nothing is allocated by this method.</para>
/// </remarks>
public static class ShipKinematics
{
    /// <summary>
    /// The already-effective kinematic inputs one <see cref="Step"/> consumes.
    /// Built by <see cref="Ship.ExecuteMove"/> from the hull + side GA, and by
    /// the client dead-reckoner from the received pose + resolved hull record.
    /// <c>AfterburnerMaxThrust</c> of 0 denotes "no afterburner fitted" —
    /// behaviourally identical to native's absent-afterburner branch (both the
    /// <c>thrustRatio</c> and the power drag term are zeroed), so absence need
    /// not be a separate flag.
    /// </summary>
    public readonly record struct Inputs(
        ControlData Controls,
        int StateM,
        float Thrust,             // effective engine thrust, N (hull.Thrust × c_gaThrust)
        float MaxSpeed,           // effective cruise speed, u/s (hull.Speed × c_gaMaxSpeed)
        float Mass,               // hull mass, kg (== hull.Mass; Ship.cs:174)
        float SideMultiplier,     // hull side-thrust multiplier
        float BackMultiplier,     // hull backward-thrust multiplier
        float MaxTurnRateYaw, float MaxTurnRatePitch, float MaxTurnRateRoll,
        float TurnTorqueYaw, float TurnTorquePitch, float TurnTorqueRoll,
        float AfterburnerMaxThrust,   // 0 => no afterburner
        float AfterburnerPower,       // current afterburner power 0..1
        bool Ripcording);             // native m_pmodelRipcord != null

    /// <summary>Effective max turn rate about an axis (yaw=0, pitch=1, roll=2).</summary>
    public static float MaxTurnRate(in Inputs i, int axis) => axis switch
    {
        ControlData.AxisYaw => i.MaxTurnRateYaw,
        ControlData.AxisPitch => i.MaxTurnRatePitch,
        _ => i.MaxTurnRateRoll,
    };

    /// <summary>Effective turn torque about an axis (yaw=0, pitch=1, roll=2).</summary>
    public static float TurnTorque(in Inputs i, int axis) => axis switch
    {
        ControlData.AxisYaw => i.TurnTorqueYaw,
        ControlData.AxisPitch => i.TurnTorquePitch,
        _ => i.TurnTorqueRoll,
    };

    /// <summary>
    /// Advance the kinematic state one step of <paramref name="dT"/> seconds
    /// given the controls — exactly the body of <c>ExecuteShipMove</c>
    /// (<c>shipIGC.cpp:2858-3049</c>). Integrates turn rates toward the
    /// commanded values (torque-limited), yaws/pitches/rolls the orientation,
    /// computes drag + the engine vector (manual / coast / throttle-cruise
    /// branches, afterburner boost), clips to available thrust and folds it
    /// into the velocity. <b>Does not move <see cref="Vector3"/> position</b> —
    /// the C++ does not either; integrating <c>Δposition = velocity·ΔT</c> is
    /// the mission/chunk slice (<see cref="Mission.Update"/>) or the dead
    /// reckoner's chunk loop.
    /// </summary>
    public static void Step(in Inputs i, ref Vector3 velocity, Orientation orientation, float[] turnRates, float dT)
    {
        float thrust = i.Thrust;
        float thrust2 = thrust * thrust;
        float thrustToVelocity = dT / i.Mass;        // Newtons → Δv; shipIGC.cpp:2868

        // ── manoeuvre: ramp turn rates toward the desired, clipped by torque ─
        // (shipIGC.cpp:2885-2910). Suppressed while ripcording — a ripcord
        // countdown pins turn rates in place (no maneuvering), exactly as the
        // C++ guards the ramp with `if (!m_pmodelRipcord)`.
        if (!i.Ripcording)
        {
            float l = i.Controls.Yaw * i.Controls.Yaw
                    + i.Controls.Pitch * i.Controls.Pitch
                    + i.Controls.Roll * i.Controls.Roll;
            l = l > 1f ? 1f / MathF.Sqrt(l) : 1f;    // constrain desired rate to a sphere, not a box

            float tm = TorqueMultiplier(velocity, i.MaxSpeed) * thrustToVelocity;
            for (int k = 0; k < 3; k++)
            {
                float desiredRate = i.Controls.TurnAxis(k) * l * MaxTurnRate(i, k);   // shipIGC.cpp:2900
                float maxDelta = tm * TurnTorque(i, k);                               // shipIGC.cpp:2901
                if (desiredRate < turnRates[k] - maxDelta)
                    turnRates[k] -= maxDelta;
                else if (desiredRate > turnRates[k] + maxDelta)
                    turnRates[k] += maxDelta;
                else
                    turnRates[k] = desiredRate;
            }
        }

        orientation.Yaw(turnRates[ControlData.AxisYaw] * dT);
        orientation.Pitch(-turnRates[ControlData.AxisPitch] * dT);
        orientation.Roll(turnRates[ControlData.AxisRoll] * dT);
        orientation.Renormalize();

        var backward = orientation.GetBackward();
        float speed = velocity.Length();
        float maxSpeed = i.MaxSpeed;

        // Drag = thrust required to produce the velocity change caused by drag.
        // Computed with double intermediates exactly as the C++ casts
        // (shipIGC.cpp:2930-2934).
        double f = Math.Exp((-thrust) * (double)thrustToVelocity / maxSpeed);
        var drag = velocity * (float)((1.0 - f) / thrustToVelocity);

        // P412 — afterburner thrust boost (native shipIGC.cpp:2939-2959). The
        // mounted afterburner's effective max thrust (including c_gaThrust)
        // sets thrustRatio when the
        // button is held, and an active power ramp adds a backward thrust term
        // into drag so it factors into the engine thrust (shipIGC.cpp:2952-2957).
        bool afterF = (i.StateM & ShipControlState.AfterburnerButton) != 0;
        float thrustRatio = 0.0f;
        float abThrust = i.AfterburnerMaxThrust;      // 0 => absent (no-op below, same as native's null branch)
        if (afterF)
            thrustRatio = abThrust / thrust;
        float power = i.AfterburnerPower;
        if (power != 0.0f)
            drag += (power * abThrust) * backward;    // shipIGC.cpp:2956

        // no maneuvering while ripcording (shipIGC.cpp:2961-2962): the whole
        // engine-thrust block is suppressed, so the engine vector stays Zero
        // and the velocity below changes by drag only.
        Vector3 engineVector = Vector3.Zero;
        if (!i.Ripcording)
        {
            Vector3 localThrust;
            if ((i.StateM & ShipControlState.MoveButtonsMask) != 0)
            {
                // Manual control: whichever direction buttons are pressed, at full thrust.
                int x = ((i.StateM & ShipControlState.LeftButton) != 0 ? -1 : 0)
                      + ((i.StateM & ShipControlState.RightButton) != 0 ? 1 : 0);
                int y = ((i.StateM & ShipControlState.DownButton) != 0 ? -1 : 0)
                      + ((i.StateM & ShipControlState.UpButton) != 0 ? 1 : 0);
                int z = ((i.StateM & ShipControlState.BackwardButton) != 0 ? 1 : 0)
                      + ((i.StateM & ShipControlState.ForwardButton) != 0 ? -1 : 0);
                localThrust = (x != 0 || y != 0 || z != 0)
                    ? new Vector3(thrust * x, thrust * y, thrust * z)
                    : Vector3.Zero;
            }
            else if ((i.StateM & ShipControlState.CoastButton) != 0 && !afterF)
            {
                // Vector lock: kill lateral velocity (bring drag into ship-local).
                localThrust = orientation.TimesInverse(drag);
            }
            else
            {
                // Throttle cruise: aim at a target speed and cancel drag.
                float negDesiredSpeed = afterF
                    ? maxSpeed * (-1.0f - thrustRatio)                     // real AB (shipIGC.cpp:2992-2993)
                    : (-0.5f * (1.0f + i.Controls.Throttle)) * ((speed > maxSpeed) ? speed : maxSpeed);
                var desiredVelocity = backward * negDesiredSpeed;
                localThrust = orientation.TimesInverse((desiredVelocity - velocity) / thrustToVelocity + drag);
            }

            // Clip the engine vector to the engine's available thrust.
            // (shipIGC.cpp:3008-3033)
            float sm = i.SideMultiplier;
            float backMult = i.BackMultiplier;
            var scaledThrust = new Vector3(
                localThrust.X / sm,
                localThrust.Y / sm,
                localThrust.Z <= 0f ? localThrust.Z : localThrust.Z / backMult);
            float r2 = scaledThrust.LengthSquared();

            if (r2 == 0f)
                engineVector = Vector3.Zero;
            else if (r2 <= thrust2)
                engineVector = localThrust * orientation;
            else
                engineVector = (localThrust * orientation) * (thrust / MathF.Sqrt(r2));
        }

        velocity += thrustToVelocity * (engineVector - drag);
    }

    /// <summary>
    /// <c>CshipIGC::GetTorqueMultiplier()</c> (<c>shipIGC.h:542-549</c>): 0.50
    /// at rest rising toward 2.50 as speed approaches/exceeds max, using
    /// <c>f = |v|/maxSpeed</c>. Read from the current velocity (at the moment
    /// the ramp runs) exactly as the reference reads <c>GetVelocity()</c>.
    /// </summary>
    private static float TorqueMultiplier(Vector3 velocity, float maxSpeed)
    {
        const float c = 0.50f;
        float fraction = velocity.Length() / maxSpeed;
        return c + (1.0f - c) * 2.0f * fraction / (fraction + 1.0f);
    }
}

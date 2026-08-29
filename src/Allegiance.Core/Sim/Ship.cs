using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// A standalone ship — the flight-model slice. Holds the kinematic state the
/// C++ <c>CshipIGC</c> exposes (position, velocity, orientation, controls,
/// turn rates, state bits) and implements <see cref="ExecuteMove"/>, the port
/// of <c>CshipIGC::ExecuteShipMove(Time, Time, Vector*, Orientation*)</c>
/// (<c>src/Igc/shipIGC.cpp:2852-3054</c>). Deliberately <b>not</b> wired into
/// <c>Sector</c>/<c>Mission</c> — integration is a separate slice.
/// </summary>
/// <remarks>
/// What is and is not modelled (faithfulness notes — every deviation is a
/// simplification that this slice deliberately does not attempt, documented
/// here so the port is honest):
/// <list type="bullet">
/// <item><b>No ripcord.</b> The C++ guards both the turn-rate ramp and the
/// engine-thrust block with <c>if (!m_pmodelRipcord)</c> (<c>shipIGC.cpp:2885,
/// 2962</c>); this ship has no ripcord path, so both blocks always run. Drag is
/// still computed (and subtracted) regardless, exactly as the C++ does even
/// when ripcording.</item>
/// <item><b>No afterburner part.</b> The C++ folds a mounted afterburner's
/// thrust into <c>thrustRatio</c> and drag (<c>shipIGC.cpp:2942-2959</c>).
/// This ship mounts nothing, so <c>thrustRatio == 0</c> and no afterburner
/// drag is added. The <c>afterburnerButtonIGC</c> bit is still honoured as a
/// gate (it changes which thrust branch runs even with no AB mounted), so the
/// full decision tree is reproduced.</item>
/// <item><b>No <c>engineVector</c> field.</b> It is computed as a local and
/// immediately folded into velocity, matching the observable behaviour.</item>
/// </list>
/// </remarks>
public sealed class Ship : Model
{
    private readonly DataHullTypeIGC _hull;
    private readonly float[] _turnRates = new float[3];

    /// <summary>Construct with a hull and initial kinematic state. Turn rates start at 0 (like the C++ constructor, <c>shipIGC.cpp:120-121</c>).</summary>
    public Ship(short shipId, DataHullTypeIGC hull, Vector3 position, Vector3 velocity, Orientation orientation)
        : base(shipId, position)
    {
        _hull = hull;
        Velocity = velocity;
        Orientation = orientation ?? throw new ArgumentNullException(nameof(orientation));
        Controls = ControlData.Neutral;
        StateM = 0;
    }

    /// <summary>The hull's <c>Mass</c> — the "wet" mass the movement math uses.</summary>
    public float Mass => _hull.Mass;

    public Vector3 Velocity { get; set; }
    public Orientation Orientation { get; }
    public ControlData Controls { get; set; }

    /// <inheritdoc/>
    public override Vector3 CollisionVelocity => Velocity;

    /// <inheritdoc/>
    public override Orientation CollisionOrientation => Orientation;

    /// <summary>State bitmask; the buttons live in <see cref="ShipControlState"/>.</summary>
    public int StateM { get; set; }

    /// <summary>The three current angular rates (yaw, pitch, roll), rad/s. Indexed by <see cref="ControlData"/> axis constants.</summary>
    public IReadOnlyList<float> TurnRates => _turnRates;

    /// <summary>Copies the turn rates into <paramref name="dest"/> and returns it (avoids exposing the backing array for mutation at a distance).</summary>
    public float[] GetTurnRates(float[] dest)
    {
        Array.Copy(_turnRates, dest, 3);
        return dest;
    }

    /// <summary>
    /// Advance the ship's kinematics from <paramref name="timeStart"/> to
    /// <paramref name="timeStop"/> given the current controls — the port of
    /// <c>CshipIGC::ExecuteShipMove(timeStart, timeStop, &amp;vel, &amp;ori)</c>
    /// (<c>shipIGC.cpp:2852-3054</c>). Mutates <see cref="Velocity"/> and
    /// <see cref="Orientation"/>. <b>Does not move <see cref="Position"/></b> —
    /// the C++ does not either; integrating <c>Δposition = velocity·ΔT</c> is
    /// the mission/cluster slice.
    /// </summary>
    /// <param name="timeStart">Start instant (seconds).</param>
    /// <param name="timeStop">End instant (seconds); must exceed <paramref name="timeStart"/> (the C++ <c>assert(dT &gt; 0)</c>).</param>
    public void ExecuteMove(double timeStart, double timeStop)
    {
        if (!(timeStop > timeStart))
            throw new ArgumentOutOfRangeException(nameof(timeStop), "ExecuteMove requires timeStop > timeStart");

        float dT = (float)(timeStop - timeStart);
        float thrust = _hull.Thrust;
        float thrust2 = thrust * thrust;
        float thrustToVelocity = dT / Mass;          // Newtons → Δv; shipIGC.cpp:2868

        // ── manoeuvre: ramp turn rates toward the desired, clipped by torque ─
        // (shipIGC.cpp:2887-2910; no ripcord, so this block always runs)
        float l = Controls.Yaw * Controls.Yaw + Controls.Pitch * Controls.Pitch + Controls.Roll * Controls.Roll;
        l = l > 1f ? 1f / MathF.Sqrt(l) : 1f;         // constrain desired rate to a sphere, not a box

        float tm = GetTorqueMultiplier() * thrustToVelocity;
        for (int i = 0; i < 3; i++)
        {
            float desiredRate = Controls.TurnAxis(i) * l * _hull.MaxTurnRates[i];
            float maxDelta = tm * _hull.TurnTorques[i];
            if (desiredRate < _turnRates[i] - maxDelta)
                _turnRates[i] -= maxDelta;
            else if (desiredRate > _turnRates[i] + maxDelta)
                _turnRates[i] += maxDelta;
            else
                _turnRates[i] = desiredRate;
        }

        Orientation.Yaw(_turnRates[ControlData.AxisYaw] * dT);
        Orientation.Pitch(-_turnRates[ControlData.AxisPitch] * dT);
        Orientation.Roll(_turnRates[ControlData.AxisRoll] * dT);
        Orientation.Renormalize();

        var backward = Orientation.GetBackward();
        float speed = Velocity.Length();
        float maxSpeed = _hull.Speed;

        // Drag = thrust required to produce the velocity change caused by drag.
        // Computed with double intermediates exactly as the C++ casts
        // (shipIGC.cpp:2930-2934).
        double f = Math.Exp((-thrust) * (double)thrustToVelocity / maxSpeed);
        var drag = Velocity * (float)((1.0 - f) / thrustToVelocity);

        bool afterF = (StateM & ShipControlState.AfterburnerButton) != 0;

        Vector3 localThrust;
        if ((StateM & ShipControlState.MoveButtonsMask) != 0)
        {
            // Manual control: whichever direction buttons are pressed, at full thrust.
            int x = ((StateM & ShipControlState.LeftButton) != 0 ? -1 : 0)
                  + ((StateM & ShipControlState.RightButton) != 0 ? 1 : 0);
            int y = ((StateM & ShipControlState.DownButton) != 0 ? -1 : 0)
                  + ((StateM & ShipControlState.UpButton) != 0 ? 1 : 0);
            int z = ((StateM & ShipControlState.BackwardButton) != 0 ? 1 : 0)
                  + ((StateM & ShipControlState.ForwardButton) != 0 ? -1 : 0);
            localThrust = (x != 0 || y != 0 || z != 0)
                ? new Vector3(thrust * x, thrust * y, thrust * z)
                : Vector3.Zero;
        }
        else if ((StateM & ShipControlState.CoastButton) != 0 && !afterF)
        {
            // Vector lock: kill lateral velocity (bring drag into ship-local).
            localThrust = Orientation.TimesInverse(drag);
        }
        else
        {
            // Throttle cruise: aim at a target speed and cancel drag.
            float negDesiredSpeed = afterF
                ? maxSpeed * (-1.0f - 0.0f)                          // thrustRatio == 0 (no AB)
                : (-0.5f * (1.0f + Controls.Throttle)) * ((speed > maxSpeed) ? speed : maxSpeed);
            var desiredVelocity = backward * negDesiredSpeed;
            localThrust = Orientation.TimesInverse((desiredVelocity - Velocity) / thrustToVelocity + drag);
        }

        // Clip the engine vector to the engine's available thrust.
        // (shipIGC.cpp:3008-3033)
        float sm = _hull.SideMultiplier;
        float backMult = _hull.BackMultiplier;
        var scaledThrust = new Vector3(
            localThrust.X / sm,
            localThrust.Y / sm,
            localThrust.Z <= 0f ? localThrust.Z : localThrust.Z / backMult);
        float r2 = scaledThrust.LengthSquared();

        Vector3 engineVector;
        if (r2 == 0f)
            engineVector = Vector3.Zero;
        else if (r2 <= thrust2)
            engineVector = localThrust * Orientation;
        else
            engineVector = (localThrust * Orientation) * (thrust / MathF.Sqrt(r2));

        Velocity += thrustToVelocity * (engineVector - drag);
    }

    /// <summary>
    /// <c>CshipIGC::GetTorqueMultiplier()</c> (<c>shipIGC.h:542-549</c>): 0.50
    /// at rest rising toward 2.50 as speed approaches/exceeds max, using
    /// <c>f = |v|/maxSpeed</c>. Read from the current velocity (at the moment
    /// the ramp runs) exactly as the reference reads <c>GetVelocity()</c>.
    /// </summary>
    private float GetTorqueMultiplier()
    {
        const float c = 0.50f;
        float fraction = Velocity.Length() / _hull.Speed;
        return c + (1.0f - c) * 2.0f * fraction / (fraction + 1.0f);
    }

    /// <summary>
    /// The sector-tick hook (called via <see cref="Model.Update"/> once per
    /// chunk). Runs the flight model for the elapsed <c>dt</c> —
    /// <see cref="ExecuteMove"/> — then integrates the C++-owned position step
    /// <c>Δposition = velocity·ΔT</c>, which <c>ExecuteShipMove</c> deliberately
    /// does <b>not</b> do (<c>CalculateShip</c>, <c>shipIGC.cpp:3273-3286</c>;
    /// the mission/chunk driver owns it in the reference). Integrating here is
    /// the minimal honest spot: the whole <c>Move()</c> pass lives in the
    /// out-of-scope collision block (<c>clusterIGC.cpp:338-346</c>), and without
    /// a position step a sector tick would not move a ship at all. Physics is
    /// unchanged — <c>ExecuteMove</c> is called verbatim with the same elapsed
    /// time it would receive as a standalone move, so velocity/orientation match
    /// a direct call exactly (the measured split-vs-single divergence is the
    /// mission cadence, not this wrapper).
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        ExecuteMove(now - dt, now);   // flight kinematics for exactly this chunk
        Position += Velocity * (float)dt;   // integrate Δpos = velocity·ΔT
    }
}

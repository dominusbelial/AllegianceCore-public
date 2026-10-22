using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P448a — a pure, allocation-free (per <c>PoseAt</c>) dead-reckoner for a
/// <b>remote</b> ship, ported from native <c>CshipIGC::CalculateShip</c>
/// (<c>shipIGC.cpp:3244-3297</c>) via <c>WarpShip</c> (<c>:3104</c>). Instead
/// of blending between received poses (the removed <c>RemoteShipPredictor</c>),
/// it <b>re-runs the real flight model forward</b> from the last authoritative
/// state using the pilot's own controls, so a turning, thrusting ship keeps
/// turning and thrusting between packets exactly as the server will simulate
/// it — error comes only from control changes the client hasn't heard about
/// yet.
/// </summary>
/// <remarks>
/// <para><b>Chunking (native-faithful).</b> The replay uses
/// <c>CalculateShip</c>'s chunk scheme: <c>n = 1 + (int)(ΔT / TimeIncrement)</c>
/// chunks (TimeIncrement = 0.25&nbsp;s, <see cref="Mission.TimeIncrement"/>),
/// each advancing the kinematics with <see cref="ShipKinematics.Step"/> and
/// integrating <c>position += velocity·ΔT</c> per chunk (exactly
/// <c>shipIGC.cpp:3280-3291</c>).</para>
/// <para><b>Future / past updates.</b> For <c>ΔT ≤ 0</c> (a query at/before the
/// seed time) it backs out linearly with the turn rates, exactly native's
/// branch (<c>shipIGC.cpp:3249-3266</c>). Extrapolation is <b>capped</b> at
/// <see cref="ExtrapolationCap"/> (1.5&nbsp;s) so a stalled stream never flings
/// a ship arbitrarily far — matching native's ±1.5&nbsp;s acceptance window
/// (<c>shipIGC.cpp:3113-3115</c>): beyond it native drops the update as
/// <c>c_susOutOfDate</c>, and we hold the pose at the cap.</para>
/// <para><b>Ripcord state (P781).</b> <c>ServerShipUpdate</c> carries no ripcord
/// state, so the replay's <c>Ripcording</c> flag comes from the per-ship client
/// state instead: the host sets <see cref="Ripcording"/> from the
/// RIPCORD_ACTIVATE/ABORTED bookkeeping (native <c>appmsg.cpp:950-962</c> sets
/// the model for any ripcording ship) so a remotely-ripcording ship dead-reckons
/// with the frozen flight model — pinned turn rates, drag-only speed — exactly
/// like the server is simulating it (native runs <c>ExecuteShipMove</c> with the
/// model set on every client-side CshipIGC, shipIGC.cpp:2885-3037). Until set,
/// the flag is false (the pre-P781 behaviour).</para>
/// <para><b>Hull-stat source (item 3).</b> The caller resolves the
/// <em>already-effective</em> hull stats — hull record × side global-attribute
/// factors — the same way the client already resolves hulls from the shop
/// catalogue / static core (the autopilot's hull lookup,
/// <c>Game1.FlightInput.cs:~1325</c>). <see cref="HullStats.FromHull"/> builds
/// them from a <see cref="DataHullTypeIGC"/> + GA factors (identity when the
/// side is unknown). Afterburner stats come from the ship's known parts when
/// available; when they are not, the afterburner is treated as <b>absent</b>
/// (<c>afterburnerMaxThrust = 0</c>) and the ship simply gets no afterburner
/// boost in the extrapolation. Mass is hull mass (<c>Ship.cs:174</c>).</para>
/// <para><b>No allocation in the hot path.</b> Every scratch buffer
/// (<see cref="_scratchTurnRates"/>, <see cref="_scratchOrientation"/>) is
/// allocated once at construction and reused; <c>PoseAt</c> writes results
/// into caller-provided <c>out</c> targets.</para>
/// </remarks>
public sealed class ShipDeadReckoner
{
    /// <summary>
    /// The extrapolation cap, seconds. Matches native's ±1.5&nbsp;s acceptance
    /// window for <c>ProcessShipUpdate</c> (<c>shipIGC.cpp:3113-3115</c>): a
    /// heavy update arriving more than 1.5&nbsp;s off is rejected as
    /// out-of-date rather than trusted. Here it bounds how far a stalled remote
    /// stream may dead-reckon before holding at the cap — so a ship that stops
    /// sending never drifts unboundedly into infinity.
    /// </summary>
    public const float ExtrapolationCap = 1.5f;

    /// <summary>P612 — the server sim time scale (default 1.0). <see cref="PoseAt"/>
    /// multiplies its wall-elapsed by this so a slow-motion server (sim.timeScale
    /// &lt; 1) is extrapolated in SIM time, keeping remote ships on their
    /// authoritative slowed positions instead of overshooting. Written by the
    /// world on the render thread (under the gate) before each PoseAt.</summary>
    public double TimeScale = 1.0;

    /// <summary>
    /// The static, already-effective hull statistics a dead-reckoned ship
    /// needs. Kept separate from the per-update state (<see cref="Seed"/> reads
    /// position/velocity/orientation/turn-rates/controls from the
    /// <see cref="ServerShipUpdate"/>) because these are stable for a given
    /// hull across updates.
    /// </summary>
    public readonly record struct HullStats(
        float Thrust,
        float MaxSpeed,
        float Mass,
        float SideMultiplier,
        float BackMultiplier,
        float MaxTurnRateYaw, float MaxTurnRatePitch, float MaxTurnRateRoll,
        float TurnTorqueYaw, float TurnTorquePitch, float TurnTorqueRoll)
    {
        /// <summary>
        /// Build the effective hull stats from a hull record and the side
        /// global-attribute factors — the client's shop-catalogue resolution
        /// path (<c>Game1.FlightInput.cs:~1325</c>), made reusable for the
        /// dead-reckoner. GA factors default to 1.0 (identity) when the ship's
        /// side is unknown, exactly as the server treats a sideless ship.
        /// </summary>
        public static HullStats FromHull(
            DataHullTypeIGC hull,
            float gaThrust = 1f, float gaMaxSpeed = 1f,
            float gaTurnRate = 1f, float gaTurnTorque = 1f) => new(
            Thrust: hull.Thrust * gaThrust,
            MaxSpeed: hull.Speed * gaMaxSpeed,
            Mass: hull.Mass,
            SideMultiplier: hull.SideMultiplier,
            BackMultiplier: hull.BackMultiplier,
            MaxTurnRateYaw: hull.MaxTurnRates[ControlData.AxisYaw] * gaTurnRate,
            MaxTurnRatePitch: hull.MaxTurnRates[ControlData.AxisPitch] * gaTurnRate,
            MaxTurnRateRoll: hull.MaxTurnRates[ControlData.AxisRoll] * gaTurnRate,
            TurnTorqueYaw: hull.TurnTorques[ControlData.AxisYaw] * gaTurnTorque,
            TurnTorquePitch: hull.TurnTorques[ControlData.AxisPitch] * gaTurnTorque,
            TurnTorqueRoll: hull.TurnTorques[ControlData.AxisRoll] * gaTurnTorque);
    }

    private readonly float[] _scratchTurnRates = new float[3];
    private readonly Orientation _scratchOrientation = new();

    private ShipKinematics.Inputs _inputs;
    private bool _seeded;
    private bool _ripcording;   // P781 — settable per-ship frozen-flight flag (see class doc)
    private double _arrivalTime;
    private Vector3 _seedPosition;
    private Vector3 _seedVelocity;
    private Vector3 _seedForward;      // authoritative nose, copied at Seed for a non-allocating replay reset
    private Vector3 _seedUp;           // authoritative up, copied at Seed
    private readonly float[] _seedTurnRates = new float[3];

    /// <summary>Whether <see cref="Seed"/> has been called (false ⇒ <see cref="PoseAt"/>
    /// returns the seed's zero/default pose).</summary>
    public bool Seeded => _seeded;

    /// <summary>The local arrival time of the last seed (seconds).</summary>
    public double ArrivalTime => _arrivalTime;

    /// <summary>
    /// P781 — whether this ship is currently ripcording (the native client-side
    /// ripcord model, appmsg.cpp:950-962). When true, <see cref="PoseAt"/>
    /// replays the FROZEN flight model: turn rates stay at their seed values
    /// (no ramp), thrust is skipped (drag-only decay), and an active afterburner
    /// still boosts — the exact physics the server runs for a ripcording ship
    /// (ShipKinematics.Step with <c>Ripcording: true</c>, shipIGC.cpp:2885-3037).
    /// Applied to <see cref="_inputs"/> on change; survives re-seeds
    /// (<see cref="Seed"/> adopts the flag, and only a real clear message
    /// clears it). Default false.
    /// </summary>
    public bool Ripcording
    {
        get => _ripcording;
        set
        {
            if (_ripcording == value) return;               // non-allocating no-op when unchanged
            _ripcording = value;
            if (_seeded) _inputs = _inputs with { Ripcording = value };
        }
    }

    /// <summary>
    /// Store the authoritative state from a received update, anchored at the
    /// <b>local arrival time</b> <paramref name="arrivalTime"/>.
    /// <c>ServerShipUpdate.Time</c> is the server <b>tick index</b>
    /// (<c>Server.Broadcast.cs:422</c>), not seconds, and the client doesn't
    /// know the tick period — so replay anchors on local arrival time.
    /// </summary>
    /// <param name="update">The received heavy ship update (controls, stateM,
    /// position, orientation, velocity, turn rates, power).</param>
    /// <param name="hull">The ship's already-effective hull stats.</param>
    /// <param name="afterburnerMaxThrust">The mounted afterburner's max thrust;
    /// 0 means no afterburner (treated as absent).</param>
    /// <param name="arrivalTime">Local wall-clock time the update arrived.</param>
    public void Seed(ServerShipUpdate update, in HullStats hull, float afterburnerMaxThrust, double arrivalTime)
    {
        _arrivalTime = arrivalTime;
        _seedPosition = update.Position;
        _seedVelocity = update.Velocity;
        _seedForward = update.Orientation.GetForward();
        _seedUp = update.Orientation.GetUp();
        update.TurnRates.CopyTo(_seedTurnRates, 0);

        _inputs = new ShipKinematics.Inputs(
            Controls: update.Controls,
            StateM: update.StateM,
            Thrust: hull.Thrust,
            MaxSpeed: hull.MaxSpeed,
            Mass: hull.Mass,
            SideMultiplier: hull.SideMultiplier,
            BackMultiplier: hull.BackMultiplier,
            MaxTurnRateYaw: hull.MaxTurnRateYaw,
            MaxTurnRatePitch: hull.MaxTurnRatePitch,
            MaxTurnRateRoll: hull.MaxTurnRateRoll,
            TurnTorqueYaw: hull.TurnTorqueYaw,
            TurnTorquePitch: hull.TurnTorquePitch,
            TurnTorqueRoll: hull.TurnTorqueRoll,
            AfterburnerMaxThrust: afterburnerMaxThrust,
            AfterburnerPower: update.Power,
            Ripcording: _ripcording);   // P781 — adopt the settable frozen-flight flag (survives re-seeds)

        _seeded = true;
    }

    /// <summary>
    /// P505 — replace the controls the forward replay uses with the caller's
    /// CURRENT commanded controls (the same controls the own-ship predictor steps
    /// with), leaving the seed pose/velocity/hull untouched. The own-ship predictor
    /// calls this after <see cref=\"Seed\"/> so the authoritative target reflects
    /// where the ship is heading under the <b>current</b> command instead of the
    /// update's <b>stale</b> controls. That removes the spurious reconciliation
    /// error a throttle/control change otherwise injects over the extrapolation
    /// window (the own-ship predictor steps with current controls each frame, so
    /// comparing against a target replayed with the old throttle makes the target
    /// race metres ahead during a brake — the decel micro-stutter). Remote ships
    /// never call this; they keep replaying with the update's own controls.
    /// </summary>
    public void OverrideReplayControls(ControlData controls, int stateM)
    {
        if (!_seeded) return;
        _inputs = _inputs with { Controls = controls, StateM = stateM };
    }

    /// <summary>
    /// Compute the position and orientation the ship would be in at
    /// <paramref name="now"/>, replayed from the seed with the real flight
    /// model (<c>CalculateShip</c>). Results are written into
    /// <paramref name="position"/> and <paramref name="orientation"/> (the
    /// latter is this instance's reusable scratch <see cref="Orientation"/>,
    /// overwritten on every call — copy it if you need to retain it). No
    /// allocation occurs in the hot path.
    /// </summary>
    public void PoseAt(double now, out Vector3 position, out Orientation orientation)
    {
        position = _seedPosition;
        orientation = _scratchOrientation;

        if (!_seeded)
        {
            orientation.Reset();
            return;
        }

        float deltaT = (float)((now - _arrivalTime) * TimeScale);   // P612 — sim time = wall elapsed × server time scale
        if (deltaT > ExtrapolationCap) deltaT = ExtrapolationCap;       // stall guard (see class doc)
        else if (deltaT < -ExtrapolationCap) deltaT = -ExtrapolationCap;

        // Reset the scratch state to the authoritative seed (non-allocating:
        // reconstruct the orientation from the copied axes, exactly what the
        // sim's own Renormalize does each step).
        Vector3 velocity = _seedVelocity;
        float[] turnRates = _scratchTurnRates;
        Array.Copy(_seedTurnRates, turnRates, 3);
        orientation.Set(_seedForward, _seedUp);

        if (deltaT <= 0.0f)
        {
            // Update applies at/after now — back the ship out linearly
            // (shipIGC.cpp:3249-3266), using the turn rates (backing out a full
            // integration would be hard, exactly native's reasoning).
            position += velocity * deltaT;
            orientation.Roll(-turnRates[ControlData.AxisRoll] * deltaT);
            orientation.Pitch(turnRates[ControlData.AxisPitch] * deltaT);
            orientation.Yaw(-turnRates[ControlData.AxisYaw] * deltaT);
            orientation.Renormalize();
            return;
        }

        // Forward replay, never advancing more than TimeIncrement per chunk
        // (shipIGC.cpp:3270-3291). The chunk schedule only ever depends on
        // *differences* (ExecuteShipMove uses nextTime-thisTime; position
        // integrates by the same), so anchoring at 0 is equivalent to native's
        // timeUpdate-based schedule.
        int n = 1 + (int)(deltaT / Mission.TimeIncrement);
        float elapsed = 0f;
        for (int i = 1; i <= n; i++)
        {
            float nextTime = deltaT * (i / (float)n);
            float dt = nextTime - elapsed;
            ShipKinematics.Step(in _inputs, ref velocity, orientation, turnRates, dt);
            position += velocity * dt;
            elapsed = nextTime;
        }
    }
}

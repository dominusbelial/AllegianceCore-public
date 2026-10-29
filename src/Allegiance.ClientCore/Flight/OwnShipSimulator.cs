using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P508 — the client-side <b>own-ship simulation</b>: the client flies its own
/// ship locally every render frame with the port's real flight step
/// (<see cref="ShipKinematics.Step"/>, P448a) and the side-GA-scaled hull stats,
/// and the server only sanity-checks the reported pose (P507). This is native's
/// model — the client <em>is</em> the authority on where its own ship is; the
/// rendered pose <em>is</em> this local simulation at full frame rate, with
/// <b>no reconciliation, blending or snap</b> against the periodic authoritative
/// <c>ServerShipUpdate</c> (the P447/P502/P505 <c>OwnShipPredictor</c> blend/snap
/// is retired). Divergence from the server is resolved only by the server's real
/// <c>SHIP_RESET</c>, which the host applies by calling <see cref="Seed"/>
/// (snap to the server pose + adopt its cookie) — exactly how native re-syncs.
///
/// <para><b>Lifecycle (all on the main render thread; the world gate serialises
/// the wire-side pose against the renderer):</b>
/// <list type="bullet">
/// <item><see cref="SetHull"/> once the hull/GA/afterburner are known (respawn /
/// lifepod / hull swap), so <see cref="Step"/> uses the real effective stats.</item>
/// <item><see cref="Step"/>(dt, controls, stateM) every frame with the <em>exact</em>
/// controls the client sends, so a control change moves the local pose on the same
/// frame — the whole point of flying locally.</item>
/// <item><see cref="Seed"/> from the server pose at first sight, on a received
/// <c>SHIP_RESET</c> (snap + resume), and after a hard transition settles
/// (dock / undock / launch / warp / ripcord / death / respawn), so each
/// discontinuity re-seeds the simulation from the server's authoritative pose.</item>
/// <item><see cref="SetRipcording"/> for the ripcord countdown (P781): the ship
/// KEEPS flying the frozen flight model (turn rates pinned, drag-only — native
/// <c>ExecuteShipMove</c>, shipIGC.cpp:2885-3037) instead of freezing; a hard
/// freeze is reserved for dock/death/sector-change transitions.</item>
/// <item><see cref="Freeze"/> / <see cref="Resume"/> to hold the server pose across
/// a hard transition (docked, dead, warp / sector change), mirroring the predictor's
/// freeze/resume edges; while frozen <see cref="Step"/> is inert and no native
/// SHIP_UPDATE is sent.</item>
/// </list></para>
///
/// <para><b>Boundary:</b> the simulator is purely local kinematics. It has no
/// server collision / bump / clamp knowledge — a predicted ship may briefly fly
/// through a rock until the server's <c>SHIP_RESET</c> (its own re-sync) pulls it
/// back, exactly native's behaviour for a client-reported pose.</para>
/// </summary>
public sealed class OwnShipSimulator
{
    /// <summary>Physics substep cap, seconds — one server tick (20 Hz). Never
    /// integrate a render frame's motion over a larger quantum than the server's
    /// per-tick step, so a render hitch can't race the simulation ahead of the
    /// server (the P502 anti-over-integration guarantee, kept from the predictor).</summary>
    public const float Substep = 0.05f;

    private ShipKinematics.Inputs _inputs;
    private bool _haveHull;
    private bool _frozen;
    private readonly float[] _turnRates = new float[3];
    private DataPartTypeIGC.Afterburner? _afterburner;
    private float _boostFuel, _boostPower, _mountRate = 1f;
    private float _boostMountedFraction = 1f;
    private bool _boostActive;

    /// <summary>Whether a hull has been resolved yet (false ⇒ <see cref="Step"/>
    /// is a no-op until <see cref="SetHull"/>).</summary>
    public bool HaveHull => _haveHull;

    /// <summary>P624 - own hull max speed (u/s), for match-speed (native's
    /// GetThrottle divides the target speed by the pilot's max speed,
    /// WinTrek.cpp:125-132). Null until a hull resolves.</summary>
    public float? MaxSpeed => _haveHull ? _inputs.MaxSpeed : null;

    /// <summary>Whether the simulation is frozen to a held server pose (a hard
    /// transition in progress). While frozen <see cref="Step"/> does nothing.</summary>
    public bool Frozen => _frozen;

    /// <summary>The simulated position.</summary>
    public Vector3 Position { get; private set; }

    private Vector3 _velocity;

    /// <summary>The simulated velocity.</summary>
    public Vector3 Velocity => _velocity;

    private readonly Orientation _orientation = new();

    /// <summary>A copy of the simulated orientation (the internal one mutates in
    /// place during <see cref="Step"/>; copy it if you retain it).</summary>
    public Orientation Orientation => _orientation.Copy();

    /// <summary>A copy of the simulated turn rates (yaw/pitch/roll), rad/s —
    /// reported verbatim on the wire.</summary>
    public float[] TurnRates => (float[])_turnRates.Clone();

    /// <summary>The last controls stepped (and therefore the controls to report
    /// on the next native SHIP_UPDATE).</summary>
    public ControlData Controls { get; private set; } = ControlData.Neutral;

    /// <summary>The last stateM stepped.</summary>
    public int StateM { get; private set; }

    /// <summary>
    /// P881a — the native <c>turnToFace</c> hull/attitude inputs for the
    /// training <c>TurnToAction</c> (Igc/common.cpp:152-252 — mass, torque
    /// multiplier, effective turn torques and max turn rates plus the live turn
    /// rates). Null until a hull resolves. Read-only; the client's
    /// <c>TurnToFace</c> binding feeds it to <see cref="TrainingTurnTo.Compute"/>.
    /// </summary>
    public Allegiance.ClientCore.Training.TrainingTurnTo.TurnHull? TurnHull => !_haveHull ? null : new(
        Mass: _inputs.Mass,
        TorqueMultiplier: ShipKinematics.GetTorqueMultiplier(_velocity, _inputs.MaxSpeed),
        TurnTorqueYaw: _inputs.TurnTorqueYaw,
        TurnTorquePitch: _inputs.TurnTorquePitch,
        MaxTurnRateYaw: _inputs.MaxTurnRateYaw,
        MaxTurnRatePitch: _inputs.MaxTurnRatePitch,
        CurrentTurnRateYaw: _turnRates[ControlData.AxisYaw],
        CurrentTurnRatePitch: _turnRates[ControlData.AxisPitch]);

    /// <summary>
    /// The current afterburner power (0..1), integrated with the same power,
    /// mount and fuel rules as Ship.UpdateAfterburner.
    /// </summary>
    public float Power => _boostPower;

    /// <summary>P641 — configure on equipment change/reset, not each frame.
    /// Mirrors Ship.UpdateAfterburner / native afterburnerIGC.cpp:103-164.
    /// Carries the real power/fuel ramp into the shared movement step.</summary>
    public void SetAfterburner(DataPartTypeIGC.Afterburner? type, byte mounted, float fuel, float mountRate, float power = 0, float thrustFactor = 1)
    {
        _afterburner = type;
        _boostMountedFraction = Math.Min(1f, mounted / (float)ExpandedPartData.FullyMounted);
        _boostFuel = fuel;
        _mountRate = mountRate; // loaded zero must remain zero
        _boostPower = type is null ? 0 : Math.Clamp(power, 0, 1);
        _boostActive = _boostPower > 0;
        _inputs = _inputs with { AfterburnerMaxThrust = (type?.MaxThrust ?? 0) * thrustFactor, AfterburnerPower = _boostPower };
    }

    private void StepAfterburner(float dt, bool useFuel)
    {
        if (_afterburner is not { } type) return;
        if (_boostMountedFraction < 1f)
        {
            _boostActive = false;
            _boostPower = 0;
            if (useFuel) _boostMountedFraction = Math.Min(1f, _boostMountedFraction + dt * _mountRate);
            if (_boostMountedFraction < 1f) return;
        }
        bool activate = (StateM & ShipControlState.AfterburnerButton) != 0 && _boostFuel != 0;
        if (activate && !_boostActive) { _boostActive = true; _boostPower = 0; }
        if (!_boostActive) return;
        _boostPower = Math.Clamp(_boostPower + dt * (activate ? type.OnRate : -type.OffRate), 0, 1);
        if (_boostPower <= 0) _boostActive = false;
        if (useFuel)
        {
            // Native fuel consumption uses raw thrust, not the side GA bonus.
            float used = _boostPower * type.FuelConsumption * type.MaxThrust * dt;
            if (used < _boostFuel) _boostFuel -= used;
            else if (_boostFuel != 0) { _boostFuel = 0; _boostPower = 0; _boostActive = false; }
        }
    }

    /// <summary>
    /// Configure the own ship's already-effective hull stats + afterburner max thrust
    /// (the values <see cref="Step"/> integrates) and establish the hull.
    /// Call only when the hull / side GA / loadout changes (respawn, lifepod,
    /// hull swap), not every frame. Preserves the current ripcord state
    /// (<see cref="SetRipcording"/>) — a hull swap mid-countdown keeps flying frozen.
    /// </summary>
    public void SetHull(in ShipDeadReckoner.HullStats hull, float afterburnerMaxThrust)
    {
        bool ripcording = _inputs.Ripcording;
        _inputs = BuildInputs(in hull, afterburnerMaxThrust, 0f, Controls, StateM)
            with { Ripcording = ripcording };
        _haveHull = true;
    }

    /// <summary>
    /// P781 — engage / disengage the native frozen flight model for a ripcord
    /// countdown: turn rates are pinned at their activation values (no ramp —
    /// <c>shipIGC.cpp:2885-2910</c> is guarded by <c>if (!m_pmodelRipcord)</c>),
    /// engine thrust is skipped (shipIGC.cpp:2961-2962) so the velocity decays
    /// drag-only toward zero, while the orientation keeps integrating the frozen
    /// rates (shipIGC.cpp:2912-2917). An active afterburner is NOT gated
    /// (shipIGC.cpp:2939-2957 folds it into drag) and still accelerates forward.
    /// While ripcording the sim is NOT frozen: the client keeps flying and keeps
    /// sending native SHIP_UPDATEs (BaseClient::SendUpdate gates only on
    /// flyingF(), clintlib.cpp:1815-1866). The client is the authority on its own
    /// pose — native does not snap or re-seed at the activation edge
    /// (appmsg.cpp:940-944 sets only the model + countdown).
    /// </summary>
    public void SetRipcording(bool ripcording)
    {
        if (_inputs.Ripcording == ripcording) return;   // non-allocating no-op when unchanged
        _inputs = _inputs with { Ripcording = ripcording };
    }

    /// <summary>
    /// Advance the local simulation one render frame with the <em>exact</em>
    /// controls the client sends that frame (manual or autopilot). The full
    /// elapsed <paramref name="dt"/> is covered in ≤ <see cref="Substep"/> quanta
    /// (the P505 guarantee: never discard physics on a long frame, never integrate
    /// more than one server tick per sub-step). Inert until a hull is set or while
    /// frozen.
    /// </summary>
    public void Step(float dt, ControlData controls, int stateM)
    {
        if (!_haveHull || _frozen) return;
        Controls = controls;
        StateM = stateM;
        _inputs = _inputs with { Controls = controls, StateM = stateM };

        int n = Math.Max(1, (int)MathF.Ceiling(dt / Substep));
        float dc = dt / n;
        for (int i = 0; i < n; i++)
        {
            // shipIGC.cpp:2945-2957 advances power before movement; the
            // mounted-parts pass advances it again and alone consumes fuel.
            StepAfterburner(dc, useFuel: false);
            _inputs = _inputs with { AfterburnerPower = _boostPower };
            ShipKinematics.Step(in _inputs, ref _velocity, _orientation, _turnRates, dc);
            Position += _velocity * dc;
            StepAfterburner(dc, useFuel: true);
        }
    }

    /// <summary>
    /// Seed the local simulation from a server pose and resume flying (clears the
    /// freeze). Used at first sight, on a received <c>SHIP_RESET</c> (snap to the
    /// server's authoritative pose + resume), and after a hard transition settles.
    /// </summary>
    public void Seed(Vector3 position, Vector3 velocity, Orientation orientation, float[] turnRates)
    {
        Position = position;
        _velocity = velocity;
        _orientation.Set(orientation.GetForward(), orientation.GetUp());
        turnRates.CopyTo(_turnRates, 0);
        _frozen = false;
    }

    /// <summary>Hold the current pose (a hard transition is in progress); <see cref="Step"/>
    /// becomes inert until <see cref="Resume"/>. Seed to the server pose first so the held
    /// pose is the authoritative one.</summary>
    public void Freeze() => _frozen = true;

    /// <summary>Clear the freeze and resume simulating from the current pose.</summary>
    public void Resume() => _frozen = false;

    private static ShipKinematics.Inputs BuildInputs(
        in ShipDeadReckoner.HullStats hull, float afterburnerMaxThrust, float afterburnerPower,
        ControlData controls, int stateM) => new(
        Controls: controls,
        StateM: stateM,
        Thrust: hull.Thrust,
        MaxSpeed: hull.MaxSpeed,
        Mass: hull.Mass,
        SideMultiplier: hull.SideMultiplier,
        BackMultiplier: hull.BackMultiplier,
        MaxTurnRateYaw: hull.MaxTurnRateYaw, MaxTurnRatePitch: hull.MaxTurnRatePitch, MaxTurnRateRoll: hull.MaxTurnRateRoll,
        TurnTorqueYaw: hull.TurnTorqueYaw, TurnTorquePitch: hull.TurnTorquePitch, TurnTorqueRoll: hull.TurnTorqueRoll,
        AfterburnerMaxThrust: afterburnerMaxThrust,
        AfterburnerPower: afterburnerPower,
        Ripcording: false);   // caller overrides via SetRipcording (P781: ripcording ships still fly — frozen flight model)
}

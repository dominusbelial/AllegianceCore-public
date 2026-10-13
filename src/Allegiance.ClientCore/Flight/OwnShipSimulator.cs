using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

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
/// <item><see cref="Freeze"/> / <see cref="Resume"/> to hold the server pose across
/// a hard transition (docked, dead, ripcording, warp), mirroring the predictor's
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
    /// The current afterburner power (0..1) reported in the wire <c>power</c> slot.
    /// The port has no afterburner-engagement input in flight, so the simulation
    /// integrates (and reports) flat 0 — the server ignores the client-reported
    /// power in its replay anyway (<c>BuildKinematicsInputs</c> uses the server's
    /// own state), so this only needs to be a valid 0..1 value.
    /// </summary>
    public float Power => 0f;

    /// <summary>
    /// Configure the own ship's already-effective hull stats + afterburner max thrust
    /// (the values <see cref="Step"/> integrates) and establish the hull.
    /// Call only when the hull / side GA / loadout changes (respawn, lifepod,
    /// hull swap), not every frame.
    /// </summary>
    public void SetHull(in ShipDeadReckoner.HullStats hull, float afterburnerMaxThrust)
    {
        _inputs = BuildInputs(in hull, afterburnerMaxThrust, 0f, Controls, StateM);
        _haveHull = true;
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
            ShipKinematics.Step(in _inputs, ref _velocity, _orientation, _turnRates, dc);
            Position += _velocity * dc;
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
        Ripcording: false);   // never simulated while ripcording (a frozen state)
}

using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P447a — a <b>pure</b> client-side predictor for the player's <b>own</b>
/// ship. The port is server-authoritative (the client sends controls only —
/// <c>ConnectionSession.CommandControls</c>), so every turn and throttle change
/// previously showed up a full round trip plus up to 50&nbsp;ms late and the
/// camera/hull stepped at the server's 20&nbsp;Hz. <c>OwnShipPredictor</c>
/// closes that gap: it runs the <b>same flight model</b> the server runs
/// (<see cref="ShipKinematics.Step"/>, P448a) each render frame using the
/// <b>same controls the client sends that frame</b>, so the player's inputs take
/// effect on the same frame — while the server stays authoritative and the
/// predictor reconciles against the received updates.
/// </summary>
/// <remarks>
/// <para><b>Primary signal = local prediction.</b> Each <see cref="Step"/>
/// integrates the real flight model forward from the predictor's pose with the
/// frame's controls (chunked to <see cref="Mission.TimeIncrement"/> like the
/// server), plus <c>position += velocity·dt</c>. Because the server integrates
/// the <b>same</b> controls through the <b>same</b> integrator, the local
/// prediction tracks where the ship truly is; the received updates are a
/// <b>correction/secondary</b> signal used to resorb server-side-only effects
/// (collisions, bumps, authoritative clamping) and to re-sync after a
/// discontinuity.</para>
/// <para><b>Reconciliation (P447a scope item 1).</b> <see cref="OnServerUpdate"/>
/// builds the authoritative target by replaying the received update to "now"
/// with a <see cref="ShipDeadReckoner"/> (P448a) using the update's own
/// controls, then compares it with the current prediction. If the position error
/// exceeds <see cref="SnapDistance"/> (20&nbsp;m — native's own-ship acceptance
/// threshold, <c>shipIGC.cpp:3176</c> <c>d &lt; 400</c>) or the orientation error
/// exceeds <see cref="SnapOrientationDegrees"/>, the predictor <b>snaps</b> to
/// the target outright. Otherwise the correction is a <b>one-shot exponential
/// blend</b>: each update moves the pose/velocity/turn-rates a fraction
/// <c>α = 1−exp(−Δt/τ)</c> (Δt = update interval, τ =
/// <see cref="CorrectionTimeConstant"/>) of the way to the target, so the error
/// offset decays geometrically toward zero over ~τ across successive updates.
/// This <em>decays the error offset</em> rather than lerping toward a stale
/// target, the flaw that sank the removed <c>RemoteShipPredictor</c> (a blend/
/// lerp smoother that stacked extrapolation on top of interpolation toward old
/// poses). Orientation converges through the turn-rate blend (a physically-
/// consistent re-orientation), never a pose lerp.</para>
/// <para><b>Hard transitions.</b> States where the pose discontinuously changes
/// — docked, ripcording, dead, lifepod ejection, warp / sector change,
/// teleport — must <em>freeze</em> prediction: <see cref="HardReset"/> snaps to
/// the server pose and holds it (<see cref="Frozen"/>) until <see cref="Resume"/>
/// re-arms prediction. See the P447a report for the client signal that drives
/// each. The snap inside <see cref="OnServerUpdate"/> (an ordinary resync) does
/// <em>not</em> freeze — prediction resumes on the next <see cref="Step"/>.</para>
/// <para><b>Known limitation (documented):</b> collisions are server-side only.
/// A predicted ship will briefly fly through a rock until the server's response
/// moves it and the correction snaps/blends it back. This stage deliberately
/// adds no client collision.</para>
/// </remarks>
public sealed class OwnShipPredictor
{
    /// <summary>
    /// The position threshold, metres, beyond which an authoritative update is a
    /// hard snap instead of a blend. Ports native's own-ship acceptance
    /// threshold: the server accepts the client's reported position when the
    /// replayed pose is within 20&nbsp;m (<c>d &lt; 400</c> =
    /// <c>d² &lt; 400²</c>, <c>shipIGC.cpp:3176</c>), otherwise it answers
    /// <c>SHIP_RESET</c>. Matching that figure means a snap fires in exactly the
    /// cases native would reject outright. Configurable.
    /// </summary>
    public float SnapDistance = 20f;

    /// <summary>
    /// The orientation error, degrees, beyond which an authoritative update is a
    /// hard snap. Native accepts the own-ship orientation outright (no
    /// validation, <c>shipIGC.cpp:3139-3183</c>), so this is a port-local
    /// judgement: beyond ~30° of facing disagreement a smooth blend reads as a
    /// visible ghost-spin of the player's own hull, so it is better to snap
    /// directly; below it the blend is perceptually invisible. Configurable.
    /// </summary>
    public float SnapOrientationDegrees = 30f;

    /// <summary>
    /// The error-offset time constant τ, seconds. On a non-snap update the
    /// residual position/velocity/turn-rate error is decayed exponentially:
    /// each update moves the pose <c>1−exp(−Δt/τ)</c> of the way to the target
    /// (Δt = update interval), so the error offset shrinks geometrically across
    /// updates. 0.15&nbsp;s (150&nbsp;ms) sits in the 100–200&nbsp;ms window the
    /// brief suggests: fast enough to hide the correction within a few updates,
    /// slow enough that it never jolts the camera. Configurable.
    /// </summary>
    public float CorrectionTimeConstant = 0.15f;

    /// <summary>The nominal server update interval, seconds (20&nbsp;Hz,
    /// <c>server.json:5</c>), used to size the per-update blend fraction
    /// <c>α = 1−exp(−Δt/τ)</c>.</summary>
    public float UpdateInterval = 0.05f;

    private readonly ShipDeadReckoner _reckoner = new();

    // Predicted (displayed) pose — advanced by Step, reconciled by OnServerUpdate.
    private Vector3 _position;
    private Vector3 _velocity;
    private readonly Orientation _orientation = new();
    private readonly float[] _turnRates = new float[3];

    // The already-effective flight inputs (hull stats fixed by SetHull; controls/
    // stateM refreshed per Step; afterburner power refreshed per update).
    private ShipKinematics.Inputs _inputs;
    private bool _haveHull;

    private bool _frozen;

    // P447b — live diagnostics: accumulate the per-update reconciliation distance
    // (the position error dist between the prediction and the authoritative target
    // at each OnServerUpdate) plus the hard-snap count, so a live session can log a
    // periodic "[predict]" line (item 4). Purely observational; does not change the
    // integration or reconciliation.
    private long _correctCount;
    private double _correctSum;
    private float _correctMax;
    private long _snapCount;

    /// <summary>Whether a hull has been resolved yet (false ⇒ <see cref="Step"/>
    /// is a no-op until <see cref="SetHull"/> is called).</summary>
    public bool HaveHull => _haveHull;

    /// <summary>P447b — cumulative count of hard snaps (&gt; 20 m or &gt; 30°
    /// discontinuities) since construction (monotonic; not reset by
    /// <see cref="TakeCorrectionStats"/>).</summary>
    public long SnapCount => _snapCount;

    /// <summary>P447b — the per-update reconciliation distance statistics
    /// (count, mean, max in metres) accumulated since the last call, then reset —
    /// the mirrored counterpart of <c>ClientWorld.TakeRemoteCorrectionStats</c>,
    /// polled by the host to log a periodic <c>[predict]</c> line. Reset here so a
    /// live poll sees only the samples since the previous poll.</summary>
    public (long Count, double Mean, float Max) TakeCorrectionStats()
    {
        var result = (
            Count: _correctCount,
            Mean: _correctCount > 0 ? _correctSum / _correctCount : 0d,
            Max: _correctMax);
        _correctCount = 0;
        _correctSum = 0d;
        _correctMax = 0f;
        return result;
    }

    /// <summary>Whether prediction is frozen to the server pose (a hard
    /// transition: dock / ripcord / death / lifepod / warp / teleport). While
    /// frozen, <see cref="Step"/> does nothing. Cleared by <see cref="Resume"/>.</summary>
    public bool Frozen => _frozen;

    /// <summary>The predicted position.</summary>
    public Vector3 Position => _position;

    /// <summary>The predicted velocity.</summary>
    public Vector3 Velocity => _velocity;

    /// <summary>A copy of the predicted orientation (the internal one mutates in
    /// place; copy it if you retain it beyond the immediate read).</summary>
    public Orientation PredictedOrientation => _orientation.Copy();

    /// <summary>A copy of the predicted turn rates (yaw/pitch/roll).</summary>
    public float[] TurnRates => (float[])_turnRates.Clone();

    /// <summary>
    /// Resolve the ship's <em>already-effective</em> hull statistics (hull
    /// record × side GA factors) and afterburner max thrust — the values the
    /// prediction's <see cref="ShipKinematics.Step"/> consumes. Call once the
    /// hull is known (P447b resolves it from the shop catalogue / static core,
    /// exactly like the P448a autopilot lookup), and again if the hull or side
    /// GA changes.
    /// </summary>
    public void SetHull(in ShipDeadReckoner.HullStats hull, float afterburnerMaxThrust)
    {
        _inputs = BuildInputs(in hull, afterburnerMaxThrust, 0f, ControlData.Neutral, 0);
        _haveHull = true;
    }

    /// <summary>
    /// Advance the prediction one render frame. <paramref name="controls"/> and
    /// <paramref name="stateM"/> must be the <b>same</b> controls the client
    /// sends that frame (<c>ConnectionSession.CommandControls</c>) — that is what
    /// makes a control change move the pose on the same frame. The step is
    /// chunked to <see cref="Mission.TimeIncrement"/> (0.25&nbsp;s) like the
    /// server so a single frame never advances further than a mission slice,
    /// then <c>position += velocity·dt</c> is integrated per chunk.
    /// </summary>
    public void Step(float dt, ControlData controls, int stateM)
    {
        if (!_haveHull || _frozen) return;

        _inputs = _inputs with { Controls = controls, StateM = stateM };

        // Chunk to TimeIncrement like the server (n = 1 + (int)(dt/0.25), the
        // same n scheme Mission.Update and the dead-reckoner use): at typical
        // render rates n is 1; a stall or hitch never advances more than 0.25 s
        // per Step.
        int n = 1 + (int)(dt / Mission.TimeIncrement);
        float dc = dt / n;
        for (int i = 0; i < n; i++)
        {
            ShipKinematics.Step(in _inputs, ref _velocity, _orientation, _turnRates, dc);
            _position += _velocity * dc;
        }
    }

    /// <summary>
    /// Feed a received authoritative update for the own ship and reconcile the
    /// prediction. The authoritative target is the received state replayed to
    /// <paramref name="now"/> with a <see cref="ShipDeadReckoner"/> (P448a)
    /// anchored at <paramref name="arrivalTime"/>, using the update's own
    /// controls. If the position error exceeds <see cref="SnapDistance"/> or the
    /// orientation error exceeds <see cref="SnapOrientationDegrees"/>, snap to
    /// the target; otherwise blend a fraction
    /// <c>α = 1−exp(−<see cref="UpdateInterval"/>/<see cref="CorrectionTimeConstant"/>)</c>
    /// of the position/velocity/turn-rate error away toward the target (the
    /// error offset decays geometrically across successive updates). Orientation
    /// converges through the turn-rate blend, not a pose lerp.
    /// </summary>
    /// <param name="update">The received own-ship <see cref="ServerShipUpdate"/>.</param>
    /// <param name="hull">The ship's already-effective hull stats (same resolution as <see cref="SetHull"/>).</param>
    /// <param name="afterburnerMaxThrust">The mounted afterburner's max thrust (0 = absent).</param>
    /// <param name="arrivalTime">Local wall-clock time the update arrived (seconds).</param>
    /// <param name="now">Current local render time, ≥ <paramref name="arrivalTime"/> (seconds).</param>
    public void OnServerUpdate(
        ServerShipUpdate update, in ShipDeadReckoner.HullStats hull, float afterburnerMaxThrust,
        double arrivalTime, double now)
    {
        if (!_haveHull)
        {
            // First authoritative pose also establishes the hull so prediction
            // can start immediately once wired.
            SetHull(in hull, afterburnerMaxThrust);
        }
        else
        {
            // Hull/GA/afterburner may have changed since the last update; keep
            // the inputs aligned with the authoritative snapshot (refreshing the
            // afterburner power the server actually reports).
            _inputs = BuildInputs(in hull, afterburnerMaxThrust, update.Power, _inputs.Controls, _inputs.StateM);
        }

        // Authoritative target: the server state, replayed to now with the
        // update's own controls (P448a dead-reckoner — same physics the server
        // is running).
        _reckoner.Seed(update, in hull, afterburnerMaxThrust, arrivalTime);
        _reckoner.PoseAt(now, out Vector3 targetPos, out Orientation targetOri);

        Vector3 posErr = targetPos - _position;
        float dist = posErr.Length();
        float oriErr = RelativeAngleDeg(_orientation, targetOri);

        // P447b — accumulate the live correction diagnostics (item 4): the
        // per-update reconciliation distance and the snap count.
        _correctCount++;
        _correctSum += dist;
        if (dist > _correctMax) _correctMax = dist;

        if (dist > SnapDistance || oriErr > SnapOrientationDegrees)
        {
            // Genuine discontinuity / large divergence — snap to the authoritative
            // target outright (equivalent to native's SHIP_RESET).
            _snapCount++;
            _position = targetPos;
            _velocity = update.Velocity;
            _orientation.Set(targetOri.GetForward(), targetOri.GetUp());
            update.TurnRates.CopyTo(_turnRates, 0);
        }
        else
        {
            // Blend the error out smoothly: move a fraction α of the position
            // residual toward the target so the error offset decays exponentially
            // across updates (~τ to vanish), and adopt the server's velocity
            // outright — the cruise controller would otherwise keep killing a
            // weakly-blended lateral velocity each frame, so velocity must be set
            // (not nudged) to follow the server's authoritative flight state.
            float alpha = 1f - MathF.Exp(-UpdateInterval / CorrectionTimeConstant);
            _position += posErr * alpha;
            _velocity = update.Velocity;
            for (int i = 0; i < 3; i++)
                _turnRates[i] += (update.TurnRates[i] - _turnRates[i]) * alpha;
        }
    }

    /// <summary>
    /// Hard reset to a server pose with prediction <b>held</b> — the transport
    /// for the discrete transition states (docked, ripcording, dead, lifepod,
    /// warp / sector change, teleport). Snaps <see cref="Position"/>/velocity/
    /// orientation/turn-rates to the given authoritative values and sets
    /// <see cref="Frozen"/> so <see cref="Step"/> does not move the pose until
    /// <see cref="Resume"/> is called (once the transition has settled).
    /// </summary>
    public void HardReset(Vector3 position, Vector3 velocity, Orientation orientation, float[] turnRates)
    {
        _position = position;
        _velocity = velocity;
        _orientation.Set(orientation.GetForward(), orientation.GetUp());
        turnRates.CopyTo(_turnRates, 0);
        _frozen = true;
    }

    /// <summary>Clear <see cref="Frozen"/> and resume prediction from the current pose.</summary>
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
        Ripcording: false);   // never predicted while ripcording (that is a frozen state)

    /// <summary>The angle, degrees, of the relative rotation from <paramref name="from"/>
    /// to <paramref name="to"/> (the trace of <c>to·fromᵀ</c>), used as the orientation
    /// error metric for the snap threshold.</summary>
    private static float RelativeAngleDeg(Orientation from, Orientation to)
    {
        float tr = Dot(to.GetRight(), from.GetRight())
                 + Dot(to.GetUp(), from.GetUp())
                 + Dot(to.GetBackward(), from.GetBackward());
        float theta = MathF.Acos(Math.Clamp((tr - 1f) * 0.5f, -1f, 1f));
        return theta * 180f / MathF.PI;
    }

    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

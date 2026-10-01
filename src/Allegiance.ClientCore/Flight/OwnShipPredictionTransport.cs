using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P447b — the transport that wires <see cref="OwnShipPredictor"/> (P447a) into
/// the MonoGame client's per-frame flight path, exposed as a single MonoGame-free,
/// testable seam. The client (<c>Game1</c>) owns exactly one of these and drives it
/// from <c>Game1.FlightInput.Update</c>:
/// <list type="bullet">
/// <item><b>Every frame</b>, AFTER the command that is sent through
/// <c>ConnectionSession.CommandControls</c> (manual <em>or</em> autopilot) has been
/// chosen, call <see cref="Step"/> with that <b>exact</b> <c>(controls, stateM)</c>
/// — the same values, same frame — so a control change moves the local render pose
/// on the same frame instead of a round trip plus up to 50&nbsp;ms later.</item>
/// <item><b>Under the world gate</b>, when the own ship's <c>ServerShipUpdate</c>
/// changes (detected via <see cref="ShipEntity.LastUpdateTick"/>), call
/// <see cref="FeedUpdate"/> to reconcile the prediction toward the authoritative
/// stream.</item>
/// <item><b>Hard transitions</b> (dock/undock/launch, warp/sector change, ripcord,
/// death, lifepod, teleport): the host calls <see cref="HardReset"/> to hold the
/// server pose and <see cref="Resume"/> once the transition settles, so the
/// predictor re-seeds from the next server pose.</item>
/// </list>
/// Because the class depends only on <c>Allegiance.Core</c> (no MonoGame types), the
/// P447b wiring contract — <em>"change the sent controls ⇒ the render pose responds
/// on the same frame, whereas the raw path waits for the next server update"</em> —
/// is unit-testable against a fake/loopback session without constructing a
/// <c>Game1</c>.
/// </summary>
public sealed class OwnShipPredictionTransport
{
    /// <summary>The wrapped predictor (P447a).</summary>
    public OwnShipPredictor Predictor { get; } = new();

    /// <summary>
    /// P447b — the kill switch (the <c>liveSettings.ownShipPrediction</c>-style flag,
    /// a client-only launch toggle; default <b>ON</b>). When false, <see cref="Active"/>
    /// is false (the host renders the raw server pose) and <see cref="Step"/> is inert
    /// — a full return to today's pre-prediction behaviour.
    /// </summary>
    public bool Enabled = true;

    private ShipDeadReckoner.HullStats _hull;
    private float _afterburnerMaxThrust;
    private bool _haveHull;

    /// <summary>Whether a hull has been configured via <see cref="SetHull"/> yet.</summary>
    public bool HaveHull => _haveHull;

    /// <summary>Whether the predictor is currently frozen (a hard transition in
    /// progress). While frozen, <see cref="Step"/> is inert and the displayed pose
    /// holds the server pose until <see cref="Resume"/>.</summary>
    public bool Frozen => Predictor.Frozen;

    /// <summary>
    /// Whether the host should render the <b>predicted</b> pose this frame: prediction
    /// is enabled AND a hull is known. When false the host renders the raw server pose
    /// (kill switch off, or no hull resolved yet). While frozen this still returns true
    /// when a hull is known — the frozen predicted pose equals the held server pose, so
    /// the pose consumers keep reading one consistent source.
    /// </summary>
    public bool Active => Enabled && _haveHull;

    /// <summary>The predicted position.</summary>
    public Vector3 Position => Predictor.Position;

    /// <summary>The predicted velocity (feeds the HUD speed readout).</summary>
    public Vector3 Velocity => Predictor.Velocity;

    /// <summary>A copy of the predicted orientation.</summary>
    public Orientation Orientation => Predictor.PredictedOrientation;

    /// <summary>
    /// Configure the own ship's already-effective hull stats + afterburner max thrust
    /// (the values the prediction integrates) and establish the hull. Call when the own
    /// hull / side GA / loadout changes (respawn, lifepod, hull swap); OnServerUpdate
    /// refreshes afterburner power from each authoritative update, so the host must
    /// NOT re-call this every frame (that would reset the afterburner power to 0).
    /// </summary>
    public void SetHull(in ShipDeadReckoner.HullStats hull, float afterburnerMaxThrust)
    {
        _hull = hull;
        _afterburnerMaxThrust = afterburnerMaxThrust;
        _haveHull = true;
        Predictor.SetHull(in hull, afterburnerMaxThrust);
    }

    /// <summary>
    /// Advance the prediction one render frame with the <b>exact</b> controls the
    /// client sent that frame (manual or autopilot — whatever went through
    /// <c>CommandControls</c>). Inert while disabled (<see cref="Enabled"/>) or frozen.
    /// </summary>
    public void Step(float dt, ControlData controls, int stateM)
    {
        if (!Enabled) return;
        Predictor.Step(dt, controls, stateM);
    }

    /// <summary>
    /// Feed a newly-arrived authoritative update for the own ship and reconcile it,
    /// anchored at the update's local arrival time (<see cref="ShipEntity.LatestArrivalTime"/>)
    /// and extrapolated to <paramref name="now"/> on the same monotonic clock
    /// (<c>ClientWorld.NowSeconds</c>). Requires a configured hull; inert while disabled.
    /// </summary>
    public void FeedUpdate(ServerShipUpdate update, double arrivalTime, double now)
    {
        if (!Enabled) return;
        if (!_haveHull) return;
        Predictor.OnServerUpdate(update, in _hull, _afterburnerMaxThrust, arrivalTime, now);
    }

    /// <summary>Snap to a server pose and freeze prediction (a hard transition).</summary>
    public void HardReset(Vector3 position, Vector3 velocity, Orientation orientation, float[] turnRates)
        => Predictor.HardReset(position, velocity, orientation, turnRates);

    /// <summary>Clear the freeze and resume prediction from the current pose.</summary>
    public void Resume() => Predictor.Resume();
}

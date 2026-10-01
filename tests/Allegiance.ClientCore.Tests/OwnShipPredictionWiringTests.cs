using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P447b — client-level wiring test for the own-ship predictor, driven by a
/// fake/loopback session exactly as <see cref="OwnShipPredictorTests"/> does, but
/// exercising the production transport the MonoGame client actually uses
/// (<see cref="OwnShipPredictionTransport"/>), not a test-local re-implementation.
/// The must-fail claim is the P447b wiring contract:
/// <list type="number">
/// <item><b>Prediction ON:</b> change the sent controls and the local RENDER pose
/// responds within the same frame (the transport is Stepped each frame with the very
/// controls that go through CommandControls), and</item>
/// <item><b>Prediction OFF (the kill switch / today's behaviour):</b> the same control
/// change moves the render pose only after the next server update completes a full
/// round trip.</item>
/// </list>
/// Both are asserted in the first test; the second nails down that the kill switch
/// returns the render pose to the raw server pose.
/// </summary>
public class OwnShipPredictionWiringTests
{
    private readonly ITestOutputHelper _output;
    public OwnShipPredictionWiringTests(ITestOutputHelper o) => _output = o;

    private static readonly DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f],
        TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 545, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 511f, HardpointOffset: 581,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private const short ShipId = 988;

    /// <summary>
    /// A loopback authoritative server driving the PRODUCTION transport the way the
    /// MonoGame client drives it: each frame the client sends controls (they reach the
    /// server RTT/2 later), the server integrates at 20 Hz and publishes updates that
    /// reach the client RTT/2 later; the transport is Stepped each frame with the exact
    /// sent controls and is Fed each newly-delivered update. <see cref="RenderPos"/>/
    /// <see cref="RenderOri"/> are the local RENDER pose the client draws — the
    /// predicted pose when the transport is active, else the raw last-delivered
    /// server pose (the pre-P447b behaviour).
    /// </summary>
    private sealed class Sim
    {
        public const float Tick = 0.05f;      // 20 Hz server (server.json:5)
        public const float Frame = 0.0125f;   // 80 fps render resolution
        public readonly Ship Ship;
        public readonly OwnShipPredictionTransport Pred = new();
        public readonly float RttHalf;
        public double T;

        public ControlData Sent = ControlData.Neutral;
        public int SentM;

        /// <summary>The most recently DELIVERED server update — the raw pose source.</summary>
        public ServerShipUpdate? LastDelivered;

        private readonly ShipDeadReckoner.HullStats _hull;
        private readonly Queue<(double at, ControlData c, int m)> _toServer = new();
        private readonly Queue<(double at, ServerShipUpdate u)> _toClient = new();
        private double _nextTick = Tick;

        public Sim(Ship ship, float rttHalf, bool enabled)
        {
            Ship = ship;
            RttHalf = rttHalf;
            Pred.Enabled = enabled;   // the P447b kill switch (ON: predict; OFF: raw)
            _hull = ShipDeadReckoner.HullStats.FromHull(Hull);

            // Establish the hull and lock onto the ship's initial authoritative pose.
            var init = ServerShipUpdate.FromModel(ShipId, ship, 900000u);
            Pred.SetHull(_hull, 0f);
            Pred.FeedUpdate(init, T, T);
            LastDelivered = init;
        }

        public void Command(ControlData c, int m = 0) { Sent = c; SentM = m; }

        public void Advance()
        {
            T += Frame;

            // This frame's controls are on the wire to the server, arriving RTT/2 later.
            _toServer.Enqueue((T + RttHalf, Sent, SentM));
            while (_toServer.Count > 0 && _toServer.Peek().at <= T)
            {
                var (_, c, m) = _toServer.Dequeue();
                Ship.Controls = c;
                Ship.StateM = m;
            }

            // Run any due 20 Hz server tick and publish an update.
            while (_nextTick <= T + 1e-9)
            {
                double t0 = _nextTick - Tick, t1 = _nextTick;
                Ship.ExecuteMove(t0, t1);
                Ship.Position += Ship.Velocity * Tick;
                var up = ServerShipUpdate.FromModel(ShipId, Ship, (uint)(t1 * 20.0));
                _toClient.Enqueue((t1 + RttHalf, up));
                _nextTick += Tick;
            }

            // Deliver updates that have reached the client; feed the transport.
            while (_toClient.Count > 0 && _toClient.Peek().at <= T)
            {
                var (_, u) = _toClient.Dequeue();
                LastDelivered = u;
                Pred.FeedUpdate(u, T, T);
            }

            // Step the prediction with this frame's exact sent controls (P447b wiring).
            Pred.Step(Frame, Sent, SentM);
        }

        public void Run(double seconds)
        {
            int frames = (int)Math.Round(seconds / Frame);
            for (int i = 0; i < frames; i++) Advance();
        }

        // The local RENDER pose the client draws (what Game1._poses[me] reads):
        // predicted when the transport is active, raw server pose otherwise.
        public Vector3 RenderPos => Pred.Active ? Pred.Position : LastDelivered!.Position;
        public Orientation RenderOri => Pred.Active ? Pred.Orientation : LastDelivered!.Orientation;
    }

    private static float FwdAngleDiff(Vector3 fa, Vector3 fb)
    {
        float dot = Math.Clamp(fa.X * fb.X + fa.Y * fb.Y + fa.Z * fb.Z, -1f, 1f);
        return MathF.Acos(dot);   // radians
    }

    [Fact]
    public void ControlChange_MovesRenderPoseSameFrame_PredictionOffWaitsForNextUpdate()
    {
        // Straight level cruise; both the predicted and the kill-switch-off (raw) sims
        // start from the identical authoritative ship so the comparison is apples-to-apples.
        float throttle = 0.604f;
        var ori = Orientation.Identity;
        // Both sims carry the SAME realistic RTT so the comparison isolates prediction:
        // the predicted render pose reacts within a frame, while the raw/kill-switch
        // render pose cannot react before a full round trip (RTT/2 up + tick + RTT/2 back).
        var predicted = new Sim(new Ship(ShipId, Hull, new Vector3(0f, 0f, 6280f), Vector3.Zero, ori),
            rttHalf: 0.033f, enabled: true);    // RTT = 66 ms, prediction ON
        var raw = new Sim(new Ship(ShipId, Hull, new Vector3(0f, 0f, 6290f), Vector3.Zero, ori),
            rttHalf: 0.033f, enabled: false);   // kill switch OFF (raw pose), same RTT

        predicted.Command(new ControlData(0f, 0f, 0f, throttle));
        raw.Command(new ControlData(0f, 0f, 0f, throttle));
        predicted.Run(1.0);
        raw.Run(1.0);

        var prePred = predicted.RenderOri.GetForward();
        var preRaw = raw.RenderOri.GetForward();

        // The player commands HARD RIGHT yaw.
        double predStart = predicted.T;
        double rawStart = raw.T;
        predicted.Command(new ControlData(Yaw: 1f, 0f, 0f, throttle));
        raw.Command(new ControlData(Yaw: 1f, 0f, 0f, throttle));

        // Measure when each render pose first departs its pre-turn facing. The predicted
        // path is polled every frame; the raw/off path only moves when an update lands.
        double predictedResponse = double.PositiveInfinity;
        double rawResponse = double.PositiveInfinity;
        double end = predicted.T + 0.30;
        while (predicted.T < end)
        {
            predicted.Advance();
            raw.Advance();
            if (double.IsPositiveInfinity(predictedResponse) &&
                FwdAngleDiff(prePred, predicted.RenderOri.GetForward()) > 1e-3f)
                predictedResponse = predicted.T - predStart;
            if (double.IsPositiveInfinity(rawResponse) &&
                FwdAngleDiff(preRaw, raw.RenderOri.GetForward()) > 1e-3f)
                rawResponse = raw.T - rawStart;
        }

        _output.WriteLine($"RTT(pred)={2f * predicted.RttHalf * 1000f:F0} ms");
        _output.WriteLine($"PREDICTED render-pose response time = {predictedResponse * 1000f:F1} ms (reacts on the same/next frame, 80 fps)");
        _output.WriteLine($"RAW/kill-switch render-pose response = {rawResponse * 1000f:F1} ms (waits for the next server update / round trip)");

        // The wiring contract: with prediction ON the render pose moves on the same
        // frame the controls are sent (within ~a frame).
        Assert.True(predictedResponse <= Sim.Frame * 1.5,
            $"prediction-ON render pose must respond on the same frame (got {predictedResponse * 1000f:F1} ms)");

        // With prediction OFF (today's behaviour) the render pose can only move when a
        // server update completes a round trip — strictly later than the predicted pose.
        Assert.True(rawResponse > predictedResponse + 1e-6,
            $"kill-switch render pose must lag the predicted pose (raw {rawResponse * 1000f:F1} ms vs pred {predictedResponse * 1000f:F1} ms)");
        Assert.True(rawResponse >= 2f * raw.RttHalf - 1e-6,
            $"kill-switch render pose cannot beat the round trip (got {rawResponse * 1000f:F1} ms, RTT={2f * raw.RttHalf * 1000f:F0} ms)");
    }

    [Fact]
    public void KillSwitchOff_ReturnsRenderPoseToRawServerPose()
    {
        // Prediction OFF: the transport is inert and the render pose must track the raw
        // delivered server pose exactly (returns to today's behaviour), never a predicted
        // pose, even though the controls command a turn.
        float throttle = 0.588f;
        var ship = new Ship(ShipId, Hull, new Vector3(0f, 0f, 8100f), Vector3.Zero, Orientation.Identity);
        var sim = new Sim(ship, rttHalf: 0.020f, enabled: false);
        sim.Command(new ControlData(0f, 0f, 0f, throttle));
        sim.Run(0.5);

        // Exercise a commanded turn under the disabled transport.
        sim.Command(new ControlData(Yaw: 1f, 0f, 0f, throttle));
        sim.Run(0.5);

        Assert.False(sim.Pred.Active, "kill switch OFF must deactivate prediction");
        Assert.Equal(sim.LastDelivered!.Position.X, sim.RenderPos.X, 5);
        Assert.Equal(sim.LastDelivered.Position.Z, sim.RenderPos.Z, 5);
        Assert.Equal(0f, FwdAngleDiff(sim.LastDelivered.Orientation.GetForward(), sim.RenderOri.GetForward()), 5);
    }
}

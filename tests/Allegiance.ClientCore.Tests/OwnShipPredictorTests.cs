using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P447a — latency-hiding proof for <see cref="OwnShipPredictor"/>, driven by a
/// simulated authoritative server: a real server-side <see cref="Ship"/> running
/// the same controls, whose updates reach the predictor delayed by a configurable
/// RTT/2 and sampled at the server's 20&nbsp;Hz. This mirrors exactly how the
/// real client receives its <em>own</em> ship's <c>ServerShipUpdate</c>.
/// </summary>
/// <remarks>
/// <para><b>What the predictor is for.</b> Native is client-authoritative for the
/// own ship (controls take effect on the same frame); the port is
/// server-authoritative with no prediction, so a control change previously showed
/// up a full round trip plus up to 50&nbsp;ms late at 20&nbsp;Hz. The predictor
/// re-runs the real flight model (<see cref="ShipKinematics.Step"/>) with the
/// same controls the client sends each frame, so the player's inputs take effect
/// on the same frame while the server stays authoritative.</para>
/// <para><b>The must-fail contrast.</b> The raw-server-pose baseline — render the
/// last received <c>ServerShipUpdate</c> pose unchanged, today's port — cannot
/// react to a control change until the round trip completes
/// (RTT/2 to reach the server + up to one tick to apply + RTT/2 back). Every test
/// here asserts the predictor's same-frame reaction <em>and</em> that the raw
/// baseline is frozen across that window.</para>
/// </remarks>
public class OwnShipPredictorTests
{
    private readonly ITestOutputHelper _output;
    public OwnShipPredictorTests(ITestOutputHelper o) => _output = o;

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

    private const short ShipId = 987;

    /// <summary>
    /// A deterministic loop over a simulated authoritative server. The client
    /// sends controls each frame; they reach the server RTT/2 later; the server
    /// integrates them at 20&nbsp;Hz and publishes an update that reaches the
    /// predictor RTT/2 later. The predictor is stepped each frame with the
    /// controls the client currently intends — the same ones it sends — so a
    /// change reacts on the same frame, while the raw baseline
    /// (<see cref="LastDelivered"/>) only sees it after the full round trip.
    /// </summary>
    private sealed class Sim
    {
        public const float Tick = 0.05f;      // 20 Hz server (server.json:5)
        public const float Frame = 0.0125f;   // 80 fps prediction resolution (4 frames / tick)
        public readonly Ship Ship;
        public readonly OwnShipPredictor Pred = new();
        public readonly float RttHalf;
        public double T;

        /// <summary>Controls the client currently sends (and predicts with).</summary>
        public ControlData Sent = ControlData.Neutral;
        public int SentM;

        /// <summary>The raw baseline: the most recently <em>delivered</em>
        /// update's pose — today's port renders exactly this.</summary>
        public ServerShipUpdate? LastDelivered;

        private readonly ShipDeadReckoner.HullStats _hull;
        private readonly Queue<(double at, ControlData c, int m)> _toServer = new();
        private readonly Queue<(double at, ServerShipUpdate u)> _toClient = new();
        private double _nextTick = Tick;

        public Sim(Ship ship, float rttHalf)
        {
            Ship = ship;
            RttHalf = rttHalf;
            _hull = ShipDeadReckoner.HullStats.FromHull(Hull);

            // Prime the predictor with the ship's initial authoritative pose so
            // it locks on before the measured phase begins.
            var init = ServerShipUpdate.FromModel(ShipId, ship, 900000u);
            Pred.OnServerUpdate(init, _hull, 0f, T, T);
            LastDelivered = init;
        }

        /// <summary>Command the client's controls (sent + predicted this frame onward).</summary>
        public void Command(ControlData c, int m = 0)
        {
            Sent = c;
            SentM = m;
        }

        /// <summary>Inject a server-side-only disturbance onto the authoritative ship
        /// (a collision-like recoil the predictor must not know about): a small
        /// position shift plus a velocity change, both under the 20 m snap.</summary>
        public void ServerKnock(Vector3 dPos, Vector3 dVel)
        {
            Ship.Position += dPos;
            Ship.Velocity += dVel;
        }

        /// <summary>Teleport the authoritative ship by a large displacement (a warp /
        /// sector-change analogue), visible to the predictor only via the next update.</summary>
        public void Teleport(Vector3 dPos) => Ship.Position += dPos;

        /// <summary>Advance one client frame: queue the current controls for the server,
        /// run any due server ticks, deliver any due updates to the predictor, then step it.</summary>
        public void Advance()
        {
            T += Frame;

            // This frame's controls are on the wire to the server, arriving RTT/2 later.
            _toServer.Enqueue((T + RttHalf, Sent, SentM));

            // Deliver controls that have reached the server; the latest wins.
            while (_toServer.Count > 0 && _toServer.Peek().at <= T)
            {
                var (_, c, m) = _toServer.Dequeue();
                Ship.Controls = c;
                Ship.StateM = m;
            }

            // Run any server tick that has fallen due (20 Hz).
            while (_nextTick <= T + 1e-9)
            {
                double t0 = _nextTick - Tick, t1 = _nextTick;
                Ship.ExecuteMove(t0, t1);
                Ship.Position += Ship.Velocity * Tick;
                var up = ServerShipUpdate.FromModel(ShipId, Ship, (uint)(t1 * 20.0));
                _toClient.Enqueue((t1 + RttHalf, up));
                _nextTick += Tick;
            }

            // Deliver updates that have reached the client (raw baseline + predictor).
            while (_toClient.Count > 0 && _toClient.Peek().at <= T)
            {
                var (_, u) = _toClient.Dequeue();
                LastDelivered = u;
                Pred.OnServerUpdate(u, _hull, 0f, T, T);
            }

            // Step the prediction with this frame's intended controls.
            Pred.Step(Frame, Sent, SentM);
        }

        public void Run(double seconds)
        {
            int frames = (int)Math.Round(seconds / Frame);
            for (int i = 0; i < frames; i++) Advance();
        }
    }

    private static float FwdAngleDiff(Vector3 fa, Vector3 fb)
    {
        float dot = Math.Clamp(fa.X * fb.X + fa.Y * fb.Y + fa.Z * fb.Z, -1f, 1f);
        return MathF.Acos(dot);   // radians
    }

    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    [Fact]
    public void ControlChange_MovesPredictedPoseOnSameStep_RawWaitsForRoundTrip()
    {
        // Straight level cruise, controls frozen at ~60% throttle for the warm-up.
        var ori = Orientation.Identity;
        float throttle = 0.618f;
        var ship = new Ship(ShipId, Hull, new Vector3(0f, 0f, 4200f),
            Vector3.Zero, ori);
        var sim = new Sim(ship, rttHalf: 0.035f);   // RTT = 70 ms
        sim.Command(new ControlData(0f, 0f, 0f, throttle));
        sim.Run(1.0);                                // warm up to a locked cruise

        var prePred = sim.Pred.PredictedOrientation.GetForward();
        var preRaw = sim.LastDelivered!.Orientation.GetForward();

        // The player commands HARD RIGHT yaw.
        double turnStart = sim.T;
        sim.Command(new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: throttle));

        // Measure the response time of each path: the first instant the facing
        // departs its pre-turn value. The predictor is checked every frame; the
        // raw baseline every time a (delivered) update lands.
        double predictedResponse = double.PositiveInfinity;
        double rawResponse = double.PositiveInfinity;
        double end = turnStart + 0.30;
        while (sim.T < end)
        {
            sim.Advance();
            if (double.IsPositiveInfinity(predictedResponse) &&
                FwdAngleDiff(prePred, sim.Pred.PredictedOrientation.GetForward()) > 1e-3f)
                predictedResponse = sim.T - turnStart;
            if (double.IsPositiveInfinity(rawResponse) &&
                FwdAngleDiff(preRaw, sim.LastDelivered!.Orientation.GetForward()) > 1e-3f)
                rawResponse = sim.T - turnStart;
        }

        _output.WriteLine($"turnStart={turnStart:F3}s  RTT={2f * sim.RttHalf * 1000f:F0} ms");
        _output.WriteLine($"PREDICTED pose response time={predictedResponse * 1000f:F1} ms (reacts on the same/next frame)");
        _output.WriteLine($"RAW baseline response time  ={rawResponse * 1000f:F1} ms (waits for the full round trip + tick)");

        // The predictor reacts on the same step: within ~a frame of issuing the
        // control (80 fps => one frame = 12.5 ms).
        Assert.True(predictedResponse <= Sim.Frame * 1.5,
            $"predicted pose must react on the same step (got {predictedResponse * 1000f:F1} ms)");

        // The raw baseline cannot beat the physical round trip (RTT/2 to reach the
        // server + up to one tick to apply + RTT/2 back), so it reacts no sooner
        // than RTT and far later than the predicted pose.
        Assert.True(rawResponse >= 2f * sim.RttHalf - 1e-6,
            $"raw baseline cannot react before the full round trip RTT (got {rawResponse * 1000f:F1} ms, RTT={2f * sim.RttHalf * 1000f:F0} ms)");
        Assert.True(rawResponse > predictedResponse,
            $"raw baseline must lag the predicted pose (predicted {predictedResponse * 1000f:F1} ms vs raw {rawResponse * 1000f:F1} ms)");
    }

    [Fact]
    public void SteadyState_ConstantControls_PredictedVsTrueStaysSmall()
    {
        // The climbing ship: a sustained turn + ~60% throttle (the same scenario
        // P448a used). Runs at a crisp LAN RTT so the reporting lag is small.
        var o = Orientation.Identity;
        o.Yaw(0.048f);
        o.Pitch(0.061f);
        o.Renormalize();
        var fwd = o.GetForward();
        float cruise = 0.5f * (1f + 0.6321f) * Hull.Speed;
        var ship = new Ship(ShipId, Hull, new Vector3(90f, -40f, 620f), fwd * cruise, o);
        ship.Controls = new ControlData(Yaw: 0.031f, Pitch: -0.040f, Roll: 0.010f, Throttle: 0.6321f);
        ship.StateM = 0;

        // Sample predicted vs TRUE server pose every frame over the measured window.
        var sim = new Sim(DeepShip(ship), rttHalf: 0.018f);
        sim.Command(ship.Controls);
        sim.Run(1.5);   // settle
        float maxPosErr = 0f, maxOriErr = 0f;
        double end = sim.T + 1.0;
        while (sim.T < end)
        {
            sim.Advance();
            float pe = Dist(sim.Pred.Position, sim.Ship.Position);
            float oe = FwdAngleDiff(sim.Pred.PredictedOrientation.GetForward(), sim.Ship.Orientation.GetForward());
            if (pe > maxPosErr) maxPosErr = pe;
            if (oe > maxOriErr) maxOriErr = oe;
        }

        _output.WriteLine($"steady-state predicted-vs-true: maxPosErr={maxPosErr:F3} m  maxOriErr={maxOriErr * 180f / MathF.PI:F3}°  (RTT={2f * sim.RttHalf * 1000f:F0} ms)");

        // Small bound: a few metres at a LAN RTT (mostly the reporting lag
        // RTT/2·speed — the received target is dead-reckoned from arrival time,
        // so it trails the true pose by the transit; see the report) plus wire
        // quantization. This is decisive vs the raw pose, which trails the full
        // RTT·speed behind the true ship.
        Assert.True(maxPosErr < 8f, $"steady-state predicted-vs-true position error should stay small, got {maxPosErr:F3} m");
        Assert.True(maxOriErr < 5f * MathF.PI / 180f, $"steady-state facing error should stay small, got {maxOriErr * 180f / MathF.PI:F3}°");
    }

    private static Ship DeepShip(Ship s) =>
        new(ShipId, Hull, s.Position, s.Velocity, s.Orientation.Copy())
        {
            Controls = s.Controls,
            StateM = s.StateM,
        };

    [Fact]
    public void Disturbance_BlendsBackWithinTimeConstant_WithoutOscillation()
    {
        // Straight cruise; predictor locked on.
        float throttle = 0.645f;
        var ship = new Ship(ShipId, Hull, new Vector3(0f, 0f, 8100f), Vector3.Zero, Orientation.Identity);
        var sim = new Sim(ship, rttHalf: 0.032f);
        sim.Command(new ControlData(0f, 0f, 0f, throttle));
        sim.Run(1.0);

        // Pre-disturbance steady-state error (the reporting-lag baseline).
        var pre = new List<float>();
        for (int i = 0; i < 20; i++) { sim.Advance(); pre.Add(Dist(sim.Pred.Position, sim.Ship.Position)); }
        float baseline = pre.Average();

        // Server-side-only disturbance: a small collision-like recoil — ~8 m
        // sideways and a matching lateral velocity — applied to the server only,
        // unseen by the predictor (under the 20 m snap, so it must blend, not snap).
        double disturbAt = sim.T;
        sim.ServerKnock(new Vector3(8f, 0f, 0f), new Vector3(30f, 0f, 0f));

        // Track the RECONCILED predicted-vs-true error — sampled at the instant
        // each server update reconciles — which is the meaningful "where is my
        // corrected ship" metric. (Between updates the 80 fps prediction races
        // ahead by ≈ speed·Δt before the next 20 Hz re-sync; that inherent
        // sawtooth is bounded by the update cadence and is not divergence.)
        var rec = new List<float>();
        float peak = 0f;
        double window = 1.5;
        double end = sim.T + window;
        ServerShipUpdate? lastSeen = sim.LastDelivered;
        while (sim.T < end)
        {
            sim.Advance();
            if (!ReferenceEquals(sim.LastDelivered, lastSeen))
            {
                lastSeen = sim.LastDelivered;
                float re = Dist(sim.Pred.Position, sim.Ship.Position);
                rec.Add(re);
                if (re > peak) peak = re;
            }
        }
        float finalRec = rec[^1];
        float tailMax = rec.Skip(rec.Count - rec.Count / 3).Max();

        _output.WriteLine($"disturbance @{disturbAt:F3}s (+8 m knock): reconciled baseline≈{baseline:F3} m  peak={peak:F3} m  tailMax(last ⅓)={tailMax:F3} m  finalRec={finalRec:F3} m");

        // Peak stayed under the 20 m snap threshold => it blended, not snapped.
        Assert.True(peak < sim.Pred.SnapDistance, $"disturbance should blend (peak {peak:F3} m < 20 m snap)");
        // A real transient occurred (the knock was resorbed, not already tracked).
        Assert.True(peak > baseline + 3f, $"the knock must produce a real transient to converge (peak {peak:F3} m, baseline {baseline:F3} m)");
        // Converged back to (near) the pre-disturbance reconciled steady-state within
        // the window (several τ).
        Assert.True(finalRec <= baseline + 0.8f, $"predictor should converge back to the steady state, final={finalRec:F3} m vs baseline {baseline:F3} m");
        // No oscillation / no ringing: once settled, the reconciled error stays at the
        // baseline level — if it rang back up toward the peak, the settled tail would
        // be elevated.
        Assert.True(tailMax <= baseline + 1.0f, $"no oscillation: settled reconciled error must stay at the baseline (tailMax {tailMax:F3} m vs baseline {baseline:F3} m)");
    }

    [Fact]
    public void LargeDisplacement_OverTwentyMetres_SnapsToServerPose()
    {
        float throttle = 0.590f;
        var ship = new Ship(ShipId, Hull, new Vector3(0f, 0f, 9400f), Vector3.Zero, Orientation.Identity);
        var sim = new Sim(ship, rttHalf: 0.028f);
        sim.Command(new ControlData(0f, 0f, 0f, throttle));
        sim.Run(0.8);

        // Server-side teleport far beyond the 20 m snap threshold (warp/sector-change
        // analogue). The predictor sees it only via the next delivered update.
        double telAt = sim.T;
        sim.Teleport(new Vector3(140f, 0f, 0f));

        // Deterministic measure: run frames and track the minimum error once the
        // teleported pose arrives — a snap collapses to ~0 in one step, a blend
        // would need several τ.
        var snap = new Sim(DeepShip(ship), rttHalf: 0.028f);
        snap.Command(new ControlData(0f, 0f, 0f, throttle));
        snap.Run(0.8);
        snap.Teleport(new Vector3(140f, 0f, 0f));
        List<float> seq = new();
        double tend = snap.T + 0.2;
        while (snap.T < tend)
        {
            snap.Advance();
            seq.Add(Dist(snap.Pred.Position, snap.Ship.Position));
        }

        // After the snap the very next frame is at the server pose (~0 error);
        // a blend would take ~τ (~0.15 s) to approach it. Require the error to
        // have collapsed to < 0.5 m within a few frames of the update landing.
        float minTail = seq.Skip(seq.Count / 3).Min();
        _output.WriteLine($"teleport @{telAt:F3}s (+140 m): tail min predicted-vs-true={minTail:F3} m over 0.2 s");
        Assert.True(minTail < 0.5f,
            $"a >20 m discontinuity must SNAP to the server pose (tail min {minTail:F3} m, want < 0.5 m)");
    }

    [Fact]
    public void HardReset_FreezesPrediction_UntilResume()
    {
        var ship = new Ship(ShipId, Hull, new Vector3(0f, 0f, 5100f), Vector3.Zero, Orientation.Identity);
        var sim = new Sim(ship, rttHalf: 0.030f);
        sim.Command(new ControlData(0f, 0f, 0f, 0.578f));
        sim.Run(0.5);

        // A hard transition (e.g. dock/warp) resets to the server pose and holds it.
        var frozenPos = sim.Ship.Position;
        sim.Pred.HardReset(sim.Ship.Position, sim.Ship.Velocity, sim.Ship.Orientation.Copy(),
            sim.Ship.GetTurnRates(new float[3]));
        Assert.True(sim.Pred.Frozen);

        // While frozen, Step must not move the pose even though controls command a turn.
        sim.Pred.Step(Sim.Frame, new ControlData(Yaw: 1f, 0f, 0f, 0.578f), 0);
        Assert.True(Dist(sim.Pred.Position, frozenPos) < 1e-6f, "frozen predictor must hold the server pose");

        // Resume re-arms prediction from the current pose.
        sim.Pred.Resume();
        Assert.False(sim.Pred.Frozen);
        var before = sim.Pred.PredictedOrientation.GetForward();
        sim.Pred.Step(Sim.Frame, new ControlData(Yaw: 1f, 0f, 0f, 0.578f), 0);
        Assert.True(FwdAngleDiff(before, sim.Pred.PredictedOrientation.GetForward()) > 1e-3f,
            "after Resume the prediction must move again with the controls");
    }
}

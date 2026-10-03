using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P505 — failing-first tests for the own-ship prediction DECELERATION bias
/// (the micro-stutter Domingo saw on 2026-10-02, P502 build): when the cruise
/// throttle is wheeled down below the current speed, the predicted pose and the
/// authoritative server pose disagree by ~15-23 m continuously (straddling the
/// 20 m snap threshold), producing repeated hard snaps — the stutter. Plus the
/// self-target exclusion bug.
///
/// Reproduced headlessly with P502's harness: a real server-side Ship integrated
/// at 20 Hz whose updates reach the predictor RTT/2 late, client controls encoded
/// through the real wire (quantized), the predictor stepping with the same
/// controls the client sends each frame.
/// </summary>
public class P505DecelPredictionTests
{
    private readonly ITestOutputHelper _o;
    public P505DecelPredictionTests(ITestOutputHelper o) => _o = o;

    private static readonly DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f],
        TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 813, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 511f, HardpointOffset: 819,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private const short ShipId = 8117;

    /// <summary>
    /// Faithful loop over a simulated authoritative server (P502's Sim), extended
    /// to reproduce the DECELERATION regime: the predictor is fed each update with a
    /// realistic now&gt;arrivalTime extrapolation gap (the real client feeds
    /// FeedUpdate(up, LatestArrivalTime, world.NowSeconds()), now&gt;arrival), and the
    /// server applies the wire-QUANTIZED controls while the predictor steps with the
    /// RAW controls the client sent that frame — exactly the live asymmetry.
    /// </summary>
    private sealed class Sim
    {
        public const float Tick = 0.05f;
        public readonly Ship Ship;
        public readonly OwnShipPredictor Pred = new();
        public readonly float RttHalf;
        public double T;
        public bool ExtrapolateTo = true;
        public double ExtraFeedDelay = 0.0;

        public ControlData Sent = ControlData.Neutral;
        public int SentM;

        private readonly ShipDeadReckoner.HullStats _hull;
        private readonly Queue<(double at, ControlData c, int m)> _toServer = new();
        private readonly Queue<(double at, ServerShipUpdate u)> _toClient = new();
        private double _nextTick = Tick;

        public Sim(Ship ship, float rttHalf)
        {
            Ship = ship;
            RttHalf = rttHalf;
            _hull = ShipDeadReckoner.HullStats.FromHull(Hull);
            var init = ServerShipUpdate.FromModel(ShipId, ship, 909000u);
            Pred.OnServerUpdate(init, _hull, 0f, T, T);
        }

        public void Command(ControlData c, int m = 0) { Sent = c; SentM = m; }

        public void Advance(float frame)
        {
            T += frame;
            _toServer.Enqueue((T + RttHalf, Sent, SentM));

            while (_toServer.Count > 0 && _toServer.Peek().at <= T)
            {
                var (_, c, m) = _toServer.Dequeue();
                // Real wire path: encode then decode -> the quantized controls the server applies.
                var wire = ClientShipUpdate.FromControls(0u, m, c);
                Ship.Controls = wire.Controls;
                Ship.StateM = wire.StateM;
            }

            while (_nextTick <= T + 1e-9)
            {
                double t0 = _nextTick - Tick, t1 = _nextTick;
                Ship.ExecuteMove(t0, t1);
                Ship.Position += Ship.Velocity * Tick;
                var up = ServerShipUpdate.FromModel(ShipId, Ship, (uint)(t1 * 20.0));
                _toClient.Enqueue((t1 + RttHalf, up));
                _nextTick += Tick;
            }

            // Real client order (Game1.FlightInput): the predictor is STEPPED with the
            // frame's chosen controls FIRST (FlightInput line ~1362), THEN the newly-
            // arrived authoritative updates are fed under the gate (Game1.Prediction).
            Pred.Step(frame, Sent, SentM);

            while (_toClient.Count > 0 && _toClient.Peek().at <= T)
            {
                var (at, u) = _toClient.Dequeue();
                // Real client: anchor at the update's arrival, extrapolate to NOW
                // (the current render time, >= arrival by up to a frame).
                double feedNow = ExtrapolateTo ? T + ExtraFeedDelay : at;
                Pred.OnServerUpdate(u, _hull, 0f, at, feedNow);
            }
        }
    }

    private static Ship NewCruisingShip(float throttle)
    {
        var o = Orientation.Identity;
        o.Pitch(0.011f); o.Renormalize();
        var fwd = o.GetForward();
        var cruise = 0.5f * (1f + throttle) * Hull.Speed;
        var ship = new Ship(ShipId, Hull, new Vector3(0f, 0f, 9400f), fwd * cruise, o);
        ship.Controls = new ControlData(0f, 0f, 0f, throttle);
        return ship;
    }

    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    private static void Warmup(Sim sim, double seconds)
        => Warmup(sim, seconds, 0.0167f);

    private static void Warmup(Sim sim, double seconds, float frameDt)
    {
        int frames = (int)Math.Round(seconds / frameDt);
        for (int i = 0; i < frames; i++) sim.Advance(frameDt);
        sim.Pred.TakeCorrectionStats();   // discard the initial prime snap from stats
    }

    /// <summary>
    /// MUST-FAIL (original): cruise at throttle 0.8, then wheel the throttle down to
    /// -0.8 and decelerate ~5 s (the live micro-stutter regime, stutter log lines
    /// ~215-250). Driven at a LOW render fps (~8 fps — the live client's asset/VFX
    /// loading dips it below ~20 fps), where the P502 one-tick Step cap discards
    /// physics and makes the prediction trail the server into the 20 m snap regime.
    /// The predictor and the authoritative target must stay in agreement: no hard
    /// snap may fire and the per-update reconciliation distance must stay well under
    /// the 20 m snap threshold throughout the deceleration.
    /// </summary>
    [Fact]
    public void CruiseToBrake_DecelProducesNoSnaps()
    {
        const float frame = 0.22f;                       // ~4.5 fps — live under-load low-fps regime
        var ship = NewCruisingShip(0.8f);
        var sim = new Sim(ship, rttHalf: 0.025f);        // ~50 ms RTT (LAN, as the live run)
        sim.Command(new ControlData(0f, 0f, 0f, 0.8f));
        Warmup(sim, 1.5f, frame);
        long snapsAtStart = sim.Pred.SnapCount;   // measure snaps across the WHOLE decel event incl. the wheel

        // Wheel the persistent throttle down to -0.8 in quick steps (stutter log lines 215-223),
        // then hold and decelerate for ~5 s.
        float[] steps = { 0.6f, 0.4f, 0.2f, 0.0f, -0.2f, -0.4f, -0.6f, -0.8f };
        for (int s = 0; s < steps.Length; s++)
        {
            sim.Command(new ControlData(0f, 0f, 0f, steps[s]));
            for (int f = 0; f < 3; f++) sim.Advance(frame);
        }
        sim.Command(new ControlData(0f, 0f, 0f, -0.8f));

        double end = sim.T + 5.0;
        float maxErr = 0f;
        double meanErr = 0d;
        long updates = 0;
        while (sim.T < end)
        {
            sim.Advance(frame);
            var (cnt, mean, mx) = sim.Pred.TakeCorrectionStats();
            if (cnt == 0) continue;
            updates += cnt;
            meanErr += mean * cnt;
            if (mx > maxErr) maxErr = mx;
        }
        if (updates > 0) meanErr /= updates;

        long snaps = sim.Pred.SnapCount - snapsAtStart;
        _o.WriteLine($"decel 0.8->-0.8 @~{1.0/frame:F0}fps over 5s: snaps={snaps} absSnap={sim.Pred.SnapCount}  meanErr={meanErr:F2}m  maxErr={maxErr:F2}m");

        Assert.True(snaps == 0,
            $"the deceleration must not trip hard snaps (got {snaps} snaps, maxErr={maxErr:F2}m)");
        Assert.True(maxErr < 8f,
            $"the decel reconciliation error must stay well under the 20 m snap threshold (maxErr={maxErr:F2}m)");
    }

    /// <summary>
    /// The constant ~3.33 m steady-cruise offset (stutter log lines 253+): even in a
    /// straight line at fixed throttle the per-update reconciliation shows a small but
    /// constant offset. Bound it so it cannot straddle the snap threshold and is not a
    /// growing/random walk.
    /// </summary>
    [Fact]
    public void SteadyCruise_HasSmallBoundedOffset()
    {
        var ship = NewCruisingShip(1.0f);
        var sim = new Sim(ship, rttHalf: 0.020f);
        sim.Command(new ControlData(0f, 0f, 0f, 1.0f));
        Warmup(sim, 1.0f);

        double end = sim.T + 3.0;
        float maxErr = 0f;
        while (sim.T < end)
        {
            sim.Advance(0.0167f);
            var (cnt, _, mx) = sim.Pred.TakeCorrectionStats();
            if (mx > maxErr) maxErr = mx;
        }
        _o.WriteLine($"steady cruise over 3s: max correction = {maxErr:F2}m");
        Assert.True(maxErr < 4f,
            $"steady-cruise reconciliation should be small and bounded (max={maxErr:F2}m)");
    }
}

using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P502 — failing-first tests for the live own-ship prediction desync (the
/// post-undock left/right nose twitch Domingo saw on 2026-10-02).
///
/// Root cause: the predictor's ORIENTATION was never reconciled directly — the
/// original OnServerUpdate only blended turn-RATES toward each update (α of the
/// turn-rate delta), never corrected the predicted facing itself. During
/// sustained/rapid maneuvering the predicted facing therefore free-ran out of
/// agreement with the authoritative target and, combined with a render-frame dt
/// chunked to 0.25 s (so a hitch over-integrated a quarter-second of motion),
/// routinely crossed the 30° orientation snap — the visible twitching.
///
/// Fix: (1) converge the predicted orientation directly toward the authoritative
/// target each update (a geometric slerp by the same α as the position blend) so
/// the facing can never drift to the snap threshold in steady flight, and (2) cap
/// each Step's integration to one server tick (0.05 s) so a render hitch can no
/// longer over-integrate the nonlinear flight model past the server pose.
/// </summary>
public class P502PredictionDesyncTests
{
    private readonly ITestOutputHelper _o;
    public P502PredictionDesyncTests(ITestOutputHelper o) => _o = o;

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

    private const short ShipId = 977;

    /// <summary>
    /// A deterministic loop over a simulated authoritative server, faithful to the
    /// real client→server control path: the client encodes controls through
    /// <c>ClientShipUpdate.FromControls</c> (which QUANTIZES the axes), the server
    /// decodes and applies them, the predictor steps with the RAW controls, and
    /// each 20 Hz update reaches the predictor RTT/2 late. Supports render hitches
    /// (a large per-frame dt) — the live post-undock failure ingredient.
    /// </summary>
    private sealed class Sim
    {
        public const float Tick = 0.05f;
        public readonly Ship Ship;
        public readonly OwnShipPredictor Pred = new();
        public readonly float RttHalf;
        public double T;

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
            var init = ServerShipUpdate.FromModel(ShipId, ship, 989000u);
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

            while (_toClient.Count > 0 && _toClient.Peek().at <= T)
            {
                var (_, u) = _toClient.Dequeue();
                Pred.OnServerUpdate(u, _hull, 0f, T, T);
            }

            Pred.Step(frame, Sent, SentM);
        }
    }

    private static float AngleDeg(Vector3 a, Vector3 b)
    {
        float dot = Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }
    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    private static Ship NewCruisingShip()
    {
        var o = Orientation.Identity;
        o.Pitch(0.031f); o.Renormalize();
        var fwd = o.GetForward();
        var cruise = 0.5f * (1f + 0.982f) * Hull.Speed;
        var ship = new Ship(ShipId, Hull, new Vector3(0f, 0f, 9000f), fwd * cruise, o);
        ship.Controls = new ControlData(0f, 0f, 0f, 0.982f);
        return ship;
    }

    /// <summary>
    /// MUST-FAIL (original): during sustained oscillating maneuvering punctuated by
    /// render hitches — the exact live post-undock regime — the predicted facing
    /// must never diverge to the 30° orientation snap and no hard snap may fire.
    /// The original predictor (no direct orientation convergence, 0.25 s step
    /// chunking) trips 3 snaps with a 44° facing excursion here; the fixed
    /// predictor stays well under the threshold with 0 snaps.
    /// </summary>
    [Fact]
    public void ManeuveringWithRenderHitches_NoOrientationSnaps()
    {
        var ship = NewCruisingShip();
        var sim = new Sim(ship, rttHalf: 0.024f);
        sim.Command(new ControlData(0f, 0f, 0f, 0.982f));
        Warmup(sim, 1.0f);

        long snapsAtStart = sim.Pred.SnapCount;
        double end = sim.T + 3.0;
        double startT = sim.T;
        float maxAng = 0f;
        double nextHitch = startT + 0.7;
        while (sim.T < end)
        {
            double rel = sim.T - startT;
            float yaw = MathF.Sin((float)(rel * 4.0 * 2.0 * Math.PI));
            sim.Command(new ControlData(yaw, 0f, 0f, 0.982f));
            float frame = 1f / 60f;
            if (sim.T >= nextHitch) { frame = 0.19f; nextHitch = sim.T + 0.75; }
            sim.Advance(frame);
            float ang = AngleDeg(sim.Pred.PredictedOrientation.GetForward(), sim.Ship.Orientation.GetForward());
            if (ang > maxAng) maxAng = ang;
        }

        long snaps = sim.Pred.SnapCount - snapsAtStart;
        _o.WriteLine($"maneuvering+hitches over 3s: maxFacingDivergence={maxAng:F1}deg  snaps={snaps}");

        // No orientation snap may fire in steady flight / maneuvering (the twitch).
        Assert.True(snaps == 0, $"the predicted facing must not trip the 30° snap during maneuvering+hitches (got {snaps} snaps, maxFacingDivergence={maxAng:F1}deg)");
        // Facing stays comfortably below the 30° threshold (a few metres of lead at
        // LAN RTT is expected; > 30° is not).
        Assert.True(maxAng < 30f, $"predicted facing diverged past the snap threshold ({maxAng:F1}deg) in steady flight");
    }

    /// <summary>
    /// MUST-FAIL (original): a single render hitch (large per-frame dt) must never
    /// over-integrate the prediction PAST the real elapsed time — but it must also
    /// not UNDER-advance it (P505). P502 capped a Step to one server tick, which on
    /// a frame longer than one tick discarded physics and made the prediction trail
    /// the server below ~20 fps (the decel micro-stutter). P505 subdivides the FULL
    /// dt into ≤ one-tick substeps, so a single 0.2 s Step advances ~0.2·speed =
    /// ~24 m — exactly the real elapsed time the server covered — while no single
    /// integration substep exceeds one server tick. Assert the advance is within
    /// real time (not over-integrated PAST dt·speed) and not under-advanced either.
    /// </summary>
    [Fact]
    public void SingleRenderHitch_AdvancesFullFrame_InOneTickSubsteps()
    {
        var ship = NewCruisingShip();
        var sim = new Sim(ship, rttHalf: 0.010f);
        sim.Command(new ControlData(0f, 0f, 0f, 0.982f));
        Warmup(sim, 1.0f);

        // Isolate Step: one Step with a 0.20 s hitch dt, no new server update interleaved.
        var before = sim.Pred.Position;
        sim.Pred.Step(0.20f, sim.Sent, sim.SentM);
        float advance = Dist(sim.Pred.Position, before);

        float dt = 0.20f;
        float overIntegrateCeiling = Hull.Speed * dt * 1.01f;   // never past real time
        float underAdvanceFloor = Hull.Speed * (dt - 0.05f);    // covers the full frame
        _o.WriteLine($"hitch Step(0.20s) advanced {advance:F1}m (real-time ceiling {overIntegrateCeiling:F1}m, floor {underAdvanceFloor:F1}m)");

        Assert.True(advance <= overIntegrateCeiling,
            $"a single render hitch must not over-integrate past real elapsed time (advanced {advance:F1}m, ceiling {overIntegrateCeiling:F1}m)");
        Assert.True(advance >= underAdvanceFloor,
            $"a single render hitch must not under-advance the prediction (advanced {advance:F1}m, floor {underAdvanceFloor:F1}m)");
    }

    private static void Warmup(Sim sim, double seconds)
    {
        int frames = (int)Math.Round(seconds / 0.0167f);
        for (int i = 0; i < frames; i++) sim.Advance(0.0167f);
        sim.Pred.TakeCorrectionStats();   // discard the initial prime snap from stats
    }
}

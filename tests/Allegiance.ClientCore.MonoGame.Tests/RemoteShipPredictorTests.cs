using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="RemoteShipPredictor"/>: synthetic
/// <see cref="ServerShipUpdate"/> observations with known positions/velocities
/// and chosen timestamps drive assertions on linear extrapolation and on the
/// "corrects toward new data without snapping" behaviour that is the whole
/// point of the algorithm. No graphics device, no network.
/// </summary>
public class RemoteShipPredictorTests
{
    private const double Interval = 0.05;   // 1 / 20 updates-per-second (ServerConfig default)

    [Fact]
    public void Constructor_RejectsNonPositiveInterval() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteShipPredictor(0.0));

    [Fact]
    public void Predict_BeforeFirstObservation_IsNull()
    {
        var p = new RemoteShipPredictor(Interval);
        Assert.Null(p.Predict(0.5));
    }

    [Fact]
    public void SingleObservation_ExtrapolatesLinearlyAlongVelocity()
    {
        var p = new RemoteShipPredictor(Interval);
        p.Observe(Update(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f)), timestamp: 0.0);

        // pos(t) = p0 + v*t.
        var atHalf = Pose(p, 0.5);
        AssertClose(atHalf.Position.X, 5f);
        AssertClose(atHalf.Position.Y, 0f);
        AssertClose(atHalf.Position.Z, 0f);

        var atOne = Pose(p, 1.0);
        AssertClose(atOne.Position.X, 10f);
    }

    [Fact]
    public void ConflictingUpdate_DoesNotSnap_ThenCorrectsTowardNewData()
    {
        var p = new RemoteShipPredictor(Interval);

        // Steady +10 m/s along X …
        p.Observe(Update(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f)), 0.0);

        // … then an authoritative update jumps the ship to x=100.
        p.Observe(Update(new Vector3(100f, 0f, 0f), new Vector3(10f, 0f, 0f)), 0.080);

        // IMMEDIATELY after the jump we must NOT have snapped to 100 — the
        // extrapolation only moves a little toward it, proving the "correct
        // without snapping" property. (Sampled 1ms after the update, not 5ms:
        // the blend now converges within one real update interval — see
        // "convergence within one interval" below — so it is well underway by
        // 5ms and no longer a useful no-snap probe.)
        var justAfter = Pose(p, 0.081);
        Assert.True(justAfter.Position.X < 30f,
            $"expected no snap (x far below 100), got x={justAfter.Position.X}");

        // By one real update interval after the blend starts, it must have
        // FULLY converged onto the authoritative x≈100 and resumed
        // extrapolating from it — the blend must finish before the next real
        // Observe() would arrive, or it can never complete (the root cause of
        // the live "flashing"/"teleporting" bug this fix addresses).
        var settled = Pose(p, 0.150);
        Assert.True(settled.Position.X > 95f && settled.Position.X < 115f,
            $"expected convergence toward authoritative ~101, got x={settled.Position.X}");
    }

    [Fact]
    public void ConflictingUpdate_BlendCompletesWithinOneInterval()
    {
        var p = new RemoteShipPredictor(Interval);
        p.Observe(Update(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f)), 0.0);
        p.Observe(Update(new Vector3(100f, 0f, 0f), new Vector3(0f, 0f, 0f)), 0.0);

        // Sampling exactly one interval after the blend starts must already
        // reflect the authoritative data (no residual lag), proving the blend
        // does not require several intervals (~5.3x) to converge — the exact
        // defect that let every real Observe() restart it before it finished.
        var atOneInterval = Pose(p, Interval);
        Assert.True(atOneInterval.Position.X > 95f,
            $"expected blend essentially complete after one interval, got x={atOneInterval.Position.X}");
    }

    [Fact]
    public void SteadyAgreeingStream_StaysNearTheTrueTrajectory()
    {
        var p = new RemoteShipPredictor(Interval);
        // True trajectory: x = 10*t (matches the velocity of each observation).
        double[] ts = { 0.0, 0.052, 0.097, 0.145, 0.241, 0.353 };
        foreach (var t in ts)
        {
            double x = 10.0 * t;
            p.Observe(Update(new Vector3((float)x, 0f, 0f), new Vector3(10f, 0f, 0f)), t);
        }

        var pose = Pose(p, 0.460);
        double trueX = 10.0 * 0.461;   // extrapolated true position
        // Smoothness: the smoothed pose must hug the truth tightly (within a
        // small bound) — no accumulated drift, no spikes.
        Assert.True(Math.Abs(pose.Position.X - trueX) < 1.0,
            $"smoothed pose drifted from truth: got x={pose.Position.X}, truth~{trueX:F2}");
    }

    [Fact]
    public void Predict_ReportsLatestOrientation()
    {
        var p = new RemoteShipPredictor(Interval);
        p.Observe(Update(new Vector3(0f, 0f, 0f), Vector3.Zero), 0.0);
        p.Observe(Update(new Vector3(5f, 0f, 0f), Vector3.Zero), 0.098);

        var pose = Pose(p, 0.126);
        var fwd = pose.Orientation.GetForward();
        AssertClose(fwd.X, 0f);
        AssertClose(fwd.Y, 0f);
        AssertClose(fwd.Z, -1f);   // identity forward = (0,0,-1)
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static ServerShipUpdate Update(Vector3 pos, Vector3 vel) =>
        ServerShipUpdate.FromModel(7, new Ship(7, TestHull(), pos, vel, Orientation.Identity), 0u);

    private static PredictedPose Pose(RemoteShipPredictor p, double t) =>
        p.Predict(t)!.Value;   // tests always have an observation seeded

    private static void AssertClose(float actual, float expected) =>
        Assert.Equal(expected, actual, 1);

    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 903, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 618f, HardpointOffset: 542,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);
}

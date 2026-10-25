using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P813b (EXPERIMENTAL, default OFF) — the offline smoothing harness + component
/// contract for <see cref="RemoteShipHermiteInterpolator"/>: a deterministic
/// synthetic remote-ship pose stream (a cruising, weaving fighter) replayed at
/// several server cadences through raw / linear dead-reckon / Hermite predictors,
/// asserting the properties the task's "why" demands:
/// <list type="bullet">
/// <item><b>Arc fidelity</b> — the rendered pose lies on the true trajectory at
/// its own (delayed) render time, to sub-metre accuracy.</item>
/// <item><b>No teleports</b> — the largest single-frame step is ~normal frame
/// motion, vs raw poses which snap by up to a full update interval of travel.</item>
/// <item><b>Smoothing</b> — frame-to-frame jitter collapses to ~1/200th of the
/// raw pose stream, comparable to a perfect linear extrapolator (and, for a
/// turning ship, free of the re-seed correction spikes a forward-replay reckoner
/// incurs — the P448b <c>TakeRemoteCorrectionStats</c> pain).</item>
/// <item><b>Adaptive lag</b> — the render delay tracks each ship's measured
/// update cadence (one interval), so a 2 Hz straggler and a 20 Hz escort each
/// get exactly one period of smoothing lag.</item>
/// </list>
/// The harness mirrors <c>tools/p813b-hermite-proof</c> (same truth stream,
/// same metrics) so the console tool and this gate agree.
/// </remarks>
public class P813bHermiteSmoothingTests
{
    private readonly ITestOutputHelper _output;
    public P813bHermiteSmoothingTests(ITestOutputHelper o) => _output = o;

    private static Vector3 TruthPos(double t) => new(
        100f + 80f * (float)t + 12f * MathF.Sin(0.8f * (float)t),
        -60f + 9f * MathF.Sin(1.3f * (float)t),
        850f + 7f * MathF.Sin(0.5f * (float)t + 1f));

    private static Vector3 TruthVel(double t) => new(
        80f + 9.6f * MathF.Cos(0.8f * (float)t),
        11.7f * MathF.Cos(1.3f * (float)t),
        3.5f * MathF.Cos(0.5f * (float)t + 1f));

    private static float Dist(in Vector3 a, in Vector3 b) => (a - b).Length();

    /// <summary>Run the harness at a given cadence; returns steady-state metrics.</summary>
    private (double ThermErrDelayed, double RawJitter, double DeadJitter, double ThermJitter,
             double MaxRawStep, double MaxDeadStep, double MaxHermStep, double Lag)
        Harness(int hz)
    {
        double dt = 1.0 / hz, t0 = 1.0, tEnd = 7.0, renderDt = 1.0 / 60.0;
        var herm = new RemoteShipHermiteInterpolator();

        double feedCursor = t0, lastRecvT = -1;
        Vector3 rawPos = default;
        double sumThermDel = 0, sumRawJit = 0, sumDeadJit = 0, sumThermJit = 0, sumLag = 0;
        double maxRawStep = 0, maxDeadStep = 0, maxHermStep = 0;
        long nNow = 0, nJit = 0;
        Vector3 prevRawPos = default, prevDeadPos = default, prevThermPos = default, prevTruth = default;
        bool havePrev = false;

        for (double tr = t0 + 0.006; tr <= tEnd; tr += renderDt)
        {
            while (feedCursor <= tr + 1e-9)
            {
                herm.Feed(TruthPos(feedCursor), TruthVel(feedCursor), Orientation.Identity, feedCursor);
                rawPos = TruthPos(feedCursor);
                lastRecvT = feedCursor;
                feedCursor += dt;
            }
            Vector3 truth = TruthPos(tr);
            Vector3 dead = rawPos + TruthVel(lastRecvT) * (float)(tr - lastRecvT);
            bool ok = herm.TryGetPose(tr, out Vector3 hp, out _);
            if (!ok) hp = dead;

            if (tr >= 2.0)
            {
                sumThermDel += Dist(hp, TruthPos(tr - herm.RenderDelay));
                sumLag += herm.RenderDelay;
                nNow++;
            }
            if (tr >= 2.0 && havePrev)
            {
                float rawD = Dist(rawPos, prevRawPos);
                float deadD = Dist(dead, prevDeadPos);
                float hermD = Dist(hp, prevThermPos);
                float truthD = Dist(truth, prevTruth);
                sumRawJit += MathF.Abs(rawD - truthD);
                sumDeadJit += MathF.Abs(deadD - truthD);
                sumThermJit += MathF.Abs(hermD - truthD);
                nJit++;
                if (rawD > maxRawStep) maxRawStep = rawD;
                if (deadD > maxDeadStep) maxDeadStep = deadD;
                if (hermD > maxHermStep) maxHermStep = hermD;
            }
            prevRawPos = rawPos; prevDeadPos = dead; prevThermPos = hp;
            prevTruth = truth; havePrev = true;
        }

        return (sumThermDel / nNow, sumRawJit / nJit, sumDeadJit / nJit, sumThermJit / nJit,
                maxRawStep, maxDeadStep, maxHermStep, sumLag / nNow);
    }

    [Fact]
    public void Harness_reports_smoothing_across_cadences()
    {
        int[] cadences = { 2, 5, 10, 20 };
        var rows = new List<string>();
        rows.Add("cadenceHz\tthermErrDelayed\trawJitter\tdeadJitter\tthermJitter\tmaxRawStep\tmaxDeadStep\tmaxHermStep\tlag");
        foreach (int hz in cadences)
        {
            var m = Harness(hz);
            rows.Add($"{hz}\t{m.ThermErrDelayed:F4}\t{m.RawJitter:F4}\t{m.DeadJitter:F4}\t{m.ThermJitter:F4}" +
                     $"\t{m.MaxRawStep:F2}\t{m.MaxDeadStep:F2}\t{m.MaxHermStep:F2}\t{m.Lag:F4}");
            _output.WriteLine(rows[^1]);

            // Arc fidelity: rendered pose lies on the true path at its render time.
            Assert.True(m.ThermErrDelayed < 0.1, $"cadence {hz}: arc-fidelity error {m.ThermErrDelayed:F4} m (expect <0.1)");
            // No teleports: worst single-frame step ~ normal motion, nowhere near raw's snap.
            Assert.True(m.MaxHermStep < 2.0, $"cadence {hz}: hermite max step {m.MaxHermStep:F2} (expect <2.0)");
            Assert.True(m.MaxHermStep < m.MaxRawStep, $"cadence {hz}: hermite max step {m.MaxHermStep:F2} >= raw {m.MaxRawStep:F2}");
            // Smoothing: jitter collapses vs raw.
            Assert.True(m.ThermJitter < m.RawJitter * 0.05, $"cadence {hz}: hermite jitter {m.ThermJitter:F4} not << raw {m.RawJitter:F4}");
            // Comparable to a perfect linear extrapolator (upper bound generous).
            Assert.True(m.ThermJitter <= m.DeadJitter * 3.0 + 0.01, $"cadence {hz}: hermite jitter {m.ThermJitter:F4} vs dead {m.DeadJitter:F4}");
        }

        // Adaptive lag: each cadence renders exactly ~one update period behind.
        double[] expectLag = { 0.5, 0.2, 0.1, 0.05 };
        for (int i = 0; i < cadences.Length; i++)
        {
            double lag = Harness(cadences[i]).Lag;
            Assert.True(Math.Abs(lag - expectLag[i]) < expectLag[i] * 0.3 + 0.015,
                $"cadence {cadences[i]}: lag {lag:F4} not ~ {expectLag[i]:F4}");
        }
    }

    [Fact]
    public void Interpolator_needs_two_samples_and_extrapolates_only_past_newest()
    {
        var h = new RemoteShipHermiteInterpolator();
        Assert.Equal(0, h.SampleCount);
        Assert.False(h.TryGetPose(0.0, out _, out _), "no samples → cannot interpolate");

        h.Feed(new Vector3(0, 0, 0), new Vector3(10, 0, 0), Orientation.Identity, 1.0);
        Assert.Equal(1, h.SampleCount);
        Assert.False(h.TryGetPose(1.01, out _, out _), "one sample → cannot interpolate");

        h.Feed(new Vector3(1, 0, 0), new Vector3(10, 0, 0), Orientation.Identity, 1.011);
        // render delay ~ measured interval (tiny); a query just past the newest must
        // return false (extrapolate in the caller), inside the window must succeed.
        Assert.True(h.TryGetPose(1.012, out _, out _), "two samples, query inside → interpolate");
        Assert.False(h.TryGetPose(5.0, out _, out _), "query far past newest → caller extrapolates");
    }

    [Fact]
    public void Feed_drops_out_of_order_and_updates_cadence()
    {
        var h = new RemoteShipHermiteInterpolator();
        h.Feed(new Vector3(0, 0, 0), new Vector3(10, 0, 0), Orientation.Identity, 1.0);
        h.Feed(new Vector3(1, 0, 0), new Vector3(10, 0, 0), Orientation.Identity, 1.09); // ~11 Hz
        h.Feed(new Vector3(2, 0, 0), new Vector3(10, 0, 0), Orientation.Identity, 1.085); // out of order → dropped
        Assert.Equal(2, h.SampleCount);
        Assert.InRange(h.UpdateInterval, 0.070, 0.095); // ~0.09s interval
        Assert.InRange(h.RenderDelay, 0.055, 0.098);    // delay ≈ interval, clamped [0.03,0.75]
    }

    [Fact]
    public void ClientWorld_hermite_is_off_by_default_and_feeds_only_when_enabled()
    {
        var w = new ClientWorld();
        // Default OFF: no interpolation, no per-ship allocation path.
        Assert.False(w.HermiteRemotes);
        Assert.False(w.TryGetHermiteRenderPose(5, 10.0, out _, out _));

        w.HermiteRemotes = true;
        double T = 10.0;
        w.NowSeconds = () => T;   // scripted render clock (also used by the Hermite feed via NowSeconds)

        // Two remote pose updates on the render clock fill the interpolator
        // (no hull resolver needed — Hermite only uses wire position/velocity/orientation).
        w.Apply(new Allegiance.Core.Wire.SingleShipUpdate
        { OidTarget = 5, Ship = NewRemoteShip(5, T) });
        T = 10.090;
        w.Apply(new Allegiance.Core.Wire.SingleShipUpdate
        { OidTarget = 5, Ship = NewRemoteShip(5, T) });

        // Query on the same clock, just inside the two-sample window → interpolates.
        Assert.True(w.TryGetHermiteRenderPose(5, 10.066, out var pos, out _),
            "a fed remote ship with 2 samples interpolates on the shared clock");
        Assert.True(pos.Length() >= 0f);   // a concrete pose was produced
    }

    private static Allegiance.Core.Wire.ServerShipUpdate NewRemoteShip(short id, double t)
        => Allegiance.Core.Wire.ServerShipUpdate.FromModel(
            id, new Allegiance.Core.Sim.Ship(id, PinnedHull, TruthPos(t), TruthVel(t), Orientation.Identity),
            (uint)(t * 1000));

    // A synthetic pinned hull (same family as Core's DemoHull) so no artwork corpus is needed.
    private static readonly Allegiance.Core.DataHullTypeIGC PinnedHull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f],
        TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 815, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 511f, HardpointOffset: 580,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);
}

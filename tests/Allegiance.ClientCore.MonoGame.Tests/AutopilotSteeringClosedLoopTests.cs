using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P343 — closed-loop stability proof for the player autopilot's velocity-aware
/// braking. Drives the REAL production steering law
/// (<see cref="AutopilotSteering.SteerToward"/>) through the REAL ship physics
/// (<see cref="Ship.ExecuteMove"/> + the position integration from
/// <c>Ship.OnUpdate</c>, <c>Ship.cs:5733-5734</c>) with real hulls from the
/// artwork core, on a stationary target — the exact dynamics a live flight
/// reproduces (the controls wire merely relays the computed <see cref="ControlData"/>
/// and the resulting poses). The pre-P343 naive two-zone law is reproduced by
/// calling the same production method with a huge
/// <see cref="AutopilotSteering.AutopilotBrakeDeceleration"/>: its stopping-
/// distance term then collapses to ~0, leaving the fixed 420-unit ramp, so the
/// before/after trajectories are directly comparable without duplicating any
/// logic.
///
/// Two hulls bracket the population: the "stress" hull has the lowest
/// Thrust/Mass (weakest braking — the worst case for a distance-only ramp, and
/// where the diagnosed overshoot is largest), the "mild" hull the highest. Four
/// approaches cover the live-observed regimes: far-fast nose-on, mid-range
/// off-axis (real turn), near-fast at the old 420-unit threshold, and the
/// slow/rest approach that already worked and must not regress.
/// </summary>
public class AutopilotSteeringClosedLoopTests
{
    private readonly ITestOutputHelper _output;

    public AutopilotSteeringClosedLoopTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<DataHullTypeIGC> UsableHulls = LoadUsableHulls();

    /// <summary>Weakest-braking-over-distance hull — the one whose stopping
    /// distance at cruise (<c>Speed²/decel</c>) swamps the fixed 420-unit ramp,
    /// i.e. where the P343 velocity term actually activates and the naive ramp
    /// overshoots most. Restricted to mobile hulls so approaches complete.</summary>
    private static readonly DataHullTypeIGC StressHull =
        PickHull(hs => Mobile(hs).OrderByDescending(StopDistanceAtCruise).First());

    /// <summary>Hull whose stopping distance at cruise fits inside the fixed
    /// ramp — the easy case where the velocity term rarely activates, so the
    /// fix must leave behaviour unchanged (no regression).</summary>
    private static readonly DataHullTypeIGC MildHull =
        PickHull(hs => Mobile(hs).OrderBy(StopDistanceAtCruise).First());

    /// <summary>Stopping distance at cruise: Speed²/(Thrust/Mass) — how far the
    /// ship needs to shed full cruise speed at its real max deceleration.</summary>
    private static float StopDistanceAtCruise(DataHullTypeIGC h) =>
        h.Thrust > 0f && h.Mass > 0f ? h.Speed * h.Speed * h.Mass / h.Thrust : 0f;

    private static IEnumerable<DataHullTypeIGC> Mobile(IReadOnlyList<DataHullTypeIGC> hs) =>
        hs.Where(h => h.Speed >= 100f);   // fast enough to traverse the approach distances

    private static DataHullTypeIGC PickHull(Func<IReadOnlyList<DataHullTypeIGC>, DataHullTypeIGC> pick) => pick(UsableHulls);

    private static List<DataHullTypeIGC> LoadUsableHulls()
    {
        string root = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
                      ?? "/home/domingo/Development/Artwork";
        string corePath = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.igc", SearchOption.AllDirectories)
                .OrderBy(p => Path.GetFileName(p).Contains("PCore001", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(p => SafeCore(p) is not null) ?? ""
            : "";
        var core = SafeCore(corePath);
        if (core is null)
            throw new InvalidOperationException($"no static core with a hull found under {root}");
        var usable = core.HullTypes.Values.Where(h => h.Thrust > 0f && h.Mass > 0f && h.Speed > 0f).ToList();
        if (usable.Count == 0)
            throw new InvalidOperationException("no usable (Thrust/Mass/Speed > 0) hull in core");
        return usable;
    }

    private static StaticCore? SafeCore(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { return StaticCore.Load(IgcReader.ParseFile(path)); }
        catch { return null; }
    }

    [Fact]
    public void FastAndSlowApproaches_ConvergeAndHold_NoOscillation()
    {
        Report($"core hull: stress Speed={StressHull.Speed:0.#} decel={StressHull.Thrust / StressHull.Mass:0.#} u/s² | " +
               $"mild Speed={MildHull.Speed:0.#} decel={MildHull.Thrust / MildHull.Mass:0.#} u/s²");

        var hulls = new[] { StressHull, MildHull };
        for (int hi = 0; hi < hulls.Length; hi++)
        {
            var hull = hulls[hi];
            Report($"\n=== {(hi == 0 ? "STRESS" : "mild")} hull Speed={hull.Speed:0.#}, decel={hull.Thrust / hull.Mass:0.#} u/s² ===");

            // A — far, fast, nose-on (builds to cruise speed).
            CheckConverges(hull, "A: far fast nose-on",
                start: new Vector3(0f, 0f, 985f),
                vel: new Vector3(0f, 0f, -0.93f * hull.Speed),
                target: Vector3.Zero);

            // B — medium range, off-axis (needs a real turn), fast.
            CheckConverges(hull, "B: mid off-axis fast",
                start: new Vector3(345f, 0f, 615f),
                vel: new Vector3(0f, 0f, -0.78f * hull.Speed),
                target: Vector3.Zero);

            // C — the dangerous regime: already inside/near the old 420 brake
            // start, coming in very fast (large stopping distance).
            CheckConverges(hull, "C: near fast (old-threshold regime)",
                start: new Vector3(0f, 414f, 634f),
                vel: (new Vector3(0f, -0.452f, -0.891f)) * hull.Speed,
                target: Vector3.Zero);

            // R — slow/rest approach (the already-working data point, must not regress).
            CheckConverges(hull, "R: slow rest approach (regression)",
                start: new Vector3(0f, 0f, 735f),
                vel: new Vector3(0f, 0f, -0.14f * hull.Speed),
                target: Vector3.Zero);
        }
    }

    private void CheckConverges(DataHullTypeIGC hull, string label, Vector3 start, Vector3 vel, Vector3 target)
    {
        float decel = hull.Thrust / hull.Mass;
        var fixedRun = RunApproach(hull, start, vel, target, brakeDeceleration: decel);

        Summarize(label, fixedRun);

        // Must converge to a stop near the target and then HOLD station.
        int settled = fixedRun.FindIndex(d => d < 100f);
        Assert.True(settled >= 0, $"{label}: never converged below 100 (final {fixedRun[^1]:0.#})");
        Assert.True(fixedRun[^1] < 150f, $"{label}: did not hold a stop near target (final {fixedRun[^1]:0.#})");
        float postSettleMax = settled >= 0 ? fixedRun.Skip(settled).Max() : float.MaxValue;
        Assert.True(postSettleMax < 270f,
            $"{label}: re-expanded far past the target after settling (peak {postSettleMax:0.#}) — oscillation not killed");
    }

    [Fact]
    public void LowDecelerationHull_VelocityAwareBeatsNaiveRamp_RegressionGuard()
    {
        // The regression guard: on the weakest-braking hull's fast nose-on
        // approach, the P343 velocity-aware law must stop the ship noticeably
        // tighter than the old distance-only ramp (whose stopping-distance term
        // is collapsed by the huge deceleration argument). If the law were ever
        // reverted to the naive ramp, fixed==naive and this assertion fails.
        float decel = StressHull.Thrust / StressHull.Mass;
        var start = new Vector3(0f, 0f, 885f);
        var vel = new Vector3(0f, 0f, -0.97f * StressHull.Speed);
        var target = Vector3.Zero;

        var fixedRun = RunApproach(StressHull, start, vel, target, brakeDeceleration: decel);
        var naiveRun = RunApproach(StressHull, start, vel, target, brakeDeceleration: 1e9f);

        Summarize("regression guard (fixed law)", fixedRun);
        Summarize("regression guard (naive ramp) ", naiveRun);

        Assert.True(fixedRun[^1] < 150f, $"fixed law did not converge (final {fixedRun[^1]:0.#})");
        Assert.True(oldPostSettleMax(naiveRun) >= oldPostSettleMax(fixedRun) + 50f,
            $"naive ramp ({oldPostSettleMax(naiveRun):0.#}) not meaningfully worse than velocity-aware " +
            $"({oldPostSettleMax(fixedRun):0.#}) — the P343 improvement is not being exercised");
    }

    private static float oldPostSettleMax(List<float> run)
    {
        int s = run.FindIndex(d => d < 100f);
        return s >= 0 ? run.Skip(s).Max() : float.MaxValue;
    }

    private void Summarize(string label, List<float> dists)
    {
        int settled = dists.FindIndex(d => d < 100f);
        float peak = dists.Max();
        _output.WriteLine($"{label,-38} settled@{settled.ToString().PadLeft(3)} ticks final={dists[^1]:0.#} peak={peak:0.#} " +
                          $"postSettlePeak={(settled >= 0 ? dists.Skip(settled).Max() : -1):0.#}");
    }

    private void Report(string s) => _output.WriteLine(s);

    private static List<float> RunApproach(DataHullTypeIGC hull, Vector3 start, Vector3 vel, Vector3 target,
        float brakeDeceleration)
    {
        const double dt = 0.031;     // game-ish tick; robust to cadence
        const int ticks = 545;       // up to ~17 s for the farthest approach
        var ship = new Ship(shipId: 901, hull: hull, position: start, velocity: vel,
            orientation: Orientation.Identity);

        var dists = new List<float>(ticks);
        for (int i = 0; i < ticks; i++)
        {
            var controls = AutopilotSteering.SteerToward(
                ship.Position, ship.Orientation, target, ship.Velocity,
                brakeDeceleration: brakeDeceleration);
            ship.Controls = controls;
            double t = i * dt;
            // Exactly what Ship.OnUpdate (the sector-tick hook, Ship.cs:5733-5734)
            // does for the flight model: ExecuteMove advances Velocity, then the
            // C++-owned position step Δpos = velocity·ΔT is integrated.
            ship.ExecuteMove(t, t + dt);
            ship.Position += ship.Velocity * (float)dt;
            dists.Add((target - ship.Position).Length());
        }
        return dists;
    }
}

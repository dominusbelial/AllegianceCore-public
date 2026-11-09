using System.Globalization;
using System.Text.Json;
using Allegiance.ClientCore.Flight;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

// P985 — deterministic sender-timestamp replay. The same synthetic flight and
// seeded arrival transcript drives the production P448 reckoner, P813b Hermite
// and P985 timeline. Optional frame CSVs feed the retained A/B clip renderer.
// No network, server, synthetic CPU load, or external host is involved.

if (args.Contains("--render", StringComparer.Ordinal))
{
    P985ClipRenderer.Run(args);
    return 0;
}

string? output = Arg("--out");
bool writeFrames = args.Contains("--frames", StringComparer.Ordinal);
var scenarios = new[]
{
    new Impairment("clean", Jitter: false, Reorder: false, Duplicate: false, Loss: false, Stall: false),
    new Impairment("jitter-0-150ms", Jitter: true, Reorder: false, Duplicate: false, Loss: false, Stall: false),
    new Impairment("reorder", Jitter: false, Reorder: true, Duplicate: false, Loss: false, Stall: false),
    new Impairment("duplicate", Jitter: false, Reorder: false, Duplicate: true, Loss: false, Stall: false),
    new Impairment("loss-5pct", Jitter: false, Reorder: false, Duplicate: false, Loss: true, Stall: false),
    new Impairment("stall-1s", Jitter: false, Reorder: false, Duplicate: false, Loss: false, Stall: true),
    new Impairment("combined", Jitter: true, Reorder: true, Duplicate: true, Loss: true, Stall: true),
};
var results = new List<MetricRow>();
var frameRows = new List<FrameRow>();
var truth = TruthTrajectory.Generate();
for (int scenarioIndex = 0; scenarioIndex < scenarios.Length; scenarioIndex++)
{
    var impairment = scenarios[scenarioIndex];
    var arrivals = BuildArrivals(truth.Samples, impairment, 985000 + scenarioIndex);
    foreach (string method in new[] { "default-reckoner", "p813b-hermite", "p985-timeline" })
    {
        var run = Replay(impairment.Name, method, truth, arrivals, writeFrames && impairment.Name == "combined" && method != "p813b-hermite");
        results.Add(run.Metrics);
        frameRows.AddRange(run.Frames);
    }
}

string header = "scenario\tmethod\tmeanM\tp95M\tmaxM\tspikes\textrapPct\taddedLatencyMs\tframes";
Console.WriteLine(header);
foreach (var row in results)
    Console.WriteLine(row.ToString());
if (output is not null)
{
    Directory.CreateDirectory(output);
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(output, "p985-replay.json"), JsonSerializer.Serialize(new
    {
        seed = 985000,
        trajectory = "ShipKinematics 60 Hz, constant turning/climbing controls; source poses 10 Hz",
        jitter = "uniform [0,150] ms; reorder swaps adjacent arrival slots; duplicates 20%; loss seeded 5%; stall 7-8 s",
        errorConvention = "truth(now-delay) when interpolating; truth(now) while extrapolating; added latency separately",
        correctionSpike = "abs((poseNow-posePrevious)-(truthNow-truthPrevious)) > 1 m at 60 Hz",
        scenarios = results,
    }, jsonOptions));
    var lines = new List<string> { header };
    lines.AddRange(results.Select(r => r.ToString()));
    File.WriteAllLines(Path.Combine(output, "p985-replay.tsv"), lines);
    if (writeFrames)
    {
        var csv = new List<string> { "time,method,x,y,z,truthX,truthY,truthZ,delay,interpolated" };
        csv.AddRange(frameRows.Select(f => string.Join(',',
            f.Time.ToString("R", CultureInfo.InvariantCulture), f.Method,
            f.X.ToString("R", CultureInfo.InvariantCulture), f.Y.ToString("R", CultureInfo.InvariantCulture), f.Z.ToString("R", CultureInfo.InvariantCulture),
            f.TruthX.ToString("R", CultureInfo.InvariantCulture), f.TruthY.ToString("R", CultureInfo.InvariantCulture), f.TruthZ.ToString("R", CultureInfo.InvariantCulture),
            f.Delay.ToString("R", CultureInfo.InvariantCulture), f.Interpolated ? "1" : "0")));
        File.WriteAllLines(Path.Combine(output, "p985-combined-frames.csv"), csv);
    }
}

// Fail the proof if the central reorder-tolerance claim regresses.
var combinedNew = results.Single(r => r.Scenario == "combined" && r.Method == "p985-timeline");
var combinedOld = results.Single(r => r.Scenario == "combined" && r.Method == "p813b-hermite");
if (combinedNew.P95M >= combinedOld.P95M || combinedNew.Spikes >= combinedOld.Spikes)
{
    Console.Error.WriteLine("P985 replay FAIL: timeline did not beat P813b p95 error and spike count in combined impairment");
    return 2;
}
return 0;

string? Arg(string name)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return null;
}

static List<Arrival> BuildArrivals(IReadOnlyList<ServerShipUpdate> samples, Impairment impairment, int seed)
{
    var random = new Random(seed);
    var arrivals = new List<Arrival>(samples.Count * 2);
    for (int i = 0; i < samples.Count; i++)
    {
        if (impairment.Loss && random.NextDouble() < 0.05) continue;
        double sourceTime = samples[i].Time / 20.0;
        double arrival = sourceTime + (impairment.Jitter ? random.NextDouble() * 0.150 : 0);
        if (impairment.Stall && sourceTime >= 7.0 && sourceTime < 8.0) arrival = 8.0 + (sourceTime - 7.0) * 0.01;
        arrivals.Add(new Arrival(samples[i], sourceTime, arrival, arrivals.Count));
        if (impairment.Duplicate && random.NextDouble() < 0.20)
            arrivals.Add(new Arrival(samples[i], sourceTime, arrival + 0.003, arrivals.Count));
    }
    if (impairment.Reorder)
    {
        // Swap ARRIVAL slots, not source timestamps: the older source pose then
        // arrives after its successor, exactly the case arrival-keying misses.
        for (int i = 1; i + 1 < arrivals.Count; i += 4)
        {
            double firstTime = arrivals[i].ArrivalTime;
            arrivals[i] = arrivals[i] with { ArrivalTime = arrivals[i + 1].ArrivalTime };
            arrivals[i + 1] = arrivals[i + 1] with { ArrivalTime = firstTime };
        }
    }
    arrivals.Sort(static (a, b) =>
    {
        int time = a.ArrivalTime.CompareTo(b.ArrivalTime);
        return time != 0 ? time : a.Sequence.CompareTo(b.Sequence);
    });
    return arrivals;
}

static ReplayResult Replay(string scenario, string method, TruthTrajectory truth, List<Arrival> arrivals, bool captureFrames)
{
    var reckoner = new ShipDeadReckoner();
    var hermite = new RemoteShipHermiteInterpolator();
    var timeline = new RemoteShipTimeline();
    int nextArrival = 0;
    bool seeded = false;
    var errors = new List<double>(900);
    int spikes = 0, extrapolated = 0;
    double delaySum = 0;
    Vector3 previousPose = default, previousTruth = default;
    bool havePrevious = false;
    var frames = new List<FrameRow>();

    for (double now = 0.2; now <= 14.0 + 1e-8; now += 1.0 / 60.0)
    {
        while (nextArrival < arrivals.Count && arrivals[nextArrival].ArrivalTime <= now + 1e-9)
        {
            var update = arrivals[nextArrival].Update;
            reckoner.Seed(update, TruthTrajectory.Stats, 0, arrivals[nextArrival].ArrivalTime);
            seeded = true;
            if (method == "p813b-hermite")
                hermite.Feed(update.Position, update.Velocity, update.Orientation, arrivals[nextArrival].ArrivalTime);
            else if (method == "p985-timeline")
                timeline.Feed(update.Position, update.Velocity, update.Orientation,
                    update.Time / 20.0, arrivals[nextArrival].ArrivalTime);
            nextArrival++;
        }
        if (!seeded) continue;

        Vector3 pose;
        bool interpolated = false;
        double delay = 0;
        if (method == "p813b-hermite")
            interpolated = hermite.TryGetPose(now, out pose, out _);
        else if (method == "p985-timeline")
            interpolated = timeline.TryGetPose(now, out pose, out _);
        else pose = default;
        if (interpolated)
            delay = method == "p813b-hermite" ? hermite.RenderDelay : timeline.RenderDelay;
        else reckoner.PoseAt(now, out pose, out _);

        double truthTime = interpolated ? now - delay : now;
        Vector3 expected = truth.PositionAt(truthTime);
        if (now >= 2.0)
        {
            float error = (pose - expected).Length();
            errors.Add(error);
            if (!interpolated) extrapolated++;
            delaySum += delay;
            if (havePrevious && MathF.Abs((pose - previousPose).Length() - (expected - previousTruth).Length()) > 1.0f) spikes++;
            previousPose = pose;
            previousTruth = expected;
            havePrevious = true;
            if (captureFrames)
                frames.Add(new FrameRow(now, method, pose.X, pose.Y, pose.Z,
                    expected.X, expected.Y, expected.Z, delay, interpolated));
        }
    }

    errors.Sort();
    double mean = errors.Average();
    double p95 = errors[Math.Min(errors.Count - 1, (int)Math.Ceiling(errors.Count * 0.95) - 1)];
    double max = errors[^1];
    var metrics = new MetricRow(scenario, method, mean, p95, max, spikes,
        100.0 * extrapolated / errors.Count, 1000.0 * delaySum / errors.Count, errors.Count);
    return new ReplayResult(metrics, frames);
}

sealed record Impairment(string Name, bool Jitter, bool Reorder, bool Duplicate, bool Loss, bool Stall);
sealed record Arrival(ServerShipUpdate Update, double SourceTime, double ArrivalTime, int Sequence);
sealed record ReplayResult(MetricRow Metrics, List<FrameRow> Frames);
sealed record MetricRow(string Scenario, string Method, double MeanM, double P95M, double MaxM,
    int Spikes, double ExtrapPct, double AddedLatencyMs, int Frames)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Scenario}\t{Method}\t{MeanM:F4}\t{P95M:F4}\t{MaxM:F4}\t{Spikes}\t{ExtrapPct:F2}\t{AddedLatencyMs:F2}\t{Frames}");
}
sealed record FrameRow(double Time, string Method, double X, double Y, double Z,
    double TruthX, double TruthY, double TruthZ, double Delay, bool Interpolated);

sealed class TruthTrajectory
{
    private readonly TruthState[] _states;
    public IReadOnlyList<ServerShipUpdate> Samples { get; }
    private TruthTrajectory(TruthState[] states, IReadOnlyList<ServerShipUpdate> samples)
    { _states = states; Samples = samples; }

    public static readonly Allegiance.Core.DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f], TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 985, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 511f, HardpointOffset: 580,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    public static ShipDeadReckoner.HullStats Stats => ShipDeadReckoner.HullStats.FromHull(Hull);

    public static TruthTrajectory Generate()
    {
        const int hz = 60;
        const int count = 14 * hz + 1;
        var states = new TruthState[count];
        var samples = new List<ServerShipUpdate>(140);
        var orientation = new Orientation();
        Vector3 position = new(0, 0, 0);
        Vector3 velocity = orientation.GetForward() * 60f;
        float[] turnRates = new float[3];
        var controls = new ControlData(Yaw: 0.18f, Pitch: -0.08f, Roll: 0.03f, Throttle: 0.65f);
        var inputs = new ShipKinematics.Inputs(controls, 0,
            Stats.Thrust, Stats.MaxSpeed, Stats.Mass, Stats.SideMultiplier, Stats.BackMultiplier,
            Stats.MaxTurnRateYaw, Stats.MaxTurnRatePitch, Stats.MaxTurnRateRoll,
            Stats.TurnTorqueYaw, Stats.TurnTorquePitch, Stats.TurnTorqueRoll,
            0, 0, false);
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)hz;
            states[i] = new TruthState(t, position, velocity);
            if (i % 6 == 0 && t >= 0.2 - 1e-9)
                samples.Add(ServerShipUpdate.FromValues(5, 0, controls, (uint)Math.Round(t * 20),
                    position, orientation, velocity, turnRates, 1, 1, 1, 1, 1, 0, 0));
            if (i == count - 1) break;
            ShipKinematics.Step(in inputs, ref velocity, orientation, turnRates, 1f / hz);
            position += velocity * (1f / hz);
        }
        return new TruthTrajectory(states, samples);
    }

    public Vector3 PositionAt(double time)
    {
        double slot = Math.Clamp(time, 0, 14) * 60;
        int a = Math.Min(_states.Length - 1, (int)slot);
        int b = Math.Min(_states.Length - 1, a + 1);
        float u = (float)(slot - a);
        return _states[a].Position + (_states[b].Position - _states[a].Position) * u;
    }

    private readonly record struct TruthState(double Time, Vector3 Position, Vector3 Velocity);
}

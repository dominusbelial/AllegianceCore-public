using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.Flight;

// P813b offline harness — replay a deterministic synthetic "recorded" remote-ship
// update stream (a cruising, weaving fighter — a realistic mid-combat pose
// stream) at several server cadences through three render predictors and report
// position error vs truth and jitter. "Raw" = hold the last received pose;
// "Dead-reckon" = extrapolate the last received pose by the sent velocity (the
// classic stale-tangent forward extrapolation the P448b reckoner generalises);
// "Hermite" = RemoteShipHermiteInterpolator (rendered-late cubic Hermite with
// sent-velocity tangents on one render clock).
//
// Received states arrive progressively on the render clock (the feed cursor
// advances with the frame), exactly as a live client consumes its network queue —
// so the 8-slot ring buffer always holds the CURRENT window, not the tail.
//
// Usage: p813b-hermite-proof [csvPath]

static Vector3 TruthPos(double t) => new(
    100f + 80f * (float)t + 12f * MathF.Sin(0.8f * (float)t),
    -60f + 9f * MathF.Sin(1.3f * (float)t),
    850f + 7f * MathF.Sin(0.5f * (float)t + 1f));

static Vector3 TruthVel(double t) => new(
    80f + 9.6f * MathF.Cos(0.8f * (float)t),
    11.7f * MathF.Cos(1.3f * (float)t),
    3.5f * MathF.Cos(0.5f * (float)t + 1f));

static float Dist(in Vector3 a, in Vector3 b) => (a - b).Length();

string? outCsv = args.Length > 0 ? args[0] : null;
var lines = new List<string>();

double t0 = 1.0, tEnd = 7.0;
double renderDt = 1.0 / 60.0;
int[] cadences = { 2, 5, 10, 20 };

lines.Add("cadenceHz\trawErrNow\tdeadErrNow\thermErrNow\thermErrDelayed\trawJitter\tdeadJitter\tthermJitter\tmaxRawStep\tmaxDeadStep\tmaxHermStep\tthermLag");

foreach (int hz in cadences)
{
    double dt = 1.0 / hz;
    var herm = new RemoteShipHermiteInterpolator();

    double feedCursor = t0;       // next received-state time not yet fed
    double lastRecvT = -1;
    Vector3 rawPos = default;     // last received pose (raw)
    double sumRawNow = 0, sumDeadNow = 0, sumThermNow = 0, sumThermDel = 0;
    double sumRawJit = 0, sumDeadJit = 0, sumThermJit = 0;
    double maxRawStep = 0, maxDeadStep = 0, maxHermStep = 0;
    long nNow = 0, nJit = 0;
    double thermLag = 0;

    Vector3 prevRawPos = default, prevDeadPos = default, prevThermPos = default;
    Vector3 prevTruth = default;
    bool havePrev = false;

    for (double tr = t0 + 0.006; tr <= tEnd; tr += renderDt)
    {
        // Feed every received state whose time has arrived by this render frame
        // (network arrival on the render clock).
        while (feedCursor <= tr + 1e-9)
        {
            herm.Feed(TruthPos(feedCursor), TruthVel(feedCursor), Orientation.Identity, feedCursor);
            rawPos = TruthPos(feedCursor);
            lastRecvT = feedCursor;
            feedCursor += dt;
        }

        Vector3 truth = TruthPos(tr);
        Vector3 truthDel = TruthPos(tr - herm.RenderDelay);

        // Raw: hold last received.
        Vector3 raw = rawPos;
        // Dead-reckon: extrapolate last received by sent velocity.
        Vector3 dead = rawPos + TruthVel(lastRecvT) * (float)(tr - lastRecvT);
        // Hermite: query on the same render clock.
        bool ok = herm.TryGetPose(tr, out Vector3 hp, out _);
        if (!ok) hp = dead;

        if (tr >= 2.0)   // steady-state window
        {
            sumRawNow += Dist(raw, truth);
            sumDeadNow += Dist(dead, truth);
            sumThermNow += Dist(hp, truth);
            sumThermDel += Dist(hp, truthDel);
            thermLag += herm.RenderDelay;
            nNow++;
        }

        if (tr >= 2.0 && havePrev)   // steady-state window (skip cold-start warmup)
        {
            float rawD = Dist(raw, prevRawPos);
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

        prevRawPos = raw; prevDeadPos = dead; prevThermPos = hp;
        prevTruth = truth;
        havePrev = true;
    }

    string row =
        $"{hz}\t{sumRawNow / nNow:F3}\t{sumDeadNow / nNow:F3}\t{sumThermNow / nNow:F3}\t" +
        $"{sumThermDel / nNow:F3}\t{sumRawJit / nJit:F4}\t{sumDeadJit / nJit:F4}\t{sumThermJit / nJit:F4}\t" +
        $"{maxRawStep:F2}\t{maxDeadStep:F2}\t{maxHermStep:F2}\t{thermLag / nNow:F4}";
    Console.WriteLine(row);
    lines.Add(row);
}

if (outCsv is not null)
    File.WriteAllLines(outCsv, lines);

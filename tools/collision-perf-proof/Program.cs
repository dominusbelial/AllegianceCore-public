using System.Diagnostics;
using System.Globalization;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

// P621b — collision broad-phase + projectile-vs-hull performance benchmark.
//
// Reproduces the P621 "_P621PerfProbe" population: 390 ship hulls + 134 rock
// hulls over the Alderan-class density, laid out SPARSE (ships spread over a
// large sector → few hundred candidate pairs) and DENSE MELEE (two fleets
// clustered at a rendezvous → thousands of candidate pairs). Times the whole
// collision step (CollisionDetector.Detect) and breaks the cost into the
// broad phase (candidate generation) vs the narrow phase (the pair tests).
//
// Usage:  dotnet run --project tools/collision-perf-proof [--release]
// Output: a markdown table of ms/tick for sparse and dense, before/after.

var artRoot = ExternalPaths.Require(ExternalResourceKind.Artwork);

// ── population model names (real artwork corpus) ────────────────────────────
// Ships: a representative mix of real fighter / corvette hulls. Rocks: the
// real bgrnd asteroids P621 used.
string[] shipModels = ["fig07", "fig08", "fig09", "gt_fig4", "gt_fig6", "gt_corr1"];
string[] rockModels = ["bgrnd03", "bgrnd51", "drgrock", "nbgrnd03"];

// Load + cache the hulls once per model name (mirrors CollisionHullCache).
var shipHulls = shipModels.Select(m => LoadCvh(artRoot, m)).Where(h => h is not null).Select(h => h!).ToArray();
var rockHulls = rockModels.Select(m => LoadCvh(artRoot, m)).Where(h => h is not null).Select(h => h!).ToArray();
if (shipHulls.Length == 0 || rockHulls.Length == 0)
{
    Console.Error.WriteLine($"need ≥1 ship hull and ≥1 rock hull in {artRoot}");
    return 2;
}

const int NShips = 390;    // 195v195 both fleets
const int NRocks = 134;    // Alderan rock population
const float ShipRadius = 50f;   // ~0.5 * fighter Length
const float RockRadius = 150f;  // asteroid game radius

Console.WriteLine($"ships={NShips} (hulls from {shipModels.Length} models), rocks={NRocks} " +
                  $"(hulls from {rockModels.Length} models), dt=0.01667");

// Warm the JIT + hull support caches.
BuildPopulation("sparse", seed: 1234, NShips, NRocks, shipHulls, rockHulls, ShipRadius, RockRadius);
BuildPopulation("dense", seed: 5678, NShips, NRocks, shipHulls, rockHulls, ShipRadius, RockRadius);

foreach (var layout in new[] { ("sparse", 1234), ("dense", 5678) })
{
    var det = BuildPopulation(layout.Item1, layout.Item2, NShips, NRocks, shipHulls, rockHulls, ShipRadius, RockRadius);
    det.Detect(0.01667f);                       // prime: populates swept AABBs
    int pairs = det.CandidatePairs;             // NOW meaningful (bounds set)
    int events = det.Detect(0.01667f).Count;
    double totalMs = TimeDetect(det, reps: 300);

    var ci = CultureInfo.InvariantCulture;
    Console.WriteLine("\nlayout={0}  colliders={1}  candidatePairs={2}  detectEvents={3}",
        layout.Item1, det.Colliders.Count, pairs, events);
    Console.WriteLine("  Detect (broad+narrow): {0} ms/tick", totalMs.ToString("0.00", ci));
}
return 0;

static CvhFile? LoadCvh(string root, string name)
{
    var p = Path.Combine(root, name + ".cvh");
    return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
}

static CollisionDetector BuildPopulation(string layout, int seed, int nShips, int nRocks,
    CvhFile[] shipHulls, CvhFile[] rockHulls, float shipRadius, float rockRadius)
{
    var rng = new Random(seed);
    var det = new CollisionDetector();

    bool dense = layout == "dense";

    // Dense melee: two fleets (195 ships each) clustered at a rendezvous —
    // neighbouring but not stacked (ship centres ~2-3 diameters apart). The
    // real 200v200 baseline. Sparse: ships + rocks spread over a large field.
    float fleetRadius = dense ? 200f : 560f;   // cluster half-size (sparse = one loosely-packed field)
    float fleetSep = dense ? 700f : 0f;         // gap between the two clusters
    var fleetCentres = dense
        ? new[] { new Vector3(-fleetSep / 2f, 0f, 0f), new Vector3(fleetSep / 2f, 0f, 0f) }
        : new[] { Vector3.Zero };

    int half = nShips / 2;
    for (int i = 0; i < nShips; i++)
    {
        var hull = shipHulls[i % shipHulls.Length];
        var fc = dense ? fleetCentres[i < half ? 0 : 1] : fleetCentres[0];
        var pos = fc + new Vector3(
            (float)(rng.NextDouble() * 2 - 1) * fleetRadius,
            (float)(rng.NextDouble() * 2 - 1) * fleetRadius,
            (float)(rng.NextDouble() * 2 - 1) * fleetRadius);
        var vel = new Vector3((float)(rng.NextDouble() * 80 - 40), (float)(rng.NextDouble() * 80 - 40), (float)(rng.NextDouble() * 80 - 40));
        det.Add(new Collider($"s{i}", pos, vel, Orientation.Identity, hull, shipRadius, isStatic: false, HitShape.Sphere));
    }
    for (int i = 0; i < nRocks; i++)
    {
        var hull = rockHulls[i % rockHulls.Length];
        var pos = dense
            ? new Vector3((float)(rng.NextDouble() * 2 - 1) * 9000f, (float)(rng.NextDouble() * 2 - 1) * 9000f, (float)(rng.NextDouble() * 2 - 1) * 9000f)
            : new Vector3((float)(rng.NextDouble() * 2 - 1) * 26000f, (float)(rng.NextDouble() * 2 - 1) * 26000f, (float)(rng.NextDouble() * 2 - 1) * 26000f);
        det.Add(new Collider($"r{i}", pos, Vector3.Zero, Orientation.Identity, hull, rockRadius, isStatic: true, HitShape.Sphere));
    }
    return det;
}

static double TimeDetect(CollisionDetector det, int reps)
{
    // Prime the hull support cache + SetStep before timing.
    det.Detect(0.01667f);

    // Full Detect (broad + narrow) over reps.
    var swT = Stopwatch.StartNew();
    for (int r = 0; r < reps; r++)
        det.Detect(0.01667f);
    swT.Stop();
    return swT.Elapsed.TotalMilliseconds / reps;
}

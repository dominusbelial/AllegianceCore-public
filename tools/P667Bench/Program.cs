using System.Buffers.Binary;
using System.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;

// P667 — local crowded-sector benchmark. Builds a single heavily-populated
// sector (dense hulled ships with real .cvh hulls so the detector runs GJK,
// plus a salvo of ordnance so the combat pass's detector runs too) and measures
// the per-tick Sector.Update wall time (mean/p95/max) with sim.parallelSectorCells
// OFF and ON, and the per-sub-step breakdown via the built-in tick profiler CSV.
//
// Usage:
//   ALLEGIANCE_ARTWORK_PATH=/path/to/Artwork dotnet run --project tools/P667Bench -- [shipCount] [ticks]

var artRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
Console.WriteLine($"P667-BENCH: artwork = {artRoot}");
if (!Directory.Exists(artRoot)) { Console.WriteLine("no artwork; set ALLEGIANCE_ARTWORK_PATH"); return; }

int shipCount = args.Length > 0 ? int.Parse(args[0]) : 320;
int ticks = args.Length > 1 ? int.Parse(args[1]) : 160;
Console.WriteLine($"P667-BENCH: ships={shipCount} ticks={ticks} cpu={Environment.ProcessorCount}");

var (core, map) = LoadCoreAndMap(artRoot);
Console.WriteLine($"P667-BENCH: core={core.HullTypes.Count} hulls, {core.ProjectileTypes.Count} projectiles; map clusters={map.Clusters.Count}");

var hull = PickHull(core, artRoot);
Console.WriteLine($"P667-BENCH: hull={hull.ModelName} (cvh present: {HasCvh(artRoot, hull.ModelName)})");

// Serial run on a fresh mission.
var (serialWall, serialCsv) = Measure(BuildMission(core, map, artRoot, hull, shipCount), enabled: false, threads: 1, ticks, "serial");
// Parallel run on a fresh identical mission.
var (parWall, parCsv) = Measure(BuildMission(core, map, artRoot, hull, shipCount), enabled: true, threads: 8, ticks, "parallel");

PrintStats("OFF (serial)", serialWall);
PrintStats("ON  t=8 ", parWall);

Console.WriteLine("\nP667-BENCH: per-bucket breakdown (mean ms / p95 ms / % of tick wall):");
foreach (var bucket in serialCsv.Keys.Union(parCsv.Keys).OrderByDescending(b => serialCsv.GetValueOrDefault(b, (0,0))))
{
    var (sm, sp) = serialCsv.GetValueOrDefault(bucket, (0, 0));
    var (pm, pp) = parCsv.GetValueOrDefault(bucket, (0, 0));
    Console.WriteLine($"  {bucket,-30} serial mean={sm,8:0.00} p95={sp,8:0.00}   parallel mean={pm,8:0.00} p95={pp,8:0.00}");
}

static (StaticCore core, MissionMap map) LoadCoreAndMap(string artRoot)
{
    StaticCore? core = null; MissionMap? map = null;
    foreach (var p in Directory.EnumerateFiles(artRoot, "*.igc", SearchOption.AllDirectories))
    {
        if (p.Contains("/.git/")) continue;
        try
        {
            var f = IgcReader.ParseFile(p);
            if (f.Wrapper == IgcWrapper.VersionAndDataSize && core is null) core = StaticCore.Load(f);
            else if (f.Wrapper == IgcWrapper.DataSize && map is null)
            {
                var mm = MissionMap.Load(f);
                if (mm.Clusters.Count > 0) map = mm;
            }
        }
        catch { }
        if (core is not null && map is not null) break;
    }
    return (core ?? throw new Exception("no static core"), map ?? throw new Exception("no map"));
}

static bool HasCvh(string artRoot, string modelName)
    => File.Exists(Path.Combine(artRoot, modelName + ".cvh"));

static CollisionHullCache ArtHulls(string artRoot) => new(name =>
{
    var p = Path.Combine(artRoot, name + ".cvh");
    return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
});

static DataHullTypeIGC PickHull(StaticCore core, string artRoot)
{
    // Prefer a hull whose model has a .cvh so detection runs the expensive GJK path.
    foreach (var h in core.HullTypes.Values.OrderBy(h => h.HullId))
        if (HasCvh(artRoot, h.ModelName)) return h;
    return core.HullTypes.Values.OrderBy(h => h.HullId).First();
}

static IReadOnlyList<SideSpec> SideSpecs(StaticCore core)
{
    var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
    return [new SideSpec(0, "Alpha", civs[0].CivilizationId), new SideSpec(1, "Bravo", civs[1].CivilizationId)];
}

static Mission BuildMission(StaticCore core, MissionMap map, string artRoot, DataHullTypeIGC hull, int shipCount)
{
    var rng = new Random(667);
    var m = Mission.Assemble(core, map, SideSpecs(core), new NullIgcSite());
    m.Stage = MissionStage.Started;
    foreach (var s in m.Sectors) s.CollisionHulls = ArtHulls(artRoot);
    var sector = m.Sectors.OrderByDescending(s => s.Mineable.Count).First();
    var projectiles = core.ProjectileTypes.Values.Where(pt => pt.Power > 0f).ToList();

    short nextId = 9800;
    // Tightly-packed cloud: ships placed uniformly in a sphere sized so the
    // average spacing is on the order of one hull length — hundreds of mutually
    // overlapping hulls → maximal, sustained collision-detection load (the
    // parallelised phase). Velocities are near-zero so the elastic resolution
    // debounce keeps the cloud in place (detection-only, no dispersal).
    float R = 52f * MathF.Pow(shipCount, 1f / 3f);   // tune: bigger R = sparser
    for (int i = 0; i < shipCount; i++)
    {
        double a = rng.NextDouble() * Math.PI * 2, b = rng.NextDouble() * Math.PI;
        float u = (float)Math.Cbrt(rng.NextDouble());      // uniform volume density
        float r = R * u;
        var pos = new Vector3(
            r * MathF.Sin((float)b) * MathF.Cos((float)a),
            r * MathF.Sin((float)b) * MathF.Sin((float)a),
            r * MathF.Cos((float)b));
        var vel = new Vector3(
            (float)(rng.NextDouble() * 4 - 2), (float)(rng.NextDouble() * 4 - 2), (float)(rng.NextDouble() * 4 - 2));
        var ship = new Ship(nextId++, hull, pos, vel, Orientation.Identity) { SideId = (short)(i % 2) };
        m.PlaceShip(ship, sector);
    }
    if (projectiles.Count > 0 && sector.Models.FirstOrDefault(mm => mm is Ship) is Ship launcher)
        for (int k = 0; k < 40; k++)
        {
            var pt = projectiles[k % projectiles.Count];
            var dir = new Vector3(MathF.Cos(k * 0.7f), MathF.Sin(k * 0.4f), 0.3f).Normalize();
            sector.Add(new Projectile(nextId++, sector, pt, -dir * 700f, dir * pt.Speed, dir, m.LastUpdate, launcher));
        }
    return m;
}

static (List<double> walls, Dictionary<string, (double mean, double p95)>) Measure(
    Mission m, bool enabled, int threads, int ticks, string label)
{
    ParallelSectorSim.Enabled = enabled;
    ParallelSectorSim.ThreadCount = threads;
    // Long interval so the window never rolls mid-run; we read the aggregates
    // directly via TickProfiler.Snapshot() instead of the CSV.
    TickProfiler.Configure(enabled: true, reportIntervalSeconds: 1000, csvPath: null);

    var walls = new List<double>(ticks);
    double t = m.LastUpdate;
    var swRun = Stopwatch.StartNew();
    while (swRun.ElapsedMilliseconds < 2600)   // run ~2.6 s of ticks
    {
        var sw = Stopwatch.StartNew();
        TickProfiler.EnterTick();
        t += 0.053;
        m.Update(t);
        TickProfiler.EndTick();
        sw.Stop();
        walls.Add(sw.Elapsed.TotalMilliseconds);
    }

    var snap = TickProfiler.Snapshot();
    TickProfiler.Configure(enabled: false, reportIntervalSeconds: 1000, csvPath: null);
    ParallelSectorSim.Reset();

    var buckets = new Dictionary<string, (double, double)>();
    foreach (var (bucket, (cnt, mean, p95, mx, tot)) in snap)
        buckets[bucket] = (mean, p95);
    return (walls, buckets);
}

static void PrintStats(string label, List<double> walls)
{
    var s = walls.OrderBy(x => x).ToList();
    double mean = s.Average();
    double p95 = s[(int)Math.Ceiling(0.95 * s.Count) - 1];
    Console.WriteLine($"P667-BENCH {label}: step:update mean={mean,6:0.00} ms  p95={p95,6:0.00} ms  max={s[^1],6:0.00} ms  ({s.Count} ticks)");
}

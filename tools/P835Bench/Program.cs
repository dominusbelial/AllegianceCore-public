using System.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;

// P835 — clash-peak sector benchmark. Reconstructs the rig's 200v200 clash-peak
// mix in ONE sector (two sides of moving ships + a sustained salvo of
// projectiles + a minefield + real asteroids) and measures Mission.Update —
// the server's `step:update` bucket — per tick, with the tick profiler's
// per-sub-bucket breakdown, at several ship counts, so the phase that grows at
// the clash peak can be attributed and optimised.
//
// Unlike P667Bench (a tightly-packed static cloud, maximal collision load —
// itself removed with its flag in P858),
// this bench spreads the ships across a fixed-radius sphere so the per-pass mix
// matches a real dogfight: ship-ship overlaps are rare, projectile-ship
// detection is continuous, the every-10th-pass cross-side visibility sweep
// scores hundreds of scanner/target pairs, and the models loop walks every
// ship each tick.
//
// Usage:
//   ALLEGIANCE_ARTWORK_PATH=/path/to/Artwork dotnet run --project tools/P835Bench -- [shipCount] [projectiles] [mines]

var artRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
Console.WriteLine($"P835-BENCH: artwork = {artRoot}");
if (!Directory.Exists(artRoot)) { Console.WriteLine("no artwork; set ALLEGIANCE_ARTWORK_PATH"); return; }

int shipCount = args.Length > 0 ? int.Parse(args[0]) : 400;
int projectileCount = args.Length > 1 ? int.Parse(args[1]) : 240;
int mineCount = args.Length > 2 ? int.Parse(args[2]) : 60;
Console.WriteLine($"P835-BENCH: ships={shipCount} projectiles={projectileCount} mines={mineCount} cpu={Environment.ProcessorCount}");

var (core, map) = LoadCoreAndMap(artRoot, args.Length > 3 ? args[3] : "m2xspiderw.igc");
Console.WriteLine($"P835-BENCH: core={core.HullTypes.Count} hulls, {core.ProjectileTypes.Count} projectiles, {core.MineTypes.Count} mines; map clusters={map.Clusters.Count}");

var hull = PickHull(core, artRoot);
Console.WriteLine($"P835-BENCH: hull={hull.ModelName} scannerRange={hull.ScannerRange}");

var bench = new ClashBench(core, map, artRoot, hull, shipCount, projectileCount, mineCount);
var (walls, snap) = bench.Measure(ticks: 500);
PrintStats($"ships={shipCount}", walls);
double wallSum = walls.Sum();
Console.WriteLine("P835-BENCH: per-bucket breakdown (amortised mean ms per tick / conditional mean / p95 / share of tick wall):");
foreach (var (bucket, v) in snap.OrderByDescending(kv => kv.Value.TotalMs))
    Console.WriteLine($"  {bucket,-34} amort={v.TotalMs / Math.Max(1, walls.Count),8:0.00}  mean={v.MeanMs,8:0.00} (n={v.Count,4})  p95={v.P95Ms,8:0.00}  {100.0 * v.TotalMs / wallSum,5:0.#}%");

static void PrintStats(string label, List<double> walls)
{
    var s = walls.OrderBy(x => x).ToList();
    double mean = s.Average();
    double p95 = s[(int)Math.Ceiling(0.95 * s.Count) - 1];
    Console.WriteLine($"P835-BENCH {label}: Mission.Update mean={mean,6:0.00} ms  p95={p95,6:0.00} ms  max={s[^1],6:0.00} ms  ({s.Count} ticks)");
}

static (StaticCore core, MissionMap map) LoadCoreAndMap(string artRoot, string mapName)
{
    StaticCore? core = null; MissionMap? map = null;
    string? mapPath = Directory.EnumerateFiles(artRoot, mapName, SearchOption.AllDirectories).FirstOrDefault();
    if (mapPath is not null)
    {
        var mf = IgcReader.ParseFile(mapPath);
        if (mf.Wrapper == IgcWrapper.DataSize) map = MissionMap.Load(mf);
    }
    foreach (var p in Directory.EnumerateFiles(artRoot, "*.igc", SearchOption.AllDirectories))
    {
        if (p.Contains("/.git/")) continue;
        try
        {
            var f = IgcReader.ParseFile(p);
            if (f.Wrapper == IgcWrapper.VersionAndDataSize && core is null) core = StaticCore.Load(f);
        }
        catch { }
        if (core is not null && map is not null) break;
    }
    return (core ?? throw new Exception("no static core"), map ?? throw new Exception($"no map '{mapName}'"));
}

static bool HasCvh(string artRoot, string modelName)
    => File.Exists(Path.Combine(artRoot, modelName + ".cvh"));

static DataHullTypeIGC PickHull(StaticCore core, string artRoot)
{
    // A player fighter hull with a .cvh (GJK when ships do overlap) and a
    // fighter-scale scanner range, exactly as on the rig.
    var best = core.HullTypes.Values
        .Where(h => HasCvh(artRoot, h.ModelName) && h.ScannerRange is > 300f and < 1500f)
        .OrderByDescending(h => h.Speed)
        .FirstOrDefault();
    if (best.ScannerRange > 0) return best;
    return core.HullTypes.Values.OrderBy(h => h.HullId).First();
}

internal sealed class ClashBench
{
    private const float SectorRadius = 2200f;   // ≈ a clash sector's fighting volume

    private readonly Mission _m;
    private readonly Sector _sector;
    private readonly List<DataProjectileTypeIGC> _projectileTypes;
    private readonly Random _rng = new(835);
    private readonly int _projectileTarget;
    private short _nextId = 9800;

    public ClashBench(StaticCore core, MissionMap map, string artRoot, DataHullTypeIGC hull,
        int shipCount, int projectileCount, int mineCount)
    {
        var hulls = new CollisionHullCache(name =>
        {
            var p = Path.Combine(artRoot, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        var sides = new SideSpec[] { new(0, "Alpha", civs[0].CivilizationId), new(1, "Bravo", civs[1].CivilizationId) };
        _m = Mission.Assemble(core, map, sides, new NullIgcSite());
        _m.Stage = MissionStage.Started;
        foreach (var s in _m.Sectors) s.CollisionHulls = hulls;
        _sector = _m.Sectors.OrderByDescending(s => s.Mineable.Count).First();
        _projectileTypes = core.ProjectileTypes.Values.Where(pt => pt.Power > 0f && pt.Speed > 0f).ToList();
        var mineTypes = core.MineTypes.Values.Where(mt => mt.Power > 0f).ToList();
        _projectileTarget = projectileCount;

        for (int i = 0; i < shipCount; i++)
        {
            var pos = RandomInSphere(SectorRadius);
            var vel = 60f * RandomUnit();
            var ship = new Ship(_nextId++, hull, pos, vel, Orientation.Identity) { SideId = (short)(i % 2) };
            _m.PlaceShip(ship, _sector);
        }

        for (int k = 0; k < mineCount && mineTypes.Count > 0; k++)
            _sector.Add(new Mine(_nextId++, _sector, mineTypes[k % mineTypes.Count],
                RandomInSphere(SectorRadius * 0.6f), _m.LastUpdate));

        TopUpProjectiles();
    }

    private Vector3 RandomInSphere(float radius)
    {
        double a = _rng.NextDouble() * Math.PI * 2, b = _rng.NextDouble() * Math.PI;
        float r = radius * (float)Math.Cbrt(_rng.NextDouble());
        return new Vector3(
            r * MathF.Sin((float)b) * MathF.Cos((float)a),
            r * MathF.Sin((float)b) * MathF.Sin((float)a),
            r * MathF.Cos((float)b));
    }

    private Vector3 RandomUnit()
    {
        double a = _rng.NextDouble() * Math.PI * 2, b = _rng.NextDouble() * Math.PI;
        return new Vector3(
            MathF.Sin((float)b) * MathF.Cos((float)a),
            MathF.Sin((float)b) * MathF.Sin((float)a),
            MathF.Cos((float)b));
    }

    private void TopUpProjectiles()
    {
        // Sustain the salvo: expired projectiles are replaced from random points
        // each tick, so combat detection keeps a dogfight's continuous load.
        int alive = 0;
        foreach (var mm in _sector.Models) if (mm is Projectile) alive++;
        for (int k = alive; k < _projectileTarget && _projectileTypes.Count > 0; k++)
        {
            var pt = _projectileTypes[_rng.Next(_projectileTypes.Count)];
            var dir = new Vector3(MathF.Cos(_rng.NextSingle() * 6.28f), MathF.Sin(_rng.NextSingle() * 6.28f), 0.3f).Normalize();
            _sector.Add(new Projectile(_nextId++, _sector, pt, RandomInSphere(SectorRadius),
                dir * pt.Speed, dir, _m.LastUpdate));
            if (_nextId is 0) _nextId = 9800;   // short ids wrap harmlessly for a benchmark
        }
    }

    public (List<double> walls, Dictionary<string, (int Count, double MeanMs, double P95Ms, double MaxMs, double TotalMs)> snap)
        Measure(int ticks)
    {
        TickProfiler.Configure(enabled: true, reportIntervalSeconds: 1000, csvPath: null);
        var walls = new List<double>(ticks);
        double t = _m.LastUpdate;
        // Warmup (JIT, detector caches) — not measured.
        for (int i = 0; i < 60; i++) { t += 0.053; _m.Update(t); TopUpProjectiles(); }
        // P838 — baseline the GC counters AFTER warmup so the deltas below cover
        // exactly the measured loop. `step:update` is wrapped around
        // Mission.Update with the server's own bucket name so the profiler's
        // P838 per-step allocation attribution fills and the top-alloc table is
        // comparable with a real server run.
        long a0 = GC.GetTotalAllocatedBytes(precise: false);
        double p0 = GC.GetTotalPauseDuration().TotalMilliseconds;
        long g00 = GC.CollectionCount(0), g10 = GC.CollectionCount(1), g20 = GC.CollectionCount(2);
        for (int i = 0; i < ticks; i++)
        {
            var sw = Stopwatch.StartNew();
            TickProfiler.EnterTick();
            t += 0.053;
            using (TickProfiler.Enter("step:update"))
                _m.Update(t);
            TickProfiler.EndTick();
            TopUpProjectiles();
            sw.Stop();
            walls.Add(sw.Elapsed.TotalMilliseconds);
        }
        long allocBytes = GC.GetTotalAllocatedBytes(precise: false) - a0;
        double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - p0;
        long dg0 = GC.CollectionCount(0) - g00, dg1 = GC.CollectionCount(1) - g10, dg2 = GC.CollectionCount(2) - g20;
        double wallSum = walls.Sum();
        Console.WriteLine(
            $"P838-GC: alloc={allocBytes / 1048576.0:0.0}MB ({allocBytes / 1024.0 / ticks:0.0}KB/tick) " +
            $"pause={pauseMs:0.0}ms ({pauseMs / ticks:0.000}ms/tick, {100.0 * pauseMs / wallSum:0.0}% of measured wall) " +
            $"gc gen0={dg0} gen1={dg1} gen2={dg2} ticks={ticks}");
        foreach (var (bucket, bytes) in TickProfiler.SnapshotAlloc().OrderByDescending(kv => kv.Value).Take(8))
            Console.WriteLine($"P838-GC: step-alloc {bucket,-34} {bytes / 1048576.0,8:0.0}MB total  {bytes / 1024.0 / ticks,8:0.0}KB/tick");
        var snap = TickProfiler.Snapshot()
            .ToDictionary(kv => kv.Key, kv => (kv.Value.Count, kv.Value.MeanMs, kv.Value.P95Ms, kv.Value.MaxMs, kv.Value.TotalMs));
        TickProfiler.Configure(enabled: false, reportIntervalSeconds: 1000, csvPath: null);
        return (walls, snap);
    }
}

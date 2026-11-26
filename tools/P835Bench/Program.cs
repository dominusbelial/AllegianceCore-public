using System.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;

// P835 sector benchmark. From the repo with ALLEGIANCE_ARTWORK_PATH set:
//   dotnet run -c Release --project tools/P835Bench -- [ships] [projectiles] [mines] [map] [--legacy-fixture]
// Defaults: 400 ships, 240 replenished rounds, 60 initial mines, m2xspiderw.igc.
// First contact samples passes 1–10 of 20 fresh scenes. Sustained measures passes
// 61–560 in one scene. Between updates, repair surviving ships in the living fixture.
// --legacy-fixture retains the original controls and damage accumulation. Both modes
// finish printing diagnostics and exit 1 if fewer than 95% of ships are flying.

bool legacy = args.Contains("--legacy-fixture");
var positional = args.Where(arg => arg != "--legacy-fixture").ToArray();
if (positional.Length > 4 || positional.Any(arg => arg.StartsWith("--")) ||
    !ReadCount(0, 400, out int shipCount) || !ReadCount(1, 240, out int projectileCount) ||
    !ReadCount(2, 60, out int mineCount))
{
    Console.Error.WriteLine("Usage: P835Bench [nonnegative ships] [nonnegative projectiles] [nonnegative mines] [map] [--legacy-fixture]");
    return 2;
}

bool ReadCount(int index, int fallback, out int value)
{
    value = fallback;
    return index >= positional.Length || int.TryParse(positional[index], out value) && value >= 0;
}

var artRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
if (!Directory.Exists(artRoot))
{
    Console.Error.WriteLine("No artwork. Set ALLEGIANCE_ARTWORK_PATH.");
    return 2;
}

int minimumLive = (int)Math.Ceiling(shipCount * 0.95m);
Console.WriteLine($"P835-BENCH: fixture={(legacy ? "legacy" : "living")} ships={shipCount} projectiles={projectileCount} mines={mineCount} minimumLive={minimumLive} cpu={Environment.ProcessorCount}");
Console.WriteLine($"P928 baseline: serverGC={System.Runtime.GCSettings.IsServerGC} nativeGotoPlan=true nativeShipPhases=false nativeCollisionQueue=false");

var (core, map) = LoadCoreAndMap(artRoot, positional.Length > 3 ? positional[3] : "m2xspiderw.igc");
Console.WriteLine($"P835-BENCH: core={core.HullTypes.Count} hulls, {core.ProjectileTypes.Count} projectiles, {core.MineTypes.Count} mines; map clusters={map.Clusters.Count}");

var hull = PickHull(core, artRoot);
Console.WriteLine($"P835-BENCH: hull={hull.ModelName} scannerRange={hull.ScannerRange}");

ClashBench NewScene() => new(core, map, artRoot, hull, shipCount, projectileCount, mineCount, legacy);

// Exercise the instrumented code on a disposable scene before measuring fresh sightings.
TickProfiler.Configure(true, 3600, null);
NewScene().Run(60, minimumLive);
TickProfiler.Shutdown();

const int contactScenes = 20, contactPasses = 10, sustainedPasses = 500;
var contact = new Measurements("first-contact", contactScenes * contactPasses);
bool valid = true;
for (int scene = 1; scene <= contactScenes; scene++)
    valid &= NewScene().Run(contactPasses, minimumLive, contact, $"contact-{scene}");
contact.Report();

var clash = NewScene();
valid &= clash.Run(60, minimumLive, label: "warm-up");
var sustained = new Measurements("sustained", sustainedPasses);
valid &= clash.Run(sustainedPasses, minimumLive, sustained, "sustained");
sustained.Report();
return valid ? 0 : 1;

static (StaticCore core, MissionMap map) LoadCoreAndMap(string artRoot, string mapName)
{
    StaticCore? core = null; MissionMap? map = null;
    string? mapPath = Directory.EnumerateFiles(artRoot, mapName, SearchOption.AllDirectories).FirstOrDefault();
    if (mapPath is not null)
    {
        var mf = IgcReader.ParseFile(mapPath);
        if (mf.Wrapper == IgcWrapper.DataSize) map = MissionMap.Load(mf);
    }
    // Match the production server's core rather than filesystem enumeration
    // order (the corpus also contains unrelated/older static cores).
    var corePath = Path.Combine(artRoot, "PCore001.igc");
    if (File.Exists(corePath)) core = StaticCore.Load(IgcReader.ParseFile(corePath));
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
    private readonly bool _legacy;
    private int _pass;
    private short _nextId = 9800;

    public ClashBench(StaticCore core, MissionMap map, string artRoot, DataHullTypeIGC hull,
        int shipCount, int projectileCount, int mineCount, bool legacy)
    {
        _legacy = legacy;
        var hulls = new CollisionHullCache(name =>
        {
            var p = Path.Combine(artRoot, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        var sides = new SideSpec[] { new(0, "Alpha", civs[0].CivilizationId), new(1, "Bravo", civs[1].CivilizationId) };
        _m = Mission.Assemble(core, map, sides, new NullIgcSite());
        _m.Stage = MissionStage.Started;
        _m.NativeGotoPlanEnabled = true;
        foreach (var s in _m.Sectors) s.CollisionHulls = hulls;
        _sector = _m.Sectors.OrderByDescending(s => s.Mineable.Count).First();
        // P863 low-power salvo/mine mix, identical in both fixtures.
        _projectileTypes = core.ProjectileTypes.Values.Where(pt => pt.Power > 0f && pt.Speed > 0f)
            .Select(pt => pt with { Power = 0.000001f, BlastPower = 0.000001f }).ToList();
        var mineTypes = core.MineTypes.Values.Where(mt => mt.Power > 0f)
            .Select(mt => mt with { Power = 0.000001f }).ToList();
        _projectileTarget = projectileCount;

        for (int i = 0; i < shipCount; i++)
        {
            var pos = RandomInSphere(SectorRadius);
            var vel = 60f * RandomUnit();
            var ship = new Ship(_nextId++, hull, pos, vel, Orientation.Identity) { SideId = (short)(i % 2) };
            // Opposed turns at cruise throttle keep bare fighters moving within scanner range.
            if (!legacy) ship.Controls = new ControlData(i % 2 == 0 ? 0.5f : -0.5f, 0f, 0f, 0f);
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

    private readonly record struct Population(int Ships, int Moving, int Projectiles, int Mines)
    {
        public override string ToString() => $"ships={Ships} projectiles={Projectiles} mines={Mines} moving={Moving}";
    }

    private Population Census(bool repair)
    {
        int ships = 0, moving = 0, projectiles = 0, mines = 0;
        var models = _sector.Models;
        for (int i = 0; i < models.Count; i++)
        {
            switch (models[i])
            {
                case Ship { Dead: false, StationId: null, ParentShip: null } ship:
                    ships++;
                    if (ship.Velocity.LengthSquared() > 1f) moving++;
                    if (repair) ship.SetFraction(1f);
                    break;
                case Projectile:
                    projectiles++;
                    break;
                case Mine:
                    mines++;
                    break;
            }
        }
        return new(ships, moving, projectiles, mines);
    }

    public bool Run(int ticks, int minimumLive, Measurements? measurements = null, string? label = null)
    {
        var population = Census(repair: false);
        int lowest = population.Ships;
        int? firstLow = lowest < minimumLive ? _pass : null;
        var checkpoints = new List<(int Pass, Population Count)>(12) { (_pass, population) };
        var start = Measurements.Counters.Read();
        for (int i = 0; i < ticks; i++)
        {
            long tickStart = Stopwatch.GetTimestamp();
            TickProfiler.EnterTick();
            using (TickProfiler.Enter("step:update"))
            {
                double nextTime = _m.LastUpdate + 0.053;
                long updateStart = Stopwatch.GetTimestamp();
                _m.Update(nextTime);
                measurements?.Updates.Add(Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds);
            }
            TickProfiler.EndTick();
            TopUpProjectiles();
            measurements?.FixtureWalls.Add(Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds);

            _pass++;
            population = Census(repair: !_legacy);
            lowest = Math.Min(lowest, population.Ships);
            if (population.Ships < minimumLive) firstLow ??= _pass;
            if (_pass is 1 or 5 or 10 or 20 or 24 or 40 || _pass % 100 == 0 || i == ticks - 1)
                checkpoints.Add((_pass, population));
        }
        measurements?.Accumulate(start);
        if (label != null)
        {
            foreach (var (pass, count) in checkpoints)
                Console.WriteLine($"population {label} pass={pass} {count}");
            Console.WriteLine($"population {label} minLive={lowest} required={minimumLive}");
            if (label == "sustained") Console.WriteLine($"P863 population: {population}");
            if (firstLow.HasValue)
                Console.Error.WriteLine($"FAIL population guard: {label} first below 95% at pass={firstLow}, minimum live={lowest}, required={minimumLive}.");
        }
        return firstLow is null;
    }
}

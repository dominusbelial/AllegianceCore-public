using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.P925;

string Option(string key, string fallback) => Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : fallback;
string output = Option("--output", ".scratch/p925");
Directory.CreateDirectory(output);
string art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
var mapPath = Path.Combine(art, "m2xspiderw.igc");
var map = MissionMap.Load(IgcReader.ParseFile(mapPath));
using var fixtureMission = Mission.Assemble(core, map, [new SideSpec(0, "Alpha", 18), new SideSpec(1, "Bravo", 4)], new NullIgcSite());
fixtureMission.ReplaceUnavailableStationTypes();
FixtureMap.PinRocks(fixtureMission, map);
var rocksForFixture = fixtureMission.Sectors.SelectMany(s => s.Asteroids).ToArray();
var options = new JsonSerializerOptions { WriteIndented = true };
using var audit = JsonDocument.Parse(File.ReadAllText(Option("--audit", ".scratch/p925/audit.json")));
if (args.Contains("--loopback"))
{
    Loopback.Run(core, audit.RootElement, output, args.Contains("--native-turn"), args.Contains("--swap"), args.Contains("--strike"), Option("--target-home", "Ganymede"));
    return;
}
var bayCache = new StationLandingBayCache(name => CvhReader.Parse(File.ReadAllText(Path.Combine(art, name + ".cvh"))));
var rows = new List<object>();
foreach (var home in map.Stations)
    foreach (var civ in core.Civilizations.Values.Where(c => c.Name is "Gigacorp" or "Phoenix" or "Omicron"))
    {
        // Reachability catalogue: repeatedly add effects of affordable-in-tech
        // developments/buildings, ignoring cost/time. NoDevTechs is the native
        // no-developments preset, not a mask of forbidden research.
        var allowed = new ResearchSettings().StartingTechs(civ)
            .Or(new TechTreeBitMask(core.StationTypes[civ.InitialStationTypeId].TtbmEffects))
            .Or(new TechTreeBitMask(core.StationTypes[civ.InitialStationTypeId].TtbmLocal));
        for (int pass = 0; pass < 100; pass++)
        {
            var before = allowed;
            foreach (var dev in core.Developments.Values)
                if (new TechTreeBitMask(dev.TtbmRequired).IsSubsetOf(allowed)) allowed = allowed.Or(new TechTreeBitMask(dev.TtbmEffects));
            foreach (var st in core.StationTypes.Values)
                if (new TechTreeBitMask(st.TtbmRequired).IsSubsetOf(allowed))
                    allowed = allowed.Or(new TechTreeBitMask(st.TtbmEffects)).Or(new TechTreeBitMask(st.TtbmLocal));
            if (before.Equals(allowed)) break;
        }
        foreach (var type in core.StationTypes.Values.Where(t => t.StationTypeId == civ.InitialStationTypeId
                     || new TechTreeBitMask(t.TtbmRequired).IsSubsetOf(allowed)).OrderBy(t => t.StationTypeId))
            rows.Add(Geometry(home with { StationTypeId = type.StationTypeId }, type, civ.Name, "candidate-at-authored-home"));
    }
foreach (var st in audit.RootElement.GetProperty("stations").EnumerateArray())
{
    var type = core.StationTypes[st.GetProperty("StationType").GetInt16()];
    var data = new DataStationIGC(Read(st, "StationPosition"), Read(st, "StationUp"), Read(st, "StationForward"),
        [0, 0, 0, 1], st.GetProperty("HomeSector").GetInt16(), st.GetProperty("Side").GetInt16(),
        st.GetProperty("Station").GetInt16(), type.StationTypeId, 255, 255, type.Name);
    rows.Add(new { Match = st.GetProperty("Match").GetInt32(), Geometry = Geometry(data, type, "observed", "pod-destination") });
}
File.WriteAllText(Path.Combine(output, "geometry.json"), JsonSerializer.Serialize(new
{
    Map = "m2xspiderw.igc", Seed = 925, MapSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(mapPath))),
    AuthoredRockCount = map.Asteroids.Count, FixtureRockCount = rocksForFixture.Length,
    RockSource = "authored Alderan rocks plus native file-map top-up pinned to seed 925; not a reconstruction of b63 ambient home-rock draws",
    LaneDefinition = "outward bay ray, length 6000; rock sphere + pod radius + 50; geometric candidate, not a collision proof",
    CandidateDefinition = "fixed-point tech reachability from native starting techs and initial station, union of possible research branches; candidates at authored home, observed expansions separately",
    Pods = core.Civilizations.Values.Where(c => c.Name is "Gigacorp" or "Phoenix" or "Omicron").Select(c =>
    {
        var h = core.HullTypes[c.Lifepod];
        return new { Civ = c.Name, h.HullId, h.ModelName, h.Speed, h.Mass, h.Thrust, h.Length,
            h.MaxTurnRates, h.TurnTorques, h.BackMultiplier, Attributes = c.GasBaseAttributes };
    }).ToArray(),
    Stations = rows,
}, options));
var results = audit.RootElement.GetProperty("samples").EnumerateArray().SelectMany(t => new[] { false, true }
    .Select(native => PodReplay.Run(core, art, t, native))).ToArray();
File.WriteAllText(Path.Combine(output, "replay.json"), JsonSerializer.Serialize(results, options));
Console.WriteLine($"seed=925 geometry={rows.Count} replay={results.Length} docked={results.Count(r => r.Docked)}");

object Geometry(DataStationIGC data, DataStationTypeIGC type, string civ, string placement)
{
    var station = new Station(data, type);
    var bay = bayCache.LaunchGeometry(type)!;
    var center = station.Center;
    var orient = station.Orientation;
    Vector3 World(Vector3 v) => center + v * orient;
    var rocks = rocksForFixture.Where(r => r.ClusterId == data.ClusterId && (V(r.Position) - center).Length() <= 6000).ToArray();
    return new
    {
        Home = map.Clusters.First(c => c.ClusterId == data.ClusterId).Name, Civ = civ, Placement = placement,
        data.StationId, Type = type.StationTypeId, type.Name, type.ClassId, type.ModelName, type.Radius,
        Position = A(center), Forward = A(orient.GetForward()), Up = A(orient.GetUp()),
        Launch = bay.LaunchSlots.Select(s => new { Position = A(World(s.Position)), Direction = A(s.Direction * orient) }).ToArray(),
        Dock = bay.LandSlots.Select((s, i) => new { Slot = i, Capital = i < bay.CapLandSlots,
            Planes = s.Planes.Select(p => new { Position = A(World(p.Position)), Direction = A(p.Direction * orient) }).ToArray(),
            LaneRocks = rocks.Where(r => InLane(V(r.Position), r.Radius,
                World(s.Planes[0].Position), s.Planes[0].Direction.Normalize() * orient)).Select(r => r.AsteroidId).ToArray() }).ToArray(),
        Rocks = rocks.Select(r => new { r.AsteroidId, r.Name, r.Position, r.Radius, Distance = (V(r.Position) - center).Length() }).ToArray(),
        Entries = map.Warps.Where(w => w.ClusterId == data.ClusterId).Select(w =>
        {
            var source = map.Warps.First(x => x.WarpId == w.DestinationId);
            var hull = core.HullTypes[195];
            // Invoke the production pure helper; access is intentionally confined
            // to this proof tool, with no new native-rule implementation.
            var apply = typeof(Mission).Assembly.GetType("Allegiance.Core.Model.AlephExit")!
                .GetMethod("Apply", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var exit = apply.Invoke(null, [new Orientation(), V(source.Position), source.Radius,
                V(w.Position), V(w.Forward), V(source.Position), -V(source.Forward) * 100,
                hull.Mass, hull.Length / 2, core.Constants.FloatConstants[3], new Random(925 ^ w.WarpId)])!;
            var position = (Vector3)exit.GetType().GetProperty("Position")!.GetValue(exit)!;
            float bound = hull.Length / 2 + 5 + MathF.Sqrt(50);
            float distance = (V(w.Position) - center).Length();
            return new
            {
                w.WarpId, w.Position, w.Radius,
                PartnerSector = map.Clusters.First(c => c.ClusterId == source.ClusterId).Name,
                Distance = distance, EntryExample = A(position), EntryExampleDistance = (position - center).Length(),
                EntryDefinition = "production AlephExit; hull 195, centre crossing at speed 100, seed=925 xor warpId; not the b63 entry pose",
                EntryDistanceEnvelope = new[] { Math.Max(0, distance - bound), distance + bound },
                InsideStandoffAtMouth = distance <= 3500,
                InsideGatherAtMouth = distance <= 3600,
                ToStandoff = Math.Max(0, distance - 3500),
            };
        }).ToArray(),
    };
}
static bool InLane(Vector3 p, float radius, Vector3 mouth, Vector3 direction)
{
    var d = p - mouth;
    float along = d * direction;
    return along >= 0 && along <= 6000 && (d - direction * along).Length() <= radius + 2 + 50;
}
static float[] Read(JsonElement e, string key) => e.GetProperty(key).EnumerateArray().Select(x => x.GetSingle()).ToArray();
static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
static float[] A(Vector3 v) => [v.X, v.Y, v.Z];

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;

// Local, socket-free 15v15 NPC travel/economy comparison. No MCP pilot changes.
// Usage: dotnet run --project tools/p817-gotoplan-proof -c Release -- --seconds 2400 --output result.json
int seconds = Option("--seconds") is { } duration ? int.Parse(duration, CultureInfo.InvariantCulture) : 2400;
if (seconds <= 10) throw new ArgumentException("--seconds must exceed the 10-second timing warmup.");
string output = Option("--output") ?? "p817-comparison.json";
var writer = Console.Out;
var measurements = new List<Measurement>();
try
{
    Console.SetOut(TextWriter.Null);
    foreach (bool enabled in new[] { false, true }) measurements.Add(Run(enabled, seconds));
}
finally { Console.SetOut(writer); }
var json = JsonSerializer.Serialize(measurements.ToArray(), ProofJson.Default.MeasurementArray);
File.WriteAllText(output, json + "\n");
Console.WriteLine(json);
return;

string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static Measurement Run(bool enabled, int seconds)
{
    using var server = new Server();
    server.Boot(new ServerConfig
    {
        ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        UpdatesPerSecond = 20, BenchmarkSeed = 817,
        Sim = new() { NativeGotoPlan = enabled },
        LiveSettings = new() { TimeDilation = "off" },
        Games = [new() { Name = "P817 local 15v15 NPC comparison", Map = "m2xspiderw.igc", Teams = 2,
            CivIds = [25, 3], SideNames = ["Alpha", "Bravo"], ShowMap = true }]
    });
    var mission = server.Missions[0];
    mission.Stage = MissionStage.Started;
    mission.SeedStartingVisibility();
    // This control/economy comparison starts with a scouted field. Scouting
    // and MCP client pilots are outside the experiment.
    foreach (var sector in mission.Sectors)
        foreach (var rock in sector.Mineable)
            foreach (var side in mission.Sides) rock.SideVisibility.SetSideVisibility(side.Id, true);
    if (mission.NativeGotoPlanEnabled != enabled)
        throw new InvalidOperationException("Unset ALLEGIANCE_SIM_NATIVE_GOTOPLAN for a config-based A/B run.");
    var miners = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>())
        .Where(s => s.PilotType == Ship.PilotTypeMiner).Select(s => new Miner(s)).ToArray();
    var drone = mission.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeWingman);
    var builders = new List<Ship>();
    for (short side = 0; side < 2; side++)
    {
        var home = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == side));
        var station = home.StationRuntimes.First(st => st.SideId == side);
        for (int i = 0; i < 13; i++)
        {
            // NPC wingmen exercise the same cross-sector travel alongside miners;
            // native combat takes over on encountering an enemy in Alderan.
            var ship = server.SpawnNpcShip(mission, home, side,
                station.Center + new Vector3(2000 + i * 150, 1000, 1000), drone.DroneTypeId);
            ship.GotoWaypoint = new(GotoKind.Cluster, 1052, Vector3.Zero);
        }
        var builderDrone = mission.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeBuilder);
        var buildType = mission.Core.StationTypes.Values.First(t => t.ClassId == 0 && t.AabmBuild != 0);
        // Home clusters on this map have no buildable rocks. Add one native-sized
        // rotating construction fixture per side to compare actual arrivals.
        var rockPosition = station.Center + new Vector3(-3000, 2000, 2500);
        var rock = new Asteroid(new DataAsteroidIGC(1,
            [rockPosition.X, rockPosition.Y, rockPosition.Z], [0, 1, 0], [0, 0, 1], [0, 1, 0, 0],
            0, 0, buildType.AabmBuild, (short)(29000 + side), 0, 200, "roid.mdl", "", "", home.Id, "P817 construction", 0));
        mission.PlaceAsteroid(rock, home);
        rock.SideVisibility.SetSideVisibility(side, true);
        {
            var builder = server.SpawnNpcShip(mission, home, side,
                new Vector3(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]) + new Vector3(1500, 900, 500), builderDrone.DroneTypeId);
            builder.BuilderStationType = buildType; builder.BuilderAsteroidTarget = rock;
            builders.Add(builder);
        }
    }
    int constructorArrivals = 0;
    var arrived = new HashSet<short>();
    var ticks = new double[seconds * 20];
    var allocations = new long[ticks.Length];
    var loadedTrips = new List<double>(); var outboundTrips = new List<double>();
    double delivered = 0;
    double simStartedAt = mission.LastUpdate;
    for (int i = 0; i < ticks.Length; i++)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        server.TickSim();
        ticks[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        allocations[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        double now = mission.LastUpdate;
        foreach (var miner in miners)
        {
            var s = miner.Ship;
            if (miner.WasDocked && s.StationId is null) miner.LaunchedAt = now;
            if (s.IsMining && !miner.WasMining && miner.LaunchedAt > 0)
            { outboundTrips.Add(now - miner.LaunchedAt); miner.LaunchedAt = 0; }
            if (miner.Ore > 0 && s.Ore <= 0 && s.StationId is not null)
            {
                delivered += miner.Ore;
                if (miner.DepartedAt > 0) loadedTrips.Add(now - miner.DepartedAt);
                miner.DepartedAt = 0;
            }
            if (s.Ore >= s.EffectiveMineCapacity() - .001 && s.MiningAsteroidTarget is null && miner.DepartedAt == 0)
                miner.DepartedAt = now;
            miner.Ore = s.Ore; miner.WasDocked = s.StationId is not null; miner.WasMining = s.IsMining;
        }
        foreach (var builder in builders)
            if ((builder.IsDrilling || builder.IsBuilding) && arrived.Add(builder.Id)) constructorArrivals++;
    }
    // Exclude the first 10 seconds from timing/allocation statistics, retaining
    // the whole run for delivered ore and travel durations.
    var steadyTicks = ticks.Skip(200).Order().ToArray();
    var steadyAllocations = allocations.Skip(200).Order().ToArray();
    double elapsedSimSeconds = mission.LastUpdate - simStartedAt;
    return new(enabled, 817, elapsedSimSeconds, miners.Length + 26 + builders.Count, miners.Length, miners.Count(m => m.Ship.Dead),
        delivered, delivered * 60 / elapsedSimSeconds, outboundTrips.Count, Mean(outboundTrips), loadedTrips.Count, Mean(loadedTrips),
        builders.Count, constructorArrivals, steadyTicks.Average(), steadyTicks[(int)(steadyTicks.Length * .95)],
        steadyAllocations.Average(), steadyAllocations[(int)(steadyAllocations.Length * .95)]);
}
static double Mean(List<double> values) => values.Count == 0 ? 0 : values.Average();

sealed class Miner(Ship ship)
{
    public Ship Ship { get; } = ship;
    public float Ore;
    public bool WasDocked = ship.StationId is not null, WasMining;
    public double LaunchedAt, DepartedAt;
}
internal sealed record Measurement(bool NativeGotoPlan, int Seed, double SimSeconds, int InitialNpcShips, int Miners,
    int MinerDeaths, double He3Delivered, double He3PerMinute, int OutboundTrips, double OutboundTripMeanSeconds,
    int LoadedTrips, double LoadedTripMeanSeconds, int Constructors, int ConstructorArrivals,
    double TickMeanUs, double TickP95Us, double TickAllocationMeanBytes, long TickAllocationP95Bytes);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Measurement[]))]
internal partial class ProofJson : JsonSerializerContext;

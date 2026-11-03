using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Srv = Allegiance.Server.Server;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.WriteLine($"P923 serverGc={System.Runtime.GCSettings.IsServerGC}; local only; native transport/broadcast defaults");
Cost();
if (args.Contains("--cost-only", StringComparer.Ordinal)) return;
foreach (bool legacy in new[] { false, true })
foreach (string mode in new[] { "ordinary", "exhausted-home", "isolated-target" })
foreach (int seed in new[] { 923, 924, 925 })
foreach (bool enabled in new[] { false, true })
    Observe(seed, enabled, legacy, mode);

ServerConfig Config(int seed, bool enabled) => new()
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    LiveSettings = new() { SeenWarpRouting = enabled, TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
    Games = [new() { Name = "P923 local observation", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
        MapGen = new() { Type = "SingleRing", MapSize = 1, Seed = seed } }],
};

void Cost()
{
    using var server = new Srv { SpawnInitialMinersAtBoot = false };
    server.Boot(Config(923, false));
    var m = server.Missions[0];
    var start = m.Sectors[0]; var target = m.Sectors[m.Sectors.Count / 2];
    var ship = Spawn(server, m, start, 0, Vector3.Zero);
    foreach (var warp in m.Warps) warp.SideVisibility.SetSideVisibility(0, true);
    int expected = start.FindPathToward(target, ship)?.Warp.WarpId ?? -1;
    Console.WriteLine($"COST graph={m.Sectors.Count}sectors/{m.Warps.Count}directed-warps all-known from={start.Id} to={target.Id} warp={expected}");
    foreach (int round in Enumerable.Range(0, 7))
    foreach (bool enabled in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
    {
        m.SeenWarpRoutingEnabled = enabled;
        for (int i = 0; i < 2000; i++) start.FindPathToward(target, ship);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        const int count = 20000;
        int sum = 0;
        for (int i = 0; i < count; i++) sum += start.FindPathToward(target, ship)?.Warp.WarpId ?? -1;
        watch.Stop();
        if (sum != expected * count) throw new InvalidOperationException("All-known route changed");
        Console.WriteLine($"COST round={round} flag={enabled} calls={count} ns/call={watch.Elapsed.TotalNanoseconds / count:F1} bytes/call={(GC.GetAllocatedBytesForCurrentThread() - before) / (double)count:F1}");
    }
}

void Observe(int seed, bool enabled, bool legacy, string mode)
{
    using var server = new Srv();
    server.Boot(Config(seed, enabled));
    var m = server.Missions[0];
    if (legacy) m.NativeGotoPlanEnabled = false; // explicit old-path diagnostic, never a performance baseline
    if (!legacy && !m.NativeGotoPlanEnabled) throw new InvalidOperationException("Native baseline required");
    m.Stage = MissionStage.Started;
    m.SeedStartingVisibility();
    if (mode != "ordinary")
    {
        // Controlled home depletion: distant neutral rocks were reported.
        foreach (var s in m.Sectors.Where(s => s.StationRuntimes.Count > 0))
            foreach (var rock in s.Mineable.Where(r => r.CanMineHe3)) rock.MineOre(float.MaxValue);
        foreach (var s in m.Sectors.Where(s => s.StationRuntimes.Count == 0))
            foreach (var rock in s.Mineable.Where(r => r.CanMineHe3))
                foreach (short side in new short[] { 0, 1 }) rock.SideVisibility.RevealToSide(side);
    }
    // A 15v15 population, including each side's real initial miner. Wingmen
    // use ordinary server decision/flight updates; there is no forced discovery.
    for (short side = 0; side < 2; side++)
    {
        var home = m.GetCluster(m.GetStations().First(s => s.SideId == side).Data.ClusterId)!;
        short wingman = m.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeWingman).DroneTypeId;
        for (int i = 0; i < 14; i++)
            Spawn(server, m, home, side, new(1000 + 300 * i, 6000, 0), wingman);
    }
    var miners = m.GetShips().Where(s => s.IsNpc && s.PilotType == Ship.PilotTypeMiner).ToArray();
    if (mode == "isolated-target")
    {
        // A deliberately isolated known mining contact: command each miner to
        // a real rock in a sector without a seen route. Measure the consequence
        // separately from naturally selected orders; do not call this a natural
        // incidence estimate. No fallback or discovery is injected afterwards.
        m.SeenWarpRoutingEnabled = true;
        foreach (var miner in miners)
        {
            var remote = m.Sectors.First(s => s != miner.Sector && s.Mineable.Any(r => r.CanMineHe3)
                && miner.Sector!.FindPathToward(s, miner) is null);
            var rock = remote.Mineable.First(r => r.CanMineHe3);
            rock.SideVisibility.RevealToSide(miner.SideId!.Value);
            miner.MiningAsteroidTarget = rock;
        }
        m.SeenWarpRoutingEnabled = enabled;
    }
    var blockedMiners = new HashSet<short>(); var knownBlockedMiners = new HashSet<short>();
    var blockedSince = new Dictionary<short, double>();
    int observations = 0, cross = 0, noRoute = 0, visibleRockNoRoute = 0, hiddenRock = 0;
    double longest = 0; float sold = 0;
    var previousOre = miners.ToDictionary(s => s.Id, s => s.Ore);
    for (int tick = 0; tick < 12000; tick++) // 600 simulation seconds at 20 Hz
    {
        server.TickSim();
        foreach (var miner in miners)
        {
            if (!miner.Dead && miner.StationId is not null && previousOre[miner.Id] > miner.Ore)
                sold += previousOre[miner.Id] - miner.Ore;
            previousOre[miner.Id] = miner.Ore;
        }
        if (tick % 20 != 0) continue;
        foreach (var miner in miners)
        {
            if (miner.Dead || miner.StationId is not null || miner.Sector is not { } sector) continue;
            observations++;
            var rock = miner.MiningAsteroidTarget;
            short? targetId = miner.PlanTarget?.ClusterId ?? rock?.Data.ClusterId;
            if (targetId is not { } id || id == sector.Id) { blockedSince.Remove(miner.Id); continue; }
            var target = m.GetCluster(id);
            if (target is null) continue;
            cross++;
            // Observe the discovery-aware BFS in either A/B arm, restoring the
            // setting before the next simulation tick. No visibility is written.
            m.SeenWarpRoutingEnabled = true;
            bool blocked = sector.FindPathToward(target, miner) is null;
            m.SeenWarpRoutingEnabled = enabled;
            if (!blocked) { blockedSince.Remove(miner.Id); continue; }
            noRoute++; blockedMiners.Add(miner.Id);
            if (!blockedSince.TryGetValue(miner.Id, out double since)) blockedSince[miner.Id] = since = server.SimNow;
            longest = Math.Max(longest, server.SimNow - since);
            if (rock is not null && miner.SideId is { } sid)
            {
                if (rock.SeenBySide(sid)) { visibleRockNoRoute++; knownBlockedMiners.Add(miner.Id); }
                else hiddenRock++;
            }
        }
    }
    Console.WriteLine($"OBS seed={seed} mode={mode} planner={(legacy ? "legacy" : "native")} flag={enabled} sim={server.SimNow:F1} "
        + $"sectors={m.Sectors.Count} warps={m.Warps.Count} miners={miners.Length} samples={observations} cross={cross} "
        + $"noSeenRoute={noRoute} seenRockNoRoute={visibleRockNoRoute} hiddenRock={hiddenRock} "
        + $"blockedMiners={blockedMiners.Count} seenRockBlockedMiners={knownBlockedMiners.Count} longest={longest:F1} soldOre={sold:F1} "
        + $"stage={m.Stage} simFailures={server.SimPassStageFailures}");
    if (server.SimPassStageFailures != 0) throw new InvalidOperationException("Simulation stage failed");
}

static Ship Spawn(Srv server, Mission mission, Sector sector, short side, Vector3 position, short? drone = null) =>
    (Ship)typeof(Srv).GetMethod("SpawnNpcShip", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(server, [mission, sector, side, position, drone])!;

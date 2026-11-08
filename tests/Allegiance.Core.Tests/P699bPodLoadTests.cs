using System.Reflection;
using Allegiance.ClientCore.Flight;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

// Fixed seed 23 replays the worst legacy failure in P958's sweep. Strict
// per-pod checks remain; this is not an all-seed throughput guarantee.
[Trait("Tier", "Live")]
public sealed class P699bPodLoadTests(ITestOutputHelper output)
{
    internal const int SimulationSeed = 23;

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic() => Run(false);

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Hundred_pods_per_side_return_with_native_rescue_capable_launch_traffic() => Run(true);

    private void Run(bool rescueTraffic)
    {
        int seed = int.TryParse(Environment.GetEnvironmentVariable("P958_SEED"), out int selected) ? selected : SimulationSeed;
        bool legacySteering = Environment.GetEnvironmentVariable("P958_LEGACY_STEERING") == "1";
        Validate(Simulate(rescueTraffic, seed, legacySteering));
    }

    internal sealed record PodRun(bool RescueTraffic, PodTripTelemetry.Result[] Results,
        int Launched, long SimFailures, Dictionary<int, int> BayUsage)
    {
        internal string Signature => string.Join('\n', Results.OrderBy(r => r.Ship)
            .Select(r => System.Text.Json.JsonSerializer.Serialize(r, PodTripJsonContext.Default.Result)));
    }

    internal PodRun Simulate(bool rescueTraffic, int seed, bool legacySteering = false)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(seed) };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new() { Name = "P699b load", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }]
        });
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        output.WriteLine($"P958_RANDOM seed={seed} serverGc={System.Runtime.GCSettings.IsServerGC}");
        // File maps retain their authored station types regardless of selected civs.
        // Use each side's actual initial garrison, as generated maps and the rig do.
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.StationRuntimes.ToArray())
            {
                var civ = mission.Sides.Single(s => s.Id == station.SideId).Civilization;
                var type = mission.Core.StationTypes[civ.InitialStationTypeId];
                sector.RemoveStation(station);
                mission.PlaceStation(new Station(station.Data with { StationTypeId = type.StationTypeId }, type), sector);
            }
        var start = mission.Sectors.Single(s => s.Id == 1052);
        var warps = mission.Warps.ToDictionary(w => w.Warp.WarpId, w => w.Warp);
        var hull = mission.Core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod)
            && h.MaxFixedWeapons > 0);
        var launchHull = rescueTraffic ? hull : mission.Core.HullTypes.Values.First(h => h.Speed > 0
            && h.Length < 100 && !h.HasCapability(DataHullTypeIGC.HabmLifepod)
            && !h.HasCapability(DataHullTypeIGC.HabmRescue));
        var homes = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Where(st => st.Type.HasCapability(DataStationTypeIGC.SabmRestart)).GroupBy(st => st.SideId)
            .ToDictionary(g => g.Key, g => g.First());
        var pods = new List<(Ship Ship, Station Home)>();
        var release = new Dictionary<Ship, int>();
        var eject = typeof(Srv).GetMethod("EjectSurvivor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var random = new Random(699);
        var outbound = homes.ToDictionary(kv => kv.Key, kv => start.Warps
            .Where(w => w.Destination!.Warps.Any(next => next.Destination!.Id == kv.Value.Data.ClusterId))
            .OrderBy(w => w.Warp.WarpId).ToArray());
        // Actual ejection helper, native pod hull, deadline and random burst;
        // only controls are driven synchronously to separate flight from socket cadence.
        for (short side = 0; side < 2; side++)
            for (int i = 0; i < 100; i++)
            {
                // Sample both two-hop return mouths, starting outside transit
                // range as in P699. Their native travel budget is sufficient;
                // arbitrary far-side sector starts need not fit 300 seconds.
                var mouth = outbound[side][i % outbound[side].Length].Warp;
                var center = new Vector3(mouth.Position[0], mouth.Position[1], mouth.Position[2]);
                var p = center - center.Normalize() * (mouth.Radius + 350)
                    + new Vector3(random.Next(-50, 51), random.Next(-50, 51), random.Next(-50, 51));
                var ship = new Ship((short)(20000 + side * 100 + i), hull, p, Vector3.Zero, Orientation.Identity) { SideId = side };
                release[ship] = i / 25 * 800; // 50-pod waves; native crowding remains enabled
                pods.Add((ship, homes[side]));
            }
        var traceIds = (Environment.GetEnvironmentVariable("P958_TRACE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(short.Parse).ToHashSet();
        static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
        void Trace(string kind, object data) => output.WriteLine("P958_TRACE " + kind + " " + System.Text.Json.JsonSerializer.Serialize(data));
        if (traceIds.Count > 0) mission.CollisionResolved = c =>
        {
            if (!traceIds.Contains(c.Ship.Id)) return;
            string other = c.Other switch { Ship sh => $"ship:{sh.Id}", Station st => $"station:{st.StationId}", Asteroid a => $"asteroid:{a.Data.AsteroidId}", _ => c.Other.GetType().Name };
            Trace("collision", new { now = server.SimNow, ship = c.Ship.Id, other, sector = c.Ship.Sector?.Id,
                c.CoincidentCenters, p = V(c.Position), v = V(c.Velocity), contact = V(c.ContactPosition), separated = V(c.SeparatedPosition),
                separation = V(c.SeparatedPosition - c.ContactPosition), deltaVelocity = V(c.NewVelocity - c.Velocity), afterVelocity = V(c.NewVelocity) });
        };
        if (traceIds.Count > 0) ((ServerHost)mission.Site).ShipWarped += (ship, from, to) =>
        {
            if (traceIds.Contains(ship.Id)) Trace("warp", new { now = server.SimNow, ship = ship.Id, from = from.Id, to = to.Id,
                alreadyHome = from.Id == homes[ship.SideId!.Value].Data.ClusterId, p = V(ship.Position), v = V(ship.Velocity) });
        };
        var results = new Dictionary<short, PodTripTelemetry.Result>();
        server.PodTrips.Completed += result => results[result.Ship] = result;
        var navigationObstacles = new List<NativeGotoPlan.Obstacle>();
        var bayUsage = new Dictionary<int, int>();
        var lastBay = new Dictionary<short, int>();
        int launched = 0;
        var traffic = new List<Ship>();
        bool baseline = Environment.GetEnvironmentVariable("P699B_BASELINE") == "1";
        legacySteering |= baseline;
        var launch = typeof(Srv).GetMethod("UndockShipState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = start.StationLandingBays!;
        for (int tick = 0; tick < 8500; tick++)
        {
            foreach (var (ship, home) in pods)
            {
                if (tick == release[ship])
                {
                    mission.PlaceShip(ship, start);
                    Assert.True((bool)eject.Invoke(server, [ship, null, ship.Position, Vector3.Zero])!);
                    Assert.Equal(300, ship.LifepodTimeToDie!.Value - mission.LastUpdate, 5);
                }
                if (results.ContainsKey(ship.Id) || ship.Dead || ship.Sector is null) continue;
                if (ship.StationId is not null) continue;
                if (ship.Sector.Id == home.Data.ClusterId)
                {
                    var geom = cache.Bay(home.Type)!;
                    var bays = geom.LandSlots.Select(s => new DockApproach.DockBay(s.Planes[0].Position * home.Orientation,
                        s.Planes[0].Direction.Normalize() * home.Orientation)).ToArray();
                    float speed = ship.Velocity.Length();
                    float rest = baseline ? speed * speed / (2 * ship.HullType.Thrust / ship.Mass)
                        : DockApproach.StoppingDistance(speed, ship.EffectiveThrust(), ship.Mass, ship.EffectiveMaxSpeed(), ship.HullType.BackMultiplier);
                    var step = DockApproach.DockStation(ship.Position, ship.Velocity, ship.Orientation, home.Center,
                        bays, bays.Length, rest, speed > .1f ? ship.Position + ship.Velocity * (rest / speed) : ship.Position,
                        home.Radius + ship.HullType.Length / 2 + 10, ship.TurnRates[ControlData.AxisYaw],
                        ship.TurnRates[ControlData.AxisPitch], false, baseline ? 0 : ship.Id);
                    if (legacySteering)
                    {
                        ship.Controls = DockApproach.Steer(step, ship.Position, ship.Orientation, ship.Velocity, rest, dt: .05f, turnState: DockApproach.TurnState.FromShip(ship));
                        ship.StateM = DockApproach.MovementButtons(step, ship.Position, ship.Orientation, ship.Velocity, DockApproach.TurnState.FromShip(ship));
                    }
                    else
                    {
                        var target = new NativeGotoPlan.Target(Ship.NativeKey(NativeGotoPlan.Kind.Station, home.StationId),
                            home.Data.ClusterId, NativeGotoPlan.Kind.Station, NativeGotoPlan.Objective.Enter, home.Center, home.Radius);
                        var navigation = NativeControls(ship, target, bays, navigationObstacles);
                        ship.Controls = navigation.Controls;
                        ship.StateM = navigation.Buttons;
                        step = navigation.Step;
                    }
                    lastBay[ship.Id] = step.BayIndex;
                    if (traceIds.Contains(ship.Id) && tick % 10 == 0)
                        Trace("dock", new { now = server.SimNow, ship = ship.Id, step.Mask, step.BayIndex, goal = V(step.Goal), facing = V(step.Facing), rest });
                    if (step.IsEnter()) bayUsage[step.BayIndex] = bayUsage.GetValueOrDefault(step.BayIndex) + 1;
                }
                else
                {
                    DataWarpIGC? mouth;
                    if (baseline)
                    {
                        // P699's sector-id ordered BFS tie choice.
                        var graph = new AllegianceRouteGraph(mission);
                        short next = graph.Next(ship.Sector.Id, home.Data.ClusterId);
                        mouth = ship.Sector.Warps.First(w => w.Destination!.Id == next).Warp;
                    }
                    else mouth = PodReturnNavigation.NextWarp(warps, ship.Sector.Id, home.Data.ClusterId, ship.Position, home.Center);
                    if (traceIds.Contains(ship.Id) && tick % 10 == 0)
                        Trace("route", new { now = server.SimNow, ship = ship.Id, sector = ship.Sector.Id, home = home.Data.ClusterId, mouth = mouth?.WarpId });
                    Assert.NotNull(mouth);
                    var w = mouth!.Value;
                    if (legacySteering)
                    {
                        // Original P699b: MCP transit control without native Dodge.
                        ship.Controls = AutopilotSteering.Pursue(ship.Position, ship.Orientation,
                            new(w.Position[0], w.Position[1], w.Position[2]));
                    }
                    else
                    {
                        var target = new NativeGotoPlan.Target(Ship.NativeKey(NativeGotoPlan.Kind.Warp, w.WarpId),
                            ship.Sector.Id, NativeGotoPlan.Kind.Warp, NativeGotoPlan.Objective.Enter,
                            new(w.Position[0], w.Position[1], w.Position[2]), w.Radius);
                        var navigation = NativeControls(ship, target, [], navigationObstacles);
                        ship.Controls = navigation.Controls;
                        ship.StateM = navigation.Buttons;
                    }
                }
            }
            // Continuous physical launch traffic through production launch frames.
            if (tick >= 1200 && tick < 8000 && tick % 100 == 0)
                foreach (var home in homes.Values)
                {
                    var ship = new Ship((short)(25000 + launched), launchHull, home.Center, Vector3.Zero, Orientation.Identity)
                        { SideId = home.SideId, StationId = home.StationId };
                    var sector = mission.Sectors.Single(s => s.Id == home.Data.ClusterId);
                    mission.PlaceShip(ship, sector); launch.Invoke(server, [ship, sector]);
                    ship.Controls = new(0, 0, 0, 1); traffic.Add(ship); launched++;
                }
            foreach (var ship in traffic.Where(s => !s.Dead && s.Sector is not null && s.StationId is null))
            {
                if (ship.Sector == start) { start.Remove(ship); continue; } // outbound traffic has reached the battle
                if (PodReturnNavigation.NextWarp(warps, ship.Sector!.Id, start.Id, ship.Position) is { } mouth)
                {
                    ship.Controls = AutopilotSteering.Pursue(ship.Position, ship.Orientation,
                        new(mouth.Position[0], mouth.Position[1], mouth.Position[2]));
                }
            }
            // These player ships have no client, so the harness supplies what a pod's
            // client does: an accepted SHIP_UPDATE at the native 0.15 s cadence
            // (clintlib.cpp:1832,1858-1863). Since P788c, players re-arm after a warp
            // only through accepted updates (fsship.cpp:79-82,1185-1191).
            if (tick % 3 == 0)
                foreach (var flyer in pods.Select(p => p.Ship).Concat(traffic))
                    if (!flyer.Dead && flyer.Sector is not null && flyer.StationId is null)
                        server.AdvanceWarpHandshakeOnAcceptedUpdate(flyer);
            foreach (var (pod, home) in pods)
                if (traceIds.Contains(pod.Id) && !results.ContainsKey(pod.Id) && pod.Sector is { } current && tick % 10 == 0)
                    Trace("pose", new { now = server.SimNow, ship = pod.Id, sector = current.Id, p = V(pod.Position), v = V(pod.Velocity),
                        stationDistance = (pod.Position - home.Center).Length(), pod.StateM,
                        warps = current.Warps.Select(w => new { id = w.Warp.WarpId, to = w.Destination?.Id, distance = (pod.Position - new Vector3(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2])).Length(), radius = w.Warp.Radius }).ToArray() });
            server.TickSim();
            if (results.Count == 200) break;
        }
        foreach (var r in results.Values.Where(r => r.Outcome is not ("docked" or "rescued")))
            output.WriteLine($"FAILED ship={r.Ship} outcome={r.Outcome} route={string.Join(',', r.Route.Select(v => v.Sector))} homeAt={r.HomeAt} radiusAt={r.RadiusAt} endPosition={string.Join(',', r.EndPosition)} seconds={r.Seconds}");
        server.PodTrips.Flush(server.SimNow);
        var times = results.Values.Where(r => r.Outcome is "docked" or "rescued").Select(r => r.Seconds).Order().ToArray();
        output.WriteLine($"baseline={baseline} legacySteering={legacySteering} rescueTraffic={rescueTraffic} docked={results.Values.Count(r => r.Outcome == "docked")} rescued={results.Values.Count(r => r.Outcome == "rescued")} total={times.Length}/200 oxygen={results.Values.Count(r => r.Outcome == "oxygen")} other={results.Values.Count(r => r.Outcome != "docked" && r.Outcome != "rescued" && r.Outcome != "oxygen")} homeReached={results.Values.Count(r => r.HomeAt is not null)} launchTraffic={launched} median={(times.Length > 0 ? times[times.Length / 2] : 0):F2} p95={(times.Length > 0 ? times[(int)(times.Length * .95)] : 0):F2} bays={string.Join(",", bayUsage.Select(x => $"{x.Key}:{x.Value}"))}");
        foreach (var result in results.Values.OrderBy(r => r.Ship))
            output.WriteLine("P958_POD " + System.Text.Json.JsonSerializer.Serialize(result, PodTripJsonContext.Default.Result));
        output.WriteLine("P958_BAYS " + string.Join(",", results.Values.Where(r => r.Outcome == "docked")
            .GroupBy(r => (r.Side, Bay: lastBay.GetValueOrDefault(r.Ship, -1)))
            .OrderBy(g => g.Key.Side).ThenBy(g => g.Key.Bay).Select(g => $"side{g.Key.Side}/bay{g.Key.Bay}:{g.Count()}")));
        return new(rescueTraffic, results.Values.OrderBy(r => r.Ship).ToArray(), launched,
            server.SimPassStageFailures, bayUsage);
    }

    // Use the same native controller and Ignore-filtered obstacle packing as
    // production drones. GotoPlan gives Dodge first priority (common.cpp:1999-2020),
    // including on the station approach; Enter | NoDodge still charges the bay.
    internal static NativeGotoPlan.Result NativeControls(Ship ship, NativeGotoPlan.Target target,
        DockApproach.DockBay[] bays, List<NativeGotoPlan.Obstacle>? obstacles = null)
    {
        var mover = new NativeGotoPlan.Mover(ship.Position, ship.Velocity, ship.Orientation, ship.Radius,
            DockApproach.TurnState.FromShip(ship), ship.EffectiveMaxTurnRate(ControlData.AxisRoll),
            ship.EffectiveTurnTorque(ControlData.AxisRoll), ship.TurnRates[ControlData.AxisRoll]);
        obstacles ??= [];
        ship.FillNativeObstacles(ship.Sector!, obstacles);
        return NativeGotoPlan.Execute(mover, target, bays, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(obstacles),
            .05f, ship.HullType.BackMultiplier);
    }

    internal static void Validate(PodRun run)
    {
        var results = run.Results;
        var times = results.Where(r => r.Outcome is "docked" or "rescued").Select(r => r.Seconds).Order().ToArray();
        Assert.True(run.Launched >= 20);
        Assert.Equal(200, results.Length);
        Assert.DoesNotContain(results, r => r.Outcome != "docked" && r.Outcome != "rescued" && r.Outcome != "oxygen");
        Assert.Equal(0, results.Count(r => r.Outcome == "oxygen"));
        Assert.DoesNotContain(results, r => r.Outcome == "oxygen" && r.RadiusAt is not null);
        Assert.All(results, r => Assert.Equal(300, r.OxygenAllowance, 5));
        if (!run.RescueTraffic)
        {
            Assert.All(results, r => { Assert.Equal("docked", r.Outcome); Assert.True(r.Route.Length >= 3);
                Assert.NotNull(r.HomeAt); Assert.NotNull(r.RadiusAt); Assert.NotNull(r.BayAt); });
        }
        Assert.True(times[^1] < 300);
        Assert.Equal(0, run.SimFailures);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Hundred_pods_in_one_bay_dock_in_one_tick_without_slot_occupancy()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new() { Name = "P699b bay", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [25, 25], SideNames = ["Alpha", "Bravo"] }]
        });
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        var sector = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 0));
        var home = sector.StationRuntimes.First(st => st.SideId == 0);
        var entry = sector.StationLandingBays!.Bay(home.Type)!.LandSlots[0].Planes[0];
        var position = home.Center + (entry.Position + entry.Direction.Normalize() * 20) * home.Orientation;
        var pods = Enumerable.Range(0, 100).Select(i => new Ship((short)(20000 + i), mission.Core.HullTypes[444],
            position, Vector3.Zero, Orientation.Identity) { SideId = 0 }).ToArray();
        foreach (var pod in pods) { mission.PlaceShip(pod, sector); pod.ChangeHull(pod.HullType); }
        server.TickSim();
        Assert.All(pods, pod => { Assert.Equal(home.StationId, pod.StationId); Assert.False(pod.Dead); });
        Assert.Equal(0, server.SimPassStageFailures);
    }

    private sealed class AllegianceRouteGraph(Mission mission)
    {
        internal short Next(short current, short target)
        {
            var queue = new Queue<(short Sector, short First)>();
            var seen = new HashSet<short> { current };
            queue.Enqueue((current, current));
            while (queue.TryDequeue(out var v))
                foreach (var w in mission.Sectors.Single(s => s.Id == v.Sector).Warps.OrderBy(w => w.Destination!.Id))
                    if (seen.Add(w.Destination!.Id))
                    {
                        short first = v.Sector == current ? w.Destination.Id : v.First;
                        if (w.Destination.Id == target) return first;
                        queue.Enqueue((w.Destination.Id, first));
                    }
            throw new InvalidOperationException("unreachable home");
        }
    }
}

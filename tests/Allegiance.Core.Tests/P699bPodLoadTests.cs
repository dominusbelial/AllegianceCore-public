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

[Trait("Tier", "Live")]
public sealed class P699bPodLoadTests(ITestOutputHelper output)
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic() => Run(false);

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Hundred_pods_per_side_return_with_native_rescue_capable_launch_traffic() => Run(true);

    private void Run(bool rescueTraffic)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new() { Name = "P699b load", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }]
        });
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
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
        var results = new Dictionary<short, PodTripTelemetry.Result>();
        server.PodTrips.Completed += result => results[result.Ship] = result;
        var bayUsage = new Dictionary<int, int>();
        int launched = 0;
        var traffic = new List<Ship>();
        bool baseline = Environment.GetEnvironmentVariable("P699B_BASELINE") == "1";
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
                    ship.Controls = DockApproach.Steer(step, ship.Position, ship.Orientation, ship.Velocity, rest, dt: .05f, turnState: DockApproach.TurnState.FromShip(ship));
                    ship.StateM = DockApproach.MovementButtons(step, ship.Position, ship.Orientation, ship.Velocity, DockApproach.TurnState.FromShip(ship));
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
                    Assert.NotNull(mouth);
                    var w = mouth!.Value;
                    // Use the real MCP transit controller: docking's native
                    // turn/brake gate must not brake a pod inside an aleph.
                    ship.Controls = AutopilotSteering.Pursue(ship.Position, ship.Orientation,
                        new(w.Position[0], w.Position[1], w.Position[2]));
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
            server.TickSim();
            if (results.Count == 200) break;
        }
        foreach (var r in results.Values.Where(r => r.Outcome is not ("docked" or "rescued")))
            output.WriteLine($"FAILED ship={r.Ship} outcome={r.Outcome} route={string.Join(',', r.Route.Select(v => v.Sector))} homeAt={r.HomeAt} radiusAt={r.RadiusAt} endPosition={string.Join(',', r.EndPosition)} seconds={r.Seconds}");
        server.PodTrips.Flush(server.SimNow);
        var times = results.Values.Where(r => r.Outcome is "docked" or "rescued").Select(r => r.Seconds).Order().ToArray();
        output.WriteLine($"baseline={baseline} rescueTraffic={rescueTraffic} docked={results.Values.Count(r => r.Outcome == "docked")} rescued={results.Values.Count(r => r.Outcome == "rescued")} total={times.Length}/200 oxygen={results.Values.Count(r => r.Outcome == "oxygen")} other={results.Values.Count(r => r.Outcome != "docked" && r.Outcome != "rescued" && r.Outcome != "oxygen")} homeReached={results.Values.Count(r => r.HomeAt is not null)} launchTraffic={launched} median={(times.Length > 0 ? times[times.Length / 2] : 0):F2} p95={(times.Length > 0 ? times[(int)(times.Length * .95)] : 0):F2} bays={string.Join(",", bayUsage.Select(x => $"{x.Key}:{x.Value}"))}");
        Assert.True(launched >= 20);
        Assert.Equal(200, results.Count);
        Assert.DoesNotContain(results.Values, r => r.Outcome != "docked" && r.Outcome != "rescued" && r.Outcome != "oxygen");
        Assert.Equal(0, results.Values.Count(r => r.Outcome == "oxygen"));
        Assert.DoesNotContain(results.Values, r => r.Outcome == "oxygen" && r.RadiusAt is not null);
        Assert.All(results.Values, r => Assert.Equal(300, r.OxygenAllowance, 5));
        if (!rescueTraffic)
        {
            Assert.All(results.Values, r => { Assert.Equal("docked", r.Outcome); Assert.True(r.Route.Length >= 3);
                Assert.NotNull(r.HomeAt); Assert.NotNull(r.RadiusAt); Assert.NotNull(r.BayAt); });
        }
        Assert.True(times[^1] < 300);
        Assert.Equal(0, server.SimPassStageFailures);
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

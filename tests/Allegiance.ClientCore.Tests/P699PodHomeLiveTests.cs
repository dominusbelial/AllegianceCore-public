using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P699PodHomeLiveTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Requires", "Artwork")]
    public void Mcp_pod_flies_two_jumps_to_real_garrison_without_oxygen_death(short side)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
            Port = 0, UpdatesPerSecond = 20,
            LiveSettings = new() { TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true } },
            Games = [new() { Name = "P699", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.StationRuntimes.ToArray())
            {
                var civ = mission.Sides.Single(s => s.Id == station.SideId).Civilization;
                var type = mission.Core.StationTypes[civ.InitialStationTypeId];
                sector.RemoveStation(station);
                mission.PlaceStation(new Station(station.Data with { StationTypeId = type.StationTypeId }, type), sector);
            }
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(manager.ServerReady);
        var hull = EconomyLoadoutTestSetup.WeaponHull(mission.Core);
        var spawn = manager.SpawnBot(hull.HullId, hull.Name, side, [], "combat");
        var bot = ((System.Collections.IEnumerable)typeof(BotManager).GetField("_bots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!).Cast<object>().Single(b => (short)b.GetType().GetField("ShipId")!.GetValue(b)! == spawn.ShipId);
        var session = (ConnectionSession)bot.GetType().GetField("Cs")!.GetValue(bot)!;
        Ship ship = null!;
        Station home = null!;
        double deadline = 0, ejectedAt = 0;
        var sectors = new List<short>();
        bool landed = false;
        int crowdLaunched = 0;
        double landedAt = 0;
        ticker.Invoke(() =>
        {
            ship = server.Ships.Single(l => l.Ship.Id == spawn.ShipId).Ship;
            home = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == ship.SideId
                && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
            var homeSector = mission.Sectors.Single(s => s.StationRuntimes.Contains(home));
            var route = TwoJumpRoute(mission, homeSector);
            var start = route[0];
            var mouth = start.Warps.Single(w => w.Destination!.Id == route[1].Id).Warp;
            var p = new Vector3(mouth.Position[0], mouth.Position[1], mouth.Position[2]);
            var previous = ship.Sector!;
            previous.Remove(ship); mission.PlaceShip(ship, start);
            ship.Position = p + new Vector3(0, 0, mouth.Radius + 350);
            ship.Velocity = Vector3.Zero;
            ship.Orientation.Set(new Vector3(0, 0, -1), new Vector3(0, 1, 0));
            typeof(Srv).GetMethod("OnShipWarped", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [ship, previous, start]);
            ship.LastWarpTime = double.NegativeInfinity;
            ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
            Assert.Equal(mission.Sides.Single(s => s.Id == side).Civilization.Lifepod, ship.HullType.HullId);
            Assert.True(ship.HullType.HasCapability(DataHullTypeIGC.HabmFighter));
            Assert.NotNull(ship.LifepodTimeToDie);
            ejectedAt = server.SimNow; deadline = ship.LifepodTimeToDie!.Value;
            sectors.Add(start.Id);
            ticker.Observe = () =>
            {
                if (ship.Sector is { } sector && sectors[^1] != sector.Id) sectors.Add(sector.Id);
                if (crowdLaunched == 0 && ship.Sector!.Id == home.Data.ClusterId)
                {
                    // Exercise the mass-relaunch area with the actual launch helper,
                    // not station-sized avoidance spheres or a teleport of the pod.
                    for (short n = 0; n < 12; n++)
                    {
                        var traffic = new Ship((short)(30000 + n), hull, home.Center, Vector3.Zero, Orientation.Identity)
                            { SideId = ship.SideId, StationId = home.StationId };
                        mission.PlaceShip(traffic, ship.Sector);
                        typeof(Srv).GetMethod("UndockShipState", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(server, [traffic, ship.Sector]);
                        crowdLaunched++;
                    }
                }
                if (!landed && ship.StationId is not null)
                { landed = true; landedAt = server.SimNow; Assert.Equal(home.StationId, ship.StationId); }
            };
        });
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < 100 && !Volatile.Read(ref landed) && server.SimNow < deadline + 1)
            Thread.Sleep(10);
        ticker.Invoke(() => { });
        lock (session.Gate)
            Assert.DoesNotContain(session.World.Events.Kills, k => k.ShipId == ship.Id && k.BDeathCredit);
        output.WriteLine($"MCP route={string.Join(" -> ", sectors)} elapsed={landedAt - ejectedAt:F2} endurance={deadline - ejectedAt:F2} landed={landed} relaunchTraffic={crowdLaunched}");
        Assert.True(landed, $"pod failed to dock: sector={ship.Sector!.Id}, position={ship.Position}, home={home.Center}");
        Assert.True(landedAt < deadline, "oxygen respawn must never count as docking");
        Assert.True(sectors.Count >= 3, "pod must fly at least two real warp transits");
        Assert.Equal(home.Data.ClusterId, sectors[^1]);
        Assert.Equal(12, crowdLaunched);
        Assert.Equal(0, server.SimPassStageFailures);
    }

    private static Sector[] TwoJumpRoute(Mission mission, Sector home)
    {
        foreach (var middleWarp in home.Warps)
        {
            var middle = mission.Sectors.Single(s => s.Id == middleWarp.Destination!.Id);
            foreach (var outerWarp in middle.Warps)
            {
                var outer = mission.Sectors.Single(s => s.Id == outerWarp.Destination!.Id);
                if (outer != home && !outer.Warps.Any(w => w.Destination!.Id == home.Id))
                    return [outer, middle, home];
            }
        }
        throw new InvalidOperationException("map must contain a two-hop route home");
    }

    private sealed class Ticker : IDisposable
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Done)> _actions = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        public Action? Observe;
        public Ticker(Srv server)
        {
            _thread = new Thread(() =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    while (_actions.TryDequeue(out var work))
                    { try { work.Action(); work.Done.SetResult(); } catch (Exception e) { work.Done.SetException(e); } }
                    server.TickSim(); Observe?.Invoke(); Thread.Sleep(10);
                }
            }) { IsBackground = true };
            _thread.Start();
        }
        public void Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _actions.Enqueue((action, done)); Assert.True(done.Task.Wait(10000));
        }
        public void Dispose() { _stop.Cancel(); _thread.Join(5000); _stop.Dispose(); }
    }
}

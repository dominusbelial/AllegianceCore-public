using System.Diagnostics;
using System.Text.Json;
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
[Trait("Tier", "Live")]
public sealed class P905BotRoutinesLiveTests(ITestOutputHelper output)
{
    private static ServerConfig Config() => new()
    {
        ArtPath = Catalog.ArtRoot, StaticCore = "PCore001.igc", Port = 0,
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
        Transport = new() { Mode = "pool" }, Sim = new() { NativeGotoPlan = true },
        LiveSettings = new() { TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true } },
        Games = [new() { Name = "P905 bot routines", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 32767 }],
    };

    [SkippableFact]
    public void Ordered_peer_mines_only_after_dispatch_then_returns_and_sells_real_He3()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); server.Missions[0].Stage = MissionStage.Started;
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        var hull = Catalog.DefaultFighterHull();
        var spawn = manager.SpawnBot(hull.Id, hull.Name, 0, Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts, "miner");
        Ship ship = null!; Asteroid rock = null!;
        ticker.Do(() =>
        {
            ship = server.Ships.Single(s => s.Ship.Id == spawn.ShipId).Ship;
            Assert.True(ship.EffectiveMineCapacity() > 80, "Wopr's absolute threshold needs a >80-unit hold");
            var sector = ship.Sector!;
            var center = ship.Position + new Vector3(1000, 700, 700);
            rock = new Asteroid(new(1, [center.X, center.Y, center.Z], [0,1,0], [1,0,0], [0,0,0,1],
                1000, 1000, DataAsteroidIGC.AabmMineHe3, 29000, 1000, 40, "helium", "", "", sector.Id, "P905 ordered He3", 255));
            ship.Mission!.PlaceAsteroid(rock, sector);
        });
        Thread.Sleep(500);
        ticker.Do(() => { Assert.Equal(0, ship.Ore); Assert.Equal(-1, ship.Controls.Throttle); });
        // A real peer sends the native dispatch shape, not a direct call to the routine.
        manager.Probe.SendChatBuoy("", ChatTarget.Individual, spawn.ShipId,
            new([0,0,0], rock.Data.ClusterId, DataBuoyIGC.CBuoyCluster, 905, true), commandId: OrderChange.CidMine);
        // Fixture placement isolates drilling/return from a kilometre-long approach;
        // cargo, mining rate, wallet, controls and docking remain production code.
        ticker.Do(() =>
        {
            var p = rock.Data.Position;
            ship.Position = new Vector3(p[0], p[1], p[2]) + new Vector3(rock.Radius + ship.HullType.Length / 2 + 15, 0, 0);
            ship.Velocity = Vector3.Zero;
        });
        Wait(() => ticker.Read(() => ship.Ore > 1), 15);
        float peak = 0;
        var watch = Stopwatch.StartNew();
        Wait(() => ticker.Read(() =>
        {
            peak = Math.Max(peak, ship.Ore);
            return peak > 80 && ship.StationId is not null && ship.Ore == 0;
        }), 100);
        output.WriteLine($"ordered peer: mined={peak:F2} units, sale={peak * ship.EffectiveOreCreditPerUnit():F2} credits, wall={watch.Elapsed.TotalSeconds:F2}s");
        // A delivered miner waits for dispatch; it does not repeatedly relaunch.
        Thread.Sleep(400);
        ticker.Do(() => Assert.NotNull(ship.StationId));
        Assert.Equal("miner", Assert.Single(manager.ActiveBots()).Behavior);
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableFact]
    public void Docker_faces_and_communal_chat_queues_a_real_station_order_and_stack_resumes()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); server.Missions[0].Stage = MissionStage.Started;
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        var hull = Catalog.DefaultFighterHull();
        var spawn = manager.SpawnBot(hull.Id, hull.Name, 0, Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts, "docker");
        Ship ship = null!;
        ticker.Do(() => ship = server.Ships.Single(s => s.Ship.Id == spawn.ShipId).Ship);
        Wait(() => ticker.Read(() => ship.WingId == 8), 10);
        // Trigger the communal traffic-control event from a separate real peer.
        manager.Probe.SendChat("sendcommand2", ChatTarget.Team);
        short stationId = -1;
        Wait(() =>
        {
            lock (manager.Probe.Gate)
            {
                if (manager.Probe.World.MyQueuedCommand is not { TargetType: IgcObjectType.Station } command) return false;
                stationId = command.TargetId; return true;
            }
        }, 10);
        Assert.Equal(2, manager.ChangeBehavior(spawn.Name, "miner"));
        Assert.Equal("miner", Assert.Single(manager.ActiveBots()).Behavior);
        Assert.Equal(1, manager.ChangeBehavior(spawn.Name, null));
        Assert.Equal("docker", Assert.Single(manager.ActiveBots()).Behavior);
        Assert.Equal(1, manager.ChangeBehavior(spawn.Name, null));
        // Force no facing completion: the routine must turn without thrust.
        Wait(() => ticker.Read(() => Math.Abs(ship.Controls.Yaw) + Math.Abs(ship.Controls.Pitch) > .01f), 10);
        // Latched on the sim tick: a docker relaunches at once, so its docked
        // window can fall between two polls.
        bool docked = false;
        ticker.Do(() => ticker.Before = () => docked |= ship.StationId is not null);
        // The probe acts as the other communal docker and returns its goto.
        manager.Probe.SendChat("", ChatTarget.Individual, spawn.ShipId,
            commandId: OrderChange.CidGoto, oidTarget: stationId, otTarget: (short)IgcObjectType.Station);
        Wait(() => ticker.Read(() => docked), 45);
        output.WriteLine("docker: wing=8; real sendcommand2 -> station QUEUED_COMMAND_UPDATE; typed override restored; actual docking observed");
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableFact]
    public void Deathmatch_searches_real_visible_enemies_and_retargets_after_a_kill()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); server.Missions[0].Stage = MissionStage.Started;
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        var hull = Catalog.DefaultFighterHull();
        var spawn = manager.SpawnBot(hull.Id, hull.Name, 0, Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts, "deathmatch");
        Ship near = null!, far = null!, pilot = null!;
        ticker.Do(() =>
        {
            pilot = server.Ships.Single(s => s.Ship.Id == spawn.ShipId).Ship;
            var mission = pilot.Mission!;
            near = server.SpawnNpcShip(mission, pilot.Sector!, 1, pilot.Position + new Vector3(0, 0, -300));
            far = server.SpawnNpcShip(mission, pilot.Sector!, 1, pilot.Position + new Vector3(0, 0, -600));
            near.ClearLoadout(); far.ClearLoadout();
            ticker.Before = () =>
            {
                foreach (var (target, distance) in new[] { (near, 300), (far, 2600) })
                {
                    if (target.Dead) continue;
                    target.Position = pilot.Position + pilot.Orientation.GetForward() * distance;
                    target.Velocity = pilot.Velocity;
                    target.SetFraction(1);
                    target.SideVisibility.SetSideVisibility(0, true);
                }
            };
        });
        Wait(() => Assert.Single(manager.ActiveBots()).Target == near.Id, 15);
        ticker.Do(() => near.ReceiveDamage(0, 1_000_000, pilot.Mission!.LastUpdate, near.Position, near.Position, pilot));
        Wait(() => Assert.Single(manager.ActiveBots()).Target == far.Id, 15);
        output.WriteLine($"deathmatch: nearest={near.Id}; KILL_SHIP invalidation -> replacement={far.Id}");
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableFact]
    public void Chatter_is_explicit_and_uses_real_chat_with_a_worker_budget()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); server.Missions[0].Stage = MissionStage.Started;
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        var hull = Catalog.DefaultFighterHull();
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$"""{"hull":"{{hull.Name}}","behavior":"chatter","side":0}""")!;
        Assert.False(McpTools.Handle("spawn_bot", args, manager).IsError ?? false);
        Assert.Equal("chatter", Assert.Single(manager.ActiveBots()).Behavior);
        Wait(() => { lock (manager.Probe.Gate) return manager.Probe.World.Events.Chats.Any(c => c.Message == ScriptedBotChat.Text); }, 40);
        lock (manager.Probe.Gate) Assert.Single(manager.Probe.World.Events.Chats, c => c.Message == ScriptedBotChat.Text);
        Assert.Equal(0, server.SimPassStageFailures);
    }

    private static void Wait(Func<bool> condition, int seconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed.TotalSeconds < seconds) Thread.Sleep(25);
        Assert.True(condition(), $"P905 condition timed out after {seconds}s");
    }

    private sealed class Ticker : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        internal Action? Before;
        internal Ticker(Srv server)
        {
            _thread = new(() => { while (!_stop.IsCancellationRequested) { lock (_gate) { Before?.Invoke(); server.TickSim(); } Thread.Sleep(5); } })
                { IsBackground = true, Name = "P905-loopback" };
            _thread.Start();
        }
        internal void Do(Action action) { lock (_gate) action(); }
        internal T Read<T>(Func<T> read) { lock (_gate) return read(); }
        public void Dispose() { _stop.Cancel(); Assert.True(_thread.Join(10000)); _stop.Dispose(); }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P682NextMatchCombatLiveTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("port")]
    [InlineData("native")]
    public void Mcp_bots_buy_launch_see_enemies_and_kill_in_both_matches(string model)
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        var config = new Allegiance.Server.ServerConfig
        {
            Port = 0,
            LiveSettings = new()
            {
                AutoNextMatch = new() { Enabled = true, IntermissionSeconds = 2 },
                TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
            },
            Games = P660NextMatchLiveTests.Config(true).Games,
            Broadcast = new() { ShipUpdateModel = model },
        };
        server.Boot(config);
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(manager.ServerReady);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"size":1,"staging":false,"refill":true}""")!;
        var result = McpTools.Handle("spawn_scenario", args, manager);
        Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
        Wait(() => manager.ActiveBots().Count == 2, "MCP scenario spawn");
        var old = server.Missions[0];
        var oldIds = manager.ActiveBots().Select(b => b.ShipId).ToHashSet();
        for (int match = 1; match <= 2; match++)
        {
            Wait(() => server.Missions[0].Stage == MissionStage.Started, $"match {match} automatic start");
            // This is the failing-first boundary: a reseated fighter must make a
            // real purchase + launch, just as it did during initial MCP spawn.
            Wait(() =>
            {
                bool flying = false;
                ticker.Invoke(() => flying = manager.ActiveBots().All(b => server.Ships.Any(l =>
                    l.Ship.Id == b.ShipId && l.Ship.StationId is null && l.Ship.Weapons.Count > 0)));
                return flying;
            }, $"match {match} combat hulls bought and launched");
            if (match == 2)
            {
                Assert.NotSame(old, server.Missions[0]);
                Assert.All(manager.ActiveBots(), b => Assert.DoesNotContain(b.ShipId, oldIds));
                ticker.Invoke(() => Assert.All(server.Ships, l =>
                {
                    Assert.Same(server.Missions[0], l.Ship.Mission);
                    Assert.Contains(l.Sector, server.Missions[0].Sectors);
                }));
            }
            var ids = manager.ActiveBots().Select(b => b.ShipId).ToHashSet();
            // Arrange repeatable contact away from stations/rocks. Movement,
            // visibility, target selection, fire and damage still use production code.
            ticker.Invoke(() => ticker.Contact = ids);
            Wait(() => manager.DiagnoseWorlds().Count == 2 && manager.DiagnoseWorlds().All(v =>
                v.CrossSideShips.Any(e => ids.Contains(e.ShipId) && e.Visible && e.Sector == v.Sector && e.LastTick > 0)),
                $"match {match} both ClientWorlds have visible enemy live poses");
            var ticks = manager.DiagnoseWorlds().ToDictionary(v => v.ShipId,
                v => v.CrossSideShips.Where(e => ids.Contains(e.ShipId)).Max(e => e.LastTick));
            Wait(() => manager.DiagnoseWorlds().All(v => v.CrossSideShips.Any(e => ids.Contains(e.ShipId)
                && e.LastTick > ticks[v.ShipId])), $"match {match} enemy poses advance");
            Wait(() =>
            {
                bool seen = false;
                ticker.Invoke(() => seen = server.Ships.Where(l => ids.Contains(l.Ship.Id))
                    .All(l => l.Ship.SeenBySide((short)(1 - l.Ship.SideId!.Value))));
                return seen;
            }, $"match {match} server cross-side scanner visibility");
            var before = manager.GetCombatSummary();
            ticker.Invoke(() =>
            {
                foreach (var l in server.Ships.Where(l => ids.Contains(l.Ship.Id)))
                { l.Ship.SetFraction(.02f); l.Ship.SetShieldFraction(0); }
            });
            Wait(() => manager.GetCombatSummary().Kills > before.Kills, $"match {match} real credited gunfire kill");
            var after = manager.GetCombatSummary();
            Assert.True(after.ShotsFired > before.ShotsFired);
            Assert.True(after.ShipHits > before.ShipHits);
            output.WriteLine($"{model} match {match}: enemy live poses on both sides; shots +{after.ShotsFired - before.ShotsFired}, hits +{after.ShipHits - before.ShipHits}, kills +{after.Kills - before.Kills}");
            ticker.Invoke(() => ticker.Contact = null);
            if (match == 1)
                ticker.Invoke(() => Conquer(server));
        }
        Assert.Equal(0, server.SimPassStageFailures);
    }

    private static void Conquer(Srv server)
    {
        var mission = server.Missions[0];
        var attacker = server.Ships.First(l => l.Ship.SideId == 0).Ship;
        // Accelerate the end condition with lethal base damage, following the
        // same remove/site/victory chain as Sector's projectile-impact path.
        mission.ConquestEnabled = true;
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.StationRuntimes.Where(s => s.SideId == 1).ToArray())
            {
                if (!ReferenceEquals(server.Missions[0], mission)) return;
                var result = station.ReceiveDamage(0, 1_000_000, server.SimNow,
                    station.Center, station.Center, attacker);
                Assert.Equal(DamageResult.Killed, result);
                sector.RemoveStation(station);
                mission.Site.KillStationEvent(station, attacker, 1_000_000);
                var (winner, changed) = mission.RecomputeConquestAndTerritory(null);
                if (changed || winner is not null) mission.Site.VictoryByStationsComputed(mission, changed, winner);
                if (ReferenceEquals(server.Missions[0], mission)) mission.Site.SideLostRestartStation(mission, 1);
            }
        Assert.NotSame(mission, server.Missions[0]);
    }

    private static void Wait(Func<bool> done, string reason)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 40) { if (done()) return; Thread.Sleep(25); }
        Assert.Fail(reason + " timed out");
    }

    private sealed class Ticker : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Done)> _actions = new();
        private readonly Thread _thread;
        internal HashSet<short>? Contact;
        public Ticker(Srv server)
        {
            _thread = new Thread(() =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    while (_actions.TryDequeue(out var w))
                    { try { w.Action(); w.Done.SetResult(); } catch (Exception ex) { w.Done.SetException(ex); } }
                    if (Contact is { } ids)
                    {
                        var mission = server.Missions[0];
                        var sector = mission.Sectors.Single(s => s.Id == 1052);
                        foreach (var live in server.Ships.Where(l => ids.Contains(l.Ship.Id)).ToArray())
                        {
                            var s = live.Ship;
                            if (s.StationId is not null || s.HullType.HasCapability(Allegiance.Core.DataHullTypeIGC.HabmLifepod)) continue;
                            s.Position = new Vector3(30000, 0, s.SideId == 0 ? -150 : 150);
                            s.Velocity = Vector3.Zero;
                            s.Orientation.Set(new Vector3(0, 0, s.SideId == 0 ? 1 : -1));
                            if (s.Sector != sector)
                            {
                                var previous = s.Sector!;
                                previous.Remove(s); mission.PlaceShip(s, sector);
                                typeof(Srv).GetMethod("OnShipWarped", BindingFlags.NonPublic | BindingFlags.Instance)!
                                    .Invoke(server, [s, previous, sector]);
                            }
                        }
                    }
                    server.TickSim(); Thread.Sleep(10);
                }
            }) { IsBackground = true };
            _thread.Start();
        }
        internal void Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _actions.Enqueue((action, done));
            Assert.True(done.Task.Wait(10000));
        }
        public void Dispose() { _stop.Cancel(); _thread.Join(5000); _stop.Dispose(); }
    }
}

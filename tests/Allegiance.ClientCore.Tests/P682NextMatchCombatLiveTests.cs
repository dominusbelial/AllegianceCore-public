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
[Trait("Tier", "Live")]
public sealed class P682NextMatchCombatLiveTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("port", false)]
    [InlineData("native", false)]
    [InlineData("port", true)]
    [InlineData("native", true)]
    public void P682b_second_match_relaunches_mature_and_strike_then_third_match_starts(string model, bool pipeline)
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        var config = P660NextMatchLiveTests.Config(true, intermission: 2);
        config.LiveSettings.TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true };
        config.LiveSettings.TestUnlock = new() { Enabled = true, AllTech = true, Money = 1_000_000 };
        config.Broadcast = new() { ShipUpdateModel = model, ImmutableSnapshot = pipeline,
            EncodeOnce = pipeline, InterestTiers = pipeline };
        server.Boot(config);
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(manager.ServerReady);
        var bomber = Showcase.RepresentativeHullOfRole(Catalog.RepresentativeCore(), Showcase.HullRole.Bomber)!.Value;
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            size = 1, hull = bomber.Name, staging = false, refill = true,
            finale_after_minutes = .001, finale_min_relaunch_share = 1,
            finale_min_kills_last_5_minutes = 1, finale_side = "0", finale_bombers = 1,
        }))!;
        var result = McpTools.Handle("spawn_scenario", args, manager);
        Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
        Wait(() => manager.ActiveBots().Count == 2, "MCP scenario spawn");
        var bots = ((System.Collections.IEnumerable)typeof(BotManager).GetField("_bots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!).Cast<object>().ToArray();
        T Field<T>(object bot, string name) => (T)bot.GetType().GetField(name)!.GetValue(bot)!;
        JsonElement Run(int index) => JsonSerializer.SerializeToElement(manager.FinaleStatus()[index]);
        bool Flying() => bots.All(b => !Field<bool>(b, "MissionLaunchPending") && !Field<bool>(b, "IsPod")
            && !Field<bool>(b, "PodDocked"));
        object? previousMission = null;
        object? previousRun = null;
        for (int match = 1; match <= 3; match++)
        {
            Wait(() => server.Missions[0].Stage == MissionStage.Started && manager.FinaleStatus().Length == match,
                $"match {match} automatic start and fresh finale");
            Wait(() =>
            {
                bool flying = false;
                ticker.Invoke(() => flying = bots.All(b => server.Ships.Any(l => l.Ship.Id == Field<short>(b, "ShipId")
                    && l.Ship.StationId is null && !l.Ship.HullType.HasCapability(Allegiance.Core.DataHullTypeIGC.HabmLifepod))));
                return flying;
            }, $"match {match} initial server purchase and launch");
            var run = Run(match - 1);
            output.WriteLine($"{model} pipeline={pipeline} match {match} fresh finale: {run}");
            Assert.NotSame(previousMission, server.Missions[0]);
            Assert.NotSame(previousRun, Field<object>(bots[0], "Finale"));
            previousMission = server.Missions[0];
            previousRun = Field<object>(bots[0], "Finale");
            Assert.Equal("armed", run.GetProperty("state").GetString());
            Assert.Equal(0, run.GetProperty("killsLastFiveMinutes").GetInt32());
            Assert.Empty(run.GetProperty("selectedSides").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, run.GetProperty("triggeredAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, run.GetProperty("strikeStartedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, run.GetProperty("gameOverAt").ValueKind);
            Assert.All(bots, b => Assert.Equal(0, Field<int>(b, "CompletedPodCycles")));
            foreach (var wing in run.GetProperty("wings").EnumerateArray())
            {
                short target = wing.GetProperty("targetStation").GetInt16();
                Assert.Contains(server.Missions[0].Sectors.SelectMany(s => s.StationRuntimes), s => s.StationId == target
                    && s.SideId == wing.GetProperty("targetSide").GetInt16() && s.Data.ClusterId == wing.GetProperty("targetSector").GetInt16());
                Assert.Equal(0, wing.GetProperty("reservedBombers").GetInt32());
                Assert.Equal(0, wing.GetProperty("assignedBombers").GetInt32());
                Assert.Equal(0, wing.GetProperty("escorts").GetInt32());
                Assert.Equal(JsonValueKind.Null, wing.GetProperty("targetKilledAt").ValueKind);
            }
            if (match == 3) break;
            var ids = manager.ActiveBots().Select(b => b.ShipId).ToHashSet();
            ticker.Invoke(() => { ticker.ContactSpacing = 10000; ticker.Contact = ids; });
            Wait(() => manager.DiagnoseWorlds().All(v => v.CrossSideShips.Any(e => ids.Contains(e.ShipId)
                && e.Sector == v.Sector)), $"match {match} common combat sector");
            for (int cycle = 1; cycle <= 3; cycle++)
            {
                ticker.Invoke(() =>
                {
                    var ships = server.Ships.Where(l => bots.Any(b => Field<short>(b, "ShipId") == l.Ship.Id)).Select(l => l.Ship).ToArray();
                    foreach (var ship in ships)
                    {
                        var enemy = ships.Single(s => s.SideId != ship.SideId);
                        ship.ReceiveDamage(0, 1_000_000, server.SimNow, ship.Position, ship.Position, enemy);
                    }
                });
                Wait(() => bots.All(b =>
                {
                    var cs = Field<Allegiance.ClientCore.Session.ConnectionSession>(b, "Cs");
                    lock (cs.Gate) return cs.World.Ships.TryGetValue(Field<short>(b, "ShipId"), out var own)
                        && Catalog.IsLifepodHull(own.HullId);
                }), $"match {match} cycle {cycle} ejection received");
                ticker.Invoke(() =>
                {
                    foreach (var live in server.Ships.Where(l => bots.Any(b => Field<short>(b, "ShipId") == l.Ship.Id)).ToArray())
                    {
                        var home = server.Missions[0].Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == live.Ship.SideId);
                        typeof(Srv).GetMethod("PerformStationRescue", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(server, [live.Ship, home]);
                    }
                });
                Wait(() =>
                {
                    bool relaunched = false;
                    ticker.Invoke(() => relaunched = bots.All(b => server.Ships.Any(l => l.Ship.Id == Field<short>(b, "ShipId")
                        && l.Ship.StationId is null && !l.Ship.HullType.HasCapability(Allegiance.Core.DataHullTypeIGC.HabmLifepod))));
                    return relaunched;
                }, $"match {match} cycle {cycle} real server purchase and relaunch");
                Wait(() => Flying() && bots.All(b => Field<int>(b, "CompletedPodCycles") >= cycle),
                    $"match {match} cycle {cycle} counted real pod to relaunch; counters=" + string.Join(",", bots.Select(b => Field<int>(b, "CompletedPodCycles"))));
                Assert.All(bots, b => Assert.Equal(cycle, Field<int>(b, "CompletedPodCycles")));
                output.WriteLine($"{model} pipeline={pipeline} match {match} cycle {cycle}: " + JsonSerializer.Serialize(manager.FinaleStatus()));
                Wait(() => Run(match - 1).GetProperty("relaunchMature").GetBoolean(), $"match {match} maturity");
                Wait(() => Run(match - 1).GetProperty("state").GetString() == "strike", $"match {match} finale trigger");
            }
            Wait(() => Run(match - 1).GetProperty("strikeStartedAt").ValueKind == JsonValueKind.Number,
                $"match {match} fresh bomber wing launched");
            Assert.Single(Run(match - 1).GetProperty("selectedSides").EnumerateArray());
            Assert.Equal(0, Run(match - 1).GetProperty("selectedSides")[0].GetInt32());
            ticker.Invoke(() => { ticker.Contact = null; Conquer(server); });
            Wait(() => Run(match - 1).GetProperty("gameOverAt").ValueKind == JsonValueKind.Number,
                $"match {match} finale game over observed");
        }
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableTheory]
    [InlineData("port", false)]
    [InlineData("native", false)]
    [InlineData("port", true)]
    [InlineData("native", true)]
    public void Mcp_bots_buy_launch_see_enemies_and_kill_in_both_matches(string model, bool pipeline)
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
            Broadcast = new() { ShipUpdateModel = model, ImmutableSnapshot = pipeline,
                EncodeOnce = pipeline, InterestTiers = pipeline },
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
            output.WriteLine($"{model} pipeline={pipeline} match {match}: enemy live poses on both sides; shots +{after.ShotsFired - before.ShotsFired}, hits +{after.ShipHits - before.ShipHits}, kills +{after.Kills - before.Kills}");
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
        internal float ContactSpacing = 150;
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
                            s.Position = new Vector3(30000, 0, s.SideId == 0 ? -ContactSpacing : ContactSpacing);
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

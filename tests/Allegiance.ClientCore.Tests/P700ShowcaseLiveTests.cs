using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P700ShowcaseLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Local_showcase_never_parks_with_target_for_five_seconds()
    {
        Require.Artwork();
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","size_scale":1,"target_sector":"Alderan","staging":true,"finale_after_minutes":0}""")!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            var watch = Stopwatch.StartNew();
            var idleSince = new Dictionary<string, double>();
            var reached = new HashSet<string>();
            double firstCombat = double.NaN, longest = 0;
            int samples = 0;
            while (watch.Elapsed.TotalSeconds < 150)
            {
                double now = watch.Elapsed.TotalSeconds;
                var bots = manager.ActiveBots();
                Assert.Equal(30, bots.Count);
                foreach (var bot in bots)
                {
                    if (bot.Sector == bot.TargetSector) reached.Add(bot.Name);
                    if (bot.Target is not null && double.IsNaN(firstCombat)) firstCombat = now;
                    if (bot.Target is not null && bot.Speed < BotManager.IdleSpeedMps)
                    {
                        if (!idleSince.TryGetValue(bot.Name, out double since)) idleSince[bot.Name] = since = now;
                        longest = Math.Max(longest, now - since);
                        if (now - since >= 1)
                        {
                            var ship = server.Ships.FirstOrDefault(s => s.Ship.Id == bot.ShipId)?.Ship;
                            if (ship is not null)
                                output.WriteLine($"Idle {now - since:F2}s: {JsonSerializer.Serialize(new
                                {
                                    bot.Name, bot.ShipId, bot.Velocity, bot.Target,
                                    ServerPosition = new[] { ship.Position.X, ship.Position.Y, ship.Position.Z },
                                    ServerVelocity = new[] { ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z },
                                    ship.Controls, ship.StateM, ship.StationId, ship.Dead, ship.LastUpdate,
                                    Parent = ship.ParentShip?.Id, Ripcord = ship.RipcordModel?.ToString(),
                                    Parts = ship.Parts.ToArray().Select(p => new { p.Type.PartId, p.Type.Name, p.MountId, p.Amount, p.TimeLoaded })
                                })}");
                        }
                        Assert.True(now - since <= 5, $"Parked >5s: {JsonSerializer.Serialize(bot)}");
                    }
                    else idleSince.Remove(bot.Name);
                    // Alderan has no stations: local ship hits remain combat.
                    // A fighter may be travelling home on an actual base recall.
                    if (bot.Sector == bot.TargetSector && bot.Role != "Bomber" && bot.State == "defending")
                    {
                        Assert.NotNull(bot.DefenseSector);
                        Assert.NotEqual(bot.Sector, bot.DefenseSector);
                        lock (manager.Probe.Gate)
                            Assert.Contains(manager.Probe.World.Stations.Values,
                                s => s.SideId == bot.Side && s.ClusterId == bot.DefenseSector);
                    }
                }
                samples++;
                if (!double.IsNaN(firstCombat) && now - firstCombat >= 45 && reached.Count >= 24) break;
                Thread.Sleep(100);
            }
            var combat = manager.GetCombatSummary();
            output.WriteLine($"P700 local 15v15: {watch.Elapsed.TotalSeconds:F1}s, samples={samples}, reached={reached.Count}/30, "
                + $"longest speed<1 with target={longest:F3}s, shots={combat.ShotsFired}, hits={combat.ShipHits}, kills={combat.Kills}");
            output.WriteLine(JsonSerializer.Serialize(manager.GetPerfStats(null)));
            var perf = McpTools.Handle("get_perf_stats", new Dictionary<string, JsonElement>(), manager);
            Assert.False(perf.IsError ?? false);
            var json = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)perf.Content[0]).Text);
            var idleMetric = json.RootElement.GetProperty("idleWithTarget");
            Assert.Equal(0, idleMetric.GetProperty("currentCount").GetInt32());
            Assert.Equal(0, idleMetric.GetProperty("botsLastMinute").GetInt32());
            Assert.InRange(idleMetric.GetProperty("sampleAgeSeconds").GetDouble(), 0, 2);
            Assert.True(reached.Count >= 24, "Most showcase ships must reach their objectives");
            Assert.True(combat.ShotsFired > 0 && combat.ShipHits > 0, "Must observe real battle gunfire and damage");
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
}

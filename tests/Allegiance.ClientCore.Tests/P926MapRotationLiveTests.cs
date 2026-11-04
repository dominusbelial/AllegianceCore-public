using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P926MapRotationLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Showcase_marches_fights_and_completes_three_map_rotations()
    {
        Require.Artwork();
        string evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/p926"));
        Directory.CreateDirectory(evidence);
        string path = Path.Combine(evidence, $"loopback-{DateTime.UtcNow:yyyyMMddTHHmmss}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            var config = P660NextMatchLiveTests.Config(true, intermission: 5);
            config.BenchmarkSeed = 926;
            config.Broadcast = new() { ParallelBuild = true };
            config.Games[0].CivIds = [25, 25];
            config.Games[0].CivSeed = 926;
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].ShowMap = false;
            config.Games[0].ShowHomeSector = true;
            config.Games[0].Goals = new() { TeamKills = 5, GameLengthSeconds = 600 };
            config.LiveSettings.TestHarness = null;
            config.Showcase.TechMode = "research";
            config.Showcase.IncomeMultiplier = 5000;
            config.Showcase.ResearchTimeScale = 16;
            config.Showcase.MapRotation = P926MapRotationTests.Rotation();
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            var battleHits = new System.Collections.Concurrent.ConcurrentDictionary<(Allegiance.Core.Model.Mission Mission, short Sector), int>();
            var originalHit = server.Host.ProjectileHit;
            server.Host.ProjectileHit = (p, pos, normal, radius, target, sector, shield) =>
            {
                if (p.Launcher is Ship shooter && shooter.Mission is { } current)
                    battleHits.AddOrUpdate((current, sector), 1, (_, count) => count + 1);
                originalHit?.Invoke(p, pos, normal, radius, target, sector, shield);
            };
            var gate = new object();
            using var stop = new CancellationTokenSource(); Exception? tickerError = null;
            var ticker = new Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(20); } }
                catch (Exception ex) { tickerError = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var result = McpTools.Handle("spawn_scenario", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"showcase","count_a":15,"count_b":15,"target_sector":"Alderan","staging":true,"loadout_mode":"best","finale_after_minutes":0}""")!, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                for (int match = 0; match < 3; match++)
                {
                    Allegiance.Core.Model.Mission mission; lock (gate) mission = server.Missions[0];
                    var info = Allegiance.Core.Wire.ClusterInfo.FromSectors(mission.Sectors);
                    short battle = ShowcaseBattleSector.Choose(new() { Clusters = info.Clusters, Warps = info.Warps })!.Value;
                    var watch = Stopwatch.StartNew(); var reached = new HashSet<short>();
                    var idleSince = new Dictionary<string, double>(); double longestIdle = 0;
                    bool checkedIntel = false;
                    var validSectors = mission.Sectors.Select(s => (int)s.Id).ToHashSet();
                    int bothInBattle = 0; long startingHits = manager.GetCombatSummary().ShipHits;
                    while (watch.Elapsed.TotalSeconds < 540 && mission.Stage != MissionStage.Over)
                    {
                        Assert.Null(tickerError);
                        if (!checkedIntel && watch.Elapsed.TotalSeconds > 10)
                        {
                            lock (gate)
                            if (ReferenceEquals(server.Missions[0], mission) && mission.Stage == MissionStage.Started)
                            {
                                using var intel = JsonDocument.Parse(JsonSerializer.Serialize(manager.SideIntelReport()));
                                foreach (var side in intel.RootElement.EnumerateArray())
                                    foreach (var known in side.GetProperty("knownSectors").EnumerateArray())
                                        Assert.Contains(known.GetInt32(), validSectors);
                                checkedIntel = intel.RootElement.GetArrayLength() > 0;
                            }
                        }
                        var bots = manager.ActiveBots();
                        foreach (var bot in bots)
                        {
                            if (bot.WorldReady && bot.Sector == battle) reached.Add(bot.Side);
                            if (bot.Target is not null && bot.Speed < BotManager.IdleSpeedMps)
                            {
                                if (!idleSince.TryGetValue(bot.Name, out double since)) idleSince[bot.Name] = since = watch.Elapsed.TotalSeconds;
                                longestIdle = Math.Max(longestIdle, watch.Elapsed.TotalSeconds - since);
                                Assert.True(watch.Elapsed.TotalSeconds - since <= 5, $"Parked >5s: {JsonSerializer.Serialize(bot)}");
                            }
                            else idleSince.Remove(bot.Name);
                        }
                        if (bots.Where(b => b.Sector == battle).Select(b => b.Side).Distinct().Count() == 2) bothInBattle++;
                        Thread.Sleep(50);
                    }
                    output.WriteLine($"match={match + 1} battle={battle}:{mission.Sectors.Single(s => s.Id == battle).Cluster.Name} "
                        + $"wall={watch.Elapsed.TotalSeconds:F1}s reachedSides={string.Join(',', reached)} bothInBattleSamples={bothInBattle} "
                        + $"battleHits={battleHits.GetValueOrDefault((mission, battle))} hits={manager.GetCombatSummary().ShipHits - startingHits} longestIdle={longestIdle:F2}s stage={mission.Stage} log={path}");
                    Assert.Equal(MissionStage.Over, mission.Stage);
                    Assert.Equal(2, reached.Count);
                    Assert.True(checkedIntel);
                    Assert.True(bothInBattle > 0);
                    Assert.True(battleHits.GetValueOrDefault((mission, battle)) > 0, "Real projectile impacts must occur in the derived battle sector");
                    Assert.True(manager.GetCombatSummary().ShipHits > startingHits);
                    Assert.True(mission.Sides.Sum(s => s.Kills) > 0, "Normal combat must score kills before the goal/deadline completes");
                    // Stage flips to Over before the same tick publishes the
                    // rebuilt mission. Join that tick before inspecting rotation.
                    Allegiance.Core.Model.Mission fresh;
                    lock (gate) fresh = server.Missions[0];
                    Assert.NotSame(mission, fresh);
                    if (match < 2)
                    {
                        var wait = Stopwatch.StartNew();
                        while (wait.Elapsed.TotalSeconds < 45 && fresh.Stage != MissionStage.Started) Thread.Sleep(20);
                        Assert.Equal(MissionStage.Started, fresh.Stage);
                    }
                }
                Assert.Equal(0, server.SimPassStageFailures);
                log.Flush(); var trace = File.ReadAllText(path);
                Assert.Equal(3, trace.Split('\n').Count(l => l.StartsWith("[gameover] mission") && l.Contains("Stage Over")));
                Assert.Contains("rotation=cycle#0", trace); Assert.Contains("rotation=cycle#1", trace); Assert.Contains("rotation=cycle#2", trace);
                Assert.Contains("source=derived requested=Alderan", trace);
                Assert.Contains("clientGenerationChanged=True", trace);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

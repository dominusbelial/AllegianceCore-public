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
public sealed class P927MultiSideLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Three_factions_research_fight_and_finish_three_rotations()
    {
        Require.Artwork();
        string evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/p927"));
        Directory.CreateDirectory(evidence);
        string path = Path.Combine(evidence, $"loopback-{DateTime.UtcNow:yyyyMMddTHHmmss}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            var config = P660NextMatchLiveTests.Config(true, intermission: 5);
            config.BenchmarkSeed = 927;
            config.Broadcast = new() { ParallelBuild = true };
            config.Games[0].Teams = 3;
            config.Games[0].CivIds = [18, 4, 5];
            config.Games[0].SideNames = ["Gigacorp", "Phoenix", "Omicron"];
            config.Games[0].CivSeed = 927;
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].ShowMap = false;
            config.Games[0].ShowHomeSector = true;
            config.Games[0].Goals = new();
            config.LiveSettings.TestHarness = null;
            config.Showcase.TechMode = "research";
            config.Showcase.IncomeMultiplier = 5000;
            config.Showcase.ResearchTimeScale = 16;
            config.Showcase.FinaleTimeoutSeconds = 120;
            config.Showcase.StationKillsDecideDeadline = true;
            config.Showcase.MapRotation = new()
            {
                Mode = "cycle", Seed = 927,
                Entries = [new() { MapGen = new() { Type = "DiamondRing" } }, new() { MapGen = new() { Type = "SingleRing" } }]
            };
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            var battleHits = new System.Collections.Concurrent.ConcurrentDictionary<(Allegiance.Core.Model.Mission Mission, short Sector), int>();
            var fought = new System.Collections.Concurrent.ConcurrentDictionary<(Allegiance.Core.Model.Mission Mission, short Shooter, short Victim), int>();
            var originalHit = server.Host.ProjectileHit;
            server.Host.ProjectileHit = (p, pos, normal, radius, target, sector, shield) =>
            {
                if (p.Launcher is Ship shooter && shooter.Mission is { } current)
                {
                    battleHits.AddOrUpdate((current, sector), 1, (_, count) => count + 1);
                    if (shooter.SideId is { } attacking && current.Sectors.SelectMany(s => s.Models).OfType<Ship>().FirstOrDefault(s => s.Id == target) is { } victim && victim.SideId is { } defending && attacking != defending)
                        fought.AddOrUpdate((current, attacking, defending), 1, (_, count) => count + 1);
                }
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
                    """{"preset":"showcase","sides":[{"count":10},{"count":10},{"count":10}],"staging":true,"loadout_mode":"best","meeting_engagement":true,"finale_after_minutes":0.5,"finale_side":"all","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":3}""")!, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var spawned = WorkerReply.FromTool(result).Data.GetProperty("sides");
                Assert.Equal(new[] { 10, 10, 10 }, spawned.EnumerateArray().Select(s => s.GetProperty("spawned").GetInt32()));
                for (int match = 0; match < 3; match++)
                {
                    Allegiance.Core.Model.Mission mission; lock (gate) mission = server.Missions[0];
                    var info = Allegiance.Core.Wire.ClusterInfo.FromSectors(mission.Sectors);
                    short battle = ShowcaseBattleSector.Choose(new() { Clusters = info.Clusters, Warps = info.Warps })!.Value;
                    var watch = Stopwatch.StartNew(); var reached = new HashSet<short>();
                    var idleSince = new Dictionary<string, double>(); double longestIdle = 0;
                    bool checkedIntel = false;
                    var validSectors = mission.Sectors.Select(s => (int)s.Id).ToHashSet();
                    int allInBattle = 0; long startingHits = manager.GetCombatSummary().ShipHits;
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
                                
                            }
                            else idleSince.Remove(bot.Name);
                        }
                        if (bots.Where(b => b.Sector == battle).Select(b => b.Side).Distinct().Count() == 3) allInBattle++;
                        Thread.Sleep(50);
                    }
                    // Stage changes before scores, receipt publication and rotation
                    // finish in the same tick. Observe that completed tick first.
                    lock (gate) { }
                    output.WriteLine($"match={match + 1} battle={battle}:{mission.Sectors.Single(s => s.Id == battle).Cluster.Name} "
                        + $"wall={watch.Elapsed.TotalSeconds:F1}s reachedSides={string.Join(',', reached)} allInBattleSamples={allInBattle} "
                        + $"battleHits={battleHits.GetValueOrDefault((mission, battle))} hits={manager.GetCombatSummary().ShipHits - startingHits} longestIdle={longestIdle:F2}s stage={mission.Stage} log={path}");
                    Assert.Equal(MissionStage.Over, mission.Stage);
                    for (short side = 0; side < 3; side++)
                        Assert.Contains(fought.Keys, f => ReferenceEquals(f.Mission, mission) && f.Shooter == side);
                    output.WriteLine("fights=" + string.Join(",", fought.Where(f => ReferenceEquals(f.Key.Mission, mission)).Select(f => $"{f.Key.Shooter}->{f.Key.Victim}:{f.Value}")));
                    foreach (var side in mission.Sides)
                    {
                        int completed = side.Buckets.Count(b => b.Completed && b.Development is not null);
                        output.WriteLine($"side={side.Id} civ={side.Civilization.Name} kills={side.Kills} deaths={side.Deaths} stationKills={mission.StationKills(side.Id)} active={side.Active} completedDevelopments={completed}");
                        Assert.True(completed > 0, $"Side {side.Id} must complete real research");
                    }
                    var lines = File.ReadAllLines(path);
                    string receipt = lines.Last(l => l.StartsWith("[gameover] mission") && l.Contains("Stage Over"));
                    var deadline = lines.LastOrDefault(l => l.StartsWith("[finale] deadline winner") && l.Contains($"epoch={mission.ResearchEpoch}"));
                    Assert.True(mission.Sides.Any(s => !s.Active) || deadline is not null);
                    short expected;
                    if (deadline is not null)
                    {
                        var stations = mission.Sides.OrderByDescending(s => mission.StationKills(s.Id)).ToArray();
                        var kills = mission.Sides.OrderByDescending(s => s.Kills).ThenBy(s => s.Deaths).ToArray();
                        expected = mission.StationKills(stations[0].Id) > mission.StationKills(stations[1].Id) ? stations[0].Id
                            : kills[0].Kills != kills[1].Kills || kills[0].Deaths != kills[1].Deaths ? kills[0].Id : (short)-1;
                        Assert.Contains($"side={expected}", deadline);
                    }
                    else expected = Assert.Single(mission.Sides, s => s.Active).Id;
                    Assert.Contains($"iSideWinner={(expected < 0 ? "NA(draw)" : expected.ToString())}", receipt);
                    output.WriteLine($"winner={expected} deadline={deadline is not null} receipt={receipt}");
                    Assert.True(checkedIntel);
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
                foreach (var line in File.ReadAllLines(path).Where(l => l.StartsWith("[finale] deadline winner")))
                {
                    Assert.Contains("0:", line); Assert.Contains("1:", line); Assert.Contains("2:", line);
                }
                log.Flush(); var trace = File.ReadAllText(path);
                Assert.Equal(3, trace.Split('\n').Count(l => l.StartsWith("[gameover] mission") && l.Contains("Stage Over")));
                Assert.Contains("rotation=cycle#0", trace); Assert.Contains("rotation=cycle#1", trace); Assert.Contains("side2=5:Omicron", trace);
                Assert.Contains("source=derived", trace);
                for (int side = 0; side < 3; side++) Assert.Contains($"[research] side={side}", trace);
                Assert.Contains("clientGenerationChanged=True", trace);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

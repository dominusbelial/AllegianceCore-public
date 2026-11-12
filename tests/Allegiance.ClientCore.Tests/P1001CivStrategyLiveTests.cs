using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P1001CivStrategyLiveTests(ITestOutputHelper output)
{
    private sealed record SideResult(short Side, short Civ, int Research, short Kills, short Deaths, int StationKills,
        int MaxParallel, string[] Milestones);
    private sealed record Result(bool Enabled, double SimSeconds, double StartedSim, double ElapsedSeconds, long Hits, double LongestIdle, SideResult[] Sides);
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string EvidenceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, ".scratch", "p1001", "live"); Directory.CreateDirectory(path); return path;
    }

    [SkippableFact]
    public void Seeded_three_side_Gigacorp_Phoenix_Omicron_ON_and_OFF_socket_matches()
    {
        Require.Artwork();
        var off = Run(false); var on = Run(true);
        output.WriteLine(JsonSerializer.Serialize(new { seed = 1001, off, on }));
        Assert.All(on.Sides, s => { Assert.True(s.Research > 0); Assert.InRange(s.MaxParallel, 2, ResearchCommander.ParallelResearchLimit); });
        Assert.True(on.Hits > 0 && off.Hits > 0);
    }

    private Result Run(bool enabled)
    {
        var saved = Console.Out;
        string evidence = EvidenceDirectory();
        using var log = TextWriter.Synchronized(new StreamWriter(Path.Combine(evidence, enabled ? "on.log" : "off.log")) { AutoFlush = true });
        Console.SetOut(log);
        try
        {
            using var server = new Srv { SimulationRandom = new Random(1001) };
            var config = P733ResearchTests.Config(64);
            config.BenchmarkSeed = 1001; config.Bots.CivStrategy = enabled;
            config.LiveSettings.AutoNextMatch.Enabled = false;
            config.Showcase.FinaleTimeoutSeconds = 600;
            config.Games[0].Teams = 3; config.Games[0].CivIds = [18, 4, 5]; config.Games[0].SideNames = ["G", "P", "O"];
            config.Games[0].MapGen = new() { Type = "Brawl", Seed = 1001,
                PlayerSectorAsteroids = 0, PlayerSectorSpecialAsteroids = 0, PlayerSectorMineableAsteroids = 0,
                NeutralSectorAsteroids = 0, NeutralSectorSpecialAsteroids = 0, NeutralSectorMineableAsteroids = 0 };
            server.Boot(config);
            var mission = server.Missions[0];
            var sideCopies = mission.Sides.ToArray();
            double startedSim = double.NaN;
            var guard = new object(); using var stop = new CancellationTokenSource(); Exception? error = null;
            var ticker = new Thread(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        lock (guard)
                        {
                            server.TickSim();
                            if (mission.Stage == MissionStage.Started && double.IsNaN(startedSim)) startedSim = server.SimNow;
                        }
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"showcase","sides":[{"count":6},{"count":6},{"count":6}],"staging":false,"meeting_engagement":true,"finale_after_minutes":0,"loadout_seed":1001}""")!;
                var spawned = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(spawned.IsError ?? false, JsonSerializer.Serialize(spawned));
                var watch = Stopwatch.StartNew(); var parallel = new Dictionary<short, int>();
                var idle = new Dictionary<string, double>(); double longest = 0;
                // Normal advancing clock; no reset, scripted funding, tech grant,
                // hidden target injection or artificial wallet top-up.
                while (watch.Elapsed.TotalSeconds < 240 && mission.Stage != MissionStage.Over
                    && (double.IsNaN(startedSim) || server.SimNow - startedSim < 600))
                {
                    if (error is not null) throw error;
                    lock (guard)
                        foreach (var s in mission.Sides)
                        {
                            int active = s.Buckets.Count(b => b.ItemType == BucketItemType.BitDevelopment && !b.Completed && b.InvestedMoney > 0);
                            parallel[s.Id] = Math.Max(parallel.GetValueOrDefault(s.Id), active);
                            if (enabled) Assert.InRange(active, 0, ResearchCommander.ParallelResearchLimit);
                        }
                    foreach (var bot in manager.ActiveBots())
                    {
                        if (bot.Target is not null && bot.Speed < BotManager.IdleSpeedMps)
                        {
                            if (!idle.TryGetValue(bot.Name, out double since)) idle[bot.Name] = since = watch.Elapsed.TotalSeconds;
                            longest = Math.Max(longest, watch.Elapsed.TotalSeconds - since);
                            Assert.True(!enabled || watch.Elapsed.TotalSeconds - since < 5, $"Parked combat pilot: {JsonSerializer.Serialize(bot)}");
                        }
                        else idle.Remove(bot.Name);
                    }
                    Thread.Sleep(100);
                }
                var combat = manager.GetCombatSummary();
                log.Flush();
                var completions = Regex.Matches(File.ReadAllText(Path.Combine(evidence, enabled ? "on.log" : "off.log")),
                    @"\[research-done\] side (?<side>\d+) civ .*? tech (?<tech>\d+) .*? at sim-t (?<sim>[\d.]+)").Cast<Match>().ToArray();
                Result result;
                lock (guard)
                {
                    if (enabled) typeof(Srv).GetMethod("FlushCivStrategy", Private)!.Invoke(server, [mission, server.SimNow, true]);
                    result = new(enabled, server.SimNow, startedSim, server.SimNow - startedSim, combat.ShipHits, longest, sideCopies.Select(s => new SideResult(s.Id,
                        s.Civilization.CivilizationId, completions.Count(c => short.Parse(c.Groups["side"].Value) == s.Id),
                        s.Kills, s.Deaths, mission.StationKills(s.Id), parallel.GetValueOrDefault(s.Id),
                        completions.Where(c => short.Parse(c.Groups["side"].Value) == s.Id)
                            .Select(c => c.Groups["tech"].Value + "@" + (double.Parse(c.Groups["sim"].Value,
                                System.Globalization.CultureInfo.InvariantCulture) - startedSim).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).ToArray())).ToArray());
                }
                Assert.True(mission.Stage == MissionStage.Over || result.ElapsedSeconds >= 600, "Equal simulation observation window not reached");
                Assert.Equal(0, server.SimPassStageFailures);
                File.WriteAllText(Path.Combine(evidence, enabled ? "on.json" : "off.json"), JsonSerializer.Serialize(result));
                if (enabled)
                {
                    log.Flush();
                    string text = File.ReadAllText(Path.Combine(evidence, "on.log"));
                    Assert.Contains("stage=selected", text);
                    Assert.Contains("\"stage\":\"granted\"", text);
                    Assert.Contains("\"stage\":\"launched\"", text);
                    Assert.Contains("\"stage\":\"fired\"", text);
                    Assert.Contains("\"stage\":\"hit\"", text);
                    Assert.Contains("\"stage\":\"completed\"", text);
                }
                return result;
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableFact]
    public void Skirmish_six_per_side_ON_launches_strike_waves_and_kills_a_station()
    {
        Require.Artwork(); var saved = Console.Out;
        string path = Path.Combine(EvidenceDirectory(), "skirmish.log");
        using var log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true }); Console.SetOut(log);
        try
        {
            using var server = new Srv { SimulationRandom = new Random(1001) };
            // ResearchTimeScale is intentionally honored only in research mode
            // (Server.Lifecycle.cs). Keep this accelerated proof on that path.
            var config = P733ResearchTests.Config(64);
            config.BenchmarkSeed = 1001; config.Bots.CivStrategy = true; config.LiveSettings.AutoNextMatch.Enabled = false;
            config.Showcase.FinaleTimeoutSeconds = 2400;
            config.Games[0].CivIds = [18, 4];
            config.Games[0].MapGen = new() { Type = "Brawl", Seed = 1001,
                PlayerSectorAsteroids = 0, PlayerSectorSpecialAsteroids = 0, PlayerSectorMineableAsteroids = 0,
                NeutralSectorAsteroids = 0, NeutralSectorSpecialAsteroids = 0, NeutralSectorMineableAsteroids = 0 };
            server.Boot(config); var mission = server.Missions[0];
            using var stop = new CancellationTokenSource(); Exception? error = null;
            var ticker = new Thread(() =>
            { try { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(10); } } catch (Exception ex) { error = ex; } }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"showcase","count_a":6,"count_b":6,"staging":false,"meeting_engagement":true,"finale_after_minutes":1,"finale_side":"both","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":3,"finale_escort_share":0.7,"loadout_seed":1001}""")!;
                var spawned = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(spawned.IsError ?? false, JsonSerializer.Serialize(spawned));
                var watch = Stopwatch.StartNew(); bool killed = false;
                while (watch.Elapsed.TotalSeconds < 420 && mission.Stage != MissionStage.Over)
                {
                    if (error is not null) throw error;
                    killed |= mission.Sides.Any(s => mission.StationKills(s.Id) > 0);
                    if (killed) break;
                    Thread.Sleep(250);
                }
                log.Flush(); var text = File.ReadAllText(path);
                Assert.Contains("[strike]", text); Assert.Contains(" commit ", text);
                Assert.True(killed || mission.Sides.Any(s => mission.StationKills(s.Id) > 0), "No skirmish station kill; see skirmish.log");
                output.WriteLine($"six per side ON: station kill after {watch.Elapsed.TotalSeconds:F1}s; hits={manager.GetCombatSummary().ShipHits}; failures={server.SimPassStageFailures}");
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

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
public sealed class P832fKillBotLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Kills_preset_wins_by_native_kill_rule_while_miners_and_research_continue()
    {
        Require.Artwork();
        string logPath = Path.Combine(Path.GetTempPath(), $"p832f-kills-{Guid.NewGuid():N}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv();
            var config = P660NextMatchLiveTests.Config(false);
            config.BenchmarkSeed = 832;
            config.LiveSettings.TestHarness = null;
            config.LiveSettings.TestUnlock = null;
            config.Showcase = new() { TechMode = "research", IncomeMultiplier = 5000, ResearchTimeScale = 16 };
            config.Games[0].Map = "";
            config.Games[0].MapGen = new() { Type = "Brawl", Seed = 832,
                PlayerSectorAsteroids = 8, PlayerSectorMineableAsteroids = 4, PlayerSectorSpecialAsteroids = 8 };
            config.Games[0].Goals = new() { TeamKills = 3 };
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].CivIds = [25, 25];
            server.Boot(config);
            var mission = server.Missions[0];
            using var stop = new CancellationTokenSource();
            var gate = new object(); Exception? tickerError = null;
            var ticker = new Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(50); } }
                catch (Exception ex) { tickerError = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"kills","count_a":15,"count_b":15,"loadout_mode":"best"}""")!;
                var result = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var watch = Stopwatch.StartNew(); double nextTrace = 0;
                var miners = new Dictionary<short, int>();
                bool sawResearch = false;
                while (watch.Elapsed.TotalSeconds < 180 && mission.Stage != MissionStage.Over)
                {
                    lock (gate)
                        foreach (var side in mission.Sides)
                            miners[side.Id] = Math.Max(miners.GetValueOrDefault(side.Id), mission.Sectors.SelectMany(s => s.Models)
                                .OfType<Ship>().Count(s => s.SideId == side.Id && s.IsNpc && s.PilotType == Ship.PilotTypeMiner && !s.Dead));
                    sawResearch |= manager.ResearchStatus().Length == 2 && manager.ResearchStatus().All(r => r.Step > 0 || r.CompletedTechs.Length > 0);
                    if (watch.Elapsed.TotalSeconds >= nextTrace)
                    {
                        nextTrace += 5;
                        log.WriteLine($"[p832f-proof] wall={watch.Elapsed.TotalSeconds:F1} sim={server.SimNow:F1} " + JsonSerializer.Serialize(manager.ActiveBots()));
                    }
                    Assert.Null(tickerError);
                    Thread.Sleep(50);
                }
                var receipt = Stopwatch.StartNew();
                while (manager.Probe.World.GameOverResult is null && receipt.Elapsed.TotalSeconds < 5) Thread.Sleep(20);
                var over = manager.Probe.World.GameOverResult;
                output.WriteLine(JsonSerializer.Serialize(new { wallSeconds = watch.Elapsed.TotalSeconds, seed = 832,
                    winner = over?.SideWinner, reason = over?.GameoverMessage, miners, sawResearch, logPath, research = manager.ResearchStatus() }));
                Assert.Equal(MissionStage.Over, mission.Stage);
                Assert.NotNull(over);
                Assert.InRange(over.SideWinner, (short)0, (short)1);
                var winner = mission.Sides.Single(s => s.Id == over.SideWinner);
                Assert.Equal(3, winner.Kills);
                Assert.Equal($"{winner.Name} won by killing enough of their opponents", over.GameoverMessage);
                Assert.All(mission.Sides, side => Assert.True(miners.GetValueOrDefault(side.Id) >= 1));
                Assert.True(sawResearch);
                Assert.Empty(manager.FinaleStatus());
                Assert.Equal(0, server.SimPassStageFailures);
                log.Flush();
                Assert.Contains("duty=Hunt", File.ReadAllText(logPath));
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Server;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P832bProsperityLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Research_bots_grow_income_and_deliver_real_prosperity_game_over()
    {
        Require.Artwork();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "p832b-prosperity.json"))) root = root.Parent;
        var config = ServerConfigLoader.Load(Path.Combine(root!.FullName, "tools", "p832b-prosperity.json"), []);
        config.Port = 0;
        using var server = new Srv();
        server.Boot(config);
        var mission = server.Missions[0];
        var guard = new object();
        using var stop = new CancellationTokenSource();
        Exception? tickerError = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { lock (guard) server.TickSim(); Thread.Sleep(5); } }
            catch (Exception ex) { tickerError = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var scenario = McpTools.Handle("spawn_scenario", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","count_a":3,"count_b":3,"staging":true,"finale_after_minutes":0}""")!, manager);
            Assert.False(scenario.IsError ?? false, JsonSerializer.Serialize(scenario));
            Assert.Equal(6, manager.ActiveBots().Count);
            var watch = Stopwatch.StartNew();
            bool sawGoals = false, sawEnemyInvestment = false;
            var peakMiners = new Dictionary<short, int>();
            GameOver? result = null;
            while (watch.Elapsed.TotalSeconds < 150 && result is null)
            {
                lock (guard)
                {
                    foreach (var side in mission.Sides)
                    {
                        int count = mission.Sectors.SelectMany(s => s.Models).OfType<Ship>()
                            .Count(s => s.SideId == side.Id && !s.Dead && s.IsNpc && s.PilotType == Ship.PilotTypeMiner);
                        peakMiners[side.Id] = Math.Max(peakMiners.GetValueOrDefault(side.Id), count);
                    }
                }
                lock (manager.Probe.Gate)
                {
                    sawGoals |= manager.Probe.World.MissionGoals.TeamMoney == .01f;
                    if (manager.Probe.World.MySideId is { } side)
                        sawEnemyInvestment |= BotManager.ObserveEnemyProsperity(manager.Probe.World, side, mission.Core) >= .1;
                    result = manager.Probe.World.GameOverResult;
                }
                Assert.Null(tickerError);
                Thread.Sleep(20);
            }
            output.WriteLine(JsonSerializer.Serialize(new { pilots = manager.ActiveBots().Count, wallSeconds = watch.Elapsed.TotalSeconds,
                simulationSeconds = mission.LastUpdate, peakMiners, sawGoals, sawEnemyInvestment,
                winner = result?.SideWinner, reason = result?.GameoverMessage, research = manager.ResearchStatus() }));
            Assert.NotNull(result);
            Assert.True(sawGoals); Assert.True(sawEnemyInvestment);
            Assert.InRange(result.SideWinner, (short)0, (short)1);
            Assert.Contains("economic value", result.GameoverMessage);   // native reason text, FedSrv.CPP:11276-11277
            lock (guard)
            {
                Assert.Equal(MissionStage.Over, mission.Stage);
                Assert.True(peakMiners[result.SideWinner] >= 2);
                Assert.True(mission.Sides[result.SideWinner].Buckets.Single(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId).Completed);
                Assert.Equal(0, server.SimPassStageFailures);
            }
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
}

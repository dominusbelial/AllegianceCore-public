using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P924HomeSwapLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Swapped_fifteen_per_side_loopback_completes_by_real_combat()
    {
        Require.Artwork();
        using var server = new Srv();
        var config = P660NextMatchLiveTests.Config(false);
        config.BenchmarkSeed = 924;
        config.LiveSettings.TestHarness = null;
        config.Games[0].HomeSwap = "on";
        config.Games[0].CivIds = [25, 25];
        config.Games[0].Goals = new() { TeamKills = 3 };
        config.Games[0].MaxTeamImbalance = 0x7fff;
        // A co-located native topology bounds proof time; authored-map positions
        // and real rotations are independently covered by P924HomeAssignmentsTests.
        config.Games[0].MapGen = new() { Type = "Brawl", Seed = 924 };
        config.Showcase = new() { TechMode = "research", IncomeMultiplier = 5000, ResearchTimeScale = 16 };
        server.Boot(config);
        var mission = server.Missions[0];
        using var stop = new CancellationTokenSource();
        Exception? tickerError = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(50); } }
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
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 180 && manager.Probe.World.GameOverResult is null)
            {
                Assert.Null(tickerError);
                Thread.Sleep(50);
            }
            var over = manager.Probe.World.GameOverResult;
            Assert.NotNull(over);
            Assert.Equal(MissionStage.Over, mission.Stage);
            Assert.InRange(over.SideWinner, (short)0, (short)1);
            Assert.Equal(3, mission.Sides.Single(s => s.Id == over.SideWinner).Kills);
            Assert.Contains("won by killing enough", over.GameoverMessage);
            Assert.True(manager.GetCombatSummary().ShipHits > 0);
            Assert.Equal(0, server.SimPassStageFailures);
            output.WriteLine($"P924 15v15 homeSwap=on: completed in {watch.Elapsed.TotalSeconds:F1}s, winner={over.SideWinner}; {over.GameoverMessage}");
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
}

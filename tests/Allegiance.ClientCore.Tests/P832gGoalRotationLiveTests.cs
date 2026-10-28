using System.Diagnostics;
using System.Text.Json;
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
public sealed class P832gGoalRotationLiveTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("cycle")]
    [InlineData("random")]
    [InlineData("fixed")]
    public void Fresh_missions_advertise_selected_goals_before_countdown_and_fixed_goals_survive(string mode)
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(true);
        config.Games[0].CivSeed = 832;
        config.Games[0].Goals = mode == "fixed" ? new() { Flags = 2, TeamKills = 5 } : new();
        config.Showcase.GoalRotation = mode == "fixed" ? null : new() { Mode = mode };
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        using var peer = P660NextMatchLiveTests.Connect(server, "P832g-observer", 83207);
        var seeds = new Random(832);
        int previous = -1;
        short? previousShip = null;
        for (int match = 0; match < 8; match++)
        {
            int seed = match == 0 ? 832 : seeds.Next();
            var rotation = config.Showcase.GoalRotation;
            int index = rotation?.Select(match, seed, previous) ?? -1;
            var expected = rotation is null ? config.Games[0].Goals : rotation.Entries[index].Goals;
            Assert.Equal(expected, server.Missions[0].Goals);
            P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(peer,
                () => peer.World.MissionGoals == expected && peer.World.MyShipId != previousShip));
            previousShip = peer.World.MyShipId;
            Assert.NotEqual(MissionStage.Started, server.Missions[0].Stage);
            Assert.Equal(mode == "fixed" ? new MissionGoals { Flags = 2, TeamKills = 5 } : new(), config.Games[0].Goals);
            previous = index;
            server.Missions[0].Stage = MissionStage.Started;
            P660NextMatchLiveTests.End(server);
        }
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableFact]
    public void Two_matches_rotate_flags_to_artifacts_and_bots_win_each_native_rule()
    {
        Require.Artwork();
        var logPath = Path.Combine(Path.GetTempPath(), $"p832g-{Guid.NewGuid():N}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            var config = P660NextMatchLiveTests.Config(true, intermission: 5);
            config.BenchmarkSeed = 832;
            config.Showcase = new() { TechMode = "research", GoalRotation = new() { Entries =
                [new() { Name = "flags", Goals = new() { Flags = 1 } }, new() { Name = "artifacts", Goals = new() { Artifacts = 1 } }] } };
            config.LiveSettings.TestHarness = null;
            config.Games[0].Map = "";
            config.Games[0].CivSeed = 832;
            config.Games[0].CivIds = [25, 25];
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].MapGen = new() { Type = "Brawl", Seed = 832,
                PlayerSectorAsteroids = 0, PlayerSectorMineableAsteroids = 0, PlayerSectorSpecialAsteroids = 0,
                NeutralSectorAsteroids = 0, NeutralSectorMineableAsteroids = 0, NeutralSectorSpecialAsteroids = 0 };
            server.Boot(config);
            var gate = new object();
            using var stop = new CancellationTokenSource();
            Exception? tickerError = null;
            var ticker = new Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(20); } }
                catch (Exception ex) { tickerError = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var scenario = McpTools.Handle("spawn_scenario", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"showcase","count_a":6,"count_b":6,"loadout_mode":"best","finale_after_minutes":0}""")!, manager);
                Assert.False(scenario.IsError ?? false, JsonSerializer.Serialize(scenario));
                for (int match = 0; match < 2; match++)
                {
                    Mission mission; lock (gate) mission = server.Missions[0];
                    var expected = config.Showcase.GoalRotation.Entries[match].Goals;
                    Assert.Equal(expected, mission.Goals);
                    GameOver? over = null;
                    bool sawWireGoals = false;
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed.TotalSeconds < 360)
                    {
                        lock (manager.Probe.Gate)
                        {
                            sawWireGoals |= manager.Probe.World.MissionGoals == expected;
                            over = manager.Probe.World.GameOverResult;
                        }
                        Assert.Null(tickerError);
                        if (over is not null && mission.Stage == MissionStage.Over) break;
                        Thread.Sleep(20);
                    }
                    output.WriteLine($"match={match + 1} goals={config.Showcase.GoalRotation.Entries[match].Name} wall={watch.Elapsed.TotalSeconds:F1} winner={over?.SideWinner} reason={over?.GameoverMessage} log={logPath}");
                    Assert.True(sawWireGoals);
                    Assert.NotNull(over);
                    Assert.InRange(over.SideWinner, (short)0, (short)1);
                    var winner = mission.Sides.Single(s => s.Id == over.SideWinner);
                    Assert.Equal(1, match == 0 ? winner.Flags : winner.Artifacts);
                    Assert.StartsWith($"{winner.Name} wins with the {(match == 0 ? "flag" : "artifact")} recovered by mcp-", over.GameoverMessage);
                    if (match == 0)
                    {
                        var deadline = Stopwatch.StartNew();
                        while (deadline.Elapsed.TotalSeconds < 30)
                        {
                            lock (manager.Probe.Gate)
                                if (manager.Probe.World.MissionGoals == config.Showcase.GoalRotation.Entries[1].Goals
                                    && manager.Probe.World.GameOverResult is null) break;
                            Thread.Sleep(20);
                        }
                        lock (manager.Probe.Gate) Assert.Equal(config.Showcase.GoalRotation.Entries[1].Goals, manager.Probe.World.MissionGoals);
                        Assert.All(manager.ActiveBots(), b => Assert.True(b.WorldReady));
                    }
                }
                Assert.Equal(0, server.SimPassStageFailures);
                Assert.Empty(manager.FinaleStatus());
                log.Flush();
                var trace = File.ReadAllText(logPath);
                Assert.Contains("goals=flags(flags=1)", trace);
                Assert.Contains("goals=artifacts(artifacts=1)", trace);
                Assert.Contains("duty=Raid", trace);
                Assert.Contains("duty=Collect", trace);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

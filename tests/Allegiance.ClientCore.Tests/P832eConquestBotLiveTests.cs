using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P832eConquestBotLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Conquest_preset_discovers_and_wins_by_native_station_rule_under_fog()
    {
        Require.Artwork();
        string logPath = Path.Combine(Path.GetTempPath(), $"p832e-conquest-{Guid.NewGuid():N}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            var config = P660NextMatchLiveTests.Config(false);
            config.BenchmarkSeed = 832;
            config.LiveSettings.TestHarness = null;
            config.LiveSettings.TestUnlock = new() { Enabled = true, AllTech = true, Money = 1_000_000 };
            config.Games[0].Map = "";
            config.Games[0].MapGen = new() { Type = "Brawl", Seed = 832,
                PlayerSectorAsteroids = 0, PlayerSectorMineableAsteroids = 0, PlayerSectorSpecialAsteroids = 0,
                NeutralSectorAsteroids = 0, NeutralSectorMineableAsteroids = 0, NeutralSectorSpecialAsteroids = 0 };
            config.Games[0].Goals = new() { ConquestPercentage = 65 };
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].CivIds = [25, 25];
            server.Boot(config);
            var mission = server.Missions[0];
            // Four ordinary flag/restart bases: destroying ONE changes 50/50 to
            // 66/33 while BOTH teams still have a restart. Thus elimination
            // cannot explain this proof's GAME_OVER. No damage is injected.
            foreach (var home in mission.Sectors.SelectMany(s => s.StationRuntimes).ToArray())
            {
                var data = home.Data with { StationId = (short)(home.Data.StationId + 1000), Name = home.Data.Name + " Annex",
                    Position = [home.Data.Position[0], home.Data.Position[1] + 1500, home.Data.Position[2]] };
                mission.PlaceStation(new Station(data, home.Type), mission.Sectors.Single(s => s.Id == home.ClusterId));
            }
            var initialStations = mission.Sectors.SelectMany(s => s.StationRuntimes).Select(s => s.Data).ToArray();
            using var stop = new CancellationTokenSource();
            var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); } }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"conquest","count_a":15,"count_b":15,"loadout_mode":"best"}""")!;
                var result = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var watch = Stopwatch.StartNew(); double nextTrace = 0;
                while (watch.Elapsed.TotalSeconds < 360 && mission.Stage != MissionStage.Over)
                {
                    if (watch.Elapsed.TotalSeconds >= nextTrace)
                    {
                        nextTrace += 5;
                        log.WriteLine($"[p832e-proof] wall={watch.Elapsed.TotalSeconds:F1} sim={server.SimNow:F1} " + JsonSerializer.Serialize(manager.ActiveBots()));
                    }
                    Thread.Sleep(50);
                }
                var receipt = Stopwatch.StartNew();
                while (manager.Probe.World.GameOverResult is null && receipt.Elapsed.TotalSeconds < 5) Thread.Sleep(20);
                var over = manager.Probe.World.GameOverResult;
                output.WriteLine($"conquest goal=65 wall={watch.Elapsed.TotalSeconds:F1}s stage={mission.Stage} winner={over?.SideWinner} reason={over?.GameoverMessage} log={logPath}");
                Assert.Equal(MissionStage.Over, mission.Stage);
                Assert.NotNull(over);
                Assert.InRange(over.SideWinner, (short)0, (short)1);
                Assert.True(mission.Sides.Single(s => s.Id == over.SideWinner).ConquestPercent >= 65);
                Assert.All(mission.Sides, side => Assert.Contains(mission.Sectors.SelectMany(s => s.StationRuntimes),
                    s => s.SideId == side.Id && s.Type.HasCapability(DataStationTypeIGC.SabmRestart)));
                var winner = mission.Sides.Single(s => s.Id == over.SideWinner);
                var reasons = initialStations.Where(s => s.SideId != winner.Id).SelectMany(st =>
                    manager.ActiveBots().Where(b => b.Side == winner.Id).Select(b => $"{winner.Name} won because {b.Name} captured {st.Name}")
                        .Append($"{winner.Name} has destroyed {mission.Sides.Single(s => s.Id == st.SideId).Name}'s {st.Name} in {mission.Sectors.Single(s => s.Id == st.ClusterId).Name}."));
                Assert.Contains(over.GameoverMessage, reasons);
                Assert.Empty(manager.FinaleStatus());
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

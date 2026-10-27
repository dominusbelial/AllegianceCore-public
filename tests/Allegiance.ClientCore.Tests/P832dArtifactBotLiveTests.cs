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
public sealed class P832dArtifactBotLiveTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("Brawl", 1)]
    [InlineData("SingleRing", 1)]
    [InlineData("Brawl", 2)]
    public void Artifacts_preset_searches_collects_returns_and_wins_under_fog(string topology, int captures)
    {
        Require.Artwork();
        string logPath = Path.Combine(Path.GetTempPath(), $"p832d-artifacts-{Guid.NewGuid():N}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            var config = P660NextMatchLiveTests.Config(false);
            config.BenchmarkSeed = 832;
            config.Showcase.TechMode = "research";
            config.LiveSettings.TestHarness = null; // normal native purchase/tech gates and starting economy
            config.Games[0].Map = "";
            config.Games[0].MapGen = new() { Type = topology, Seed = 832,
                PlayerSectorAsteroids = 0, PlayerSectorMineableAsteroids = 0, PlayerSectorSpecialAsteroids = 0,
                NeutralSectorAsteroids = 0, NeutralSectorMineableAsteroids = 0, NeutralSectorSpecialAsteroids = 0 };
            config.Games[0].Goals = new() { Artifacts = (short)captures };
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].CivIds = [25, 25];
            server.Boot(config);
            var mission = server.Missions[0];
            using var stop = new CancellationTokenSource();
            var simGate = new object();
            Treasure[] Artifacts() { lock (simGate) return mission.Sectors.SelectMany(s => s.Models).OfType<Treasure>()
                .Where(t => t.Data.TreasureId == Allegiance.Core.Model.Side.ArtifactSentinelFlag).ToArray(); }
            var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { lock (simGate) server.TickSim(); Thread.Sleep(20); } }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"artifacts","count_a":6,"count_b":6,"loadout_mode":"best"}""")!;
                var result = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var watch = Stopwatch.StartNew();
                bool picked = false, intercepted = false;
                var initialArtifacts = Artifacts().Select(t => t.Id).ToHashSet();
                foreach (var artifact in Artifacts()) log.WriteLine($"[p832d-object] initial id={artifact.Id} sector={artifact.OwnerSector.Id} position={artifact.Position}");
                bool respawned = false;
                var loggedArtifacts = initialArtifacts.ToHashSet();
                var firstDistance = new Dictionary<(short Defender, short Carrier), float>();
                double nextTrace = 0;
                while (watch.Elapsed.TotalSeconds < 360 && mission.Stage != MissionStage.Over)
                {
                    var artifacts = Artifacts();
                    respawned |= artifacts.Any(t => !initialArtifacts.Contains(t.Id));
                    foreach (var artifact in artifacts.Where(t => loggedArtifacts.Add(t.Id)))
                        log.WriteLine($"[p832d-object] respawn id={artifact.Id} sector={artifact.OwnerSector.Id} position={artifact.Position}");
                    picked |= server.Ships.Any(s => s.Ship.Flag == Allegiance.Core.Model.Side.ArtifactSentinelFlag);
                    // Evidence only: decisions are made by each bot's wire world.
                    foreach (var defender in manager.ActiveBots())
                    {
                        var thief = server.Ships.FirstOrDefault(s => s.Ship.Id == defender.Target
                            && s.Ship.Flag == Allegiance.Core.Model.Side.ArtifactSentinelFlag && s.Ship.SideId != defender.Side)?.Ship;
                        var fighter = server.Ships.FirstOrDefault(s => s.Ship.Id == defender.ShipId)?.Ship;
                        if (thief is null || fighter is null || thief.Sector != fighter.Sector) continue;
                        float distance = (thief.Position - fighter.Position).Length();
                        var key = (defender.ShipId, thief.Id);
                        firstDistance.TryAdd(key, distance);
                        if (firstDistance[key] - distance > 50 && defender.Speed > 1)
                        {
                            if (!intercepted) output.WriteLine($"intercept defender={defender.ShipId} carrier={thief.Id} wall={watch.Elapsed.TotalSeconds:F1}s range={firstDistance[key]:F1}->{distance:F1}");
                            intercepted = true;
                        }
                    }
                    if (watch.Elapsed.TotalSeconds >= nextTrace)
                    {
                        nextTrace += 5;
                        log.WriteLine($"[p832d-proof] wall={watch.Elapsed.TotalSeconds:F1} sim={server.SimNow:F1} "
                            + JsonSerializer.Serialize(manager.ActiveBots()));
                    }
                    Thread.Sleep(50);
                }
                var receipt = Stopwatch.StartNew();
                while (manager.Probe.World.GameOverResult is null && receipt.Elapsed.TotalSeconds < 5) Thread.Sleep(20);
                var gameOver = manager.Probe.World.GameOverResult;
                output.WriteLine($"artifacts topology={topology} goal={captures} wall={watch.Elapsed.TotalSeconds:F1}s picked={picked} stage={mission.Stage} winner={gameOver?.SideWinner} reason={gameOver?.GameoverMessage} log={logPath}");
                if (topology == "Brawl" && captures == 2) Assert.True(intercepted, $"No visible closing intercept: {logPath}");
                if (captures > 1) Assert.True(respawned, "A below-goal secure must respawn an artifact");
                Assert.True(picked, $"No real pickup: {logPath}");
                Assert.Equal(MissionStage.Over, mission.Stage);
                Assert.NotNull(gameOver);
                Assert.InRange(gameOver.SideWinner, (short)0, (short)1);
                Assert.Equal((short)captures, mission.Sides.Single(s => s.Id == gameOver.SideWinner).Artifacts);
                Assert.StartsWith($"{mission.Sides.Single(s => s.Id == gameOver.SideWinner).Name} wins with the artifact recovered by mcp-", gameOver.GameoverMessage);
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

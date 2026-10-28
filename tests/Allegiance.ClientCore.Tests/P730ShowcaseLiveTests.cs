using System.Diagnostics;
using System.Reflection;
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
public sealed class P730ShowcaseLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch()
    {
        Require.Artwork();
        using var server = new Srv();
        var config = P733ResearchTests.Config(64);
        config.Games[0].MapGen = null; config.Games[0].Map = "m2xspiderw.igc";
        config.Games[0].MaxPlayersPerTeam = 40;
        server.Boot(config);
        var simGate = new object();
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            { lock (simGate) server.TickSim(); Thread.Sleep(20); }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","count_a":15,"count_b":15,"target_sector":"Alderan","staging":true,"finale_after_minutes":0}""")!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            for (int match = 1; match <= 2; match++)
            {
                Wait(() => server.Missions[0].Stage == MissionStage.Started && manager.ActiveBots().Count == 30, "match started");
                var mission = server.Missions[0];
                var before = manager.GetCombatSummary();
                Wait(() => manager.GetCombatSummary().ShotsFired > before.ShotsFired
                    && manager.GetCombatSummary().ShipHits > before.ShipHits, "ordinary gunfire and hits");
                Wait(() =>
                {
                    lock (simGate) return mission.Sectors.SelectMany(s => s.Mineable).Any(r => r.OreRemaining < r.Data.Ore - 1);
                }, "natural mining");
                Wait(() => manager.ResearchStatus().Any(r => r.Step > 1), "research commander progress");
                var roster = manager.ActiveBots();
                short victim;
                // Pick and kill the victim under ONE sim lock: between two locks a
                // natural fight or a dock could take the fighter first, and the
                // global Kills/PodsCreated counters below would still advance from
                // some other death — leaving a 180 s wait for a pod that never comes.
                lock (simGate)
                {
                    victim = roster.First(b => b.Role.Equals("fighter", StringComparison.OrdinalIgnoreCase)
                        && server.Ships.Any(l => l.Ship.Id == b.ShipId && l.Ship.Weapons.Count > 0 && l.Ship.StationId is null && !l.Ship.Dead)).ShipId;
                    var own = server.Ships.Single(l => l.Ship.Id == victim).Ship;
                    var enemy = server.Ships.First(l => l.Ship.SideId != own.SideId && l.Ship.Weapons.Count > 0).Ship;
                    Assert.Equal(DamageResult.Killed, own.ReceiveDamage(0, 1_000_000, server.SimNow, own.Position, own.Position, enemy));
                }
                string victimName = roster.Single(b => b.ShipId == victim).Name;
                Wait(() => manager.GetCombatSummary().Kills > before.Kills && manager.GetCombatSummary().PodsCreated > before.PodsCreated,
                    "controlled death delivered over the real wire");
                Wait(() => manager.GetCombatSummary().Bots.Any(b => b.Name == victimName && b.IsPod), "lifepod observed by its controller");
                // Exercise the native rescue endpoint at a known station. The bot
                // then buys and launches through its ordinary lifecycle controller.
                lock (simGate)
                {
                    var own = server.Ships.Single(l => l.Ship.Id == victim).Ship;
                    var home = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == own.SideId);
                    typeof(Srv).GetMethod("PerformStationRescue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [own, home]);
                }
                Wait(() =>
                {
                    lock (simGate) return server.Ships.Any(l => l.Ship.Id == victim && l.Ship.StationId is null
                        && !l.Ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod) && l.Ship.Weapons.Count > 0);
                }, "real purchase and relaunch");
                var summary = manager.GetCombatSummary();
                float depleted;
                lock (simGate) depleted = mission.Sectors.SelectMany(s => s.Mineable).Max(r => r.Data.Ore - r.OreRemaining);
                output.WriteLine($"match={match}: shots={summary.ShotsFired}, hits={summary.ShipHits}, kills={summary.Kills}, pods={summary.PodsCreated}, minedOreAtLeast={depleted:F2}; "
                    + JsonSerializer.Serialize(manager.ResearchStatus()));
                var oldIds = manager.ActiveBots().Select(b => b.ShipId).ToHashSet();
                lock (simGate) P660NextMatchLiveTests.End(server);
                Wait(() => !ReferenceEquals(mission, server.Missions[0]) && manager.ActiveBots().All(b => !oldIds.Contains(b.ShipId)), "automatic migration");
            }
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
    [SkippableFact]
    public void Live_lod_reduces_distant_decisions_and_immediately_restores_near_focus_rate()
    {
        Require.Artwork();
        string? previous = Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_LOD");
        string? pilot = Environment.GetEnvironmentVariable("ALLEGIANCE_LOCAL_PILOT");
        Environment.SetEnvironmentVariable("ALLEGIANCE_BOT_LOD", "on");
        Environment.SetEnvironmentVariable("ALLEGIANCE_LOCAL_PILOT", null);
        try
        {
            using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
            server.Boot(P660NextMatchLiveTests.Config(false));
            using var stop = new CancellationTokenSource();
            var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); } }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"size":1,"staging":false,"finale_after_minutes":0}""")!;
                var spawned = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(spawned.IsError ?? false, JsonSerializer.Serialize(spawned));
                Wait(() => manager.ActiveBots().Count == 2 && manager.ActiveBots().All(b => b.WorldReady), "two live worlds");
                var bot = manager.ActiveBots()[0];
                long Count() => manager.ActiveBots().Single(b => b.Name == bot.Name).AiDecisions;
                long start = Count(); Thread.Sleep(2100); long distant = Count() - start;
                manager.SetFocusSector(bot.Sector);
                start = Count(); Thread.Sleep(2100); long near = Count() - start;
                output.WriteLine($"decisions in 2.1s: distant={distant}, near={near}");
                Assert.InRange(distant, 2, 6);
                Assert.True(near >= 15 && near > distant * 2, $"distant={distant}, near={near}");
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_BOT_LOD", previous);
            Environment.SetEnvironmentVariable("ALLEGIANCE_LOCAL_PILOT", pilot);
        }
    }

    private static void Wait(Func<bool> condition, string reason)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed.TotalSeconds < 180) Thread.Sleep(50);
        Assert.True(condition(), reason);
    }
}

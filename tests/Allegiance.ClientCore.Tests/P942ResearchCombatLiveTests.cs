using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>Small real-wallet, real-socket measurements, with each civilization in each slot.</summary>
[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P942ResearchCombatLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Generated_commanders_complete_every_own_tree_development_with_real_buckets_and_constructors()
    {
        Require.Artwork();
        string evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/p942"));
        Directory.CreateDirectory(evidence);
        string path = Path.Combine(evidence, $"closure-{DateTime.UtcNow:yyyyMMddTHHmmss}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            var config = P660NextMatchLiveTests.Config(false);
            config.BenchmarkSeed = 942;
            config.Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" };
            config.Transport = new() { Mode = "pool" };
            config.Sim.NativeGotoPlan = true;
            config.Games[0].CivIds = [18, 4];
            // This closure proof deliberately removes combat/fog as confounders.
            // The paired small-fleet proof above retains both. No wallet or tech grants.
            config.Games[0].ShowMap = true;
            config.Games[0].Goals = new();
            config.LiveSettings.TestHarness = null;
            config.Showcase.TechMode = "research";
            config.Showcase.IncomeMultiplier = 5000;
            config.Showcase.ResearchTimeScale = 16;
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            var mission = server.Missions[0];
            var expectedIds = mission.Sides.ToDictionary(s => s.Id, s => new ResearchTree(mission.Core, s.Civilization.CivilizationId)
                .Generate("PCore001", prioritizeResearch: true).Steps.Where(step => step.Action == "develop").Select(step => step.Id).ToHashSet());
            var expected = expectedIds.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
            var gate = new object();
            using var stop = new CancellationTokenSource(); Exception? tickerError = null;
            var ticker = new Thread(() =>
            {
                // Run ordinary fixed simulation steps at a shorter wall pause:
                // this is a reachability proof, not a throughput/balance sample.
                try { while (!stop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(5); } }
                catch (Exception ex) { tickerError = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var watch = Stopwatch.StartNew();
                bool complete = false;
                while (watch.Elapsed.TotalSeconds < 1800)
                {
                    Assert.Null(tickerError);
                    lock (gate) complete = mission.Sides.All(s => mission.Skills!.Count(s.Id, ShowcaseSkill.Research) == expected[s.Id]);
                    if (complete) break;
                    Thread.Sleep(250);
                }
                var reports = manager.ResearchStatus();
                lock (gate)
                {
                    var summary = mission.Sides.Select(s => new { side = s.Id, civ = s.Civilization.CivilizationId,
                        expected = expected[s.Id], completed = mission.Skills!.Count(s.Id, ShowcaseSkill.Research),
                        commander = reports.SingleOrDefault(r => r.Side == s.Id), simTime = server.SimNow }).ToArray();
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                    foreach (var row in summary) output.WriteLine(JsonSerializer.Serialize(row));
                    Assert.True(complete, $"Own-tree closure failed: {JsonSerializer.Serialize(summary)}; evidence={path}");
                    var receipts = File.ReadAllLines(path).Select(line => Regex.Match(line,
                        @"^\[research-done\] side (\d+) civ \d+ .* tech (\d+) ")).Where(m => m.Success).ToArray();
                    foreach (var side in mission.Sides)
                    {
                        var ids = receipts.Where(m => short.Parse(m.Groups[1].Value) == side.Id).Select(m => short.Parse(m.Groups[2].Value)).ToArray();
                        Assert.Equal(expectedIds[side.Id].Order(), ids.Order());
                    }
                    P660NextMatchLiveTests.End(server);
                }
                output.WriteLine($"evidence={path} wall={watch.Elapsed.TotalSeconds:F1}s");
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_civilizations_research_and_fight_in_either_slot(bool swap)
    {
        Require.Artwork();
        string evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/p942"));
        Directory.CreateDirectory(evidence);
        string path = Path.Combine(evidence, $"combat-{(swap ? "4-18" : "18-4")}-{DateTime.UtcNow:yyyyMMddTHHmmss}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            var config = P660NextMatchLiveTests.Config(false);
            config.BenchmarkSeed = 942;
            config.Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" };
            config.Transport = new() { Mode = "pool" };
            config.Sim.NativeGotoPlan = true;
            config.Games[0].CivIds = swap ? [4, 18] : [18, 4];
            config.Games[0].ShowMap = false;
            config.Games[0].ShowHomeSector = true;
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].Goals = new() { GameLengthSeconds = 240 };
            config.LiveSettings.TestHarness = null;
            config.Showcase.TechMode = "research";
            config.Showcase.IncomeMultiplier = 5000;
            config.Showcase.ResearchTimeScale = 16;
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            var mission = server.Missions[0];
            var gate = new object();
            var sidesByShip = new Dictionary<short, short>();
            using var stop = new CancellationTokenSource(); Exception? tickerError = null;
            var ticker = new Thread(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        lock (gate)
                        {
                            server.TickSim();
                            foreach (var live in server.Ships)
                                if (live.Ship.SideId is { } side) sidesByShip[live.Ship.Id] = side;
                        }
                        Thread.Sleep(20);
                    }
                }
                catch (Exception ex) { tickerError = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var result = McpTools.Handle("spawn_scenario", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"showcase","count_a":15,"count_b":15,"staging":true,"loadout_mode":"best","meeting_engagement":true,"finale_after_minutes":0}""")!, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 300 && mission.Stage != MissionStage.Over)
                { Assert.Null(tickerError); Thread.Sleep(100); }
                lock (gate) { }
                Assert.Equal(MissionStage.Over, mission.Stage);
                Assert.Equal(0, server.SimPassStageFailures);
                var reports = manager.ResearchStatus();
                var lines = File.ReadAllLines(path);
                var purchases = lines.Select(line => Regex.Match(line,
                        @"^\[loadout\] \S+ ship (\d+) <- BUY_LOADOUT_ACK hull (\d+).* fbought True"))
                    .Where(m => m.Success).Select(m => new { Ship = short.Parse(m.Groups[1].Value), Hull = short.Parse(m.Groups[2].Value) })
                    .Where(p => sidesByShip.ContainsKey(p.Ship)).ToArray();
                var rows = mission.Sides.Select(side => new
                {
                    side = side.Id, civ = side.Civilization.CivilizationId, name = side.Civilization.Name,
                    developments = lines.Select(l => Regex.Match(l, @"^\[devcomplete\] side (\d+): completed development (\d+) '([^']+)'")).Where(m => m.Success && short.Parse(m.Groups[1].Value) == side.Id)
                        .Select(m => new { id = short.Parse(m.Groups[2].Value), name = m.Groups[3].Value }).ToArray(),
                    hullsBought = purchases.Where(p => sidesByShip[p.Ship] == side.Id).GroupBy(p => p.Hull)
                        .Select(g => new { id = g.Key, name = mission.Core.HullTypes[g.Key].Name, count = g.Count() }).ToArray(),
                    skills = SkillTelemetry.Catalogue.Select((s, i) => new { s.Id, count = mission.Skills!.Count(side.Id, (ShowcaseSkill)i) }).ToArray(),
                    commander = reports.SingleOrDefault(r => r.Side == side.Id), side.Kills, side.Deaths,
                }).ToArray();
                File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var row in rows)
                {
                    output.WriteLine(JsonSerializer.Serialize(row));
                    Assert.NotEmpty(row.developments);
                    Assert.Equal(row.developments.Length, mission.Skills!.Count(row.side, ShowcaseSkill.Research));
                    Assert.NotEmpty(row.hullsBought);
                    Assert.True(mission.Skills!.Count(row.side, ShowcaseSkill.Guns) > 0);
                }
                output.WriteLine($"evidence={path} wall={watch.Elapsed.TotalSeconds:F1}s");
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}

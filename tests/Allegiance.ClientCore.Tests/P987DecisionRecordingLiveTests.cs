using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P987 opt-in recorder, not a standing Live gate. Run one side count per
/// process with UPDATE_CORPUS=1 and the ALLEGIANCE_BOT_RECORD_* variables set.
/// The match cap supplies a real server game-over even when a strike does not
/// level a station; nothing here forces a bot decision or a winner.
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
[Trait("Requires", "Artwork")]
public sealed class P987DecisionRecordingLiveTests(ITestOutputHelper output)
{
    private static string PlanDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "showcase-plans"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data", "showcase-plans");
    }

    [SkippableTheory]
    [InlineData(2)]
    [InlineData(3)]
    public void Seeded_showcase_records_combat_finale_game_over_and_next_match(int teams)
    {
        Skip.If(Environment.GetEnvironmentVariable("UPDATE_CORPUS") != "1",
            "P987 live recording is opt-in: set UPDATE_CORPUS=1 and ALLEGIANCE_BOT_RECORD_DECISIONS=1");
        Require.Artwork();
        string scenario = Environment.GetEnvironmentVariable(BotDecisionRecorder.ScenarioVar) ?? "";
        Assert.Equal($"seeded-{teams}side", scenario);

        var config = P733ResearchTests.Config(64);
        config.BenchmarkSeed = 987;
        config.Showcase.MatchCapSeconds = teams == 2 ? 240 : 600;
        config.Showcase.FinaleTimeoutSeconds = teams == 2 ? 180 : 600;
        config.LiveSettings.AutoNextMatch.Enabled = true;
        config.LiveSettings.AutoNextMatch.IntermissionSeconds = 2;
        config.Games[0].Name = $"P987 seeded {teams}-side decision recording";
        config.Games[0].Teams = teams;
        config.Games[0].CivSeed = 987;
        config.Games[0].CivIds = teams == 2 ? [25, 3] : [25, 3, 18];
        config.Games[0].SideNames = Enumerable.Range(0, teams).Select(i => $"P987 side {i}").ToArray();
        config.Games[0].MaxPlayersPerTeam = 40;
        config.Games[0].ResearchPlanDirectory = PlanDirectory();
        config.Games[0].ResearchPlans = Enumerable.Range(0, teams).ToDictionary(i => (short)i, _ => "auto");
        if (teams == 2)
        {
            config.Games[0].MapGen = null;
            config.Games[0].Map = "m2xspiderw.igc";
            // Reveal static objects so the short capture registers its finale
            // before the cap; combat and all subsequent decisions remain live.
            config.Games[0].ShowMap = true;
        }
        else
        {
            config.Games[0].Map = "";
            config.Games[0].MapGen = new() { Type = "SingleRing", Seed = 987 };
            // Both shapes exercise the no-fog override; the two-side run also
            // uses an explicit Alderan rendezvous and this one uses the ordinary
            // public-topology rendezvous. Native starting-tech overrides give
            // this shape a real strike-wave release without disabling research-
            // mode finale duty selection; the two-side run records natural research.
            config.Games[0].ShowMap = true;
            config.Games[0].TechBitOverrides = Enumerable.Range(0, teams)
                .SelectMany(side => Enumerable.Range(0, 400).Select(bit =>
                    new Allegiance.Server.TechBitOverrideConfig { Side = (short)side, Bit = (short)bit, Setting = true }))
                .ToArray();
        }

        using var server = new Srv();
        server.Boot(config);
        using var stop = new CancellationTokenSource();
        var simGate = new object();
        Exception? tickerError = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { lock (simGate) server.TickSim(); Thread.Sleep(20); } }
            catch (Exception ex) { tickerError = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var arguments = new Dictionary<string, object?>
            {
                ["preset"] = "showcase",
                ["sides"] = Enumerable.Range(0, teams).Select(_ => new { count = teams == 2 ? 10 : 12 }).ToArray(),
                ["staging"] = true,
                ["finale_after_minutes"] = 0.05,
                ["finale_side"] = teams == 2 ? "both" : "all",
                ["finale_min_relaunch_share"] = 0,
                ["finale_min_kills_last_5_minutes"] = 1,
                ["finale_bombers"] = 4,
                ["finale_survival_margin"] = 0,
                ["finale_deadline_seconds"] = teams == 2 ? 900 : 60,
                ["loadout_mode"] = "best",
                ["__fleet_id"] = 987,
            };
            if (teams == 2) arguments["target_sector"] = "Alderan";
            var args = JsonSerializer.SerializeToElement(arguments).EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            var spawn = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(spawn.IsError ?? false, JsonSerializer.Serialize(spawn));

            Mission first = server.Missions[0];
            // Same acceleration as P733's finale fixture: export the stations
            // that scouts would eventually reveal. No bot input or decision is
            // synthesized; all later sightings, orders and strikes are live.
            lock (simGate)
                foreach (var station in first.Sectors.SelectMany(s => s.StationRuntimes))
                    foreach (short side in first.Sides.Select(s => s.Id))
                        station.SideVisibility.SetSideVisibility(side, true);
            bool combat = false, finale = false, rotated = false, nextStarted = false;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 360)
            {
                var summary = manager.GetCombatSummary();
                combat |= summary.ShotsFired > 0 && summary.ShipHits > 0;
                foreach (var row in JsonSerializer.SerializeToElement(manager.FinaleStatus()).EnumerateArray())
                    finale |= row.GetProperty("selectedSides").GetArrayLength() > 0;
                Mission current = server.Missions[0];
                if (!ReferenceEquals(first, current))
                {
                    rotated = true;
                    if (current.Stage == MissionStage.Started)
                    {
                        nextStarted = true;
                        // Let the ordinary client lifecycle consume the fresh
                        // mission and record MissionRefresh/relaunch decisions.
                        Thread.Sleep(2000);
                        break;
                    }
                }
                if (teams == 3 && watch.Elapsed.TotalSeconds >= 300 && combat && finale && !rotated)
                {
                    // Reuse P660's real server game-over path after the capture
                    // window; rotation, reseating and the next countdown are not
                    // forced. This bounds an all-tech three-way fight that would
                    // otherwise continue indefinitely.
                    lock (simGate) P660NextMatchLiveTests.End(server);
                }
                Assert.Null(tickerError);
                Thread.Sleep(200);
            }
            bool waveRelease = false;
            string? rawDirectory = Environment.GetEnvironmentVariable(BotDecisionRecorder.PathVar);
            string finaleRecords = Path.Combine(rawDirectory ?? "", "Finale.jsonl");
            if (rawDirectory is not null && File.Exists(finaleRecords))
                foreach (string line in File.ReadLines(finaleRecords))
                {
                    using var record = JsonDocument.Parse(line);
                    if (record.RootElement.GetProperty("decision").GetString() == "ShouldRelease"
                        && record.RootElement.GetProperty("outputs").GetProperty("release").GetBoolean())
                    { waveRelease = true; break; }
                }
            output.WriteLine($"P987 {scenario}: wall={watch.Elapsed.TotalSeconds:F1}s combat={combat} finale={finale} "
                + $"waveRelease={waveRelease} rotated={rotated} nextStarted={nextStarted}; {JsonSerializer.Serialize(manager.GetCombatSummary())}");
            Assert.True(combat, "The recorded match must reach real gunfire and hull damage");
            Assert.True(finale, "The recorded match must arm at least one finale/strike side");
            if (teams == 3) Assert.True(waveRelease, "The unlocked 3-side match must release a real strike wave");
            Assert.True(rotated, "The real server match cap must produce game over and a fresh mission");
            Assert.True(nextStarted, "The fresh mission must reach Started through the ordinary auto-next flow");
            Assert.Null(tickerError);
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally
        {
            stop.Cancel();
            Assert.True(ticker.Join(5000));
        }
    }
}

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class SC3CloakLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Research_showcase_cloaks_on_both_sides_and_bomber_decloaks_to_strike()
    {
        Require.Artwork();
        var config = P733ResearchTests.Config(64);
        config.Showcase.IncomeMultiplier = 10000;
        config.Showcase.FinaleTimeoutSeconds = 900;
        config.LiveSettings.AutoNextMatch.Enabled = false;
        config.Games[0].CivIds = [35, 35]; // Native stealth hulls and cloak mount masks.
        config.Games[0].Name = "SC3 legal research 15v15";
        using var server = new Srv();
        server.Boot(config);
        var mission = server.Missions[0];
        var guard = new object();
        var cloakedBombers = new HashSet<short>();
        var struck = new HashSet<short>();
        var targets = new ConcurrentDictionary<short, int>();
        var last = new Dictionary<short, (short Hull, bool Cloak, bool Requested, bool Fire)>();
        var trace = new List<string>();
        string? proofDirectory = Environment.GetEnvironmentVariable("ALLEGIANCE_SC3_PROOF_DIRECTORY");
        if (proofDirectory is not null) Directory.CreateDirectory(proofDirectory);
        using var stop = new CancellationTokenSource();
        Exception? tickerError = null;
        var ticker = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    lock (guard)
                    {
                        server.TickSim();
                        foreach (var live in server.Ships)
                        {
                            var s = live.Ship;
                            if (s.Dead || s.StationId is not null)
                            {
                                cloakedBombers.Remove(s.Id);
                                continue;
                            }
                            var hull = s.HullType;
                            bool bomber = Showcase.ClassifyHull(hull) == Showcase.HullRole.Bomber;
                            bool fire = s.Weapons.Any(w => w.FiringShot);
                            bool request = (s.StateM & ShipControlState.CloakActive) != 0;
                            var next = (hull.HullId, s.IsCloaked, request, fire);
                            if (bomber && s.IsCloaked && request) cloakedBombers.Add(s.Id);
                            var station = targets.TryGetValue(s.Id, out int target)
                                ? mission.Sectors.SelectMany(sector => sector.StationRuntimes).FirstOrDefault(st =>
                                    st.StationId == target && st.SideId != s.SideId && st.ClusterId == s.Sector?.Id) : null;
                            if (bomber && cloakedBombers.Contains(s.Id) && !request && !s.IsCloaked && fire && station is not null)
                                struck.Add(s.Id);
                            if ((bomber || request) && (!last.TryGetValue(s.Id, out var previous) || previous != next))
                                trace.Add(JsonSerializer.Serialize(new { sim = mission.LastUpdate, s.Id, s.SideId,
                                    hull = hull.HullId, hull.Name, bomber, emitter = s.IsCloaked, request, fire,
                                    s.Cloaking, s.Energy, s.StateM, sector = s.Sector?.Id, station = station?.StationId,
                                    stationDistance = station is null ? (float?)null : (station.Center - s.Position).Length() }));
                            last[s.Id] = next;
                        }
                    }
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex) { tickerError = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
                {"preset":"showcase","size_scale":1,"target_sector":"1","staging":true,
                 "finale_after_minutes":2,"finale_side":"both","finale_bombers":3,
                 "finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1}
                """)!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            var watch = Stopwatch.StartNew();
            double nextSample = 0;
            bool qualified = false;
            while (watch.Elapsed.TotalSeconds < 900)
            {
                foreach (var bot in manager.ActiveBots())
                    targets[bot.ShipId] = bot.Target ?? -1;
                lock (guard)
                {
                    qualified = mission.Sides.All(s => mission.Skills!.Count(s.Id, ShowcaseSkill.Cloak) >= 5)
                        && struck.Count > 0;
                    if (watch.Elapsed.TotalSeconds >= nextSample)
                    {
                        output.WriteLine($"SC3 progress wall={watch.Elapsed.TotalSeconds:F1} sim={mission.LastUpdate:F1} "
                            + string.Join(' ', mission.Sides.Select(s => $"side{s.Id}={mission.Skills!.Count(s.Id, ShowcaseSkill.Cloak)}"))
                            + $" cloakedBombers={cloakedBombers.Count} struck={struck.Count}");
                        if (proofDirectory is not null)
                            File.WriteAllText(Path.Combine(proofDirectory, "progress.json"), JsonSerializer.Serialize(new
                            {
                                wall = watch.Elapsed.TotalSeconds, sim = mission.LastUpdate, cloakedBombers, struck,
                                counts = mission.Sides.Select(s => new { side = s.Id, cloak = mission.Skills!.Count(s.Id, ShowcaseSkill.Cloak) }),
                                research = manager.ResearchStatus(), finale = manager.FinaleStatus(),
                                ships = server.Ships.Select(s => new { s.Ship.Id, s.Ship.SideId, hull = s.Ship.HullType.Name,
                                    s.Ship.StateM, s.Ship.IsCloaked, s.Ship.Energy }).ToArray(),
                            }));
                        nextSample += 30;
                    }
                }
                if (qualified || tickerError is not null) break;
                Thread.Sleep(50);
            }
            lock (guard)
            {
                using var skills = new StringWriter(); mission.Skills!.Write(skills);
                output.WriteLine(skills.ToString());
                foreach (string line in trace) output.WriteLine("SC3 trace " + line);
                if (proofDirectory is not null)
                {
                    File.WriteAllLines(Path.Combine(proofDirectory, "trace.jsonl"), trace);
                    File.WriteAllText(Path.Combine(proofDirectory, "skills.txt"), skills.ToString());
                }
                output.WriteLine(JsonSerializer.Serialize(manager.ResearchStatus()));
                output.WriteLine(JsonSerializer.Serialize(manager.FinaleStatus()));
                Assert.Null(tickerError);
                Assert.True(qualified, "Need >=5 actual emitter activations per side and a bomber emitter-on approach followed by emitter-off gunfire");
                Assert.Equal(0, server.SimPassStageFailures);
            }
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
}

using System.Diagnostics;
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
public sealed class SC5CloakLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Rig_civs_at_research_eight_use_real_cloaks_on_both_sides()
    {
        Require.Artwork();
        var config = P733ResearchTests.Config(8);
        config.LiveSettings.AutoNextMatch.Enabled = false;
        config.Games[0].Name = "SC5 civ25 vs civ3 research x8";
        Assert.Equal(new[] { 25, 3 }, config.Games[0].CivIds);
        Assert.False(config.LiveSettings.TestUnlock?.Enabled ?? false);
        Assert.False(config.LiveSettings.TestHarness?.IgnoreTechGate ?? false);
        using var server = new Srv();
        server.Boot(config);
        var mission = server.Missions[0];
        Assert.All(mission.ResearchPlans.Values, p => Assert.Equal("plan_a", p.Name));
        var gate = new object();
        var witnesses = new List<object>();
        var seen = new HashSet<short>();
        Exception? tickerError = null;
        string? proof = Environment.GetEnvironmentVariable("ALLEGIANCE_SC5_PROOF_DIRECTORY");
        if (proof is not null) Directory.CreateDirectory(proof);
        using var stop = new CancellationTokenSource();
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
                        {
                            var s = live.Ship;
                            if (!s.IsCloaked || !seen.Add(s.Id)) continue;
                            witnesses.Add(new { sim = mission.LastUpdate, s.Id, s.SideId,
                                hull = s.HullType.HullId, s.HullType.Name, s.Energy, s.StateM,
                                s.Cloaking, parts = s.Parts.Select(p => new { p.Type.PartId, p.Type.Name, p.MountId, p.Amount }).ToArray() });
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
                 "finale_after_minutes":0}
                """)!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            var watch = Stopwatch.StartNew();
            double nextSample = 0;
            bool qualified = false;
            while (watch.Elapsed.TotalSeconds < 900)
            {
                lock (gate)
                {
                    qualified = mission.Sides.All(s => mission.Skills!.Count(s.Id, ShowcaseSkill.Cloak) >= 5);
                    if (watch.Elapsed.TotalSeconds >= nextSample || qualified || tickerError is not null)
                    {
                        output.WriteLine($"SC5 wall={watch.Elapsed.TotalSeconds:F1} sim={mission.LastUpdate:F1} "
                            + string.Join(' ', mission.Sides.Select(s => $"side{s.Id}={mission.Skills!.Count(s.Id, ShowcaseSkill.Cloak)}")));
                        if (proof is not null)
                            File.WriteAllText(Path.Combine(proof, "progress.json"), JsonSerializer.Serialize(new
                            {
                                wall = watch.Elapsed.TotalSeconds, sim = mission.LastUpdate, qualified,
                                counts = mission.Sides.Select(s => new { side = s.Id, civ = s.Civilization.CivilizationId,
                                    cloak = mission.Skills!.Count(s.Id, ShowcaseSkill.Cloak) }),
                                research = manager.ResearchStatus(), witnesses,
                            }));
                        nextSample += 30;
                    }
                }
                if (qualified || tickerError is not null) break;
                Thread.Sleep(50);
            }
            lock (gate)
            {
                using var skills = new StringWriter();
                mission.Skills!.Write(skills);
                output.WriteLine(skills.ToString());
                if (proof is not null) File.WriteAllText(Path.Combine(proof, "skills.txt"), skills.ToString());
                Assert.Null(tickerError);
                Assert.True(qualified, "Need >=5 native emitter activations on each rig civilization, with ordinary research and purchases");
                Assert.Equal(0, server.SimPassStageFailures);
            }
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
}

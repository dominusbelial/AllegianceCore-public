using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Server;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P705TechModeTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"showcase\":{}}")]
    [InlineData("{\"showcase\":{\"techMode\":\"unlocked\"}}")]
    public void Absent_json_keys_preserve_unlocked_defaults(string json)
    {
        var c = JsonSerializer.Deserialize(json, ServerJsonContext.Default.ServerConfig)!;
        c.ResolveShowcaseMode();
        Assert.Equal("unlocked", c.Showcase.TechMode);
        Assert.Equal(1, c.Showcase.IncomeMultiplier);
        Assert.False(c.Showcase.LoopResearchPlans);
        Assert.Null(c.LiveSettings.TestHarness);
        var harness = JsonSerializer.Deserialize("{\"liveSettings\":{\"testHarness\":{}}}", ServerJsonContext.Default.ServerConfig)!;
        Assert.True(harness.LiveSettings.TestHarness!.IgnoreTechGate);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"testHarness\":{},\"testUnlock\":{}}")]
    [InlineData("{\"testHarness\":{\"enabled\":false,\"ignoreTechGate\":false},\"testUnlock\":{\"enabled\":false,\"allTech\":false}}")]
    public void Research_resolves_omitted_dependencies_and_is_idempotent(string live)
    {
        var c = JsonSerializer.Deserialize("{\"showcase\":{\"techMode\":\"research\"},\"liveSettings\":" + live + "}", ServerJsonContext.Default.ServerConfig)!;
        c.ResolveShowcaseMode(); c.ResolveShowcaseMode();
        Assert.False(c.LiveSettings.TestHarness!.Enabled);
        Assert.False(c.LiveSettings.TestHarness.IgnoreTechGate);
        Assert.False(c.LiveSettings.TestUnlock!.Enabled);
        Assert.False(c.LiveSettings.TestUnlock.AllTech);
        Assert.Equal(1, c.Showcase.IncomeMultiplier);
    }

    [Theory]
    [InlineData("\"liveSettings\":{\"testHarness\":{\"enabled\":true}}", "testHarness.enabled")]
    [InlineData("\"liveSettings\":{\"testHarness\":{\"ignoreTechGate\":true}}", "testHarness.ignoreTechGate")]
    [InlineData("\"liveSettings\":{\"testUnlock\":{\"enabled\":true,\"allTech\":false}}", "testUnlock.enabled")]
    [InlineData("\"liveSettings\":{\"testUnlock\":{\"allTech\":true}}", "testUnlock.allTech")]
    [InlineData("\"games\":[{\"allowDevelopments\":false}]", "allowDevelopments")]
    public void Contradictory_json_fails_before_listening(string fragment, string key)
    {
        var c = JsonSerializer.Deserialize("{\"showcase\":{\"techMode\":\"research\"}," + fragment + "}", ServerJsonContext.Default.ServerConfig)!;
        using var server = new Allegiance.Server.Server();
        var error = Assert.Throws<InvalidDataException>(() => server.Boot(c));
        Assert.Contains("showcase.techMode=research", error.Message);
        Assert.Contains(key, error.Message);
        Assert.Equal((ushort)0, server.BoundPort);
    }

    [Theory]
    [InlineData("Research")]
    [InlineData("")]
    [InlineData("typo")]
    public void Invalid_mode_fails_loudly(string mode)
    {
        using var server = new Allegiance.Server.Server();
        Assert.Contains("showcase.techMode", Assert.Throws<InvalidDataException>(() =>
            server.Boot(new() { Showcase = new() { TechMode = mode } })).Message);
    }

    [Theory]
    [InlineData("25,3", true)]
    [InlineData("101,35", true)]
    [InlineData("1", false)]
    [InlineData("1,2,3", false)]
    [InlineData("x,3", false)]
    [InlineData("-1,3", false)]
    public void Civ_run_parameter_is_ordered_and_validated(string ids, bool valid)
    {
        var c = new ServerConfig { Games = [new() { Teams = 2 }] };
        if (valid)
        {
            Assert.Equal(1, ServerConfigLoader.ApplyCli(c, [("civs", ids)]));
            Assert.Equal(ids.Split(',').Select(int.Parse), c.Games[0].CivIds);
        }
        else Assert.Contains("--civs", Assert.Throws<InvalidDataException>(() =>
            ServerConfigLoader.ApplyCli(c, [("civs", ids)])).Message);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_civ_or_named_plan_fails_with_side_context_before_listening(bool unknownCiv)
    {
        Require.Artwork();
        var c = new ServerConfig { Port = 0, Showcase = new() { TechMode = "research" },
            Games = [new() { Name = "invalid selection", Teams = 2, CivIds = [25, unknownCiv ? 32767 : 3],
                SideNames = ["A", "B"], Map = "m2xspiderw.igc" }] };
        if (!unknownCiv) c.Games[0].ResearchPlans[1] = "no-such-plan";
        using var server = new Allegiance.Server.Server();
        var error = Assert.Throws<InvalidDataException>(() => server.Boot(c));
        Assert.Contains("game 'invalid selection' side 1 civ ", error.Message);
        Assert.Contains("no valid research plan", error.Message);
        Assert.Equal((ushort)0, server.BoundPort);
        // A later profiler window must not call a half-booted server's host callbacks.
        TickProfiler.Configure(true, 5, null);
        try { TickProfiler.ForceReport(); }
        finally { TickProfiler.Shutdown(); }
    }

    [SkippableFact]
    public void Every_pair_of_loaded_civilizations_boots_or_has_a_precise_no_plan_error()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "PCore001.igc")));
        var civs = core.Civilizations.Keys.Order().ToArray();
        Assert.Equal(10, civs.Length);
        var results = new List<object>();
        for (int a = 0; a < civs.Length; a++)
        for (int b = a + 1; b < civs.Length; b++)
        {
            using var server = new Allegiance.Server.Server();
            var c = new ServerConfig { Port = 0, Showcase = new() { TechMode = "research" },
                Games = [new() { Name = "P705 pair matrix", Map = "m2xspiderw.igc", Teams = 2,
                    CivIds = [civs[a], civs[b]], SideNames = ["A", "B"] }] };
            try
            {
                server.Boot(c);
                Assert.Equal(2, server.Missions[0].ResearchPlans.Count);
                foreach (var side in server.Missions[0].Sides)
                {
                    var plan = server.Missions[0].ResearchPlans[side.Id];
                    Assert.Equal(side.Civilization.CivilizationId, plan.CivId);
                    Assert.Contains(plan.Steps, s => s.Action == "develop");
                    Assert.Equal(side.Civilization.CivilizationId is 25 or 3 ? "plan_a" : "generated", plan.Name);
                    var sector = server.Missions[0].Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == side.Id));
                    foreach (var group in plan.Steps.Where(s => s.Action == "station").GroupBy(s => s.AsteroidClass!.Value))
                        Assert.True(sector.Asteroids.Count(r => (r.AabmCapabilities & group.Key) != 0) >= group.Count() + 1);
                }
                results.Add(new { a = civs[a], b = civs[b], result = "valid",
                    plans = server.Missions[0].ResearchPlans.Values.Select(p => new { p.CivId, p.Name }).ToArray(),
                    mapCapabilities = server.Missions[0].Sectors.SelectMany(s => s.Asteroids).Select(r => r.AabmCapabilities).Distinct().Order().ToArray(),
                    requiredCapabilities = server.Missions[0].ResearchPlans.Values.SelectMany(p => p.Steps).Where(s => s.Action == "station").Select(s => s.AsteroidClass).Distinct().Order().ToArray() });
            }
            catch (InvalidDataException e)
            {
                Assert.Contains("no valid research plan", e.Message);
                Assert.Contains("side ", e.Message);
                Assert.Contains("civ ", e.Message);
                Assert.Contains("no plan with real developments", e.Message);
                Assert.Equal((ushort)0, server.BoundPort);
                results.Add(new { a = civs[a], b = civs[b], result = e.Message });
            }
        }
        Assert.Equal(45, results.Count);
        var path = Environment.GetEnvironmentVariable("ALLEGIANCE_P705_PROOF_DIRECTORY");
        if (path is not null) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "pair-matrix.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })); }
    }
}

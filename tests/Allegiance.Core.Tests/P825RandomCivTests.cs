using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P825RandomCivTests
{
    private static StaticCore Load() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Json_null_and_cli_minus_one_are_random_and_cli_supports_three_teams()
    {
        var game = JsonSerializer.Deserialize("""{"civIds":[null,25,-1],"teams":3}""", ServerJsonContext.Default.GameConfig)!;
        Assert.Equal([-1, 25, -1], game.CivIds);
        Assert.Contains("null", JsonSerializer.Serialize(game, ServerJsonContext.Default.GameConfig));
        var config = new ServerConfig { Games = [game] };
        Assert.Equal(2, ServerConfigLoader.ApplyCli(config, [("civs", "-1,3,-1"), ("civ-seed", "825")]));
        Assert.Equal([-1, 3, -1], game.CivIds);
        Assert.Equal(825, game.CivSeed);
        Assert.Throws<InvalidDataException>(() => ServerConfigLoader.ApplyCli(config, [("civs", "-1,3")]));
        Assert.Throws<InvalidDataException>(() => ServerConfigLoader.ApplyCli(config, [("civs", "-2,3,1")]));
        Assert.Throws<InvalidDataException>(() => ServerConfigLoader.ApplyCli(config, [("civ-seed", "bad")]));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[\"random\",null]")]
    [InlineData("[1.5,null]")]
    public void Malformed_json_has_clear_civIds_error(string ids)
    {
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"civIds\":" + ids + "}", ServerJsonContext.Default.GameConfig));
        Assert.Contains("civIds", error.Message);
    }

    [SkippableFact]
    public void Resolver_excludes_fixed_and_special_civs_handles_three_teams_and_refills()
    {
        Require.Artwork();
        var core = Load();
        var fixedGame = new GameConfig { Teams = 3, CivIds = [25, 3, 25] };
        Assert.Equal(fixedGame.CivIds, MatchCivilizations.Resolve(fixedGame, core, 825));
        var game = new GameConfig { Teams = 3, CivIds = [-1, 25, -1] };
        for (int seed = 0; seed < 100; seed++)
        {
            var resolved = MatchCivilizations.Resolve(game, core, seed);
            Assert.Equal(25, resolved[1]);
            Assert.Equal(3, resolved.Distinct().Count());
            Assert.All(resolved, id => Assert.InRange(id, 0, Srv.SpecialCivIdMin - 1));
            Assert.Equal(resolved, MatchCivilizations.Resolve(game, core, seed));
        }
        int pool = Srv.LegalLobbyCivs(core).Length - 1;
        game = new() { Teams = 2 * pool + 2, CivIds = [25, .. Enumerable.Repeat(-1, 2 * pool + 1)] };
        var draws = MatchCivilizations.Resolve(game, core, 825);
        Assert.DoesNotContain(25, draws.Skip(1));
        Assert.Equal(pool, draws.Skip(1).Take(pool).Distinct().Count());
        Assert.Equal(pool, draws.Skip(1 + pool).Take(pool).Distinct().Count());
        Assert.All(draws.Skip(1), id => Assert.InRange(id, 0, Srv.SpecialCivIdMin - 1));
        Assert.All(game.CivIds.Skip(1), id => Assert.Equal(-1, id));
        var allFixed = Srv.LegalLobbyCivs(core).Select(c => (int)c.CivilizationId).Append(-1).ToArray();
        Assert.Contains("no legal random civilizations", Assert.Throws<InvalidDataException>(() =>
            MatchCivilizations.Resolve(new() { Teams = allFixed.Length, CivIds = allFixed }, core, 825)).Message);
        foreach (var invalid in new[] { new GameConfig { Teams = 0 }, new() { Teams = 2, CivIds = [-1] },
                     new() { Teams = 1, CivIds = [-2] }, new() { Teams = 1, CivIds = [32768] } })
            Assert.Contains("game '", Assert.Throws<InvalidDataException>(() => MatchCivilizations.Resolve(invalid, core, 1)).Message);
        Assert.Contains("civilization ID 299", Assert.Throws<InvalidOperationException>(() =>
            MatchCivilizations.Resolve(new() { Teams = 1, CivIds = [299] }, core, 1)).Message);
    }

    private static ServerConfig Config(int[] ids, int? seed) => new()
    {
        Port = 0, Showcase = new() { TechMode = "research" },
        LiveSettings = new() { AutoNextMatch = new() { Enabled = false } },
        Games = [new() { Name = "P825", Teams = ids.Length, CivIds = ids, CivSeed = seed,
            SideNames = Enumerable.Range(0, ids.Length).Select(i => $"Side{i}").ToArray(),
            InitialMinersPerTeam = 0, ResearchPlanDirectory = Path.Combine(Root, "data/showcase-plans"),
            ResearchPlans = new() { [0] = "plan_a", [1] = "plan_a" },
            MapGen = new() { Type = "SingleRing", Seed = 825 } }],
    };

    private static void End(Srv server)
    {
        server.Missions[0].Stage = MissionStage.Started;
        typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [server.Missions[0], (short)0, "P825 rotation"]);
    }

    [SkippableTheory, Trait("Tier", "Live")]
    [InlineData(false)]
    [InlineData(true)]
    public void Boot_and_rotation_replay_seed_and_plans_follow_actual_civs_without_disk_reads(bool random)
    {
        Require.Artwork();
        int[] ids = random ? [-1, -1, -1] : [25, 3, 1];
        var config = Config(ids, 825);
        var sequence = new List<string>();
        var oldOut = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            for (int run = 0; run < 2; run++)
            {
                using var server = new Srv();
                server.SpawnInitialMinersAtBoot = false;
                server.Boot(config);
                var core = server.Missions[0].Core;
                // If rotation re-reads the directory it will reject this path.
                // Precomputed authored AND generated plans must remain usable.
                string? directory = config.Games[0].ResearchPlanDirectory;
                config.Games[0].ResearchPlanDirectory = Path.Combine(Root, ".scratch/p825/nonexistent-plans");
                try
                {
                    for (int match = 0; match < 12; match++)
                    {
                        var mission = server.Missions[0];
                        var civs = mission.Sides.Select(s => (int)s.Civilization.CivilizationId).ToArray();
                        string tuple = string.Join(",", civs);
                        if (run == 0) sequence.Add(tuple); else Assert.Equal(sequence[match], tuple);
                        if (!random) Assert.Equal(ids, civs);
                        else Assert.Equal(3, civs.Distinct().Count());
                        foreach (var side in mission.Sides)
                        {
                            var plan = mission.ResearchPlans[side.Id];
                            Assert.Equal(side.Civilization.CivilizationId, plan.CivId);
                            Assert.Equal(plan.CivId is 25 or 3 ? "plan_a" : "generated", plan.Name);
                            ResearchPlanCatalog.Validate(new ResearchTree(core, plan.CivId, mission.Research), "PCore001", plan);
                            var briefing = (Allegiance.Core.Wire.ResearchBriefing)typeof(Srv)
                                .GetMethod("ResearchBriefingFor", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(server, [mission, side])!;
                            Assert.Equal(plan.CivId, briefing.Plan.CivId);
                        }
                        End(server);
                        Assert.NotSame(mission, server.Missions[0]);
                        server.TickSim();
                    }
                    Assert.Equal(0, server.SimPassStageFailures);
                }
                finally { config.Games[0].ResearchPlanDirectory = directory; }
            }
            if (random) Assert.True(sequence.Distinct().Count() > 3);
            Assert.Equal(ids, config.Games[0].CivIds);
            Assert.Contains("[matchup] mission #0 match=1 seed=825 side0=", log.ToString());
            Assert.Contains("iSideWinner=0", log.ToString());
            Assert.DoesNotContain("[simpass]", log.ToString());
        }
        finally { Console.SetOut(oldOut); }
    }

    [SkippableFact]
    public void Failed_civ_boot_does_not_leave_profiler_callbacks()
    {
        Require.Artwork();
        foreach (bool research in new[] { false, true })
        {
            using var server = new Srv();
            var config = Config([299, 3], 825);
            if (!research)
            {
                config.Showcase.TechMode = "unlocked";
                config.Games[0].ResearchPlanDirectory = null;
                Assert.Throws<InvalidOperationException>(() => server.Boot(config));
            }
            else Assert.Throws<InvalidDataException>(() => server.Boot(config));
        }
        TickProfiler.Configure(true, 5, null);
        try { TickProfiler.ForceReport(); }
        finally { TickProfiler.Shutdown(); }
    }

    [SkippableFact, Trait("Tier", "Live")]
    public void Drawn_seed_is_logged_and_replays_first_match()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            using var server = new Srv();
            server.Boot(Config([-1, -1], null));
            var first = server.Missions[0];
            var line = log.ToString().Split('\n').Single(l => l.StartsWith("[matchup]"));
            int seed = int.Parse(System.Text.RegularExpressions.Regex.Match(line, @"seed=(\d+)").Groups[1].Value);
            Assert.Equal(first.Sides.Select(s => (int)s.Civilization.CivilizationId), MatchCivilizations.Resolve(Config([-1, -1], seed).Games[0], first.Core, seed));
        }
        finally { Console.SetOut(saved); }
    }
}

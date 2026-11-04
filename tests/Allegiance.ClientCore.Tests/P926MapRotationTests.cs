using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.MapGen;
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
public sealed class P926MapRotationTests(ITestOutputHelper output)
{
    internal static MapRotationConfig Rotation(string mode = "cycle") => new()
    {
        Mode = mode, Seed = 926, Entries = [new() { Map = "m2xspiderw.igc" },
            new() { MapGen = new() { Type = "SingleRing" } }, new() { MapGen = new() { Type = "PinWheel" } }]
    };

    [Fact]
    public void Json_defaults_roundtrip_and_seeded_order()
    {
        Assert.Null(JsonSerializer.Deserialize("{}", ServerJsonContext.Default.ServerConfig)!.Showcase.MapRotation);
        var config = new ServerConfig { Showcase = new() { MapRotation = Rotation("random") } };
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig), ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(926, copy.Showcase.MapRotation!.Seed);
        int[] Trace(MapRotationConfig rotation)
        {
            var rng = new Random(rotation.Seed!.Value); int previous = -1;
            return Enumerable.Range(0, 100).Select(i => { previous = rotation.Select(i, rng, previous); rng.Next(); return previous; }).ToArray();
        }
        var trace = Trace(config.Showcase.MapRotation);
        Assert.Equal(trace, Trace(copy.Showcase.MapRotation));
        Assert.Equal(3, trace.Distinct().Count());
        Assert.All(trace.Zip(trace.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i % 3), Trace(Rotation()).Take(12));
        Assert.Equal(1, ServerConfigLoader.ApplyCli(config, [("map-seed", "1234")]));
        Assert.All(config.Showcase.MapRotation.Entries.Where(e => e.MapGen is not null), e => Assert.Equal(1234, e.MapGen!.Seed));
    }

    [Theory]
    [InlineData("mode")][InlineData("empty")][InlineData("both")][InlineData("neither")]
    [InlineData("path")][InlineData("type")][InlineData("numericType")][InlineData("count")]
    [InlineData("density")][InlineData("size")][InlineData("debug")][InlineData("homeSwap")]
    public void Bad_settings_fail_before_artwork_or_listener(string fault)
    {
        var rotation = Rotation();
        var config = P660NextMatchLiveTests.Config(true);
        config.Showcase.MapRotation = rotation;
        switch (fault)
        {
            case "mode": rotation.Mode = "typo"; break;
            case "empty": rotation.Entries = []; break;
            case "both": rotation.Entries[0].MapGen = new(); break;
            case "neither": rotation.Entries[0].Map = null; break;
            case "path": rotation.Entries[0].Map = "../file.igc"; break;
            case "type": rotation.Entries[1].MapGen!.Type = "Unsupported"; break;
            case "numericType": rotation.Entries[1].MapGen!.Type = "0"; break;
            case "count": rotation.Entries[1].MapGen!.PlayerSectorAsteroids = -1; break;
            case "density": rotation.Entries[1].MapGen!.He3Density = double.NaN; break;
            case "size": rotation.Entries[1].MapGen!.MapSize = 2; break;
            case "debug": config.LiveSettings.DebugMode = true; break;
            case "homeSwap": config.Games[0].HomeSwap = "alternate"; break;
        }
        Assert.Contains("showcase.mapRotation", Assert.Throws<InvalidDataException>(config.ResolveShowcaseMode).Message);
    }

    [Fact]
    public void Battle_sector_prefers_balanced_short_routes_then_id_and_ignores_disconnected_neutrals()
    {
        var graph = new ClusterGraph
        {
            Clusters = [new(0, true), new(1, false), new(2, true), new(3, false), new(4, false)],
            Warps = [new(0, 1), new(1, 2), new(0, 3), new(3, 2)]
        };
        Assert.Equal((short?)1, ShowcaseBattleSector.Choose(graph));
        Assert.Equal((short?)0, ShowcaseBattleSector.Choose(new() { Clusters = [new(0, false)] }));
        Assert.Null(ShowcaseBattleSector.Choose(new() { Clusters = [new(0, true), new(1, false)] }));
    }

    [SkippableTheory]
    [InlineData("missing")][InlineData("notMap")][InlineData("teams")][InlineData("topologyTeams")][InlineData("territory")]
    public void Unselected_entries_are_preflighted_at_boot(string fault)
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(true);
        var rotation = config.Showcase.MapRotation = Rotation();
        switch (fault)
        {
            case "missing": rotation.Entries[2] = new() { Map = "p926-missing.igc" }; break;
            case "notMap": rotation.Entries[2] = new() { Map = "PCore001.igc" }; break;
            case "teams": config.Games[0].Teams = 3; config.Games[0].CivIds = [25, 3, 25]; break;
            case "topologyTeams": rotation.Entries[2].MapGen!.Type = "DiamondRing"; break;
            case "territory": config.Games[0].Goals = new() { TerritoryPercentage = 50 }; break;
        }
        using var server = new Srv();
        Assert.Contains("showcase.mapRotation", Assert.Throws<InvalidDataException>(() => server.Boot(config)).Message);
    }

    [SkippableFact]
    public void Rotation_absent_preserves_authored_assembly_bytes_and_existing_receipt()
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(false);
        config.Games[0].CivSeed = 926;
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(config.ArtPath, config.Games[0].Map)));
        using var log = new StringWriter(); var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            var mission = server.Missions[0];
            Assert.Equal(map.Clusters.SelectMany(c => c.Encode()), mission.Sectors.SelectMany(c => c.Cluster.Encode()));
            Assert.Equal(map.Warps.SelectMany(w => w.Encode()), mission.Sectors.SelectMany(c => c.Warps).OrderBy(w => Array.FindIndex(map.Warps.ToArray(), x => x.WarpId == w.Warp.WarpId)).SelectMany(w => w.Warp.Encode()));
            var legacy = Mission.Assemble(mission.Core, map, mission.Sides.Select(s => new SideSpec(s.Id, s.Name, s.Civilization.CivilizationId)).ToArray(), new NullIgcSite(), 0);
            legacy.ReplaceUnavailableStationTypes();
            Assert.Equal(legacy.Sectors.SelectMany(c => c.Stations).SelectMany(s => s.Encode()), mission.Sectors.SelectMany(c => c.Stations).OrderBy(s => Array.FindIndex(map.Stations.ToArray(), x => x.StationId == s.StationId)).SelectMany(s => s.Encode()));
            string receipt = log.ToString().Split('\n').Single(l => l.StartsWith("[matchup]"));
            Assert.Contains("match=1 seed=926 side0=25:", receipt);
            Assert.DoesNotContain(" map=", receipt);
            Assert.DoesNotContain("rotation=", receipt);
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableFact]
    public void Player_created_world_keeps_its_fixed_map_across_rebuilds()
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(true);
        config.Showcase.MapRotation = Rotation();
        using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
        var originalIds = server.Missions[0].Sectors.Select(c => c.Id).ToArray();
        server.Missions[0].PlayerCreated = true;
        server.Missions[0].Stage = MissionStage.Started;
        P660NextMatchLiveTests.End(server);
        Assert.True(server.Missions[0].PlayerCreated);
        Assert.Equal(originalIds, server.Missions[0].Sectors.Select(c => c.Id));
    }

    [SkippableTheory, Trait("Tier", "Live")]
    [InlineData("cycle", false)][InlineData("random", false)][InlineData("cycle", true)]
    public void Assembly_replays_layouts_civ_stream_and_receipts_across_rotations(string mode, bool research)
    {
        Require.Artwork();
        var fingerprints = new List<byte[]>(); var receipts = new List<string>();
        for (int run = 0; run < 2; run++)
        {
            var config = P660NextMatchLiveTests.Config(true);
            config.Games[0].CivSeed = 926;
            if (research)
            {
                config.Showcase.TechMode = "research";
                config.LiveSettings.TestHarness = null;
                config.Games[0].CivIds = [18, 4];
            }
            config.Showcase.MapRotation = Rotation(mode);
            config.Showcase.MapRotation.Entries[2].MapGen!.Seed = 123;
            using var log = new StringWriter(); var saved = Console.Out; Console.SetOut(log);
            try
            {
                using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
                var civSeeds = new Random(926);
                for (int match = 0; match < 7; match++)
                {
                    var mission = server.Missions[0];
                    string receipt = log.ToString().Split('\n').Last(l => l.StartsWith("[matchup]"));
                    Assert.Contains($"match={match + 1} seed={(match == 0 ? 926 : civSeeds.Next())}", receipt);
                    Assert.Matches(@" map=(m2xspiderw.igc|gen:SingleRing|gen:PinWheel) mapSeed=\d+ rotation=" + mode + @"#[012] rotationMatch=" + (match + 1) + " rotationSeed=926", receipt);
                    if (match == 0)
                    {
                        string label = System.Text.RegularExpressions.Regex.Match(receipt, @" map=(\S+)").Groups[1].Value;
                        Assert.Contains($"ready: map={label},", log.ToString());
                        Assert.Equal(label, typeof(Srv).GetMethod("MissionMapLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [mission, "stale-label"]));
                    }
                    if (receipt.Contains("map=gen:PinWheel")) Assert.Contains("mapSeed=123", receipt);
                    if (research)
                    foreach (var side in mission.Sides)
                    {
                        var plan = mission.ResearchPlans[side.Id];
                        Assert.Equal("generated", plan.Name);
                        Assert.Equal(side.Civilization.CivilizationId, plan.CivId);
                        var home = mission.Sectors.First(c => c.Stations.Any(st => st.SideId == side.Id));
                        Assert.Contains(home.Stations, st => st.SideId == side.Id && st.StationTypeId == side.Civilization.InitialStationTypeId);
                        foreach (var requirement in plan.Steps.Where(s => s.Action == "station").GroupBy(s => s.AsteroidClass!.Value))
                            Assert.True(home.Asteroids.Count(a => (a.AabmCapabilities & requirement.Key) != 0) >= requirement.Count() + 1);
                    }
                    output.WriteLine($"run={run} match={match}: {receipt}");
                    var bytes = mission.Sectors.SelectMany(c => c.Cluster.Encode().Concat(c.Warps.SelectMany(w => w.Warp.Encode())).Concat(c.Stations.SelectMany(s => s.Encode())).Concat(c.Asteroids.SelectMany(a => a.Encode()))).ToArray();
                    if (run == 0) { fingerprints.Add(bytes); receipts.Add(receipt); }
                    else
                    {
                        Assert.Equal(fingerprints[match], bytes); Assert.Equal(receipts[match], receipt);
                    }
                    mission.Stage = MissionStage.Started;
                    P660NextMatchLiveTests.End(server);
                    var over = log.ToString().Split('\n').Last(l => l.StartsWith("[gameover] mission"));
                    Assert.EndsWith(receipt[(receipt.IndexOf("match="))..].TrimEnd('\r'), over.TrimEnd('\r'));
                }
            }
            finally { Console.SetOut(saved); }
        }
    }

    [SkippableFact]
    public void Battle_sector_is_Alderan_and_every_supported_generated_type_has_a_reachable_choice()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "m2xspiderw.igc")));
        Assert.Equal("Alderan", map.Clusters.Single(c => c.ClusterId == ShowcaseBattleSector.Choose(Graph(map))).Name);
        foreach (var type in Enum.GetValues<MapGenType>())
        {
            int teams = type == MapGenType.DiamondRing ? 3 : 2;
            map = MissionMapGenerator.Generate(new() { Type = type, Teams = teams, CivIds = Enumerable.Repeat(25, teams).ToArray(), Seed = 926 }, core, 0);
            var graph = Graph(map); short target = ShowcaseBattleSector.Choose(graph)!.Value;
            Assert.All(graph.Clusters.Where(c => c.HomeSector), home => Assert.NotEmpty(graph.FindShortestPath(home.Id, target)));
            if (graph.Clusters.Any(c => !c.HomeSector)) Assert.False(graph.Clusters.Single(c => c.Id == target).HomeSector);
            output.WriteLine($"{type} teams={teams} sector={target} name={map.Clusters.Single(c => c.ClusterId == target).Name}");
        }
    }

    internal static ClusterGraph Graph(MissionMap map) => new()
    {
        Clusters = map.Clusters.Select(c => new ClusterInfo.ClusterEntry(c.ClusterId, c.HomeSector)).ToArray(),
        Warps = map.Warps.Select(w => new ClusterInfo.WarpEdge(w.ClusterId, map.Warps.Single(p => p.WarpId == w.DestinationId).ClusterId)).ToArray()
    };
}

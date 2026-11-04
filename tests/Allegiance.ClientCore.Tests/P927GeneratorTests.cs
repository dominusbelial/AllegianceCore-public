using Allegiance.Core.MapGen;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P927GeneratorTests
{
    [SkippableFact]
    public void All_fourteen_supported_three_team_generators_have_every_side_and_a_reachable_rendezvous()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        int generated = 0;
        foreach (var type in Enum.GetValues<MapGenType>())
        {
            var args = new MapGenParams { Type = type, Teams = 3, CivIds = [18, 4, 5], Seed = 927 };
            if (type == MapGenType.EastWest)
            {
                Assert.Throws<InvalidDataException>(() => MissionMapGenerator.Generate(args, core, 0));
                continue;
            }
            var map = MissionMapGenerator.Generate(args, core, 0);
            Assert.Equal(new short[] { 0, 1, 2 }, map.Stations.Select(s => s.SideId).Distinct().Order());
            var graph = P926MapRotationTests.Graph(map);
            short battle = ShowcaseBattleSector.Choose(graph)!.Value;
            foreach (var home in graph.Clusters.Where(c => c.HomeSector)) Assert.NotEmpty(graph.FindShortestPath(home.Id, battle));
            generated++;
        }
        Assert.Equal(14, generated);
    }
}

using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P420 — tests for <see cref="ClusterGraph.FindShortestPath"/>, the real
/// BFS pathfinding primitive added so the attach choreography can route an
/// opposing contingent across real warps. The first test runs it against the
/// real m2xspiderw topology (loaded exactly as the client's CLUSTERINFO is
/// built) and pins the true shortest paths between the three well-understood
/// clusters this session keeps returning to — Ganymede, Alderan and Organia —
/// proving BFS returns the mathematical minimum, not a plausible-but-longer
/// waypoint route. The remaining tests pin the algorithm's shape on a small
/// synthetic graph (deterministic tie-break, unreachable, endpoints-equal).
/// </summary>
public sealed class ClusterGraphFindPathTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    /// <summary>
    /// Build the cluster graph from the real m2xspiderw map the same way the
    /// client's <c>CLUSTERINFO</c> is derived: one cluster per sector, and one
    /// warp edge per warp whose destination resolves to its partner warp's
    /// sector (a dangling/unresolved destination is skipped, not guessed).
    /// </summary>
    private static ClusterGraph RealGraph()
    {
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "m2xspiderw.igc")));
        var byWarpId = map.Warps.ToDictionary(w => w.WarpId, w => w);
        var clusters = map.Clusters
            .Select(c => new ClusterInfo.ClusterEntry(c.ClusterId, c.HomeSector))
            .ToList();
        var warps = new List<ClusterInfo.WarpEdge>();
        foreach (var w in map.Warps)
        {
            if (!byWarpId.TryGetValue(w.DestinationId, out var partner)) continue;   // dangling -> skip
            warps.Add(new ClusterInfo.WarpEdge(w.ClusterId, partner.ClusterId));
        }
        return new ClusterGraph { Clusters = clusters, Warps = warps };
    }

    [Fact]
    public void ShortestGanymedeToOrgania_IsTheThreeHopMinimum_OnRealMap()
    {
        // The real shortest route from Ganymede (1050) to Organia (1051) on
        // m2xspiderw is 3 hops via Bug Nebula (1054) and Tellus (1055):
        //   1050 --bugNebula--> 1054 --tellus--> 1055 --organia--> 1051
        // There is no direct edge between the two home sectors, and no 2-hop
        // route (Ganymede's neighbours {1057,1054,1059,1060} and Organia's
        // {1055,1058,1062,1061} are disjoint). The frequently-cited
        // Ganymede->Alderan->Organia waypoint is 4 hops and therefore NOT the
        // minimum — BFS must reject it.
        var graph = RealGraph();

        var path = graph.FindShortestPath(1050, 1051);

        Assert.Equal([1050, 1054, 1055, 1051], path);   // 3 hops, deterministic
        Assert.Equal(4, path.Count);
    }

    [Fact]
    public void AlderanWaypointRoute_IsLongerThanTheTrueMinimum()
    {
        // Prove the "Ganymede->Alderan->Organia" waypoint is a real but
        // longer route: 1050 -> Rigel(1059) -> Alderan(1052) -> Juniper(1061)
        // -> Organia(1051) = 4 hops, which FindShortestPath rightly does NOT
        // pick over the 3-hop minimum. This pins the individual sub-paths
        // through the three named clusters too.
        var graph = RealGraph();

        Assert.Equal([1050, 1059, 1052], graph.FindShortestPath(1050, 1052));       // Ganymede->Alderan: 2 hops
        Assert.Equal([1052, 1061, 1051], graph.FindShortestPath(1052, 1051));       // Alderan->Organia: 2 hops

        var waypoint = graph.FindShortestPath(1050, 1052);
        waypoint.RemoveAt(waypoint.Count - 1);                                      // drop Alderan
        waypoint.AddRange(graph.FindShortestPath(1052, 1051));
        var direct = graph.FindShortestPath(1050, 1051);
        Assert.True(waypoint.Count > direct.Count, "the Alderan waypoint must not beat the direct minimum");
    }

    [Fact]
    public void SyntheticSmallGraph_FindsDeterministicShortestPath()
    {
        //  1 -- 2 -- 3
        //  |         |
        //  4 -------- 5 -- 6
        // Edges: 1-2, 2-3, 1-4, 4-5, 3-5, 5-6
        var graph = new ClusterGraph
        {
            Clusters =
            [
                new ClusterInfo.ClusterEntry(1, false),
                new ClusterInfo.ClusterEntry(2, false),
                new ClusterInfo.ClusterEntry(3, false),
                new ClusterInfo.ClusterEntry(4, false),
                new ClusterInfo.ClusterEntry(5, false),
                new ClusterInfo.ClusterEntry(6, false),
            ],
            Warps =
            [
                new ClusterInfo.WarpEdge(1, 2),
                new ClusterInfo.WarpEdge(2, 3),
                new ClusterInfo.WarpEdge(1, 4),
                new ClusterInfo.WarpEdge(4, 5),
                new ClusterInfo.WarpEdge(3, 5),
                new ClusterInfo.WarpEdge(5, 6),
            ],
        };

        // 1 -> 3: the direct 1-2-3 (2 hops) beats 1-4-5-3 (3 hops).
        Assert.Equal([1, 2, 3], graph.FindShortestPath(1, 3));
        // 1 -> 6: the true minimum is 1-4-5-6 (3 hops); the 1-2-3-5-6 waypoint
        // is 4 hops, so BFS picks the shorter via 4.
        Assert.Equal([1, 4, 5, 6], graph.FindShortestPath(1, 6));
        // Reverse direction must be symmetric (undirected traversal).
        Assert.Equal([6, 5, 4, 1], graph.FindShortestPath(6, 1));
    }

    [Fact]
    public void UnreachableAndDegenerateInputs_AreHandledGracefully()
    {
        var graph = new ClusterGraph
        {
            Clusters = [new ClusterInfo.ClusterEntry(1, false), new ClusterInfo.ClusterEntry(2, false)],
            Warps = [],                                            // no connectivity at all
        };

        Assert.Empty(graph.FindShortestPath(1, 2));                // disconnected -> no path
        Assert.Equal([1], graph.FindShortestPath(1, 1));           // same endpoint -> singleton
        Assert.Empty(graph.FindShortestPath(7, 99));               // unknown ids -> no path
    }
}

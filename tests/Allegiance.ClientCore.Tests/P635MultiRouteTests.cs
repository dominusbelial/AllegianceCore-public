using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.Tools.McpSpawner;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P635 — tests for the multi-route showcase wing split:
///   • <see cref="ClusterGraph.FindDistinctEntryRoutes"/> — k distinct entry-aleph
///     routes on a fixture graph and on the real m2xspiderw (221) map;
///   • <see cref="BotManager.RouteEnumeration"/> — the probe-warp-table twin, agreeing
///     with the cluster-graph version on the real map;
///   • <see cref="BotManager.PickLeastLoadedRoute"/> — the split/cap/least-loaded rule;
///   • <see cref="BotManager.SyncOffsetSeconds"/> + <see cref="BotManager.SideReadyToAdvance"/>
///     — the per-route release-timing (longer route waits longer; barrier + timeout).
/// </summary>
public sealed class P635MultiRouteTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    /// <summary>Cluster graph built from the real m2xspiderw map the way the client's
    /// CLUSTERINFO is derived (one warp edge per resolved warp pair).</summary>
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
            if (!byWarpId.TryGetValue(w.DestinationId, out var partner)) continue;
            warps.Add(new ClusterInfo.WarpEdge(w.ClusterId, partner.ClusterId));
        }
        return new ClusterGraph { Clusters = clusters, Warps = warps };
    }

    private static Dictionary<short, DataWarpIGC> RealWarps()
    {
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "m2xspiderw.igc")));
        return map.Warps.ToDictionary(w => w.WarpId, w => w);
    }

    // ── Fixture graph ──────────────────────────────────────────────────────
    //      1 ── 2 ── 3
    //      │         │
    //      4 ── 5 ── 6 ── 7
    // Edges: 1-2, 2-3, 3-6, 1-4, 4-5, 5-6, 6-7
    private static ClusterGraph FixtureGraph() => new()
    {
        Clusters =
        [
            new ClusterInfo.ClusterEntry(1, false),
            new ClusterInfo.ClusterEntry(2, false),
            new ClusterInfo.ClusterEntry(3, false),
            new ClusterInfo.ClusterEntry(4, false),
            new ClusterInfo.ClusterEntry(5, false),
            new ClusterInfo.ClusterEntry(6, false),
            new ClusterInfo.ClusterEntry(7, false),
        ],
        Warps =
        [
            new ClusterInfo.WarpEdge(1, 2),
            new ClusterInfo.WarpEdge(2, 3),
            new ClusterInfo.WarpEdge(3, 6),
            new ClusterInfo.WarpEdge(1, 4),
            new ClusterInfo.WarpEdge(4, 5),
            new ClusterInfo.WarpEdge(5, 6),
            new ClusterInfo.WarpEdge(6, 7),
        ],
    };

    [Fact]
    public void FixtureGraph_EnumeratesDistinctEntryRoutes()
    {
        var g = FixtureGraph();
        // 1 -> 6. Neighbours of 6 reachable from 1: 3 (via 1-2-3), 5 (via 1-4-5), 7 (1-2-3-6-7? no—
        // 7 is a neighbour of 6 but the route to 7's penult is 6 itself, excluded). Actually 7's only
        // edge is 6, so 7 is reachable only THROUGH 6; the banned-node rule excludes it. So entries
        // are 3 (2 hops) and 5 (2 hops). Both are 2-hop routes to their entry, then +6.
        var routes = g.FindDistinctEntryRoutes(1, 6, 10);
        // Distinct penults, both present, each entering through its own aleph.
        var penults = routes.Select(r => r[^2]).OrderBy(x => x).ToList();
        Assert.Equal([3, 5], penults);
        Assert.All(routes, r => Assert.Equal(6, r[^1]));
        // Hop ranking is ascending.
        var hops = routes.Select(r => r.Count - 1).ToList();
        Assert.Equal(hops.OrderBy(x => x), hops);
    }

    [Fact]
    public void FixtureGraph_CapsAndDedupesRoutes()
    {
        var g = FixtureGraph();
        Assert.Equal(2, g.FindDistinctEntryRoutes(1, 6, 10).Count);
        Assert.Single(g.FindDistinctEntryRoutes(1, 6, 1));       // cap honoured
        Assert.Empty(g.FindDistinctEntryRoutes(1, 6, 0));        // zero cap -> none
        // Same-endpoint degenerate input.
        Assert.Empty(g.FindDistinctEntryRoutes(1, 1, 3));
        // 7's only neighbour is 6, so the sole route to 7 enters via 6: [1,2,3,6,7].
        Assert.Equal([[1, 2, 3, 6, 7]], g.FindDistinctEntryRoutes(1, 7, 3));
    }

    // ── Real 221 map ───────────────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealMap_PinsDistinctEntryRoutesToAlderan()
    {
        Require.Artwork();
        var g = RealGraph();
        // Today every bot funnels home→Alderan through ONE penult (side 0 via Rigel
        // 1059; side 1 via Juniper 1061). The fix must discover ADDITIONAL distinct
        // entry alephs into Alderan (1052), ranked by hops, capped at 3:
        //   side 0 (Ganymede 1050): Rigel 1059, 1060, 1053
        //   side 1 (Organia 1051):  Juniper 1061, 1062, 1053
        Assert.Equal(
            [[1050, 1059, 1052], [1050, 1060, 1052], [1050, 1054, 1053, 1052]],
            g.FindDistinctEntryRoutes(1050, 1052, 3));
        Assert.Equal(
            [[1051, 1061, 1052], [1051, 1062, 1052], [1051, 1055, 1053, 1052]],
            g.FindDistinctEntryRoutes(1051, 1052, 3));
        // Every route ends at Alderan through a DISTINCT penult per side.
        foreach (var home in new short[] { 1050, 1051 })
        {
            var routes = g.FindDistinctEntryRoutes(home, 1052, 3);
            var penults = routes.Select(r => r[^2]).ToList();
            Assert.Equal(penults.Distinct().Count(), penults.Count);
            Assert.Equal(routes.Select(r => r.Count).OrderBy(x => x), routes.Select(r => r.Count));
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealMap_ClusterGraphAndWarpTable_Agree()
    {
        Require.Artwork();
        var graphRoutes = RealGraph().FindDistinctEntryRoutes(1050, 1052, 3);
        var warpRoutes = BotManager.RouteEnumeration(RealWarps(), 1050, 1052, 3);
        var norm = (List<List<short>> rs) => rs.Select(r => string.Join(",", r)).OrderBy(x => x).ToList();
        Assert.Equal(norm(graphRoutes), norm(warpRoutes));
    }

    // ── Split / cap / least-loaded ─────────────────────────────────────────

    [Fact]
    public void PickLeastLoaded_SplitsEvenlyAcrossRoutes()
    {
        var counts = new int[2];
        for (int i = 0; i < 195; i++)
            counts[BotManager.PickLeastLoadedRoute(counts)]++;
        // 195 over 2 routes -> 98 / 97 (balanced).
        Assert.Equal([98, 97], counts);
    }

    [Fact]
    public void PickLeastLoaded_ExcessGoesToLeastLoadedUnderCap()
    {
        // 195 bots, 2 routes, cap 100: even split stays under the cap (98/97) and no
        // route exceeds 100 — the cap is respected by construction when ≥2 routes.
        var counts = new int[2];
        for (int i = 0; i < 195; i++)
            counts[BotManager.PickLeastLoadedRoute(counts)]++;
        Assert.All(counts, c => Assert.True(c <= 100));
    }

    [Fact]
    public void PickLeastLoaded_AlwaysArgmin_ThreeRoutes()
    {
        var counts = new int[3];
        for (int i = 0; i < 100; i++)
            counts[BotManager.PickLeastLoadedRoute(counts)]++;
        // 100 over 3 -> 34/33/33.
        Assert.Equal([34, 33, 33], counts);
    }

    // ── Synchronised release timing ────────────────────────────────────────

    [Fact]
    public void SyncOffset_LongerRouteWaitsLonger()
    {
        const double perHop = BotManager.RouteTransitSecondsPerHop;
        // Widest route on the side is 3 hops; a 3-hop route waits 0 extra; a 2-hop
        // route waits 1 hop extra; a 1-hop (unlikely) waits 2 extra.
        Assert.Equal(0.0, BotManager.SyncOffsetSeconds(0, 3, 3));
        Assert.Equal(perHop * 1, BotManager.SyncOffsetSeconds(1, 3, 2));
        Assert.Equal(perHop * 2, BotManager.SyncOffsetSeconds(2, 3, 1));
        // Never negative even when a route is wider than the side max.
        Assert.Equal(0.0, BotManager.SyncOffsetSeconds(0, 2, 5));
    }

    [Fact]
    public void ReleaseTiming_AssembledImmediately_VsTimeoutLater()
    {
        // Assembled wing (all living staged) releases at once, before any timeout.
        Assert.True(BotManager.SideReadyToAdvance(aliveCount: 10, stagedCount: 10, now: 100.0, firstStagedAt: 99.0, timeoutSeconds: 15.0));
        // Partially assembled wing holds until its (longer) timeout fires.
        Assert.False(BotManager.SideReadyToAdvance(10, 5, now: 100.0, 90.0, 15.0));
        Assert.True(BotManager.SideReadyToAdvance(10, 5, now: 106.0, 90.0, 15.0));
        // No living bots -> release trivially (a wiped wing never deadlocks the side).
        Assert.True(BotManager.SideReadyToAdvance(0, 0, now: 100.0, null, 15.0));
    }
}

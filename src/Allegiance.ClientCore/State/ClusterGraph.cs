using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// The cluster connectivity graph, captured verbatim from a single
/// <see cref="ClusterInfo"/> (FM_S_CLUSTERINFO, 200) message. Deliberately
/// just the two lists the message already carries — reusing the wire codec's
/// own <see cref="ClusterInfo.ClusterEntry"/> / <see cref="ClusterInfo.WarpEdge"/>
/// record types rather than reinventing them. No positions, no derivation:
/// this is exactly what the server told us, nothing more.
/// </summary>
public sealed class ClusterGraph
{
    /// <summary>The clusters (<c>clusterIDs</c>/<c>homeSectors</c>), in wire order.</summary>
    public IReadOnlyList<ClusterInfo.ClusterEntry> Clusters { get; init; } = [];

    /// <summary>The warp edges (<c>warpFrom/warpTo</c>), in wire order.</summary>
    public IReadOnlyList<ClusterInfo.WarpEdge> Warps { get; init; } = [];

    /// <summary>
    /// P635 — enumerate up to <paramref name="maxRoutes"/> distinct routes from
    /// <paramref name="fromCluster"/> to <paramref name="toCluster"/> that differ
    /// in their ENTRY ALEPH into the destination: each candidate route enters the
    /// destination through a different neighbouring (penultimate) sector. For every
    /// neighbour N of <c>toCluster</c> reachable from <c>fromCluster</c> without
    /// passing through <c>toCluster</c> itself, the shortest such path is computed
    /// and <c>toCluster</c> appended — so each route has a distinct penult and thus
    /// a distinct entry aleph. Routes are ranked by hop count ascending (ties broken
    /// by ascending neighbour id, inherited from the sorted adjacency), then capped
    /// at <paramref name="maxRoutes"/>. Identical full paths are deduped. Returns an
    /// empty list when the destination is unreachable, when fewer than one distinct
    /// entry exists, or when <paramref name="maxRoutes"/> ≤ 0. Pure graph primitive —
    /// it answers "which alternative cluster sequences enter the rendezvous through
    /// different mouths"; a caller resolves each hop to a real warp against its live
    /// world (the bots' <c>TryFindWarpTo</c>) and drives the wing along it.
    /// </summary>
    public List<List<short>> FindDistinctEntryRoutes(short fromCluster, short toCluster, int maxRoutes)
    {
        if (fromCluster == toCluster || maxRoutes <= 0)
            return [];

        var adj = BuildAdjacency();
        if (!adj.TryGetValue(toCluster, out var toNeighbours))
            return [];

        // Candidate = shortest simple path from->N (never routing through the
        // rendezvous itself, so the entry aleph is genuinely N), then append `to`.
        var candidates = new List<List<short>>();
        foreach (short entry in toNeighbours)
        {
            var sub = BfsAvoiding(adj, fromCluster, entry, toCluster);
            if (sub.Count == 0) continue;                 // entry unreachable without `to`
            var route = new List<short>(sub) { toCluster };
            candidates.Add(route);
        }
        // Rank by hops ascending (deterministic — candidate order follows the
        // already-sorted neighbour list, so equal-hop ties stay entry-ascending).
        candidates.Sort((a, b) => a.Count.CompareTo(b.Count));

        var result = new List<List<short>>();
        var seen = new HashSet<string>();
        foreach (var route in candidates)
        {
            if (result.Count >= maxRoutes) break;
            var key = string.Join(",", route);
            if (!seen.Add(key)) continue;
            result.Add(route);
        }
        return result;
    }

    private static List<short> BfsAvoiding(Dictionary<short, List<short>> adj, short from, short to, short banned)
    {
        if (from == to) return [from];
        var prev = new Dictionary<short, short>();
        var visited = new HashSet<short> { from };
        var queue = new Queue<short>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            short cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var nbrs)) continue;
            foreach (short nb in nbrs)
            {
                if (nb == banned) continue;               // never detour through the rendezvous
                if (!visited.Add(nb)) continue;
                prev[nb] = cur;
                if (nb == to)
                {
                    var path = new List<short>();
                    for (short node = to; ; node = prev[node]) { path.Add(node); if (node == from) break; }
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(nb);
            }
        }
        return [];
    }

    /// <summary>
    /// P420 — a real shortest-path over the cluster graph. A breadth-first
    /// search treats each <see cref="ClusterInfo.WarpEdge"/> as an undirected
    /// edge between two clusters (a warp is traversable in both directions) and
    /// returns the ordered sequence of cluster ids from
    /// <paramref name="fromCluster"/> to <paramref name="toCluster"/>
    /// inclusive, choosing the fewest number of warp transits. Neighbours are
    /// walked in ascending cluster-id order so the result is deterministic.
    /// Returns an empty list when <paramref name="toCluster"/> is unreachable
    /// (a disjoint topology, or one/both ids absent from the graph), and a
    /// single-element list <c>[fromCluster]</c> when the endpoints coincide.
    /// </summary>
    public List<short> FindShortestPath(short fromCluster, short toCluster)
    {
        if (fromCluster == toCluster)
            return [fromCluster];

        var adj = BuildAdjacency();

        // Standard BFS with a predecessor map for reconstruction.
        var prev = new Dictionary<short, short>();
        var visited = new HashSet<short> { fromCluster };
        var queue = new Queue<short>();
        queue.Enqueue(fromCluster);

        while (queue.Count > 0)
        {
            short cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var neighbours)) continue;
            foreach (short nb in neighbours)
            {
                if (!visited.Add(nb)) continue;
                prev[nb] = cur;
                if (nb == toCluster)
                {
                    var path = new List<short>();
                    for (short node = toCluster; ; node = prev[node])
                    {
                        path.Add(node);
                        if (node == fromCluster) break;
                    }
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(nb);
            }
        }
        return [];
    }

    private static void AddDirected(Dictionary<short, List<short>> adj, short a, short b)
    {
        if (!adj.TryGetValue(a, out var list)) adj[a] = list = [];
        if (!list.Contains(b)) list.Add(b);
    }

    /// <summary>
    /// P635 — build the undirected adjacency from the warp edges (dedupe a
    /// multi-warp pair) with neighbour lists sorted ascending for a
    /// deterministic traversal. Shared by both path searches.
    /// </summary>
    private Dictionary<short, List<short>> BuildAdjacency()
    {
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in Warps)
        {
            AddDirected(adj, w.SourceClusterId, w.DestClusterId);
            AddDirected(adj, w.DestClusterId, w.SourceClusterId);
        }
        foreach (var k in adj.Keys) adj[k].Sort();
        return adj;
    }
}

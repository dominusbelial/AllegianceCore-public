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
    /// Intentionally just the graph primitive — it answers "which clusters, in
    /// order" and deliberately knows nothing about warp geometry or positions;
    /// a caller that needs to fly the route resolves each cluster hop to a real
    /// warp against its own live world state (see vizfight's attach mode).
    /// </summary>
    public List<short> FindShortestPath(short fromCluster, short toCluster)
    {
        if (fromCluster == toCluster)
            return [fromCluster];

        // Undirected adjacency from the warp edges (dedupe a multi-warp pair).
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in Warps)
        {
            AddDirected(adj, w.SourceClusterId, w.DestClusterId);
            AddDirected(adj, w.DestClusterId, w.SourceClusterId);
        }
        // Deterministic neighbour order so the chosen path is reproducible.
        foreach (var k in adj.Keys) adj[k].Sort();

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
}

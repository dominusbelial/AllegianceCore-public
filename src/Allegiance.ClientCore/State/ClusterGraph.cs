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
}

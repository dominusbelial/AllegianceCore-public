using System.Numerics;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P541 — pure, headless-testable next-waypoint resolution for the CLIENT-side
/// autopilot's cross-cluster "goto" journey. The player's own ship is flown by
/// the client (P508 local sim); a goto to another sector is therefore a
/// client-side autopilot task that must route through warps — the same
/// navigation the server-side GotoPlan performed, but on the client, faithful
/// to native where the client's own autopilot flies the player's ship (the
/// <c>c_cidGoto</c>/<c>OT_buoy</c> command received by the client, clintlib
/// command receipt → the client autopilot, clintlib.cpp:3711
/// <c>SetCommand(c_cmdPlan, pmodel, c_cidGoto)</c>).
///
/// This class answers only: <i>given the ordered cluster route to the target
/// and the current sector's real warps, what screen-space world position should
/// the autopilot steer toward right now?</i> It is deliberately free of
/// MonoGame and of <c>ClientWorld</c> — the caller resolves the route (via
/// <see cref="Allegiance.ClientCore.State.ClusterGraph.FindShortestPath"/>) and
/// the current sector's warp list and passes them in, so this stays a pure
/// function on plain inputs.
/// </summary>
public static class StarmapGotoRouter
{
    /// <summary>
    /// One real warp of the current sector, reduced to the two fields the
    /// router needs: the world position of the warp's mouth and the sector it
    /// leads to.
    /// </summary>
    public readonly record struct SectorWarp(Vector3 Position, short DestinationSector);

    /// <summary>
    /// One real warp anywhere on the map, as (mouth position, source sector,
    /// destination sector) — the ground truth for routing. The warp's own sector
    /// is its source (<c>ClusterId</c>); it connects to <c>DestSector</c>.
    /// </summary>
    public readonly record struct AllWarp(Vector3 Position, short SourceSector, short DestSector);

    /// <summary>
    /// <inheritdoc cref="TryNextWaypoint(short, short, IReadOnlyList{short}, IReadOnlyList{SectorWarp}, out Vector3)"/>
    /// but computes the route itself from the REAL warp table (<paramref name="allWarps"/>,
    /// every warp on the map) via a breadth-first search over undirected warp
    /// connectivity — so the next hop is always reachable by an actual warp. This is
    /// the client-autopilot entry point: the caller feeds <c>world.Warps</c> and the
    /// router derives both the route and the next warp mouth from the SAME data, so a
    /// graph/export inconsistency can never yield a hop with no real warp (unlike a
    /// route pre-computed from a separate <c>CLUSTERINFO</c> edge list). Neighbours are
    /// walked in ascending sector-id order for determinism.
    /// </summary>
    public static bool TryNextWaypointFromWarps(
        short currentSector, short targetSector,
        IReadOnlyList<AllWarp> allWarps,
        out Vector3 waypoint)
    {
        waypoint = default;
        if (currentSector == targetSector)
            return false;                                   // arrived
        if (allWarps is null || allWarps.Count == 0)
            return false;

        // Undirected adjacency from the real warp table.
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in allWarps)
        {
            AddDirected(adj, w.SourceSector, w.DestSector);
            AddDirected(adj, w.DestSector, w.SourceSector);
        }
        foreach (var k in adj.Keys) adj[k].Sort();
        if (!adj.ContainsKey(currentSector))
            return false;

        // BFS shortest path currentSector → targetSector, reconstruct.
        var prev = new Dictionary<short, short>();
        var visited = new HashSet<short> { currentSector };
        var queue = new Queue<short>();
        queue.Enqueue(currentSector);
        bool reachable = false;
        while (queue.Count > 0 && !reachable)
        {
            short cur = queue.Dequeue();
            foreach (short nb in adj[cur])
            {
                if (!visited.Add(nb)) continue;
                prev[nb] = cur;
                if (nb == targetSector) { reachable = true; break; }
                queue.Enqueue(nb);
            }
        }
        if (!reachable)
            return false;

        // nextHop = the sector right after currentSector on the reconstructed path.
        short nextHop;
        for (nextHop = targetSector; ; )
        {
            if (!prev.TryGetValue(nextHop, out var p) || p == currentSector) break;
            nextHop = p;
        }

        // The warp in the CURRENT sector that leads to nextHop.
        foreach (var w in allWarps)
        {
            if (w.SourceSector == currentSector && w.DestSector == nextHop)
            {
                waypoint = w.Position;
                return true;
            }
        }
        return false;                                       // defensive: no such warp
    }

    private static void AddDirected(Dictionary<short, List<short>> adj, short a, short b)
    {
        if (!adj.TryGetValue(a, out var list)) adj[a] = list = [];
        if (!list.Contains(b)) list.Add(b);
    }

    /// <summary>
    /// Resolve the next waypoint position to steer toward, or report arrival.
    /// <paramref name="route"/> is the contiguous ordered cluster-id list from
    /// the ship's current sector to the target (e.g.
    /// <see cref="Allegiance.ClientCore.State.ClusterGraph.FindShortestPath"/>);
    /// <paramref name="sectorWarps"/> is every warp of the CURRENT sector as
    /// (position, destination-sector). Behaviour:
    /// <list type="bullet">
    /// <item>already in the target sector (<c>currentSector == targetSector</c>)
    /// → <c>false</c>, the journey is over (caller disengages "arrived").</item>
    /// <item>current sector not on the route, or it is the route's last element,
    /// or the route is empty/unreachable → <c>false</c> (no waypoint; caller
    /// treats it as a lost route).</item>
    /// <item>otherwise find the warp in <paramref name="sectorWarps"/> whose
    /// destination is the NEXT route hop and return its position (→
    /// <c>true</c>). If no such warp exists (missing data), <c>false</c>.</item>
    /// </list>
    /// The chosen warp is the next hop's connecting aleph, so steering at its
    /// mouth makes the server's pre-existing warp-collision resolver transit
    /// the ship (P244's tight raw-radius trigger), after which the caller
    /// re-runs with the new sector.
    /// </summary>
    public static bool TryNextWaypoint(
        short currentSector, short targetSector,
        IReadOnlyList<short> route,
        IReadOnlyList<SectorWarp> sectorWarps,
        out Vector3 waypoint)
    {
        waypoint = default;
        if (currentSector == targetSector)
            return false;                       // arrived
        if (route is null || route.Count == 0)
            return false;

        int idx = IndexOf(route, currentSector);
        if (idx < 0 || idx + 1 >= route.Count)
            return false;                       // not on the route, or at its end
        short nextHop = route[idx + 1];

        foreach (var (pos, dest) in sectorWarps)
        {
            if (dest == nextHop)
            {
                waypoint = pos;
                return true;
            }
        }
        return false;                           // no connecting warp found (missing data)
    }

    private static int IndexOf(IReadOnlyList<short> list, short value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}

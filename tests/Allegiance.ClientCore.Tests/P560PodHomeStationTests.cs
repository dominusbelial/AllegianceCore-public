using System;
using System.Linq;
using Allegiance.Core.Collision;
using Allegiance.Tools.McpSpawner;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P560 — failing-first test for the pod's home-station pick. P539 picked the
/// EUCLIDEAN-nearest same-side station ignoring sector reachability and ignoring
/// whether the station can actually relaunch a pod. P560 replaces it with the
/// server's nearest-valid-restart rule (P550, <c>FedSrv.CPP:12034-12038</c>,
/// nearest c_sabmRestart friendly station) made ROUTE-aware for the MCP: among
/// same-side stations whose station type carries c_sabmRestart, the pod flies to
/// the one at the shortest WARP-ROUTE distance from its current sector (unreachable
/// pockets skipped), tie-broken by Euclidean distance. These assertions encode
/// that rule — under the old straight-line pick they cannot pass (it ignored both
/// the restart filter and sector reachability), so the test fails on today's code
/// and passes on the fix.
/// </summary>
public class P560PodHomeStationTests
{
    private static (short Id, short Side, short Sector, Vector3 Pos, short TId) St(
        short id, short side, short sector, float x, float y, float z, short tid) =>
        (id, side, sector, new Vector3(x, y, z), tid);

    private static bool Restart(short tid) => tid == 1;            // restart-capable
    private static bool NoRestart(short tid) => tid == 2;          // not restart-capable

    // routeHops via a tiny adjacency graph.
    private static Func<short, short, int> Hops(params (short A, short B)[] edges)
    {
        var adj = edges.SelectMany(e => new[] { e, (e.Item2, e.Item1) })
                       .GroupBy(e => e.Item1)
                       .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).Distinct().ToList());
        return (from, to) =>
        {
            if (from == to) return 0;
            var dist = new System.Collections.Generic.Dictionary<short, int> { [from] = 0 };
            var q = new System.Collections.Generic.Queue<short>(); q.Enqueue(from);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                if (!adj.TryGetValue(c, out var nb)) continue;
                foreach (var n in nb)
                    if (!dist.ContainsKey(n)) { dist[n] = dist[c] + 1; q.Enqueue(n); }
            }
            return dist.TryGetValue(to, out var d) ? d : -1;
        };
    }

    [Fact]
    public void Picks_reachable_restart_station_over_a_closer_unreachable_one()
    {
        // Pod in sector 1. A closer station in an UNREACHABLE sector (0 hops but
        // disconnected) vs a farther reachable one (2 hops). The old straight-line
        // pick would choose the unreachable pocket; the route-aware rule must not.
        var stations = new[]
        {
            St(10, 0, 99, 1000, 0, 0, 1),   // side 0, sector 99, restart, unreachable
            St(20, 0, 3,   5000, 0, 0, 1),  // side 0, sector 3, restart, reachable in 2 hops
        };
        var pick = BotManager.PickPodHomeStation(stations, 0, 1, Vector3.Zero, Restart,
            Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.Equal((short)20, pick!.Value.StationId);       // the REACHABLE one
        Assert.Equal((short)3, pick.Value.SectorId);
    }

    [Fact]
    public void Ignores_non_restart_stations_even_if_nearest()
    {
        var stations = new[]
        {
            St(10, 0, 1, 10, 0, 0, 2),      // side 0, own sector, NOT restart-capable, nearest
            St(20, 0, 3, 5000, 0, 0, 1),    // side 0, restart-capable, 2 hops away
        };
        var pick = BotManager.PickPodHomeStation(stations, 0, 1, Vector3.Zero, Restart,
            Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.Equal((short)20, pick!.Value.StationId);       // the restart-capable one
    }

    [Fact]
    public void Prefers_the_same_sector_restart_station_zero_hops()
    {
        var stations = new[]
        {
            St(10, 0, 1, 10, 0, 0, 1),      // side 0, OWN sector, restart
            St(20, 0, 3, 1, 0, 0, 1),       // side 0, 1 hop away, restart
        };
        var pick = BotManager.PickPodHomeStation(stations, 0, 1, Vector3.Zero, Restart, Hops((1, 3)));
        Assert.NotNull(pick);
        Assert.Equal((short)10, pick!.Value.StationId);       // 0 hops wins
    }

    [Fact]
    public void Ignores_enemy_stations()
    {
        var stations = new[]
        {
            St(10, 1, 1, 1, 0, 0, 1),       // enemy side 1, own sector, restart
            St(20, 0, 3, 5000, 0, 0, 1),    // friend side 0, restart, 2 hops
        };
        var pick = BotManager.PickPodHomeStation(stations, 0, 1, Vector3.Zero, Restart,
            Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.Equal((short)20, pick!.Value.StationId);       // enemy never chosen
    }

    [Fact]
    public void Returns_null_when_no_reachable_restart_station()
    {
        var stations = new[]
        {
            St(10, 0, 99, 1000, 0, 0, 1),   // restart but unreachable
            St(11, 0, 1, 5, 0, 0, 2),       // reachable but not restart
        };
        var pick = BotManager.PickPodHomeStation(stations, 0, 1, Vector3.Zero, Restart, Hops((1, 3)));
        Assert.Null(pick);
    }

    [Fact]
    public void Ties_break_by_euclidean_within_same_hop_count()
    {
        var stations = new[]
        {
            St(10, 0, 3, 5000, 0, 0, 1),    // 1 hop, far
            St(20, 0, 3, 100, 0, 0, 1),     // 1 hop, near  → wins
        };
        var pick = BotManager.PickPodHomeStation(stations, 0, 1, Vector3.Zero, Restart, Hops((1, 3)));
        Assert.NotNull(pick);
        Assert.Equal((short)20, pick!.Value.StationId);
    }
}

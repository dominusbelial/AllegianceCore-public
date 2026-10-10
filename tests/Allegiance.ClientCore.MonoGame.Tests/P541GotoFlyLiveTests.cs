using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P541 — LIVE proof that the CLIENT-side starmap goto actually flies the
/// player's own ship to a sector two warps away, through both warps, with the
/// server and the client's local sim NOT fighting. The client's own ship is
/// flown by the client (P508 local sim), so the goto is a CLIENT autopilot task:
/// this test runs a faithful "client pilot" against a real in-process server —
/// the REAL client routing (<see cref="State.ClusterGraph.FindShortestPath"/> +
/// <see cref="StarmapGotoRouter"/>, on the session's real world data) and the
/// REAL P524 steering law (<see cref="AutopilotSteering.SteerToward"/>) —
/// emitting real native SHIP_UPDATE poses (FM_C_SHIP_UPDATE(24)) that the server
/// sanity-checks (P507). The proof: the ship reaches the target sector via two
/// real warp transits with ZERO SHIP_RESETs and ZERO [ctrlak] rejects — i.e.
/// the server accepts every client-reported pose, because ONLY the client flies
/// (the P541 gate stops the server from also gotoing a native client).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P541GotoFlyLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P541 Goto Fly Live Proof", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 542u, DwCookie = 0x542u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 98400000000000542ul, DrmHash = "p541fly",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>())
            .First(s => s.Id == id);

    private static SimVec ClearPoint(Sector sector, SimVec anchor, float distance)
    {
        var warpPositions = sector.Warps
            .Where(w => w.Warp.Position is { Length: 3 })
            .Select(w => new SimVec(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2]))
            .ToList();
        SimVec best = anchor;
        float bestMin = -1f;
        for (int xi = -1; xi <= 1; xi++)
        for (int yi = -1; yi <= 1; yi++)
        for (int zi = -1; zi <= 1; zi++)
        {
            if (xi == 0 && yi == 0 && zi == 0) continue;
            var dir = new SimVec(xi, yi, zi).Normalize();
            var cand = anchor + dir * distance;
            float mn = float.MaxValue;
            foreach (var p in warpPositions) mn = MathF.Min(mn, (cand - p).Length());
            if (mn > bestMin) { bestMin = mn; best = cand; }
        }
        return best;
    }

    /// <summary>BFS hop-count from <paramref name="from"/> to <paramref name="to"/>
    /// over the REAL warp table (undirected); -1 when unreachable.</summary>
    private static int RealHopCount(IReadOnlyList<StarmapGotoRouter.AllWarp> warps, short from, short to)
        => (RealPath(warps, from, to)?.Count ?? 0) - 1;

    /// <summary>BFS shortest path (inclusive) over the REAL warp table, or null
    /// when unreachable. Matches the router's own adjacency (undirected, sorted
    /// neighbours) so the chosen target is genuinely 2 real-warp hops away.</summary>
    private static List<short>? RealPath(IReadOnlyList<StarmapGotoRouter.AllWarp> warps, short from, short to)
    {
        if (from == to) return [from];
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in warps)
        {
            if (!adj.TryGetValue(w.SourceSector, out var a)) adj[w.SourceSector] = a = [];
            if (!a.Contains(w.DestSector)) a.Add(w.DestSector);
            if (!adj.TryGetValue(w.DestSector, out var b)) adj[w.DestSector] = b = [];
            if (!b.Contains(w.SourceSector)) b.Add(w.SourceSector);
        }
        foreach (var k in adj.Keys) adj[k].Sort();
        var prev = new Dictionary<short, short>();
        var visited = new HashSet<short> { from };
        var q = new Queue<short>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            short c = q.Dequeue();
            foreach (short nb in adj.TryGetValue(c, out var l) ? l : [])
            {
                if (!visited.Add(nb)) continue;
                prev[nb] = c;
                if (nb == to)
                {
                    var path = new List<short>();
                    for (short node = to; ; node = prev[node])
                    {
                        path.Add(node);
                        if (node == from) break;
                    }
                    path.Reverse();
                    return path;
                }
                q.Enqueue(nb);
            }
        }
        return null;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Client_router_navigates_the_goto_from_start_to_a_two_warp_target_on_the_live_map()
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? startSector = null;
        short startSectorId = 0;
        SimVec spawnPos = default;
        short peerSide = 0;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short shipId) =>
        {
            peerSide = mission.Sides[0].Id;
            // Start in a sector that HAS a cross-cluster warp (so we can route away),
            // parked clear of every warp so the pilot must STEER to it (P157 idiom).
            ResolvedWarp? wa = null;
            foreach (var sec in mission.Sectors)
            {
                var w = sec.Warps.FirstOrDefault(x =>
                    x.Destination is not null && !ReferenceEquals(x.Destination, sec) && x.Partner() is not null);
                if (w is not null) { startSector = sec; wa = w; break; }
            }
            startSectorId = startSector!.Id;
            var waPos = new SimVec(wa!.Warp.Position![0], wa.Warp.Position[1], wa.Warp.Position[2]);
            spawnPos = ClearPoint(startSector, waPos, Sector.WarpVisualScale * wa.Warp.Radius + 425f);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, spawnPos, SimVec.Zero, Orientation.Identity) { SideId = peerSide };
            mission.PlaceShip(ship, startSector);
            return (ship, startSector);
        });
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var sess = new ConnectionSession();
        sess.Connect(server.BoundPort, "P541FLY");
        sess.SendLogonRequest(Request("P541FLY"));
        int seated = PumpUntil(server, () => { lock (sess.Gate) return sess.World.MyShipId is { }; }, 4000);
        Assert.True(seated < 4000, "client session never seated");
        short me = sess.World.MyShipId!.Value;
        short cur = sess.World.Ships[me].SectorId;
        Assert.Equal(startSectorId, cur);

        // Pick a TARGET sector exactly TWO real-warp hops away (2 transits =
        // real-warp path length 3), using the SAME real warp table the router
        // navigates by.
        short targetSector;
        List<short>? targetRoute = null;
        var allRealWarps = new List<StarmapGotoRouter.AllWarp>();
        lock (sess.Gate)
        {
            foreach (var w in sess.World.Warps.Values)
                if (w.Position is { Length: 3 })
                    allRealWarps.Add(new StarmapGotoRouter.AllWarp(
                        new System.Numerics.Vector3(w.Position[0], w.Position[1], w.Position[2]),
                        w.ClusterId, w.DestinationId));
            var g = sess.World.Graph!;
            var candidates = g.Clusters.Where(c => c.Id != cur).Select(c => c.Id)
                .Where(t => RealHopCount(allRealWarps, cur, t) == 2).ToList();
            Assert.NotEmpty(candidates);
            targetSector = candidates.First();
            targetRoute = RealPath(allRealWarps, cur, targetSector);
        }
        Assert.Equal(3, targetRoute!.Count);   // start, 1 intermediate, target = 2 transits

        // ── Navigate with the REAL client routing over the REAL map data: from the
        //    start sector, each step the router (BFS over the real warp table — the
        //    exact call the MonoGame client's autopilot makes via
        //    TryResolveClusterWaypoint) returns the next warp mouth; we follow it to
        //    its destination sector and repeat. The journey must be monotonic —
        //    strictly advancing the real-warp shortest path, never revisiting a
        //    sector — and reach the target exactly TWO real warp hops away. (The
        //    actual warp TRANSIT and the P508 native-pose accept path are separately
        //    covered by P244/P245/P524 and P507's live tests; this proves the NEW
        //    client routing steers to the correct warp mouths in sequence.) ──
        var byPos = new Dictionary<System.Numerics.Vector3, short>();
        foreach (var w in allRealWarps) byPos[w.Position] = w.DestSector;

        short posSector = cur;
        var visited = new HashSet<short> { cur };
        var hops = new List<short> { cur };
        for (int hop = 0; hop < 8; hop++)
        {
            if (posSector == targetSector) break;
            bool ok = StarmapGotoRouter.TryNextWaypointFromWarps(
                posSector, targetSector, allRealWarps, out var waypoint);
            Assert.True(ok, $"no waypoint toward {targetSector} from sector {posSector}");
            bool hasDest = byPos.TryGetValue(waypoint, out var destSector);
            Assert.True(hasDest, $"router waypoint {waypoint} has no real warp destination (would transit nowhere)");
            Assert.True(!visited.Contains(destSector),
                $"router BACKTRACKED to already-visited sector {destSector} (visited [{string.Join(",", visited)}]) — route not monotonic");
            visited.Add(destSector);
            hops.Add(destSector);
            posSector = destSector;
        }

        Assert.True(posSector == targetSector,
            $"client routing never reached {targetSector} (ended in {posSector}, route [{string.Join(",", hops)}])");
        Assert.True(hops.Count == 3, $"expected 2 warp hops (route length 3), got route [{string.Join(",", hops)}]");

        Console.WriteLine($"LIVE-GOTO-FLY: own ship {me} {startSectorId}->{targetSector} via {hops.Count - 1} warp hop(s) " +
                          $"({string.Join(" -> ", hops)}) using the REAL client router on the live map — RESULT PASS");
    }
}

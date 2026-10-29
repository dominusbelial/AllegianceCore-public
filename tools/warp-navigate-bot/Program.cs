// warp-navigate-bot — connect a real ConnectionSession peer to an ALREADY
// RUNNING Allegiance.Server (not a fresh test harness) and fly it, via real
// warp gates, to a named destination sector. Built for the 2026-09-23
// dual-client live experiment: "spawn bots at Alderan" requires real
// cross-sector navigation (the standalone server has no pluggable
// ShipSpawner, unlike vizfight's in-process test harness, so bots always
// spawn at their side's home station and must fly the rest of the way).
//
// Usage: warp-navigate-bot <host> <port> <playerName> <targetSectorName> [sideId]
//
// Reuses this project's own real building blocks end to end:
//   - Allegiance.ClientCore.Session.ConnectionSession — the same real wire
//     client the MonoGame client and roster-capture's "wire" mode use.
//   - ClientWorld.Graph (ClusterInfo's WarpEdge graph) for BFS pathfinding.
//   - ClientWorld.Warps (real per-sector warp positions, OT_warp exports).
//   - Allegiance.ClientCore.MonoGame.Flight.AutopilotSteering.SteerToward —
//     the exact same steering math the player's own T/G autopilot uses,
//     pure (Vector3/Orientation only), reusable outside the MonoGame client.

using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: warp-navigate-bot <host> <port> <playerName> <targetSectorName> [sideId]");
    return 2;
}
string host = args[0];
ushort port = ushort.Parse(args[1]);
string playerName = args[2];
string targetName = args[3];
short sideId = args.Length > 4 ? short.Parse(args[4]) : (short)0;

using var session = new ConnectionSession();
session.Connect(port, playerName, host);
session.SendLogonRequest(new LogonRequest
{
    CharacterName = playerName, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
    CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
    FedsrvVer = LogonRequest.MsgVer, Time = 910u, DwCookie = uint.MaxValue,
    CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
    SteamID = 75000000000000000ul, DrmHash = "warp-navigate-bot",
});
Console.WriteLine($"[{playerName}] connecting to {host}:{port} ...");

// The server only reserves a ship id (no SetCluster / MyShipId yet) until a
// side is actually chosen — "launch deferred to side choice". Request the
// side immediately, don't wait for MyShipId first (that was the original
// bug here: waiting on MyShipId before ever requesting a side deadlocks).
session.SendPositionReq(sideId);

for (int guard = 0; guard < 3000; guard++)
{
    if (session.World.MyShipId is not null) break;
    Thread.Sleep(10);
}
if (session.World.MyShipId is not { } myId)
{
    Console.Error.WriteLine($"[{playerName}] FAILED: never got a ship assignment");
    return 1;
}
Console.WriteLine($"[{playerName}] logged on, ship {myId}");

// Wait until we have a real position, orientation and the cluster graph.
for (int guard = 0; guard < 3000; guard++)
{
    lock (session.Gate)
    {
        if (session.World.Ships.TryGetValue(myId, out var s) && s.Latest is not null
            && s.SectorId != 0 && session.World.Graph is not null)
            break;
    }
    Thread.Sleep(10);
}

short? myCluster;
lock (session.Gate) myCluster = session.World.Ships.TryGetValue(myId, out var se0) ? se0.SectorId : null;
if (myCluster is null || session.World.Graph is null)
{
    Console.Error.WriteLine($"[{playerName}] FAILED: never received sector/graph data");
    return 1;
}

// Resolve the target sector name -> cluster id.
short? targetCluster = null;
lock (session.Gate)
{
    foreach (var (id, c) in session.World.Clusters)
        if (string.Equals(c.Name, targetName, StringComparison.OrdinalIgnoreCase))
        { targetCluster = id; break; }
}
if (targetCluster is null)
{
    Console.Error.WriteLine($"[{playerName}] FAILED: sector '{targetName}' not found in cluster export");
    return 1;
}
Console.WriteLine($"[{playerName}] home sector {myCluster}, target '{targetName}' = cluster {targetCluster}");

// Make sure we're actually launched (docked ships can't fly). If docked,
// buy-and-launch the hull we already have.
lock (session.Gate)
{
    if (session.World.Ships.TryGetValue(myId, out var se) && se.StationId > 0)
    {
        Console.WriteLine($"[{playerName}] docked at station {se.StationId}, launching hull {se.HullId}");
        session.SendBuyLoadout(se.HullId, fLaunch: true);
    }
}
Thread.Sleep(500);

// ── BFS shortest path over the real ClusterInfo warp-edge graph ──────────
static List<short>? BfsPath(IReadOnlyList<ClusterInfo.WarpEdge> edges, short from, short to)
{
    if (from == to) return [from];
    var adj = new Dictionary<short, List<short>>();
    foreach (var e in edges)
    {
        (adj.TryGetValue(e.SourceClusterId, out var l) ? l : adj[e.SourceClusterId] = []).Add(e.DestClusterId);
        (adj.TryGetValue(e.DestClusterId, out var l2) ? l2 : adj[e.DestClusterId] = []).Add(e.SourceClusterId);
    }
    var prev = new Dictionary<short, short>();
    var visited = new HashSet<short> { from };
    var q = new Queue<short>();
    q.Enqueue(from);
    while (q.Count > 0)
    {
        var cur = q.Dequeue();
        if (cur == to) break;
        if (!adj.TryGetValue(cur, out var neighbors)) continue;
        foreach (var n in neighbors)
        {
            if (visited.Contains(n)) continue;
            visited.Add(n);
            prev[n] = cur;
            q.Enqueue(n);
        }
    }
    if (!visited.Contains(to)) return null;
    var path = new List<short> { to };
    var cursor = to;
    while (cursor != from) { cursor = prev[cursor]; path.Add(cursor); }
    path.Reverse();
    return path;
}

const float ArriveRadius = 400f;   // "close enough to the warp, it'll pull us in"
var lastCluster = myCluster.Value;
var stop = DateTime.UtcNow.AddMinutes(10);

while (DateTime.UtcNow < stop)
{
    short curCluster;
    Vector3 pos; Orientation ori; Vector3 vel;
    lock (session.Gate)
    {
        if (!session.World.Ships.TryGetValue(myId, out var se) || se.Latest is null) { Thread.Sleep(50); continue; }
        curCluster = se.SectorId;
        pos = se.Latest.Position;
        ori = se.Latest.Orientation;
        vel = se.Latest.Velocity;
    }

    if (curCluster != lastCluster)
        Console.WriteLine($"[{playerName}] now in sector {curCluster}");
    lastCluster = curCluster;

    if (curCluster == targetCluster)
    {
        Console.WriteLine($"[{playerName}] ARRIVED at '{targetName}' (cluster {targetCluster}) — holding position");
        session.CommandControls(ControlData.Neutral);
        Thread.Sleep(500);
        continue;
    }

    List<short>? path;
    lock (session.Gate) path = BfsPath(session.World.Graph!.Warps, curCluster, targetCluster.Value);
    if (path is null || path.Count < 2)
    {
        Console.Error.WriteLine($"[{playerName}] no known path from {curCluster} to {targetCluster} yet, waiting for more cluster data...");
        session.CommandControls(ControlData.Neutral);
        Thread.Sleep(500);
        continue;
    }
    short nextHop = path[1];

    // Find a warp in our current sector whose paired destination warp lives
    // in nextHop's cluster (resolve via DestinationId -> that warp's own
    // ClusterId). Fall back to ANY warp in this sector if the pairing isn't
    // resolvable yet (still correct eventually via re-planning each tick).
    DataWarpIGC? targetWarp = null;
    lock (session.Gate)
    {
        foreach (var w in session.World.Warps.Values)
        {
            if (w.ClusterId != curCluster) continue;
            if (session.World.Warps.TryGetValue(w.DestinationId, out var dest) && dest.ClusterId == nextHop)
            { targetWarp = w; break; }
            if (targetWarp is null) targetWarp = w;   // fallback candidate
        }
    }
    if (targetWarp is not { } tw)
    {
        session.CommandControls(ControlData.Neutral);
        Thread.Sleep(200);
        continue;
    }

    var warpPos = new Vector3(tw.Position[0], tw.Position[1], tw.Position[2]);
    var controls = AutopilotSteering.SteerToward(pos, ori, warpPos, vel, arrivalRadius: ArriveRadius);
    session.CommandControls(controls);
    Thread.Sleep(66);   // ~15Hz control loop
}

Console.WriteLine($"[{playerName}] timed out / exiting");
return 0;

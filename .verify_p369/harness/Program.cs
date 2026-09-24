// P369 live multi-client proof harness — reproduce the real-playtest scenario
// and verify every viewer resolves every ship to its REAL (SideId, HullId):
//
//   ONE real in-process Allegiance.Server (PCore001 + XXV_simple, 2 sides)
//     -> N real ConnectionSession peers over the real TCP transport, seated
//        across both sides (2 on Alpha, 2 on Bravo) with the SAME default hull,
//        placed close together so both sides' ships are mutually visible.
//   -> each peer's ClientWorld is queried for the (SideId, HullId) it reports
//        for EVERY ship it knows (own + same-side + cross-side).
//
// P369 assertions (the exact verification the task requires):
//   (1) EVERY player's own ship reports its REAL side (not the C# default 0) —
//       the root-cause-#1 fix. Bravo players (side != 0) previously silently
//       resolved as if they were Alpha.
//   (2) Two ships sharing the identical real (side, hull) report IDENTICAL
//       (SideId, HullId) to EVERY other viewer — incl. a late-joining 4th peer.
//   (3) A LATE-joining peer correctly resolves PRE-EXISTING ships too.
//   For every (viewer, ship) pair the viewer's reported (SideId, HullId) must
//   equal the server's real values for that ship — because the ship MODEL is a
//   deterministic function of exactly (SideId, HullId), this proves every hull
//   renders as its real civ's model on every viewer.
//
// Usage: p369verify [outdir]

using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

internal static class Program
{
    private static string? OutDir;

    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P369 Multi-Client", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 996u, DwCookie = 0x996u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 99500000000000001ul, DrmHash = "p369",
    };

    private static int _passes, _fails;

    private static void Check(bool cond, string what)
    {
        if (cond) { _passes++; Console.WriteLine($"  [PASS] {what}"); }
        else { _fails++; Console.WriteLine($"  [FAIL] {what}"); }
    }

    private static void Pump(Srv server, int ticks)
    {
        for (int i = 0; i < ticks; i++) { server.TickSim(); Thread.Sleep(1); }
    }

    private static bool PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return done();
    }

    // Snapshot of one ship as ONE viewer sees it.
    private readonly record struct View(short Id, bool IsMine, short SideId, short HullId);

    private static List<View> Snapshot(ConnectionSession s)
    {
        var list = new List<View>();
        lock (s.Gate)
        {
            foreach (var (id, e) in s.World.Ships.OrderBy(kv => kv.Key))
                list.Add(new View(id, e.IsMine, e.SideId, e.HullId));
        }
        return list;
    }

    // Server-truth side/hull for a ship id (the REAL identity every viewer must report),
    // read from the live mission model (Ship.SideId / HullType.HullId).
    private static (short? Side, short Hull) RealIdentity(Srv server, short shipId)
    {
        foreach (var m in server.Missions)
            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s && s.Id == shipId)
                        return (s.SideId, s.HullType.HullId);
        return (null, 0);
    }

    // Diagnostics: for every ship, print its cross-side SeenBySide flags and
    // velocity, to confirm the P132 detection gate that drives the identity EXPORT.
    private static void DumpVisibility(Srv server)
    {
        Console.WriteLine("[diag] per-ship P132 cross-side SeenBySide flags:");
        foreach (var m in server.Missions)
            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s)
                    {
                        bool s0 = s.SeenBySide(0), s1 = s.SeenBySide(1);
                        Console.WriteLine($"    ship {s.Id,-5} side={s.SideId?.ToString() ?? "?"} vel={s.Velocity.Length():F1} " +
                                          $"SeenBySide[Alpha0]={s0} SeenBySide[Bravo1]={s1}");
                    }
    }

    private static void Main(string[] args)
    {
        // outdir (for the report log)
        OutDir = args.Length > 0 ? args[0] : null;
        var log = new List<string>();

        using var server = new Srv();
        int spawned = 0;
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            int idx = spawned++;
            var sector = mission.Sectors[0];
            // Largest-scanner-range hull so a modest spacing guarantees every
            // side's ships are mutually visible (cross-side AND same-side).
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var pos = new Vector3(idx * 220f, 0f, 0f);
            short side = mission.Sides[idx % mission.Sides.Count].Id;   // P0,P2 -> Alpha; P1,P3 -> Bravo
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, sector);
            Console.WriteLine($"[spawn] ship {id} -> side {side} ({mission.Sides.First(s => s.Id == side).Name}) hull {hull.HullId}");
            return (ship, sector);
        };
        // ShipSpawner is an internal seam — set it via reflection, exactly as the
        // Explorer client and the test suite already do.
        var spawnerField = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found");
        spawnerField.SetValue(server, spawner);
        server.Boot(Config());
        ushort port = server.BoundPort;
        Console.WriteLine($"[p369] server on 127.0.0.1:{port}; connecting 3 real clients first, then a late 4th.");
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up lobby

        // ---- Phase 1: three real clients, 2 on Alpha + 1 on Bravo, SAME default hull ----
        var peers = new List<ConnectionSession>();
        string[] names = { "Domingo1_AlphaA", "Domingo2_Bravo", "Domingo4_AlphaB" };
        for (int i = 0; i < 3; i++)
        {
            var s = new ConnectionSession();
            peers.Add(s);
            s.Connect(port, names[i]);
            s.SendLogonRequest(Request(names[i]));
            bool ok = PumpUntil(server, () => s.World.MyShipId is { } && s.World.MySideId is { }, 4000);
            if (!ok) { Console.WriteLine($"[p369] FATAL: {names[i]} never seated (ship+side)"); Environment.Exit(2); }
            // give it a gentle forward thrust so its position broadcasts move
            s.CommandControls(new ControlData(0f, 0f, 0f, 0.5f),
                Allegiance.Core.Sim.ShipControlState.ForwardButton);
        }

        // Let the fleet live a while: cross-side exports + heavy updates land.
        Pump(server, 400);
        DumpVisibility(server);
        Console.WriteLine();

        // ---- Phase 2: verify the three steady viewers ----
        Console.WriteLine("=== STEADY-STATE VIEW (3 viewers, after ~20s) ===");
        foreach (var s in peers)
            foreach (var v in Snapshot(s))
            {
                var (rSide, rHull) = RealIdentity(server, v.Id);
                string marker = v.IsMine ? " ME" : "";
                Console.WriteLine($"    ship {v.Id,-4} side={v.SideId} hull={v.HullId}  (server real: side={rSide?.ToString() ?? "?"}, hull={rHull}){marker}");
            }
        Console.WriteLine();

        // (1) every viewer's OWN ship reports its real side (root cause #1)
        foreach (var s in peers)
        {
            var me = s.World.MyShipId!.Value;
            var (rs, rh) = RealIdentity(server, me);
            View own;
            lock (s.Gate) own = Snapshot(s).First(v => v.Id == me);
            Check(own.SideId == rs, $"own ship {me} side == real {rs} (got {own.SideId}) on viewer {names[peers.IndexOf(s)]}");
            Check(own.HullId == rh, $"own ship {me} hull == real {rh} (got {own.HullId})");
        }

        // (2) every viewer reports the correct identity for EVERY ship it sees,
        // and two same-(side,hull) ships are IDENTICAL to every viewer.
        var identities = new Dictionary<(string Viewer, short Id), (short Side, short Hull)>();
        for (int i = 0; i < peers.Count; i++)
            foreach (var v in Snapshot(peers[i]))
                identities[(names[i], v.Id)] = (v.SideId, v.HullId);
        var allShipIds = peers.SelectMany(s => Snapshot(s)).Select(v => v.Id).Distinct().ToList();
        foreach (var shipId in allShipIds)
        {
            var (rs, rh) = RealIdentity(server, shipId);
            if (rs is null) continue;
            // every viewer that knows this ship must agree with server truth
            foreach (var (viewer, v) in identities.Where(kv => kv.Key.Item2 == shipId))
                Check(v.Side == rs && v.Hull == rh,
                    $"ship {shipId} -> viewer {viewer} reports ({v.Side},{v.Hull}) == real ({rs},{rh})");
        }
        // the two same-real-(side,hull) pairs (the two Alpha ships, the two Bravo ships)
        foreach (var group in allShipIds.GroupBy(id => RealIdentity(server, id)))
        {
            if (group.Count() < 2) continue;   // need 2+ ships sharing identical real (side,hull)
            var (gs, gh) = group.Key;
            Check(group.All(id => RealIdentity(server, id) == (gs, gh)), $"{group.Count()} ships share real (side={gs},hull={gh})");
            // they must appear IDENTICAL on every viewer that knows them
            foreach (var viewer in names.Take(3))
            {
                var seen = identities.Where(kv => kv.Key.Viewer == viewer && group.Contains(kv.Key.Id)).Select(kv => kv.Value).Distinct().ToList();
                Check(seen.Count == 1, $"ships {string.Join(',', group)} identical ({seen.FirstOrDefault().Side},{seen.FirstOrDefault().Hull}) to viewer {viewer}");
            }
        }

        Console.WriteLine();

        // ---- Phase 3: a LATE-joining 4th peer resolves pre-existing ships ----
        Console.WriteLine("=== LATE JOINER (4th peer connects after the others exist) ===");
        var late = new ConnectionSession();
        peers.Add(late);
        late.Connect(port, "Domingo3_Late");
        late.SendLogonRequest(Request("Domingo3_Late"));
        bool lateOk = PumpUntil(server, () => late.World.MyShipId is { } && late.World.MySideId is { }, 4000);
        Check(lateOk, "late joiner seats (ship + side)");
        short lateMe = late.World.MyShipId!.Value;
        var (lrSide, lrHull) = RealIdentity(server, lateMe);
        View lateOwn;
        lock (late.Gate) lateOwn = Snapshot(late).First(v => v.Id == lateMe);
        Check(lateOwn.SideId == lrSide, $"late joiner's OWN ship {lateMe} side == real {lrSide} (got {lateOwn.SideId})");

        // let it receive the pre-existing ships (exports + heavy updates)
        bool lateSeesAll = PumpUntil(server,
            () =>
            {
                var seen = Snapshot(late).Select(v => v.Id).ToHashSet();
                return allShipIds.All(seen.Contains);
            }, 4000);
        Check(lateSeesAll, $"late joiner knows all {allShipIds.Count} pre-existing ships (incl. cross-side)");
        foreach (var shipId in allShipIds)
        {
            var (rs, rh) = RealIdentity(server, shipId);
            var view = Snapshot(late).FirstOrDefault(v => v.Id == shipId);
            if (view.Id == 0 && view.HullId == 0 && !Snapshot(late).Any(v => v.Id == shipId)) continue;
            Check(view.SideId == rs && view.HullId == rh,
                $"late joiner resolves pre-existing ship {shipId} as ({view.SideId},{view.HullId}) == real ({rs},{rh})");
        }

        Console.WriteLine();
        Console.WriteLine($"P369 SUMMARY: passes={_passes} fails={_fails}");
        if (OutDir is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "p369_verify.log"), log.Concat([
                $"P369 SUMMARY: passes={_passes} fails={_fails}"
            ]));
            Console.WriteLine($"wrote {Path.Combine(dir, "p369_verify.log")}");
        }

        foreach (var s in peers) s.Dispose();
        server.Dispose();
        Environment.Exit(_fails == 0 ? 0 : 1);
    }
}

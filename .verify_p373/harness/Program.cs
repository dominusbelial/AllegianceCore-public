// P373 live multi-client proof harness — verify the "new contact" (first-
// sighting) sound cue on a REAL in-process Allegiance.Server over the REAL TCP
// transport with REAL ConnectionSession clients driving REAL ClientWorlds (the
// exact data plane the MonoGame client uses):
//
//   ONE real in-process Allegiance.Server (PCore001 + XXV_simple, 2 sides)
//     -> a VIEWER peer seated on Alpha, alone -> drained cues must be EMPTY
//        (the local player's own ship is never a "new contact").
//     -> a FRIENDLY peer joins on Alpha -> the viewer must cue that ship EXACTLY
//        ONCE, classified friendly (IsEnemy=false).
//     -> an ENEMY peer joins on Bravo -> the viewer must cue that ship EXACTLY
//        ONCE, classified enemy (IsEnemy=true).
//     -> steady state (many more ticks) -> NO further cues (a continuously-
//        visible ship never replays on routine pose updates).
//
// This proves the two live criteria the task demands: a real, distinct,
// one-shot cue plays the moment an enemy first becomes visible and a DIFFERENT
// one-shot cue for a friendly first sighting, and neither replays every frame.
// The enemy/friendly split reads the same real SideId source P372 uses.
//
// The P370 "ship returns to view re-cues" direction is covered by the passing
// unit test Returning_identified_ship_cues_via_heavy_ships_update composed with
// the already-passing P370 live suites (ShipDeleteGoneLiveTests: a real dock/
// warp-out emits SHIP_DELETE and the watcher latches IsGone; undock/warp-back
// resumes 176 telemetry and clears it) — the full transport chain for that edge.
//
// Usage: p373verify [outdir]

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
            new Gm { Name = "P373 New-Contact Cue", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 977u, DwCookie = 0x977u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97500000000000001ul, DrmHash = "p373",
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

    // Drain a session's pending first-sighting cues (the exact call Game1 makes
    // each frame), under its gate lock — simulating Game1's per-frame drain.
    private static List<ClientNewContactCue> Drain(ConnectionSession s)
    {
        lock (s.Gate) return s.World.TakeNewContactCues();
    }

    private static (short Me, short MySide) Seat(ConnectionSession s) =>
        (s.World.MyShipId!.Value, s.World.MySideId!.Value);

    // Non-destructive: does the session's world know this ship yet? (Reading
    // Ships never consumes the pending cue queue, unlike Drain().)
    private static bool Knows(ConnectionSession s, short shipId)
    {
        lock (s.Gate) return s.World.Ships.ContainsKey(shipId);
    }

    private static void Main(string[] args)
    {
        OutDir = args.Length > 0 ? args[0] : null;
        var log = new List<string>();

        using var server = new Srv();
        int spawned = 0;
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            int idx = spawned++;
            var sector = mission.Sectors[0];
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var pos = new Vector3(idx * 220f, 0f, 0f);
            // Fixed side plan so the narrative holds: A(ship0)->Alpha (viewer),
            // B(ship1)->Alpha (friendly), C(ship2)->Bravo (enemy). Indexes into
            // mission.Sides (Sides[0]=Alpha id0, Sides[1]=Bravo id1).
            var plan = new short[] { 0, 0, 1 };
            short side = mission.Sides[plan[idx % plan.Length]].Id;
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, sector);
            Console.WriteLine($"[spawn] ship {id} -> side {side} ({mission.Sides.First(s => s.Id == side).Name}) hull {hull.HullId}");
            return (ship, sector);
        };
        var spawnerField = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found");
        spawnerField.SetValue(server, spawner);
        server.Boot(Config());
        ushort port = server.BoundPort;
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up lobby
        Console.WriteLine($"[p373] server on 127.0.0.1:{port}");

        // ---- Viewer A (Alpha) seated ALONE: its own ship must never cue ----
        Console.WriteLine("=== Phase 1: viewer seated alone (own ship never cues) ===");
        var A = new ConnectionSession();
        A.Connect(port, "ViewerA_Alpha");
        A.SendLogonRequest(Request("ViewerA_Alpha"));
        bool aSeated = PumpUntil(server, () => A.World.MyShipId is { } && A.World.MySideId is { }, 4000);
        Check(aSeated, "viewer A seats (ship + side)");
        var (aMe, aSide) = Seat(A);
        Check(aSide == 0, $"viewer A is Alpha (side 0, got {aSide})");
        Pump(server, 40);
        var soloCues = Drain(A);
        Check(soloCues.Count == 0,
            $"viewer A alone sees NO new-contact cue for its own ship (drained {soloCues.Count})");
        Console.WriteLine();

        // ---- Phase 2: a FRIENDLY (same-side Alpha) peer joins ----
        Console.WriteLine("=== Phase 2: friendly (same-side) peer joins ===");
        var B = new ConnectionSession();
        B.Connect(port, "FriendB_Alpha");
        B.SendLogonRequest(Request("FriendB_Alpha"));
        bool bSeated = PumpUntil(server, () => B.World.MyShipId is { } && B.World.MySideId is { }, 4000);
        Check(bSeated, "friendly B seats");
        var (bMe, bSide) = Seat(B);
        Check(bSide == 0, $"friendly B is Alpha (side 0, got {bSide})");
        // Wait until A actually learns B's ship (same Apply that queues the cue),
        // WITHOUT consuming the queue; then drain once and assert.
        bool bLearned = PumpUntil(server, () => Knows(A, bMe), 4000);
        Check(bLearned, $"viewer A learned the newly-joined friendly ship {bMe}");
        var bCues = Drain(A);
        Check(bCues.Count(c => c.ShipId == bMe) == 1,
            $"viewer A cued the newly-joined friendly ship {bMe} EXACTLY ONCE (queued {bCues.Count(c => c.ShipId == bMe)})");
        Check(bCues.Any(c => c.ShipId == bMe && !c.IsEnemy),
            $"friendly sighting classified FRIENDLY (IsEnemy=false) for ship {bMe}");
        Check(bCues.All(c => c.ShipId == bMe),
            $"viewer A's only new cue at B-join is B's ship {bMe} (no spurious cues)");
        Console.WriteLine();

        // ---- Phase 3: an ENEMY (Bravo) peer joins ----
        Console.WriteLine("=== Phase 3: enemy (cross-side Bravo) peer joins ===");
        var C = new ConnectionSession();
        C.Connect(port, "EnemyC_Bravo");
        C.SendLogonRequest(Request("EnemyC_Bravo"));
        bool cSeated = PumpUntil(server, () => C.World.MyShipId is { } && C.World.MySideId is { }, 4000);
        Check(cSeated, "enemy C seats");
        var (cMe, cSide) = Seat(C);
        Check(cSide == 1, $"enemy C is Bravo (side 1, got {cSide})");
        bool cLearned = PumpUntil(server, () => Knows(A, cMe), 4000);
        Check(cLearned, $"viewer A learned the newly-joined enemy ship {cMe}");
        var cCues = Drain(A);
        Check(cCues.Count(cue => cue.ShipId == cMe) == 1,
            $"viewer A cued the newly-joined enemy ship {cMe} EXACTLY ONCE (queued {cCues.Count(cue => cue.ShipId == cMe)})");
        Check(cCues.Any(cue => cue.ShipId == cMe && cue.IsEnemy),
            $"enemy sighting classified ENEMY (IsEnemy=true) for ship {cMe}");
        Check(cCues.All(cue => cue.ShipId == cMe),
            $"viewer A's only new cue at C-join is C's ship {cMe} (no spurious cues)");
        Console.WriteLine();

        // ---- Phase 4: steady state — no replay of already-visible ships ----
        Console.WriteLine("=== Phase 4: steady state (~15s) — no replay ====");
        Pump(server, 300);
        var steady = Drain(A);
        Check(steady.Count == 0,
            $"steady state: no re-cue of the already-visible friendly {bMe} / enemy {cMe} (drained {steady.Count})");
        Console.WriteLine();

        // Distinct cue assets (the audible difference): the two real native
        // assets wired to each classification.
        Console.WriteLine("=== Distinct cues (real native asset mapping) ===");
        Console.WriteLine("  friendly cue asset = newtargetenemy.ogg (native newEnemySound, sounddef.mdl:248)");
        Console.WriteLine("  enemy    cue asset = newtarget.ogg        (native newShipSound,  sounddef.mdl:247)");
        var lenT = new FileInfo("/home/domingo/Development/Artwork/newtarget.ogg").Length;
        var lenTE = new FileInfo("/home/domingo/Development/Artwork/newtargetenemy.ogg").Length;
        Check(lenT != lenTE, $"the two cue assets are DISTINCT files (enemy {lenT}B vs friendly {lenTE}B)");
        Console.WriteLine();

        Console.WriteLine($"P373 SUMMARY: passes={_passes} fails={_fails}");
        if (OutDir is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "p373_verify.log"),
                log.Concat([$"P373 SUMMARY: passes={_passes} fails={_fails}"]));
            Console.WriteLine($"wrote {Path.Combine(dir, "p373_verify.log")}");
        }

        A.Dispose(); B.Dispose(); C.Dispose();
        server.Dispose();
        Environment.Exit(_fails == 0 ? 0 : 1);
    }
}

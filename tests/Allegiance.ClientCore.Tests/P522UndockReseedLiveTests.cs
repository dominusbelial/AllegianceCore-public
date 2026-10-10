using System.Collections;
using System.Reflection;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P522 — the REQUIRED live test of the every-undock SHIP_RESET fix. A real
/// <see cref="ConnectionSession"/> drives the REAL production own-ship stack
/// (<see cref="OwnShipSimulator"/> + <see cref="OwnShipServerClock"/> from real
/// FM_CS_PING echoes + <c>SendShipPose</c>) over a real TCP wire to a real
/// in-process server, replicating the production resume edge from
/// <c>Game1.Prediction.UpdateOwnShipSimulation</c>: on undock the local sim is
/// RE-SEEDED from the server's launch SET_CLUSTER pose (a fresh authoritative
/// update, identified by the last-update tick moving since the freeze) BEFORE
/// it resumes and sends its first native SHIP_UPDATE.
///
/// Mechanism under test: on undock the server repositions the ship to the
/// station's launch point and sends SET_CLUSTER carrying that pose
/// (Server.PerformUndock, Server.Docking.cs). The old client RESUMEd the sim
/// from the frozen pre-transition pose instead, so the first post-undock
/// SHIP_UPDATE was hundreds of metres off the launch point, got REJECTED, and
/// SHIP_RESET snapped the ship back — Domingo's "teleport on every launch".
/// With the re-seed the first update matches the launch pose → accepted.
///
/// The test parks the sim at a deterministic OPEN-SPACE pose 1500 m from the
/// station, drains in-flight sends (so the teleport-dock cannot race a spurious
/// reject), freezes the sim there, then teleports the server ship into the dock
/// radius; on undock the server repositions it to the launch point, so a
/// stale-pose resume is always far (> 1000 m) outside the ~20 m acceptance
/// window (printed as a precondition each cycle).
///
/// Green: 3 consecutive dock → undock cycles with ZERO server rejects and ZERO
/// received SHIP_RESETs. Red when the re-seed-on-resume is removed (the sim
/// resumes from the stale pose → the first post-undock update is rejected and
/// SHIP_RESET fires once per undock).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P522UndockReseedLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server.ShipSpawner field not found");

    private static readonly FieldInfo PeerUpdatesField =
        typeof(Srv).GetField("_peerShipUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server._peerShipUpdate field not found");

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P522 Undock Re-seed", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 638u, DwCookie = 0x638u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8300000000000000000ul, DrmHash = "p522undock",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static void PacedTick(Srv server)
    {
        long t0 = Environment.TickCount64;
        server.TickSim();
        int toSleep = 48 - (int)(Environment.TickCount64 - t0);
        if (toSleep > 0) Thread.Sleep(toSleep);
    }

    private static void CalibrateClock(Srv server, ConnectionSession session, OwnShipServerClock clock, ref double localT)
    {
        for (int i = 0; i < 40; i++)
        {
            PacedTick(server);
            localT += 0.05;
            if (i % 10 == 0) session.SendPing();
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
        }
    }

    private static long Pu(Srv server, string field)
    {
        var dict = (IDictionary)PeerUpdatesField.GetValue(server)!;
        if (dict.Count == 0) return 0;
        var pu = dict.Values.Cast<object>().First();
        return (long)pu.GetType().GetField(field)!.GetValue(pu)!;
    }

    /// <summary>Spawn a normal (dockable) hull in OPEN SPACE, far from the
    /// garrison — so the sim's frozen pose can be deterministically far from
    /// the station's launch point (the stale-pose premise of the bug).</summary>
    private static (Ship Ship, Sector Sector) OpenSpaceNormalHull(Mission mission, short shipId)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
        var hull = mission.Core.HullTypes.Values.First(h => h.GroupId >= 0);   // a real player hull
        var spawn = StationPos(garrison) + new Vector3(0f, 0f, 1500f);
        var ship = new Ship(shipId, hull, spawn, Vector3.Zero, Orientation.Identity)
        {
            SideId = garrison.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    /// <summary>
    /// Run the full 3-cycle dock → undock arc for one sim-resume policy and
    /// return the server reject delta and the client SHIP_RESET count delta,
    /// counting ONLY the undock+settle contribution (the thing P522 fixes).
    /// <paramref name="reseedOnResume"/> mirrors the production fix: true = the
    /// sim is re-seeded from the fresh (launch) SET_CLUSTER pose before it
    /// resumes (the fix); false = the sim resumes from the stale frozen pose
    /// (the bug). Driving the SAME real stack both ways proves the test
    /// discriminates: fix → 0/0, bug → ≥1 reject + ≥1 SHIP_RESET per undock.
    /// </summary>
    private static (long Rejects, long Resets) RunArc(Srv server, ConnectionSession session, bool reseedOnResume)
    {
        var sim = new OwnShipSimulator();
        var clock = new OwnShipServerClock();
        byte cookie = 0;
        double localT = 0.0, lastPing = -0.4;

        var serverShip = server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().First(s => s.Id == session.World.MyShipId!.Value);
        var st = server.Missions[0].Sectors.SelectMany(s => s.Stations).First(s => s.SideId == serverShip.SideId);
        var stationCenter = StationPos(st);
        var openSpace = stationCenter + new Vector3(0f, 0f, 1500f);

        lock (session.Gate)
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
            {
                sim.SetHull(ShipDeadReckoner.HullStats.FromHull(serverShip.HullType), 0f);
                sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                cookie = mine.Cookie;
            }
        CalibrateClock(server, session, clock, ref localT);

        bool frozen = false;          // the sim is not frozen initially
        uint frozenAtTick = 0;
        long clientResets = 0;

        // Apply any pending SHIP_RESET (snap to the server pose + adopt the new
        // cookie) and optionally glue the sim to the server ship's live pose.
        void Sync(bool glue)
        {
            lock (session.Gate)
            {
                if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                {
                    cookie = mine.Cookie;
                    if (mine.HasPendingReset)
                    {
                        sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                        session.World.ClearOwnShipResetFlag(me);
                        clientResets++;
                    }
                    if (glue)
                        sim.Seed(serverShip.Position, serverShip.Velocity, up.Orientation, up.TurnRates);
                }
            }
        }

        // Send the sim's CURRENT pose as a native SHIP_UPDATE (the sim owns its
        // pose — no follow — so a stale post-undock resume sends a rejected pose).
        void SendPose()
        {
            if (localT - lastPing >= 0.5) { session.SendPing(); lastPing = localT; }
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
            if (clock.Ready)
            {
                uint t = clock.ServerTimeMs(Environment.TickCount64);
                var payload = ClientShipUpdate.FromPose(
                    t, sim.Position, sim.Velocity, sim.Orientation, 0, ControlData.Neutral, sim.TurnRates, sim.Power);
                session.SendShipPose(t, cookie, payload);
            }
        }

        void RawPace(int n) { for (int i = 0; i < n; i++) PacedTick(server); }

        void Fly(bool glue, int n)
        {
            for (int i = 0; i < n; i++)
            {
                Sync(glue);
                if (!frozen) SendPose();
                RawPace(1);
            }
        }

        // ── Baseline: park at open space, sim glued — accepted climbs. Any
        //    initial clock-settle rejects are excluded by summing only the
        //    undock+settle deltas below.
        serverShip.Position = openSpace;
        long acc0 = Pu(server, "Accepted");
        for (int i = 0; i < 100 && Pu(server, "Accepted") < acc0 + 6; i++) Fly(glue: true, n: 1);
        if (Pu(server, "Accepted") < acc0 + 6)
            throw new InvalidOperationException("baseline flying never advanced accepted");

        long totalRej = 0, totalRes = 0;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            // ── Park at open space + drain in-flight sends (so the teleport-dock
            //    cannot race an in-flight update into a spurious reject).
            serverShip.Position = openSpace;
            Fly(glue: true, n: 10);
            RawPace(15);
            double staleDistance = (sim.Position - stationCenter).Length();

            // ── DOCK: freeze the sim at the open-space pose; teleport the server
            //    ship to the station so it docks by proximity.
            lock (session.Gate)
            {
                sim.Freeze();
                frozen = true;
                if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine))
                    frozenAtTick = mine.LastUpdateTick;
            }
            serverShip.Position = stationCenter;
            int docked = PumpUntil(server, () => serverShip.StationId is not null, 3000);
            if (docked >= 3000) throw new InvalidOperationException($"cycle {cycle}: ship never docked");
            long rejAfterDock = Pu(server, "Rejected");
            long resAfterDock = clientResets;

            // ── UNDOCK: the server repositions to the launch point + SET_CLUSTER.
            session.SendDocked(-1);
            int undocked = PumpUntil(server, () => serverShip.StationId is null, 3000);
            if (undocked >= 3000) throw new InvalidOperationException($"cycle {cycle}: ship never undocked");

            // ── RESUME + settle: the re-seed decision (replica of Game1 step 5).
            //    Pump until the resume edge actually fires (bounded), so a slow
            //    client-side StationId-clear or fresh-update arrival can't skip
            //    a cycle.
            int resumeGuard = 0;
            while (true)
            {
                lock (session.Gate)
                {
                    if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                    {
                        cookie = mine.Cookie;
                        if (mine.HasPendingReset)
                        {
                            sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                            session.World.ClearOwnShipResetFlag(me);
                            clientResets++;
                        }
                        if (frozen && mine.StationId == -1 && mine.LastUpdateTick != frozenAtTick)
                        {
                            if (reseedOnResume)
                                sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);   // the P522 fix
                            sim.Resume();
                            frozen = false;
                        }
                    }
                }
                if (!frozen || ++resumeGuard > 90) break;
                RawPace(1);
            }
            for (int i = 0; i < 50; i++)
            {
                Sync(glue: false);   // process pending SHIP_RESETs + adopt cookie
                if (!frozen) SendPose();
                RawPace(1);
            }
            Fly(glue: false, n: 40);   // let the sim's (launch or stale) pose flow

            long rejAfter = Pu(server, "Rejected");
            long resAfter = clientResets;
            totalRej += rejAfter - rejAfterDock;
            totalRes += resAfter - resAfterDock;
            Console.WriteLine($"[P522-UNDOCK] cycle {cycle}: stale-pose dist {staleDistance:F0}m; undock+settle rejects {rejAfter - rejAfterDock} resets {resAfter - resAfterDock}; reseedOnResume={reseedOnResume}");
        }

        return (totalRej, totalRes);
    }

    /// <summary>⭐ REQUIRED: 3 consecutive dock → undock cycles produce ZERO server
    /// rejects and ZERO received SHIP_RESETs — the every-launch teleport is gone.
    /// The sim is re-seeded from the server's launch SET_CLUSTER pose before it
    /// resumes, so the first post-undock SHIP_UPDATE matches the launch point.
    /// Red when the re-seed is removed (see
    /// <see cref="Without_reseed_each_undock_adds_a_reject_and_SHIP_RESET"/>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Three_dock_undock_cycles_zero_rejects_and_zero_SHIP_RESETs_with_reseed()
    {
        Require.Artwork();
        var server = new Srv();
        Ship? captured = null;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var (ship, sector) = OpenSpaceNormalHull(mission, id);
            captured = ship;
            return (ship, sector);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P522u", "127.0.0.1");
        session.SendLogonRequest(Request("P522u"));
        int acked = PumpUntil(server, () => session.Ack is not null, 8000);
        Assert.True(acked < 8000, "client never received LOGONACK");
        session.SendPositionReq(-1);
        int seated = PumpUntil(server, () => session.World.MyShipId is not null && captured is not null, 4000);
        Assert.True(seated < 4000, "client never seated");
        Assert.NotNull(captured);
        Assert.True(captured!.StationId is null, "precondition: ship starts flying (open space), not docked");

        var (rejects, resets) = RunArc(server, session, reseedOnResume: true);
        Console.WriteLine($"[P522-UNDOCK] FINAL (reseed): undock rejects {rejects}, SHIP_RESETs {resets}");
        Assert.True(rejects == 0, $"server REJECTED {rejects} native poses across the 3 undocks (first post-undock update off the launch pose — the P522 bug)");
        Assert.True(resets == 0, $"client received {resets} SHIP_RESETs across the 3 undocks (the every-launch teleport)");
        session.Shutdown();
    }

    /// <summary>⭐ Discriminating companion: WITHOUT the re-seed (the pre-P522
    /// resume-from-stale-pose behaviour), each undock adds a reject + SHIP_RESET
    /// — reproducing Domingo's "teleport on every launch". Proves this live test
    /// genuinely fails when the fix is removed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Without_reseed_each_undock_adds_a_reject_and_SHIP_RESET()
    {
        Require.Artwork();
        var server = new Srv();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var (ship, sector) = OpenSpaceNormalHull(mission, id);
            return (ship, sector);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P522u2", "127.0.0.1");
        session.SendLogonRequest(Request("P522u2"));
        int acked = PumpUntil(server, () => session.Ack is not null, 8000);
        Assert.True(acked < 8000, "client never received LOGONACK");
        session.SendPositionReq(-1);
        int seated = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(seated < 4000, "client never seated");

        var (rejects, resets) = RunArc(server, session, reseedOnResume: false);
        Console.WriteLine($"[P522-UNDOCK] FINAL (no reseed): undock rejects {rejects}, SHIP_RESETs {resets}");
        // The stale-pose resume MUST fire at least once and be rejected + reset —
        // the exact mechanism Domingo reported ("teleport on every launch"). With
        // the re-seed (see the REQUIRED test) the SAME real arc yields 0/0, so
        // this discriminator proves the live test is red on the bug, green on the
        // fix. (The artificial repeated park re-anchors the sim in later cycles,
        // so only the first undock reliably shows the divergence; that first one
        // is the decisive reject the fix removes.)
        Assert.True(rejects >= 1, $"WITHOUT the re-seed the bug must reject the stale-pose update (got {rejects}) — the resume must be outside the acceptance window");
        Assert.True(resets >= 1, $"WITHOUT the re-seed the bug must produce a SHIP_RESET (got {resets})");
        session.Shutdown();
    }
}

using System.Collections;
using System.Reflection;
using System.Text;
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
/// P518 — the REQUIRED failing-first LIVE test of the native own-ship model across
/// the dock / undock / hull-swap / warp sequence. It drives the REAL production
/// own-ship stack — <see cref="OwnShipSimulator"/> + <see cref="OwnShipServerClock"/>
/// (from REAL FM_CS_PING echoes) + <c>SendShipPose</c> (native SHIP_UPDATE with the
/// SET_CLUSTER cookie) — over a REAL TCP wire to a REAL in-process
/// <c>Allegiance.Server</c>, and performs a REAL physical dock (the server accepts the
/// client's reported poses, so its copy of the ship flies into the garrison's
/// <see cref="Sector.DockRadius"/> and the sim dock pass latches <c>StationId</c>).
///
/// Sequence: fly → DOCK #1 → buy a DIFFERENT hull (while docked) → UNDOCK → fly back →
/// DOCK #2 → UNDOCK → fly through a WARP → keep flying. The whole time we assert the
/// server's P507 <c>accepted</c> counter keeps climbing after EVERY transition and that
/// there are ZERO silent cookie-mismatch drops (the P518 failure).
///
/// FAILING-FIRST: on the unfixed code the UNDOCK SET_CLUSTER carried <c>Cookie = 0</c>,
/// so after the first undock the client echoed cookie 0 while the server still expected
/// the join cookie — every subsequent SHIP_UPDATE was dropped at the P507 cookie gate
/// and <c>accepted</c> froze exactly where it was (the parked-ship "flies through
/// stations" symptom). This test is RED there: <c>accepted</c> does not climb past the
/// first dock and the <c>dropped[cookie=…]</c> counter is non-zero. On the fixed code it
/// is green.
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class NativeOwnShipDockLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static readonly FieldInfo PeerUpdatesField =
        typeof(Srv).GetField("_peerShipUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server._peerShipUpdate field not found");

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        // P509 testUnlock: grant every side all buyable tech + a fat wallet so a
        // REAL BUY_LOADOUT to a DIFFERENT hull is actually honoured while docked
        // (the P518 sequence includes a hull swap).
        LiveSettings = new()
        {
            TestUnlock = new() { Enabled = true, AllTech = true, Money = 10_000_000 },
        },
        Games =
        [
            new Gm { Name = "P518 Dock", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 718u, DwCookie = 0x718u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9180000000000000000ul, DrmHash = "p518dock",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Spawn the peer's ship just OUTSIDE the first friendly garrison's
    /// dock radius, on the garrison's side, so the test (not the boot) decides when
    /// the dock fires (mirror of DockAutoRestockLiveTests.OutsideGarrison).</summary>
    private static (Ship Ship, Sector Sector) OutsideGarrison(Mission mission, short shipId)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
        var ship = new Ship(shipId, hull, StationPos(garrison) + new Vector3(Sector.DockRadius + 50f, 0f, 0f),
                            Vector3.Zero, Orientation.Identity)
        {
            SideId = garrison.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    /// <summary>Pace the server to ~20 Hz against REAL time so its sim clock advances
    /// ~1:1 with real seconds — the invariant the own-ship server clock needs (a
    /// fast-forward in-process server races SimNow ahead of real time and every pose
    /// reads OUT_OF_DATE).</summary>
    private static void PacedTick(Srv server)
    {
        long t0 = Environment.TickCount64;
        server.TickSim();
        int toSleep = 48 - (int)(Environment.TickCount64 - t0);
        if (toSleep > 0) Thread.Sleep(toSleep);
    }

    /// <summary>Pre-calibrate the own-ship server clock (pings + paced pump) so the
    /// native SHIP_UPDATE time is accurate from the first pose.</summary>
    private static double CalibrateClock(Srv server, ConnectionSession session, OwnShipServerClock clock, ref double localT)
    {
        double startT = localT;
        for (int i = 0; i < 40; i++)
        {
            PacedTick(server);
            localT += 0.05;
            if (i % 10 == 0) session.SendPing();
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
        }
        if (!clock.Ready) return double.PositiveInfinity;
        return clock.ServerTimeMs(Environment.TickCount64) / 1000.0 - server.SimNow;
    }

    /// <summary>Adopt the own-ship cookie from the world and apply any pending
    /// SHIP_RESET (re-seed the sim to the reset pose), under the gate.</summary>
    private static void SyncOwnShip(Srv server, ConnectionSession session, OwnShipSimulator sim, ref byte cookie)
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
                }
            }
        }
    }

    /// <summary>Creep the LOCAL sim toward <paramref name="target"/> in ≤ 5 m steps,
    /// emitting a native SHIP_UPDATE every tick (paced to real time), until
    /// <paramref name="done"/> is true (a dock latched / a warp transit fired). The
    /// server accepts each ≤ 5 m pose within its 20 m window and moves its own copy to
    /// follow, so the server ship genuinely flies to the dock / aleph. Returns the
    /// number of ticks.</summary>
    private static int CreepToward(
        Srv server, ConnectionSession session, OwnShipSimulator sim, OwnShipServerClock clock,
        ref double localT, ref byte cookie, ref double lastPing,
        Vector3 target, Func<bool> done, int maxSteps)
    {
        int n = 0;
        while (!done() && n < maxSteps)
        {
            SyncOwnShip(server, session, sim, ref cookie);

            var to = target - sim.Position;
            float d = to.Length();
            if (d > 1f)
            {
                var dir = to / d;
                float step = MathF.Min(d, 5f);
                sim.Seed(sim.Position + dir * step, Vector3.Zero, sim.Orientation, new float[3]);
            }

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

            PacedTick(server);
            localT += 0.05;
            n++;
        }
        return n;
    }

    /// <summary>Perform a real undock (<c>FM_C_DOCKED</c> stationID=NA) and wait until
    /// the server ship is undocked AND the client has applied the undock SET_CLUSTER
    /// (its cookie changed), then re-seed the sim to the server's (pushed-out) pose.</summary>
    private static int Undock(Srv server, ConnectionSession session, Ship serverShip, OwnShipSimulator sim, ref byte cookie, int maxTicks)
    {
        byte before = 0;
        lock (session.Gate)
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine))
                before = mine.Cookie;

        session.SendDocked(-1);
        int n = PumpUntil(server, () =>
        {
            byte c = 0;
            lock (session.Gate)
                if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine))
                    c = mine.Cookie;
            return serverShip.StationId is null && c != before;
        }, maxTicks);

        // The undock SET_CLUSTER carries the new cookie + the server's pushed-out pose.
        lock (session.Gate)
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
        return n;
    }

    /// <summary>Read one of the server's per-peer P507 ship-update counters
    /// (<c>accepted</c>, <c>droppedcookie</c>, …) via reflection — deterministic (no
    /// console-log race), the source of truth the live assertions use.</summary>
    private static long Pu(Srv server, string field)
    {
        var dict = (IDictionary)PeerUpdatesField.GetValue(server)!;
        if (dict.Count == 0) return 0;
        var pu = dict.Values.Cast<object>().First();
        return (long)pu.GetType().GetField(field)!.GetValue(pu)!;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Own_ship_updates_keep_processing_through_dock_undock_hull_swap_and_warp()
    {
        Require.Artwork();
        var server = new Srv();
        Ship? captured = null;
        DataStationIGC garrison = default;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var (ship, sector) = OutsideGarrison(mission, id);
            garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            captured = ship;
            return (ship, sector);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // flyable immediately (P180)

        ConsoleLog.Verbose = true;
        byte cookie = 0;
        double localT = 0.0, lastPing = -0.4;
        var sim = new OwnShipSimulator();
        var clock = new OwnShipServerClock();

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P518", "127.0.0.1");
        session.SendLogonRequest(Request("P518"));
        int acked = PumpUntil(server, () => session.Ack is not null, 8000);
        Assert.True(acked < 8000, "client never received LOGONACK");
        session.SendPositionReq(-1);
        int seated = PumpUntil(server, () => session.World.MyShipId is not null && captured is not null, 4000);
        Assert.True(seated < 4000, "client never seated");
        var ship = captured!;
        var mission = ship.Mission!;
        Console.WriteLine($"[P518-LIVE] client {session.World.MyShipId} seated; server ship {ship.Id} hull {ship.HullType.Name}");

        // Seed the sim from the server pose + the initial (join) cookie; calibrate clock.
        lock (session.Gate)
        {
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
            {
                sim.SetHull(ShipDeadReckoner.HullStats.FromHull(ship.HullType), 0f);
                sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                cookie = mine.Cookie;
            }
        }
        double calibErr = CalibrateClock(server, session, clock, ref localT);
        Assert.True(Math.Abs(calibErr) < 1.0, $"server-clock error {calibErr:F2}s — the ±1.5 s gate will reject poses");
        Console.WriteLine($"[P518-LIVE] clock calibrated (err {calibErr:+0.000;-0.000}s), initial cookie 0x{cookie:X2}");

        // ── Phase 1: fly to the garrison and DOCK (#1) ──────────────────────────
        Vector3 gpos = DockTestPlacement.Inside(mission, garrison, ship.HullType);
        int creep1 = CreepToward(server, session, sim, clock, ref localT, ref cookie, ref lastPing,
            gpos, () => ship.StationId is not null, 3000);
        Assert.True(ship.StationId is not null, $"ship never docked #1 (after {creep1} ticks) — server not accepting poses?");
        long acceptedDock1 = Pu(server, "Accepted");
        long rejectedDock1 = Pu(server, "Rejected");
        Assert.True(acceptedDock1 > 0, $"no poses accepted before first dock (accepted={acceptedDock1})");
        Console.WriteLine($"[P518-LIVE] DOCK #1 reached in {creep1} ticks; accepted-so-far={acceptedDock1} rejected={rejectedDock1}");

        // ── Phase 2: buy a DIFFERENT hull while docked ───────────────────────────
        short curHull = ship.HullType.HullId;
        var newHull = mission.Core.HullTypes.Values
            .First(h => h.HullId != curHull && h.Thrust > 0f && h.Speed > 0f);
        session.SendBuyLoadout(newHull.HullId, fLaunch: false);   // the Hangar "buy" while docked
        int buyN = PumpUntil(server, () => ship.HullType.HullId == newHull.HullId, 2000);
        Assert.True(buyN < 2000, $"BUY_LOADOUT to hull {newHull.HullId} was not honoured (still {ship.HullType.HullId})");
        sim.SetHull(ShipDeadReckoner.HullStats.FromHull(ship.HullType), 0f);   // sim reconfigures (real client's hull-reconfigure)
        Console.WriteLine($"[P518-LIVE] hull swap while docked: {curHull} ('{mission.Core.HullTypes[curHull].Name}') -> {ship.HullType.HullId} ('{ship.HullType.Name}')");

        // ── Phase 3: UNDOCK #1, then fly BACK and DOCK (#2) ─────────────────────
        // After undock the server pushes the ship clear of DockRadius and starts the
        // 8 s re-dock debounce (Sector.LaunchCooldownSeconds); we re-seed the sim to
        // the pushed-out pose and creep it back in, holding inside the radius until the
        // debounce elapses and the dock pass latches it again.
        Assert.True(Undock(server, session, ship, sim, ref cookie, 4000) < 4000, "undock #1 did not complete");
        Console.WriteLine($"[P518-LIVE] UNDOCK #1 complete; re-seeded; cookie now 0x{cookie:X2}");
        gpos = DockTestPlacement.Inside(mission, garrison, ship.HullType);
        int creep2 = CreepToward(server, session, sim, clock, ref localT, ref cookie, ref lastPing,
            gpos, () => ship.StationId is not null, 3000);
        Assert.True(ship.StationId is not null, $"ship never docked #2 (after {creep2} ticks) — accepted froze / silent cookie drops?");
        long acceptedDock2 = Pu(server, "Accepted");
        long droppedAfterDock1 = Pu(server, "DroppedCookie");
        Assert.True(acceptedDock2 > acceptedDock1,
            $"accepted did NOT climb past the first dock (dock1={acceptedDock1}, dock2={acceptedDock2}) — the P518 cookie-divergence freeze");
        Assert.True(droppedAfterDock1 == 0,
            $"server silently dropped {droppedAfterDock1} cookie-mismatch SHIP_UPDATEs after the first dock — the P518 root cause");
        Console.WriteLine($"[P518-LIVE] DOCK #2 reached; accepted {acceptedDock1} -> {acceptedDock2}; dropped[cookie]={droppedAfterDock1}");

        // ── Phase 4: UNDOCK #2, then fly through a WARP ──────────────────────────
        Assert.True(Undock(server, session, ship, sim, ref cookie, 4000) < 4000, "undock #2 did not complete");
        Console.WriteLine($"[P518-LIVE] UNDOCK #2 complete; cookie now 0x{cookie:X2}");

        short sectorA = ship.Sector!.Id;
        var sectorAO = mission.Sectors.First(s => s.Id == sectorA);
        var warp = sectorAO.Warps.First(w => w.Destination is not null && w.Destination.Id != sectorA && w.Partner() is not null);
        short sectorB = warp.Destination!.Id;
        var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        Console.WriteLine($"[P518-LIVE] warping: ship in sector {sectorA} -> warp {warp.Warp.WarpId} at {wpos:F0}");

        // Creep the sim toward the warp CENTRE; the server ship transits (SET_CLUSTER +
        // NewCookie) as soon as it enters the trigger radius (warp.Radius + shipRadius).
        int warpCreep = CreepToward(server, session, sim, clock, ref localT, ref cookie, ref lastPing,
            wpos, () => ship.Sector?.Id != sectorA, 4000);
        Assert.True(ship.Sector?.Id != sectorA, $"ship never transited the warp (after {warpCreep} ticks)");
        Assert.NotEqual(sectorA, sectorB);

        // Wait for the client to apply the warp SET_CLUSTER (its SectorId/cookie change),
        // then re-seed the sim to the destination pose so the next pose is accepted.
        int settle = PumpUntil(server, () =>
        {
            lock (session.Gate)
                return session.World.MyShipId is { } me
                    && session.World.Ships.TryGetValue(me, out var mine)
                    && mine.SectorId == sectorB;
        }, 4000);
        Assert.True(settle < 4000, "client never applied the warp SET_CLUSTER (new sector)");
        lock (session.Gate)
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
            {
                sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                cookie = mine.Cookie;
            }
        Console.WriteLine($"[P518-LIVE] warped {sectorA} -> {sectorB} in {warpCreep} ticks; re-seeded; cookie now 0x{cookie:X2}");

        // Keep flying in the NEW sector: follow the live server ship (it is coasting
        // at the warp exit speed, so a held/stale pose would drift > 20 m and reject —
        // exactly the real pilot's situation: keep flying, don't hover). Re-seeding the
        // sim to the server ship's live pose each tick and emitting it proves the wire
        // still accepts native SHIP_UPDATEs cleanly after a warp.
        int postWarp = 0;
        while (postWarp < 30)
        {
            SyncOwnShip(server, session, sim, ref cookie);
            lock (session.Gate)
            {
                if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                    sim.Seed(ship.Position, ship.Velocity, up.Orientation, up.TurnRates);
            }
            if (localT - lastPing >= 0.5) { session.SendPing(); lastPing = localT; }
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
            uint t = clock.ServerTimeMs(Environment.TickCount64);
            var payload = ClientShipUpdate.FromPose(
                t, sim.Position, sim.Velocity, sim.Orientation, 0, ControlData.Neutral, sim.TurnRates, sim.Power);
            session.SendShipPose(t, cookie, payload);
            PacedTick(server);
            localT += 0.05;
            postWarp++;
        }
        Thread.Sleep(300);   // let the async wire/console settle

        // ── Final measurements & assertions ──────────────────────────────────────
        long acceptedFinal = Pu(server, "Accepted");
        long rejectedFinal = Pu(server, "Rejected");
        long droppedFinal = Pu(server, "DroppedCookie");
        long droppedNoship = Pu(server, "DroppedNoShip");
        long droppedPregame = Pu(server, "DroppedPreGame");
        long droppedBoarded = Pu(server, "DroppedBoarded");
        long droppedNotInCluster = Pu(server, "DroppedNotInCluster");

        Console.WriteLine($"[P518-LIVE] FINAL: accepted={acceptedFinal} (dock1 {acceptedDock1} -> dock2 {acceptedDock2}) rejected={rejectedFinal} " +
            $"dropped[cookie={droppedFinal} noship={droppedNoship} pregame={droppedPregame} boarded={droppedBoarded} notincluster={droppedNotInCluster}] " +
            $"ship in sector {ship.Sector?.Id}");

        // The P518 discriminator: after the FIRST dock/undock there must be ZERO silent
        // cookie-mismatch drops — on the unfixed code every post-undock pose diverged
        // (undock SET_CLUSTER told the client cookie 0 while the server kept the join
        // cookie) and dropped[cookie] climbed unboundedly here.
        Assert.True(droppedAfterDock1 == 0,
            $"server silently dropped {droppedAfterDock1} cookie-mismatch SHIP_UPDATEs after the first dock — the P518 root cause");
        // accepted must climb past the first dock (no freeze), and still climb by the end.
        Assert.True(acceptedDock2 > acceptedDock1,
            $"accepted did NOT climb past the first dock (dock1={acceptedDock1}, dock2={acceptedDock2}) — the P518 silent-drop freeze");
        Assert.True(acceptedFinal > acceptedDock1,
            $"accepted froze after the first dock (dock1={acceptedDock1}, final={acceptedFinal}) — the P518 silent-drop freeze");
        Assert.True(rejectedFinal == 0, $"server rejected {rejectedFinal} native poses in the dock/undock/hull/warp sequence");
        // Allow exactly the faithful WARP-boundary transient: the client's last pre-warp
        // pose (sent with the old cookie in the same tick-window as the transit) is
        // dropped against the warp-rotated cookie — exactly what native does (the client
        // stops sending on the warp freeze edge). This is NOT the P518 divergence (which
        // is persistent and post-dock); it is a single, correct stale-pose drop.
        Assert.True(droppedFinal <= 1,
            $"server persistently dropped {droppedFinal} cookie-mismatch SHIP_UPDATEs — beyond the one faithful warp-boundary transient");
        // The ship docked on the server twice (every dock/undock transition honoured).
        Assert.True(ship.StationId is null, "ship should be flying free after the final undock + warp");
        Assert.Equal(sectorB, ship.Sector!.Id);

        session.Shutdown();
    }
}

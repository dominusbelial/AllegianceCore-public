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
/// P508 — the REQUIRED LIVE test of the native own-ship model (client side): a REAL
/// <see cref="ConnectionSession"/> (the production client session) drives the REAL
/// production own-ship stack — <see cref="OwnShipSimulator"/> (flies the ship locally),
/// <see cref="OwnShipServerClock"/> (server-converted time from REAL FM_CS_PING echoes),
/// <see cref="ShipUpdateCadence"/> (0.15 s), and <c>SendShipPose</c> (native SHIP_UPDATE
/// with the SET_CLUSTER cookie) — over a REAL TCP wire to a REAL in-process
/// <c>Allegiance.Server</c> (P507). The server's P507 <c>[ctrlak]</c> log is captured to
/// measure the actual accepted / rejected / out-of-date / invalid counts — exactly the
/// stats Domingo asked to report.
///
/// Maneuvers flown: cruise, hard turn, and the P505 case — a throttle cut to −0.8 then
/// decel. Target: ~0 rejections in normal flight (the client sim tracks the server's own
/// simulation within the 20 m accept window, and the time/cookie gates pass). Then an
/// induced 500 m divergence is used to prove the SHIP_RESET recovery path works live
/// (snap to server pose + new cookie).
/// </summary>
[Collection("LiveNetwork")]
public sealed class NativeOwnShipFlightLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly Vector3 Spawn = new(500f, 620f, 830f);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P508 Native Flight", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 991u, DwCookie = 0x991u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9980000000000000000ul, DrmHash = "p508",
    };

    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static int CountMark(string text, string mark)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(mark, idx, StringComparison.Ordinal)) >= 0) { count++; idx += mark.Length; }
        return count;
    }

    /// <summary>Tick the server paced to ~20 Hz against REAL time, so its sim clock
    /// advances ~1:1 with real seconds exactly like a production server — the invariant
    /// the P508 own-ship server clock relies on (a fast-forward in-process server races
    /// SimNow ahead of real time and every pose reads OUT_OF_DATE).</summary>
    private static void PacedTick(Srv server)
    {
        long t0 = Environment.TickCount64;
        server.TickSim();
        int toSleep = 48 - (int)(Environment.TickCount64 - t0);
        if (toSleep > 0) Thread.Sleep(toSleep);
    }

    /// <summary>Pre-calibrate the own-ship server clock (pings + a paced pump) so the
    /// native SHIP_UPDATE time is accurate from the first pose — no warm-up of
    /// out-of-date poses. Returns the clock's error vs the server's real SimNow at the
    /// end (sim-seconds), a strong live signal the time gate will pass.</summary>
    private static double CalibrateClock(Srv server, ConnectionSession session, OwnShipServerClock clock, double localT0, ref double localT)
    {
        localT = localT0;
        for (int i = 0; i < 40; i++)                       // ~2 s of paced ticks
        {
            PacedTick(server);
            localT += 0.05;
            if (i % 10 == 0) session.SendPing();
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
        }
        if (!clock.Ready) return double.PositiveInfinity;
        double estSec = clock.ServerTimeMs(Environment.TickCount64) / 1000.0;
        return estSec - server.SimNow;                      // sim-seconds error
    }

    /// <summary>One flight phase of <paramref name="steps"/> sim ticks: tick the server
    /// in lockstep with the local sim (both advance 0.05 s), feed the clock from pings,
    /// fly the sim, and emit native SHIP_UPDATEs at the cadence — the production stack.</summary>
    private static void FlyPhase(
        Srv server, ConnectionSession session, Ship serverShip,
        OwnShipSimulator sim, OwnShipServerClock clock, ShipUpdateCadence cadence,
        Func<int, ControlData> controlsFor, int startStep, int steps,
        ref double localT, ref byte cookie, ref long resetCount, ref bool firstSeed)
    {
        double lastPing = localT - 0.4;
        for (int i = 0; i < steps; i++)
        {
            PacedTick(server);              // server sim advances 0.05 s paced to real time
            localT += 0.05;                 // synthetic local clock, locked to the sim

            // ── production clock: ping + drain echoes (queried at the REAL clock) ──
            if (localT - lastPing >= 0.5) { session.SendPing(); lastPing = localT; }
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);

            var controls = controlsFor(startStep + i);

            // ── own-ship state under the gate (production ClientWorld) ──
            lock (session.Gate)
            {
                if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                {
                    cookie = mine.Cookie;
                    if (!firstSeed)
                    {
                        sim.SetHull(ShipDeadReckoner.HullStats.FromHull(serverShip.HullType), 0f);
                        sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                        firstSeed = true;
                    }
                    if (mine.HasPendingReset)
                    {
                        sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                        session.World.ClearOwnShipResetFlag(me);
                        resetCount++;
                        Console.WriteLine($"[P508-LIVE] SHIP_RESET #{resetCount} received — re-seeded to {up.Position}");
                    }
                }
            }

            // ── fly the sim locally (in lockstep with the server tick), send native ──
            sim.Step(0.05f, controls, 0);
            if (cadence.Update(localT) && clock.Ready)
            {
                uint t = clock.ServerTimeMs(Environment.TickCount64);   // REAL clock for the wire time
                var payload = ClientShipUpdate.FromPose(
                    t, sim.Position, sim.Velocity, sim.Orientation, 0, controls, sim.TurnRates, sim.Power);
                session.SendShipPose(t, cookie, payload);
            }
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_own_ship_flight_accepts_poses_in_normal_flight_and_recovers_from_reset()
    {
        Require.Artwork();
        var server = new Srv();
        Ship? captured = null;
        // ShipSpawner is internal to Allegiance.Server (ClientCore.Tests has no
        // InternalsVisibleTo) — set via reflection, mirroring TurretGunnerFireLiveTests.
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var hull = core.HullTypes.Values.First(h => h.Thrust > 0f && h.Speed > 0f);   // DataHullTypeIGC is a struct
            var sector = mission.Sectors[0];
            var ship = new Ship(id, hull, Spawn, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            captured = ship;
            return (ship, sector);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // flyable immediately (P180)

        // Capture the server's P507 [ctrlak] log over the whole flight (it writes to
        // Console via ConsoleLog when Verbose is on).
        ConsoleLog.Verbose = true;
        var log = new StringBuilder();
        var origOut = Console.Out;
        Console.SetOut(new StringWriter(log));

        long resetCount = 0;
        bool firstSeed = false;
        byte cookie = 0;
        double localT = 0.0;
        var sim = new OwnShipSimulator();
        var clock = new OwnShipServerClock();
        var cadence = new ShipUpdateCadence();
        try
        {
            using var session = new ConnectionSession();
            session.Connect(server.BoundPort, "P508", "127.0.0.1");
            session.SendLogonRequest(Request("P508"));
            // The in-process server must be pumped for the logon handshake to complete.
            int acked = PumpUntil(server, () => session.Ack is not null, 8000);
            Assert.True(acked < 8000, "client never received LOGONACK");
            session.SendPositionReq(-1);                                   // auto-pick a side

            int seated = PumpUntil(server, () => session.World.MyShipId is not null && captured is not null, 4000);
            Assert.True(seated < 4000, "client never seated (no MyShipId / server ship)");
            var serverShip = captured!;
            Console.WriteLine($"[P508-LIVE] client {session.World.MyShipId} seated; server ship {serverShip.Id} hull {serverShip.HullType.Name}");

            // Pre-calibrate the server clock (paced to real time) so the first pose's
            // time is already inside the gate — no warm-up of OUT_OF_DATE poses.
            double calibErr = CalibrateClock(server, session, clock, localT, ref localT);
            Console.WriteLine($"[P508-LIVE] clock calibrated: server-time error vs SimNow = {calibErr:+0.000;-0.000}s");
            Assert.True(Math.Abs(calibErr) < 1.0, $"own-ship server clock is off by {calibErr:F2}s — the ±1.5 s gate will reject poses");

            // ── Phase A: straight cruise (throttle 0.7) ──
            FlyPhase(server, session, serverShip, sim, clock, cadence, _ => new ControlData(0f, 0f, 0f, 0.70f), 0, 120, ref localT, ref cookie, ref resetCount, ref firstSeed);
            // ── Phase B: hard turn (yaw 1 while throttled) ──
            FlyPhase(server, session, serverShip, sim, clock, cadence, _ => new ControlData(1f, 0f, 0f, 0.55f), 120, 120, ref localT, ref cookie, ref resetCount, ref firstSeed);
            // ── Phase C: throttle cut to −0.8 then decel (the P505 case) ──
            FlyPhase(server, session, serverShip, sim, clock, cadence, _ => new ControlData(0f, 0f, 0f, -0.80f), 240, 120, ref localT, ref cookie, ref resetCount, ref firstSeed);

            // Let the async console-log drain finish, then read the captured server log.
            Thread.Sleep(300);
            Console.SetOut(origOut);
            string capturedLog = log.ToString();

            // ── report the server's acceptance stats (P507's [ctrlak] log) ──
            int accepted = CountMark(capturedLog, ": ACCEPTED");
            int rejected = CountMark(capturedLog, ": REJECTED");
            int outOfDate = CountMark(capturedLog, "OUT_OF_DATE");
            int invalid = CountMark(capturedLog, "INVALID position");
            Console.WriteLine($"[P508-LIVE] server [ctrlak] over normal flight: accepted={accepted} rejected={rejected} out_of_date={outOfDate} invalid={invalid}; client SHIP_RESETs={resetCount}; clock-error={calibErr:+0.000;-0.000}s");

            // Normal flight must be ~0 rejections (the target): no SHIP_RESET reached
            // the client, the server logged no rejections, and it accepted + applied
            // our poses (its ship followed the sim within the 20 m window).
            Assert.True(resetCount == 0, $"normal flight produced {resetCount} SHIP_RESET(s) — poses rejected");
            Assert.True(rejected == 0, $"server rejected {rejected} native poses in normal flight");
            Assert.True(accepted > 0, "server never accepted any native pose in normal flight");
            Assert.True(Dist(serverShip.Position, sim.Position) < 25f,
                $"server ship {serverShip.Position:F0} diverged from client sim {sim.Position:F0} (dist {Dist(serverShip.Position, sim.Position):F1} m)");

            // ── Induced divergence → SHIP_RESET recovery (live proof of the reset path) ──
            long resetsBefore = resetCount;
            sim.Seed(serverShip.Position + new Vector3(0f, 0f, 500f), serverShip.Velocity, serverShip.Orientation, serverShip.GetTurnRates(new float[3]));
            for (int i = 0; i < 150 && resetCount == resetsBefore; i++)
            {
                PacedTick(server); localT += 0.05;
                foreach (var (serverMs, recvMs) in session.TakePingEchoes()) clock.AddSample(serverMs, recvMs);
                lock (session.Gate)
                {
                    if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                    {
                        if (mine.HasPendingReset)
                        {
                            sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                            session.World.ClearOwnShipResetFlag(me);
                            resetCount++;
                            Console.WriteLine($"[P508-LIVE] SHIP_RESET #{resetCount} — re-synced to {up.Position}");
                        }
                    }
                }
                sim.Step(0.05f, ControlData.Neutral, 0);
                if (cadence.Update(localT) && clock.Ready)
                {
                    uint t = clock.ServerTimeMs(Environment.TickCount64);
                    var payload = ClientShipUpdate.FromPose(t, sim.Position, sim.Velocity, sim.Orientation, 0, ControlData.Neutral, sim.TurnRates, sim.Power);
                    session.SendShipPose(t, cookie, payload);
                }
            }
            Thread.Sleep(150);
            Console.WriteLine($"[P508-LIVE] induced-divergence SHIP_RESETs: {resetCount - resetsBefore}");
            Assert.True(resetCount > resetsBefore, "induced 500 m divergence did NOT produce a SHIP_RESET — reset recovery broken");
            Assert.True(Dist(serverShip.Position, sim.Position) < 5f,
                $"after SHIP_RESET the sim did not re-sync to the server pose (dist {Dist(serverShip.Position, sim.Position):F1} m)");

            session.Shutdown();
        }
        finally
        {
            Thread.Sleep(200);
            Console.SetOut(origOut);
            ConsoleLog.Verbose = false;
            server.Dispose();
        }
    }
}

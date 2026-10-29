using System.Reflection;
using System.Text;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;
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

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P517 — the REQUIRED LIVE test proving the turret-gunner ACTIVE_TURRET_UPDATE
/// timestamp now comes from the <b>PING-fed server clock</b> and is therefore
/// <b>accepted</b> by the server's ±1.5 s staleness gate (not dropped as stale).
///
/// <para>Scenario (P464's live turret setup, driven by real <see
/// cref="ConnectionSession"/>s): a boarder gunner crews a docked carrier's
/// turret over the real wire, both launch, and while the fire button is held the
/// production clock path emits <c>ACTIVE_TURRET_UPDATE(99)</c> frames. The update's
/// <c>time</c> is stamped from the shared <see cref="OwnShipServerClock"/>
/// (fed by <b>real FM_CS_PING echoes</b>, <c>timeServer = SimNow·1000</c> ms),
/// converted to the server sim-<b>seconds</b> the gate compares directly against
/// <c>Ship.LastUpdate</c>. The server's <c>[turret]</c> accept/reject log is
/// captured to report the actual accepted vs rejected counts — the numbers
/// Domingo asked for, and the failing-first gate (before P517 the production
/// path fed the tick-index pose slot into a seconds-estimator, stamping ~20× the
/// real server time and dropping <b>every</b> ACTIVE as stale, so
/// <c>accepted &gt; 0</c> was false).</para>
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class TurretServerClockLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private const short CarrierHullId = 792;   // turret-capable carrier, slots [2,6), price 4000
    private const short BoarderHullId = 504;   // plain 500-credit boarder hull, slots [2,4)
    private const int PumpMax = 2500;

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
            new Gm { Name = "P517 Turret Clock", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9950000000000000000ul, DrmHash = "p517",
    };

    private static Vector3 StationPos(DataStationIGC st) => new(st.Position[0], st.Position[1], st.Position[2]);

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
    /// advances ~1:1 with real seconds — the invariant the PING server clock relies
    /// on (a fast-forward in-process server races SimNow ahead of real time and every
    /// update reads OUT_OF_DATE).</summary>
    private static void PacedTick(Srv server)
    {
        long t0 = Environment.TickCount64;
        server.TickSim();
        int toSleep = 48 - (int)(Environment.TickCount64 - t0);
        if (toSleep > 0) Thread.Sleep(toSleep);
    }

    private sealed class BoardSpawn
    {
        public Ship Carrier = null!, Boarder = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
    }

    private static BoardSpawn InjectBoardSpawn(Srv server)
    {
        var sp = new BoardSpawn();
        int n = 0;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var sideId = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0).SideId;
            var station = P550MonoTestDockHome.CapLandHome(mission, sideId);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[n == 0 ? CarrierHullId : BoarderHullId];
            var ship = new Ship(id, hull, StationPos(station) + new Vector3(25f + n * 10f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            if (n == 0)
            {
                // A dispersion-0 projectile weapon so the fired round flies EXACTLY
                // along the gunner's orientation (mirrors TurretGunnerFiringTests).
                var weapon = core.PartTypes.Values
                    .First(p => p.EquipmentType == EquipmentType.Weapon
                                && p.Type is DataPartTypeIGC.Weapon w && w.Dispersion == 0f);
                ship.MountPart(weapon, 1, (byte)hull.MaxFixedWeapons, 255);
            }
            if (n == 0) sp.Carrier = ship; else sp.Boarder = ship;
            sp.Mission = mission;
            sp.Sector = sector;
            n++;
            return (ship, sector);
        });
        return sp;
    }

    private static ConnectionSession ConnectAndSeat(Srv server, string name, ushort port)
    {
        var s = new ConnectionSession();
        s.Connect(port, name, "127.0.0.1");
        s.SendLogonRequest(Request(name));
        int acked = PumpUntil(server, () => s.Ack is not null, 8000);
        if (acked >= 8000) throw new InvalidOperationException($"{name}: never received LOGONACK");
        s.SendPositionReq(-1);                               // auto-pick a side
        return s;
    }

    /// <summary>Prime the boarder's shared server clock from REAL FM_CS_PING echoes
    /// (paced to real time so the sim clock runs 1:1), so ACTIVE times are accurate
    /// from the first frame. Returns the estimated vs actual sim-seconds error at the
    /// end — a strong live signal the time gate will pass.</summary>
    private static double CalibrateClock(Srv server, ConnectionSession boarder, OwnShipServerClock clock, ref double localT)
    {
        localT = 0.0;
        for (int i = 0; i < 40; i++)
        {
            PacedTick(server);
            localT += 0.050;
            if (i % 10 == 0) boarder.SendPing();
            foreach (var (serverMs, recvMs) in boarder.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
        }
        if (!clock.Ready) return double.PositiveInfinity;
        double estSec = clock.ServerTimeMs(Environment.TickCount64) / 1000.0;
        return estSec - server.SimNow;                       // sim-seconds error
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Turret_gunner_updates_stamped_from_the_ping_clock_are_accepted_and_fire()
    {
        Require.Artwork();
        var server = new Srv();
        var sp = InjectBoardSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // bypass the ready-up lobby
        ushort port = server.BoundPort;

        // Capture the server's [turret] accept/reject log over the whole firing hold.
        ConsoleLog.Verbose = true;
        var log = new StringBuilder();
        var origOut = Console.Out;
        Console.SetOut(new StringWriter(log));

        try
        {
            using var carrier = ConnectAndSeat(server, "CARRIER", port);
            using var boarder = ConnectAndSeat(server, "BOARDER", port);

            // Identify carrier vs boarder by their assigned server ship id.
            int seated = PumpUntil(server,
                () => carrier.World.MyShipId is not null && boarder.World.MyShipId is not null
                      && (carrier.World.MyShipId == sp.Carrier.Id || carrier.World.MyShipId == sp.Boarder.Id), 4000);
            Assert.True(seated < 4000, "carrier/boarder not seated");

            ConnectionSession carrierSess, boarderSess;
            if (carrier.World.MyShipId == sp.Carrier.Id) { carrierSess = carrier; boarderSess = boarder; }
            else { carrierSess = boarder; boarderSess = carrier; }

            // Settle the docked latch, then board the gunner on the carrier.
            int dock = PumpUntil(server, () => sp.Carrier.StationId is not null && sp.Boarder.StationId is not null, 800);
            Assert.True(dock < 800, "carrier/boarder did not dock");
            boarderSess.SendBoardShip(sp.Carrier.Id);
            int ok = PumpUntil(server, () => sp.Boarder.ParentShip is not null, PumpMax);
            Assert.True(ok < PumpMax, "board did not set ParentShip");

            // Launch: the carrier (with the gunner aboard) undocks.
            carrierSess.SendDocked();
            int launched = PumpUntil(server, () => sp.Carrier.StationId is null, PumpMax);
            Assert.True(launched < PumpMax, "carrier did not undock with the gunner aboard");
            var turretWeapon = sp.Carrier.Weapons.First(w => w.MountId == sp.Boarder.TurretId!.Value);
            Assert.Same(sp.Boarder, turretWeapon.Gunner);

            // ── prime the boarder's PING server clock (paced to real time) ──
            var clock = new OwnShipServerClock();
            double localT = 0.0;
            double calibErr = CalibrateClock(server, boarderSess, clock, ref localT);
            Console.WriteLine($"[P517-LIVE] clock calibrated: server-time error vs SimNow = {calibErr:+0.000;-0.000}s");
            Assert.True(Math.Abs(calibErr) < 1.0, $"turret server clock off by {calibErr:F2}s — the ±1.5 s gate will reject");

            // ── drive the production fire hold: ACTIVE while firing ──
            var aim = new Orientation();
            aim.Yaw(0.6f);                                     // distinctly off the carrier's −z
            var cadence = new TurretGunnerCadence();
            int activeSent = 0, inactiveSent = 0;
            var projDirections = new List<Vector3>();
            double lastPing = localT - 0.4;

            for (int step = 0; step < 220; step++)             // ~11 s @20 ups
            {
                sp.Carrier.Velocity = Vector3.Zero;            // parked → clean muzzle velocity
                PacedTick(server);
                localT += 0.050;

                // production clock: ping + drain real echoes (queried at REAL clock)
                if (localT - lastPing >= 0.5) { boarderSess.SendPing(); lastPing = localT; }
                foreach (var (serverMs, recvMs) in boarderSess.TakePingEchoes())
                    clock.AddSample(serverMs, recvMs);

                Orientation integrated = aim.Copy();
                TurretLook.Integrate(integrated, ControlData.Neutral, 0.025f);

                bool fireHeld = step >= 25;
                switch (cadence.Update(localT, fireHeld, clock.Ready))
                {
                    case TurretSendAction.Active:
                    {
                        uint t = clock.ServerTimeMs(Environment.TickCount64);
                        var atu = ClientActiveTurretUpdate.From(t, ControlData.Neutral, integrated);
                        boarderSess.SendActiveTurret(t, atu);
                        activeSent++;
                        break;
                    }
                    case TurretSendAction.Inactive:
                        boarderSess.SendInactiveTurret();
                        inactiveSent++;
                        break;
                }
                if (activeSent > 0 && fireHeld)
                    foreach (var p in sp.Sector.Models.OfType<Projectile>())
                        projDirections.Add(p.Velocity.Normalize());
            }

            Thread.Sleep(300);
            Console.SetOut(origOut);
            string captured = log.ToString();

            // ── the server's [turret] gate decision, counted live ──
            int accepted = CountMark(captured, "-> StateM=weaponsMaskIGC");
            int rejected = CountMark(captured, "stale (deltaT=");
            Console.WriteLine($"[P517-LIVE] turret gunner over the firing hold: activeSent={activeSent} " +
                              $"accepted={accepted} rejected={rejected} projectiles={projDirections.Count} " +
                              $"clock-error={calibErr:+0.000;-0.000}s");

            // The failing-first gate: the server ACCEPTED the PING-clock-stamped updates
            // (before P517 the tick-index feed was ~20× off and every one was rejected).
            Assert.True(activeSent >= 3, $"send loop emitted only {activeSent} ACTIVE updates while firing");
            Assert.True(accepted > 0, "server rejected ALL ACTIVE turret updates (accepted=0) — time is out of the ±1.5 s gate");
            Assert.True(rejected == 0, $"server rejected {rejected} ACTIVE turret updates — time drifted out of the gate");
            Assert.True(projDirections.Count >= 1, "no turret projectiles were created — the accepted updates did not arm/fire the weapon");

            // The accepted updates made the turret aim: rounds fly along the gunner's
            // reported orientation (not the carrier's −z), the P464 fire path.
            var d = projDirections[0];
            var gunnerFwd = sp.Boarder.Orientation.GetForward();
            Assert.True(Math.Abs(d.X) > 0.1f, "projectile flew along the carrier, not the gunner's yawed aim");
            Assert.Equal(gunnerFwd.X, d.X, 2);
            Assert.Equal(gunnerFwd.Y, d.Y, 2);
            Assert.Equal(gunnerFwd.Z, d.Z, 2);

            carrierSess.Shutdown(); boarderSess.Shutdown();
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

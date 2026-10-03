using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame.Flight;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P464 — the loopback deliverable: a boarded turret gunner whose <b>client-side
/// send loop</b> (the <see cref="TurretGunnerCadence"/>/<see cref="TurretLook"/>/
/// <c>ClientActiveTurretUpdate.From</c> primitives the MonoGame <c>Game1</c>
/// turret controller drives) emits <c>ACTIVE_TURRET_UPDATE(99)</c> frames while
/// "firing", and the server's turret weapon responds by creating projectiles
/// aimed along the gunner's reported orientation. Built in the P109/P140 style:
/// a real in-process server, two loopback <c>WireClient</c>s (the port's
/// <c>ConnectionSession</c> message path — same <c>SendActiveTurret</c>), a
/// real board over the wire, a real undock (launch), then a controlled fire
/// hold. Verifies: (1) the send loop emits ACTIVE at the control cadence while
/// firing, (2) the server installs the gunner's aiming state, and (3) the
/// gunned turret weapon spawns real projectiles whose velocity direction follows
/// the gunner's orientation — the full dead-end-to-working parity the task set
/// out to close.
/// </summary>
[Collection("LiveNetwork")]
public class TurretGunnerFireLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private const short CarrierHullId = 792;   // turret-capable carrier, slots [2,6), price 4000
    private const short BoarderHullId = 504;   // plain 500-credit boarder hull, slots [2,4)
    private const int PumpMax = 1500;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Turret Gunner Fire", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 991u, DwCookie = 0x991u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9980000000000000000ul, DrmHash = "tfire",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) => new(st.Position[0], st.Position[1], st.Position[2]);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private sealed class BoardSpawn
    {
        public Ship Carrier = null!, Boarder = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
    }

    /// <summary>Two-ship, one-side, both-docked spawn (carrier 792 with a real
    /// weapon in its first turret slot, boarder 504) — the P108/P109 recipe,
    /// extended to capture the mission/sector for projectile inspection.</summary>
    private static BoardSpawn InjectBoardSpawn(Srv server)
    {
        var sp = new BoardSpawn();
        int n = 0;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[n == 0 ? CarrierHullId : BoarderHullId];
            var ship = new Ship(id, hull, StationPos(station) + new Vector3(25f + n * 10f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            if (n == 0)
            {
                // A dispersion-0 projectile weapon (mirrors TurretGunnerFiringTests'
                // TurretFight selection) so the fired round flies EXACTLY along the
                // gunner's orientation with no random spread.
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

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }

        public WireClient() => _t = new TcpTransport(this);
        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendBoard(short sidParent) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new BoardShip { SidParent = sidParent }.Encode() }), guaranteed: true);
        public void SendDocked() =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new Docked { StationId = -1 }.Encode() }), guaranteed: true);
        public void SendActiveTurret(uint outerTime, ClientActiveTurretUpdate atu) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new ActiveTurretUpdate { TimeUpdate = outerTime, Atu = atu }.Encode() }), guaranteed: true);
        public void SendInactiveTurret() =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new InactiveTurretUpdate().Encode() }), guaranteed: true);

        void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
        void ITransportSite.OnPeerLeft(ConnectionId id) { }
        void ITransportSite.OnSessionLost() { }
        void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            byte[][] msgs;
            try { msgs = FedPacket.Unpack(data.ToArray()); }
            catch (FedCrcException) { return; }
            lock (_gate)
            {
                foreach (var m in msgs)
                {
                    ushort fmid;
                    try { fmid = FedMessageHeader.Read(m).Fmid; } catch { continue; }
                    switch (fmid)
                    {
                        case LogonAck.Fmid: _ack = LogonAck.Decode(m); break;
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    private static (Srv server, WireClient carrier, WireClient boarder, BoardSpawn sp) Setup(out short carrierId, out short boarderId)
    {
        var server = new Srv();
        var sp = InjectBoardSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby
        ushort port = server.BoundPort;

        var a = new WireClient();
        var b = new WireClient();
        a.Connect(port, "CARRIER"); b.Connect(port, "BOARDER");
        a.Logon(Request("CARRIER")); b.Logon(Request("BOARDER"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        WireClient carrier, boarder;
        if (a.ShipAssignment!.Ship.ShipId == sp.Carrier.Id) { carrier = a; boarder = b; }
        else { carrier = b; boarder = a; }
        carrierId = carrier.ShipAssignment!.Ship.ShipId;
        boarderId = boarder.ShipAssignment!.Ship.ShipId;
        return (server, carrier, boarder, sp);
    }

    /// <summary>
    /// The whole P464 loopback scenario: a gunner boards a docked carrier, both
    /// launch, the client-side send loop emits ACTIVE turret updates while the
    /// fire button is held, and the server's gunned turret weapon creates
    /// projectiles aimed along the gunner's reported orientation.
    /// </summary>
    [Fact]
    public void Gunner_send_loop_makes_the_server_create_projectiles_along_its_orientation()
    {
        var (server, carrier, boarder, sp) = Setup(out short cId, out short bId);
        try
        {
            // Settle the docked latch, then board the gunner on the carrier.
            int dock = PumpUntil(server, () => sp.Carrier.StationId is not null && sp.Boarder.StationId is not null, 400);
            Assert.True(dock < 400, "carrier/boarder did not dock");
            boarder.SendBoard(cId);
            int ok = PumpUntil(server, () => sp.Boarder.ParentShip is not null, PumpMax);
            Assert.True(ok < PumpMax, "board did not set ParentShip");

            // Both launch: the carrier (with the gunner aboard) undocks.
            carrier.SendDocked();
            int launched = PumpUntil(server, () => sp.Carrier.StationId is null, PumpMax);
            Assert.True(launched < PumpMax, "carrier did not undock with the gunner aboard");
            var turretWeapon = sp.Carrier.Weapons.First(w => w.MountId == sp.Boarder.TurretId!.Value);
            Assert.Same(sp.Boarder, turretWeapon.Gunner);

            // ── drive the client-side send loop (the production primitives) ──
            // Local client clock advances in lockstep with the pumped sim; each
            // step the pure TurretGunnerCadence decides ACTIVE vs INACTIVE while
            // the fire button is held. The reported orientation is a yawed aim
            // (fixed) integrated via TurretLook; the update's `time` is stamped
            // from the shared PING-fed server clock (P517) — the ms
            // OwnShipServerClock converted to the seconds the gate compares
            // against the gunner's LastUpdate. Here the clock is fed the server's
            // sim-ms (the exact value a real FM_CS_PING echo carries,
            // timeServer = SimNow·1000), in lockstep with the pumped sim. Neutral
            // controls → zero slew → the server installs the reported orientation
            // exactly.
            var aim = new Orientation();
            aim.Yaw(0.6f);                                            // distinctly off the carrier's −z
            Vector3 aimForward = aim.GetForward();

            var cadence = new TurretGunnerCadence();
            var clock = new Allegiance.ClientCore.Flight.OwnShipServerClock();
            double localT = 0.0;
            int activeSent = 0;
            int inactiveSent = 0;
            var projDirections = new List<Vector3>();
            var expectedForward = aimForward;                       // decoded aim the server installs

            for (int step = 0; step < 160; step++)                    // ~8 s @20 ups
            {
                // Park the carrier (zero velocity) BEFORE the tick so the weapon
                // sees zero muzzle velocity and the projectile's direction
                // reflects pure gunner aim, not a muzzle-velocity blend — the
                // same controlled condition as TurretGunnerFiringTests.
                sp.Carrier.Velocity = Vector3.Zero;
                server.TickSim();
                localT += 0.051;

                // Feed the shared server clock from the server's sim-ms (the value
                // a real PING echo delivers), in lockstep with the local clock.
                clock.AddSample(
                    (uint)Math.Floor(sp.Boarder.LastUpdate * 1000.0),
                    (long)(localT * 1000.0));

                // TurretLook integrates the (fixed) aim locally, matching the
                // server; controls neutral so the sent orientation is installed.
                Orientation integrated = aim.Copy();
                TurretLook.Integrate(integrated, ControlData.Neutral, 0.015f);

                bool fireHeld = step >= 30;                            // hold fire after boarding/launch settles
                switch (cadence.Update(localT, fireHeld, clock.Ready))
                {
                    case TurretSendAction.Active:
                    {
                        uint t = (uint)Math.Floor(clock.ServerTimeMs((long)(localT * 1000.0)) / 1000.0);
                        var atu = ClientActiveTurretUpdate.From(t, ControlData.Neutral, integrated);
                        expectedForward = atu.Orientation.GetForward();   // the decoded aim the server installs
                        boarder.SendActiveTurret(t, atu);
                        activeSent++;
                        break;
                    }
                    case TurretSendAction.Inactive:
                        boarder.SendInactiveTurret();
                        inactiveSent++;
                        break;
                }

                // Collect projectile directions once firing begins (accumulates
                // across the whole loop, so expiry never shrinks the count).
                if (activeSent > 0 && fireHeld)
                    foreach (var p in sp.Sector.Models.OfType<Projectile>())
                        projDirections.Add(p.Velocity.Normalize());
            }

            // (1) The send loop DID emit ACTIVE updates at the cadence while
            // firing (native clintlib.cpp:1870-1905).
            Assert.True(activeSent >= 3, $"send loop emitted only {activeSent} ACTIVE updates while firing");

            // (2)+(3) The server's gunned turret weapon created projectiles, and
            // they fly along the gunner's reported orientation (not the
            // carrier's −z).
            Assert.True(projDirections.Count >= 1,
                "no turret projectiles were created by the server despite ACTIVE updates — send loop / firing broken");
            var d = projDirections[0];
            var gunnerFwd = sp.Boarder.Orientation.GetForward();   // the authoritative installed gunner facing
            Console.WriteLine($"[diag] sent-at-u decoded fw={expectedForward} gunner-live fw={gunnerFwd} projectile fw={d}");
            Assert.Equal(gunnerFwd.X, d.X, 2);
            Assert.Equal(gunnerFwd.Y, d.Y, 2);
            Assert.Equal(gunnerFwd.Z, d.Z, 2);
            // And it is NOT the carrier's own forward (identity = −z): the aim is
            // yawed, so the discriminating component is X.
            Assert.True(Math.Abs(d.X) > 0.1f, "projectile flew along the carrier, not the gunner's yawed aim");

            Console.WriteLine($"TURRET-FIRE-LIVE: {activeSent} ACTIVE updates while firing; " +
                              $"server created {projDirections.Count} projectiles, first flies fw={d} (gunner fw={gunnerFwd})");
        }
        finally
        {
            carrier.Dispose(); boarder.Dispose(); server.Dispose();
        }
    }
}

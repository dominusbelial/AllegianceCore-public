using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P109 — the turret <b>weapon-gunner binding</b>, live over a real loopback
/// in-process server. Builds on the P108 boarding recipe (same
/// two-ship/one-side/docked spawn + <c>FM_C_BOARD_SHIP(103)</c> wire path) and
/// then drives the new turret behaviour on top:
/// <list type="bullet">
/// <item>boarding binds the boarder as <c>Gunner</c> of the carrier's weapon
/// at the chosen turret slot, and leaving clears it;</item>
/// <item>an <c>FM_C_ACTIVE_TURRET_UPDATE(99)</c> with a real orientation is
/// snapshot-caught-up onto the gunner's own ship (orientation installed via
/// SetOrientation) and the "shooting" <c>weaponsMaskIGC</c> state bit is set;</item>
/// <item>between two real <c>ACTIVE_TURRET_UPDATE</c> messages the carrier's
/// per-tick loop keeps predicting the gunner's orientation forward;</item>
/// <item>an <c>FM_C_INACTIVE_TURRET_UPDATE(100)</c> clears that state bit
/// (<c>SetStateM(0)</c>);</item>
/// <item>a gunner's orientation change <b>does</b> reach a same-side observer
/// through the per-ship broadcast: since P209 the SINGLE_SHIP_UPDATE dirty-gate
/// fires on orientation as well as position (a pure rotation never moves
/// position, so the position-only check alone would never re-broadcast the new
/// facing), and the delivered update carries the rotated orientation —
/// answering the task's item-7 question with a real test.</item>
/// </list>
/// The firing-direction proofs (gunner vs carrier aim) and the fixed-weapon
/// no-regression check live in <see cref="TurretGunnerFiringTests"/>, where a
/// deliberate carrier-vs-gunner orientation difference is fully controllable.
/// </summary>
[Collection("LiveNetwork")]
public class LiveTurretGunnerBindingTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const short CarrierHullId = 792;   // turret-capable carrier, slots [2,6), price 4000
    private const short BoarderHullId = 504;   // plain 500-credit boarder hull, slots [2,4)
    private const int PumpMax = 1200;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Turret Gunner", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 991u, DwCookie = 0x991u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9980000000000000000ul, DrmHash = "turret",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) => new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Two-ship, one-side, both-docked spawn injection (carrier 792
    /// with a real part in its first turret slot, boarder 504), the P108
    /// recipe verbatim.</summary>
    private sealed class BoardSpawn { public Ship Carrier = null!, Boarder = null!; }

    private static BoardSpawn InjectBoardSpawn(Srv server, bool equipTurret)
    {
        var sp = new BoardSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[n == 0 ? CarrierHullId : BoarderHullId];
            var ship = new Ship(id, hull, StationPos(station) + new Vector3(25f + n * 10f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);

            if (n == 0 && equipTurret)
            {
                var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
                ship.MountPart(weapon, 1, (byte)hull.MaxFixedWeapons, 255);
            }

            if (n == 0) sp.Carrier = ship; else sp.Boarder = ship;
            n++;
            return (ship, sector);
        };
        return sp;
    }

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<ShipStatusMessage> _statuses = new();
        private readonly List<SingleShipUpdate> _singleUpdates = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<ShipStatusMessage> Statuses { get { lock (_gate) return _statuses.ToArray(); } }
        public IReadOnlyList<SingleShipUpdate> SingleUpdates { get { lock (_gate) return _singleUpdates.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendBoard(short sidParent) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new BoardShip { SidParent = sidParent }.Encode() }), guaranteed: true);

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
                        case ShipStatusMessage.Fmid: _statuses.Add(ShipStatusMessage.Decode(m)); break;
                        case SingleShipUpdate.Fmid: _singleUpdates.Add(SingleShipUpdate.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    private static (Srv server, WireClient carrier, WireClient boarder, BoardSpawn sp) Setup(out short carrierId, out short boarderId)
    {
        var server = new Srv();
        var sp = InjectBoardSpawn(server, equipTurret: true);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
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

    private static void WaitDocked(Srv server, params (WireClient client, Ship ship)[] ships)
    {
        int n = PumpUntil(server, () => ships.All(s => s.ship.StationId is not null), 400);
        Assert.True(n < 400, "ships did not dock");
    }

    /// <summary>A turret gunner commanded to turn (yaw hard over, full
    /// throttle) with a yawed report — the per-tick prediction driver.</summary>
    private static ClientActiveTurretUpdate YawingAtu(Ship gunner, double atuTime)
    {
        var o = new Orientation();
        o.Yaw(0.5f);
        o.Pitch(-0.2f);
        return ClientActiveTurretUpdate.From(
            (uint)Math.Floor(atuTime),
            new ControlData(0.9f, 0.0f, 0.0f, 1.0f),   // yaw hard over, full throttle → rapid slew
            o);
    }

    [Fact]
    public void Board_binds_gunner_and_active_turret_update_roundtrips_live()
    {
        var (server, carrier, boarder, sp) = Setup(out short cId, out short bId);
        WaitDocked(server, (carrier, sp.Carrier), (boarder, sp.Boarder));

        // ── board ──
        boarder.SendBoard(cId);
        int ok = PumpUntil(server, () => sp.Boarder.ParentShip is not null, PumpMax);
        Assert.True(ok < PumpMax, "board did not set ParentShip");
        Assert.Equal((short)2, sp.Boarder.TurretId);

        // Scenario 1: the carrier's weapon at the boarder's slot has Gunner set.
        var turretWeapon = sp.Carrier.Weapons.First(w => w.MountId == sp.Boarder.TurretId!.Value);
        Assert.Same(sp.Boarder, turretWeapon.Gunner);

        // ── scenario 2: gunner sends a real ACTIVE_TURRET_UPDATE(99) ──
        // Two ATUs, to keep each assertion deterministic under the async
        // delivery (deltaT at processing is unknown but bounded by ±1.5 s):
        //   ATU-A — NEUTRAL controls + a yawed report. Because neutral controls
        //           drive ExecuteTurretMove's slew to zero, the n-step catch-up
        //           is a no-op regardless of deltaT, so the installed facing
        //           EXACTLY equals the decoded report (task item 2: "their own
        //           ship's orientation is updated server-side to match").
        //   ATU-B — yaw controls + the current facing. Installs the yaw command
        //           so the per-tick prediction (scenario 3) can turn the gunner.
        ClientActiveTurretUpdate atuA = null!, atuB = null!;
        int applied = -1;
        for (int attempt = 0; attempt < 40 && applied < 0; attempt++)
        {
            if ((sp.Boarder.StateM & ShipControlState.WeaponsMask) != 0) { applied = attempt; break; }
            var o = new Orientation(); o.Yaw(0.5f); o.Pitch(-0.2f);
            atuA = ClientActiveTurretUpdate.From(
                (uint)Math.Floor(sp.Boarder.LastUpdate),
                ControlData.Neutral,                    // zero slew → catch-up no-op
                o);
            boarder.SendActiveTurret(atuA.Time, atuA);
            PumpUntil(server, () => (sp.Boarder.StateM & ShipControlState.WeaponsMask) != 0, 25);
        }
        Assert.True(applied >= 0, "ACTIVE_TURRET_UPDATE did not set the shooting state / install an orientation");
        Assert.Equal(ShipControlState.WeaponsMask, sp.Boarder.StateM);   // full assignment, shipIGC.cpp:3359

        // Snapshot installed the reported facing server-side (SetOrientation,
        // shipIGC.cpp:3386): gunner's facing matches the compact-decoded report
        // and differs from identity (the report was yawed).
        var f = sp.Boarder.Orientation.GetForward();
        Assert.NotEqual(new Vector3(0f, 0f, -1f), f);
        var repA = atuA.Orientation.GetForward();
        Assert.Equal(repA.X, f.X, 2);
        Assert.Equal(repA.Y, f.Y, 2);
        Assert.Equal(repA.Z, f.Z, 2);

        // ── arm the yaw command for the per-tick prediction ──
        for (int attempt = 0; attempt < 40 && Math.Abs(sp.Boarder.Controls.Yaw - 0.9f) > 0.1f; attempt++)
        {
            var o2 = new Orientation(); o2.SetForward(sp.Boarder.Orientation.GetForward());
            atuB = ClientActiveTurretUpdate.From(
                (uint)Math.Floor(sp.Boarder.LastUpdate),
                new ControlData(0.9f, 0f, 0f, 1f),      // yaw hard over, full throttle
                o2);
            boarder.SendActiveTurret(atuB.Time, atuB);
            PumpUntil(server, () => Math.Abs(sp.Boarder.Controls.Yaw - 0.9f) < 0.1f, 25);
        }
        Assert.True(Math.Abs(sp.Boarder.Controls.Yaw - 0.9f) < 0.1f, "ATU-B did not install the yaw controls");

        // ── scenario 3: the carrier's per-tick prediction continues turning ──
        // (gunner.Controls now commands yaw; with NO new message, the carrier's
        // own tick keeps advancing the gunner's facing via ExecuteTurretMove.)
        var fBefore = sp.Boarder.Orientation.GetForward();
        int grew = PumpUntil(server, () => {
            var ff = sp.Boarder.Orientation.GetForward();
            return Vector3.CrossProduct(ff, fBefore).LengthSquared() > 1e-6f;
        }, PumpMax);
        Assert.True(grew < PumpMax,
            "between ACTIVE_TURRET_UPDATES the carrier's tick must keep predicting the gunner's orientation forward");

        // ── scenario 4: INACTIVE clears the shooting state ──
        boarder.SendInactiveTurret();
        int cleared = PumpUntil(server, () => (sp.Boarder.StateM & ShipControlState.WeaponsMask) == 0, PumpMax);
        Assert.True(cleared < PumpMax, "INACTIVE_TURRET_UPDATE did not clear the shooting state");
        Assert.Equal(0, sp.Boarder.StateM);   // SetStateM(0), fsship.cpp:1223

        // ── scenario 7 (observer reach, REVISED for P209): ──
        // A gunner whose POSITION is fixed (parked: brake controls + zero
        // velocity) but whose ORIENTATION changes DOES reach a same-side
        // observer via SINGLE_SHIP_UPDATE(78). Since P209 the broadcast
        // dirty-gate includes orientation (commit 06b142fa: "a pure rotation
        // changes no position at all, so the position-only check alone would
        // never re-broadcast it" — forward catches pitch/yaw, up catches roll),
        // reused for the same-side teammate position path. This supersedes the
        // original P109 expectation that an orientation-only change stays
        // invisible to observers; the update must now carry the rotated facing.
        sp.Boarder.Controls = ControlData.Neutral;
        sp.Boarder.Velocity = Vector3.Zero;
        int settle = PumpUntil(server, () => true, 10);            // let a tick latch the parked state
        _ = settle;
        var reAim = new Orientation(); reAim.Yaw(1.2f);            // a pronounced, unmistakable re-aim
        int baseline = carrier.SingleUpdates.Count(u => u.OidTarget == bId);
        sp.Boarder.SetOrientation(reAim);                          // change facing only, keep position
        int n2 = PumpUntil(server, () => carrier.SingleUpdates.Count(u => u.OidTarget == bId) > baseline, 400);
        Assert.True(n2 < 400, "orientation-only change must reach the same-side observer via SINGLE_SHIP_UPDATE (P209 orientation gate)");
        // The delivered update carries the rotated facing (forward no longer identity).
        var rotUpd = carrier.SingleUpdates.Last(u => u.OidTarget == bId);
        Assert.NotEqual(new Vector3(0f, 0f, -1f), rotUpd.Ship.Orientation.GetForward());
        Console.WriteLine($"[turret] observer-reach CONFIRMED (P209): parked gunner {bId} re-aimed -> SINGLE_SHIP_UPDATE at carrier peer carries the rotated facing");

        // ── scenario 1b: leaving clears the binding ──
        boarder.SendBoard(-1);   // leave
        int left = PumpUntil(server, () => sp.Boarder.ParentShip is null, PumpMax);
        Assert.True(left < PumpMax, "leave did not clear ParentShip");
        Assert.Null(turretWeapon.Gunner);

        Console.WriteLine($"TURRET-LIVE: board {bId}->{cId} slot 2 bound gunner; ACTIVE(99) caught up orientation + set weaponsMaskIGC; " +
                          $"per-tick prediction turned it; INACTIVE(100) SetStateM(0); observer got no SINGLE_SHIP_UPDATE; leave cleared gunner");
    }
}

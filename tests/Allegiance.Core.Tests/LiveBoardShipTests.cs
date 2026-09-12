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
/// P108 — the <c>FM_C_BOARD_SHIP(103)</c> server slice: the state-transition
/// mechanism by which a player abandons their own ship to occupy a turret
/// mount (or observer slot) on another player's ship. Turret aim/fire (gunner
/// binding, P109) and the periodic turret-state broadcast (P110) are
/// explicitly out of scope — this is occupancy alone.
///
/// Layers, mirroring <see cref="LiveSuicideShipDeleteTests"/>:
///  (1) codec round-trips live in <see cref="Wire.BoardingWireTests"/>;
///  (2) a pure-unit view of the turret-slot walk proves the observe fallback;
///  (3) real loopback-in-process-server live proofs for the five verification
///      scenarios: board (state + money + the *negative* no-dedicated-ack),
///      observer fallback, NACK-to-requester-only, and leave broadcast.
///
/// The [Collection("LiveNetwork")] tag keeps this suite sequential against the
/// other live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveBoardShipTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Concrete hulls chosen from PCore001.igc (measured, not guessed):
    //   792 = a turret-capable gunship/carrier, slots [2,6), price 4000
    //   504 = a 500-credit normal hull (the boarder), slots [2,4)
    private const short CarrierHullId = 792;
    private const short BoarderHullId = 504;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Board Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 931u, DwCookie = 0x931u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8880000000000000000ul, DrmHash = "board",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Generous pump bound for asynchronous wire reception. The live
    /// server bursts a slew of EXPORT(26) frames at connect (the P48/P58/P62
    /// join telemetry — <c>LiveViewClusterTests</c> needs 1000 ticks for the
    /// same reason), and a small reply can sit behind that burst in the client's
    /// read buffer for a while. Server-side state transitions (ParentShip etc.)
    /// resolve in a handful of ticks; the wire-observation pumps get this bound.
    /// Far below the 60 s = 1200-tick payday accrual, so money assertions stay
    /// immune.</summary>
    private const int PumpMax = 1200;

    /// <summary>A two-ship, one-side, both-docked spawn injection. The first
    /// spawned ship (per connection order) is the carrier — hull 792 (turret
    /// slots [2,6)) — optionally equipped with a part in its first turret slot
    /// 2 (so <paramref name="equipTurret"/> governs whether boarding finds a
    /// real slot or falls back to observer). The second is the boarder, a plain
    /// hull 504 (positive 500-credit value, no parts) so its surrender refunds
    /// exactly its hull price. Both are placed beside the same friendly station
    /// and assigned the same side, so the first tick latches both docked.</summary>
    private sealed class BoardSpawn
    {
        public Ship Carrier = null!, Boarder = null!;
    }

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
                // Equip the carrier's FIRST turret slot (MaxFixedWeapons=2) with
                // a real part, making an unoccupied, equipped turret available.
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
        private readonly List<BoardNack> _nacks = new();
        private readonly List<LeaveShip> _leaves = new();
        private readonly List<ShipStatusMessage> _statuses = new();
        private readonly List<ushort> _allFmids = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<BoardNack> Nacks { get { lock (_gate) return _nacks.ToArray(); } }
        public IReadOnlyList<LeaveShip> Leaves { get { lock (_gate) return _leaves.ToArray(); } }
        public IReadOnlyList<ShipStatusMessage> Statuses { get { lock (_gate) return _statuses.ToArray(); } }
        public IReadOnlyList<ushort> AllFmids { get { lock (_gate) return _allFmids.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendBoard(short sidParent) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new BoardShip { SidParent = sidParent }.Encode() }), guaranteed: true);

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
                    _allFmids.Add(fmid);
                    switch (fmid)
                    {
                        case LogonAck.Fmid: _ack = LogonAck.Decode(m); break;
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); break;
                        case BoardNack.Fmid: _nacks.Add(BoardNack.Decode(m)); break;
                        case LeaveShip.Fmid: _leaves.Add(LeaveShip.Decode(m)); break;
                        case ShipStatusMessage.Fmid: _statuses.Add(ShipStatusMessage.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>Boot a server with a two-ship docked spawn and connect two
    /// clients, returning the <b>carrier</b> client (the one whose ship is the
    /// 792 turret-capable carrier), the <b>boarder</b> client (the other), the
    /// spawn refs, and the two ship ids. Role identity is decided by the ship's
    /// hull (the spawner gives the 792 hull to its first spawn) and matched to
    /// the client that owns it — <b>not</b> by socket/connect order, which the
    /// server may assign in either direction. The caller must pump to latch
    /// docked before issuing board requests.</summary>
    private static (Srv server, WireClient carrier, WireClient boarder, BoardSpawn sp) Setup(out short carrierId, out short boarderId)
    {
        var server = new Srv();
        var sp = InjectBoardSpawn(server, equipTurret: true);
        server.Boot(Config());
        ushort port = server.BoundPort;

        var a = new WireClient();
        var b = new WireClient();
        a.Connect(port, "CARRIER"); b.Connect(port, "BOARDER");
        a.Logon(Request("CARRIER")); b.Logon(Request("BOARDER"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        // Whichever client's ship is the 792 carrier-hull ship is the carrier.
        WireClient carrier, boarder;
        if (a.ShipAssignment!.Ship.ShipId == sp.Carrier.Id) { carrier = a; boarder = b; }
        else { carrier = b; boarder = a; }
        carrierId = carrier.ShipAssignment!.Ship.ShipId;
        boarderId = boarder.ShipAssignment!.Ship.ShipId;
        return (server, carrier, boarder, sp);
    }

    private static void WaitDocked(Srv server, params (WireClient client, Ship ship)[] ships)
    {
        // The dock seam latches StationId on a tick after the ship sits inside
        // a station's dock radius (DockWithStationEvent → Ship.StationId).
        int n = PumpUntil(server, () => ships.All(s => s.ship.StationId is not null), 400);
        Assert.True(n < 400, "ships did not dock");
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    // ── codec round-trips ── (live in Wire.BoardingWireTests) ────────────────

    // ── pure-unit turret-slot walk ────────────────────────────────────────────

    [Fact]
    public void Turret_slot_walk_picks_free_equipped_slot_or_observer()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "T1", civ.CivilizationId)], new RecordingIgcSite());
        mission.Stage = MissionStage.Started;

        var carrierHull = core.HullTypes[CarrierHullId];
        var boarderHull = core.HullTypes[BoarderHullId];

        // Un-armed carrier (turret slots [2,6) but nothing mounted): no
        // equipped slot → observer (null), matching the walk's empty-looped
        // fallback (FedSrv.CPP:2395-2400).
        var carrier = new Ship(601, carrierHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(carrier, mission.Sectors[0]);
        var boarder = new Ship(602, boarderHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(boarder, mission.Sectors[0]);
        Assert.Null(Srv.ChooseTurretSlot(boarder, carrier));

        // Armed carrier: equip turret slots 2 AND 3 → the first boarder gets
        // slot 2 (walk starts at maxMount-1=5, wraps to minMount=2).
        var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
        carrier.MountPart(weapon, 1, (byte)carrierHull.MaxFixedWeapons, 255);
        carrier.MountPart(weapon, 1, (byte)(carrierHull.MaxFixedWeapons + 1), 255);
        Assert.Equal((short)2, Srv.ChooseTurretSlot(boarder, carrier));

        // Slot 2 already occupied by another child → next free EQUIPPED slot (3).
        var rival = new Ship(603, boarderHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(rival, mission.Sectors[0]);
        rival.ParentShip = carrier; rival.TurretId = 2; carrier.AddChildShip(rival);
        Assert.Equal((short)3, Srv.ChooseTurretSlot(boarder, carrier));
    }

    // ── live proofs ──────────────────────────────────────────────────────────

    /// <summary>⭐ Scenario 1+4 together: a docked ship boards another's real,
    /// equipped turret slot — ParentShip/TurretId set, appears in the target's
    /// child list, money refunded to the side treasury, and <b>no dedicated ack
    /// message</b> (no BOARD_NACK, no LEAVE_SHIP) — the boarded state arrives
    /// only via the existing SHIP_STATUS(90) refresh (State=c_ssTurret,
    /// ParentId=carrier). Then it leaves (sidParent=NA): parent/turret cleared,
    /// hull restored to the side's Lifepod, and a real LEAVE_SHIP(94) broadcast
    /// to the whole side naming the correct child id.</summary>
    [Fact]
    public void Board_with_equipped_turret_and_leave()
    {
        var (server, carrier, boarder, sp) = Setup(out short cId, out short bId);
        WaitDocked(server, (carrier, sp.Carrier), (boarder, sp.Boarder));

        var side = sp.Boarder.Mission!.Sides.First(s => s.Id == sp.Boarder.SideId!.Value);
        long moneyBefore = side.Money;

        // ── board ──
        boarder.SendBoard(cId);
        int ok = PumpUntil(server, () => sp.Boarder.ParentShip is not null, PumpMax);
        Assert.True(ok < PumpMax, "board did not set ParentShip");

        Assert.Same(sp.Carrier, sp.Boarder.ParentShip);
        Assert.Equal((short)2, sp.Boarder.TurretId);          // slot 2 (the equipped one)
        Assert.Contains(sp.Carrier.ChildShips, c => ReferenceEquals(c, sp.Boarder));

        // Money refunded: the boarder surrenders hull 504 = 500 credits, deposited
        // into the side treasury. Read inside a tight tick window (the board pump
        // above returns the moment ParentShip is set, well before the 60 s = 1200
        // tick payday accrual), so the only writer to side.Money here is the refund.
        long moneyAfter = side.Money;
        Assert.True(moneyAfter >= moneyBefore, "money went backwards");
        Assert.Equal(moneyBefore + 500, moneyAfter);          // hull 504 price (no parts on the boarder)

        // Negative proof: NO dedicated success ack. Neither a BOARD_NACK(178)
        // nor a LEAVE_SHIP(94) nor any other frame arrives at the boarder as a
        // result of the successful board — only the periodic SHIP_STATUS(90)
        // refresh reflecting the boarded state.
        int seen = PumpUntil(server,
            () => boarder.Statuses.Any(st => st.State == ShipStatusMessage.StateTurret && st.ParentId == cId),
            PumpMax);
        Assert.True(seen < PumpMax, "boarded state never reached the wire via SHIP_STATUS");
        Assert.DoesNotContain(boarder.AllFmids, f => f == BoardNack.Fmid);
        Assert.DoesNotContain(boarder.AllFmids, f => f == LeaveShip.Fmid);
        var turretStatus = boarder.Statuses.Last(st => st.State == ShipStatusMessage.StateTurret && st.ParentId == cId);
        Assert.Equal(-1, turretStatus.HullId);                // NA — riding inside the carrier's hull (FedSrv.CPP:2454)

        // ── leave ──
        boarder.SendBoard(-1);                                // sidParent == NA → leave
        int left = PumpUntil(server, () => sp.Boarder.ParentShip is null && sp.Carrier.ChildShips.All(c => !ReferenceEquals(c, sp.Boarder)), PumpMax);
        Assert.True(left < PumpMax, "leave did not clear the parent/child relationship");

        Assert.Null(sp.Boarder.ParentShip);
        Assert.Null(sp.Boarder.TurretId);
        Assert.True(sp.Boarder.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), "leave should restore the side's Lifepod hull");
        Assert.DoesNotContain(sp.Carrier.ChildShips, c => ReferenceEquals(c, sp.Boarder));

        // LEAVE_SHIP(94) broadcast to the whole side, naming the detaching ship.
        int leaveSeen = PumpUntil(server, () => carrier.Leaves.Any(l => l.SidChild == bId) && boarder.Leaves.Any(l => l.SidChild == bId), PumpMax);
        Assert.True(leaveSeen < PumpMax, "LEAVE_SHIP(94) not seen on both same-side peers");
        Assert.Contains(carrier.Leaves, l => l.SidChild == bId);
        Assert.Contains(boarder.Leaves, l => l.SidChild == bId);

        Console.WriteLine($"BOARD-LIVE: ship {bId} boarded {cId} slot 2 (refund 500 to side {side.Id}), then left -> LEAVE_SHIP(94) sidChild={bId} on both peers");
    }

    /// <summary>⭐ Scenario 2: boarding a ship with turret slots but NO free
    /// equipped slot makes the boarder an observer (TurretId null), not a
    /// rejection — the ported empty-loop fallback (<c>FedSrv.CPP:2395-2400</c>).
    /// The wire reflects it as State=c_ssObserver with the carrier's parent id.</summary>
    [Fact]
    public void Board_without_equipped_turret_becomes_observer()
    {
        var server = new Srv();
        InjectBoardSpawn(server, equipTurret: false);         // slots [2,6) but nothing mounted
        server.Boot(Config());
        var carrier = new WireClient(); var boarder = new WireClient();
        carrier.Connect(server.BoundPort, "CARRIER"); boarder.Connect(server.BoundPort, "BOARDER");
        carrier.Logon(Request("CARRIER")); boarder.Logon(Request("BOARDER"));
        PumpUntil(server, () => carrier.ShipAssignment is not null && boarder.ShipAssignment is not null, 4000);

        // Role-match by spawned id (robust to socket order).
        short cId = carrier.ShipAssignment!.Ship.ShipId;
        short bId = boarder.ShipAssignment!.Ship.ShipId;
        var spShell = server.Ships.Select(s => s.Ship).ToArray();
        Ship cShip = spShell.First(s => s.Id == cId);
        Ship bShip = spShell.First(s => s.Id == bId);
        if (cShip.HullType.HullId != CarrierHullId) (carrier, boarder) = (boarder, carrier);
        cId = carrier.ShipAssignment!.Ship.ShipId; bId = boarder.ShipAssignment!.Ship.ShipId;
        cShip = server.Ships.First(s => s.Ship.Id == cId).Ship;
        bShip = server.Ships.First(s => s.Ship.Id == bId).Ship;

        WaitDocked(server, (carrier, cShip), (boarder, bShip));

        boarder.SendBoard(cId);
        int ok = PumpUntil(server, () => bShip.ParentShip is not null, PumpMax);
        Assert.True(ok < PumpMax, "board did not set ParentShip");
        Assert.Null(bShip.TurretId);                          // observer — no equipped turret slot
        Assert.Contains(cShip.ChildShips, c => ReferenceEquals(c, bShip));

        int seen = PumpUntil(server,
            () => boarder.Statuses.Any(st => st.State == ShipStatusMessage.StateObserver && st.ParentId == cId),
            PumpMax);
        Assert.True(seen < PumpMax, "observer state never reached the wire via SHIP_STATUS");
        Assert.DoesNotContain(boarder.AllFmids, f => f == BoardNack.Fmid);

        Console.WriteLine($"BOARD-OBSERVER-LIVE: ship {bId} boarded {cId} as observer (no equipped turret) -> State=c_ssObserver ParentId={cId}");
    }

    /// <summary>⭐ Scenario 3: boarding a NONEXISTENT ship sends a real
    /// BOARD_NACK(178) naming the requested parent id — to the requester ONLY
    /// (the same-side carrier peer sees nothing).</summary>
    [Fact]
    public void Board_nonexistent_ship_nacks_only_requester()
    {
        var (server, carrier, boarder, _) = Setup(out _, out short bId);

        const short bogus = 32150;                            // an id no peer owns
        boarder.SendBoard(bogus);
        int seen = PumpUntil(server, () => boarder.Nacks.Any(n => n.SidRequestedParent == bogus), PumpMax);
        Assert.True(seen < PumpMax, "no BOARD_NACK reached the requester");

        Assert.Contains(boarder.Nacks, n => n.SidRequestedParent == bogus);
        Assert.Empty(carrier.Nacks);                          // NOT broadcast — requester only

        // The boarder's own ship stayed unboarded.
        Assert.Null(ServerShip(server, bId).ParentShip);
        Console.WriteLine($"BOARD-NACK-LIVE: ship {bId} -> bogus {bogus}: BOARD_NACK(178) sidRequestedParent={bogus} to requester only");
    }

    /// <summary>⭐ Scenario 3b: boarding a ship on a DIFFERENT side — the native
    /// resolves the target via <c>GetShip()</c> on the boarder's own side, so a
    /// cross-side id is effectively a no-such-ship and must NACK.</summary>
    [Fact]
    public void Board_wrong_side_ship_nacks()
    {
        var server = new Srv();
        InjectBoardSpawn(server, equipTurret: true);
        server.Boot(Config());
        var carrier = new WireClient(); var boarder = new WireClient();
        carrier.Connect(server.BoundPort, "CARRIER"); boarder.Connect(server.BoundPort, "BOARDER");
        carrier.Logon(Request("CARRIER")); boarder.Logon(Request("BOARDER"));
        PumpUntil(server, () => carrier.ShipAssignment is not null && boarder.ShipAssignment is not null, 4000);

        // Force the boarder onto the OTHER side than the carrier: two ships
        // spawned by one injector share the first friendly station's side, so
        // flip the boarder to a different side id to prove the same-side gate.
        short cId = carrier.ShipAssignment!.Ship.ShipId;
        short bId = boarder.ShipAssignment!.Ship.ShipId;
        Ship bShip = server.Ships.First(s => s.Ship.Id == bId).Ship;
        Ship cShip = server.Ships.First(s => s.Ship.Id == cId).Ship;
        var other = bShip.Mission!.Sides.First(s => s.Id != bShip.SideId!.Value);
        bShip.SideId = other.Id;                               // now on the other side

        boarder.SendBoard(cId);
        int seen = PumpUntil(server, () => boarder.Nacks.Any(n => n.SidRequestedParent == cId), PumpMax);
        Assert.True(seen < PumpMax, "cross-side board should NACK (no same-side ship found)");
        Assert.Null(bShip.ParentShip);
        Assert.DoesNotContain(cShip.ChildShips, c => ReferenceEquals(c, bShip));
        Assert.Empty(carrier.Nacks);                          // requester only
        Console.WriteLine($"BOARD-XSIDE-LIVE: cross-side board of {cId} from side {bShip.SideId} -> BOARD_NACK(178)");
    }
}

using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

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
            var sideId = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0).SideId;
            var station = P550TestDockHome.CapLandHome(mission, sideId);
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
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
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

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Turret_slot_walk_picks_free_equipped_slot_or_observer()
    {
        Require.Artwork();
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

    /// <summary>P313 / review H1 — regression for the player-triggerable
    /// infinite loop in <see cref="Srv.ChooseTurretSlot"/>. A boarder carrying
    /// a STALE TurretId from a previous, LARGER carrier (slot 5, from the [2,6)
    /// range of hull 792) re-boards a SMALLER carrier (hull 504, range [2,4))
    /// whose every matching slot is occupied. The stale start lies OUT of
    /// [minMount, maxMount), so without the clamp the wrap
    /// <c>turret &gt;= maxMount → turret = minMount</c> cycles 2↔3 forever and
    /// never revisits startMount=5 → infinite spin (server-wide freeze). The
    /// fixed walk clamps the start into range and must terminate, falling back
    /// to observer. Executed under a watchdog so a regression FAILS cleanly
    /// instead of hanging the whole suite.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Reboard_smaller_full_carrier_terminates_observer()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "T1", civ.CivilizationId)], new RecordingIgcSite());
        mission.Stage = MissionStage.Started;

        var smallHull = core.HullTypes[BoarderHullId];       // 504 → turret slots [2,4)
        Assert.True(smallHull.MaxFixedWeapons == 2 && smallHull.MaxWeapons == 4,
            "test precondition: hull 504 must have turret range [2,4)");

        var smallCarrier = new Ship(741, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(smallCarrier, mission.Sectors[0]);
        var boarder = new Ship(742, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(boarder, mission.Sectors[0]);

        // Occupy BOTH in-range slots (2 and 3) so no free equipped slot exists →
        // the walk must complete a full loop and fall back to observer.
        var occ1 = new Ship(751, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        occ1.TurretId = 2; occ1.ParentShip = smallCarrier; smallCarrier.AddChildShip(occ1);
        var occ2 = new Ship(752, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        occ2.TurretId = 3; occ2.ParentShip = smallCarrier; smallCarrier.AddChildShip(occ2);

        // Stale slot 5 inherited from a larger carrier ([2,6)) — OUT of [2,4).
        boarder.TurretId = 5;

        // Run the walk on a worker thread under a 5 s watchdog: a regression
        // spins forever → WhenAny returns the Delay → clean failure instead of
        // hanging the whole suite.
        var done = Task.Run(() => Srv.ChooseTurretSlot(boarder, smallCarrier));
        var completed = await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(done, completed), "ChooseTurretSlot must NOT hang on an out-of-range startMount (P313/H1)");
        short? result = await done;
        Assert.Null(result);                                // observer fallback, terminated
        Assert.Equal(2, smallCarrier.ChildShips.Count);     // occupancy untouched
    }

    /// <summary>P313 companion: with a free equipped slot present, a stale
    /// out-of-range TurretId is revalidated and lands on a VALID in-range slot
    /// (the same-carrier slot preference is preserved for legal ids, normalized
    /// for illegal ones) — never spinning.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Stale_out_of_range_start_revalidates_to_valid_slot()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "T1", civ.CivilizationId)], new RecordingIgcSite());
        mission.Stage = MissionStage.Started;

        var smallHull = core.HullTypes[BoarderHullId];       // 504 → turret slots [2,4)
        var smallCarrier = new Ship(761, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(smallCarrier, mission.Sectors[0]);
        var boarder = new Ship(762, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(boarder, mission.Sectors[0]);

        // Slot 2 equipped and free; slot 3 occupied → stale id 5 must clamp into
        // range and land on the real free slot 2.
        var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
        smallCarrier.MountPart(weapon, 1, (byte)smallHull.MaxFixedWeapons, 255);
        var occ = new Ship(763, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        occ.TurretId = 3; occ.ParentShip = smallCarrier; smallCarrier.AddChildShip(occ);

        boarder.TurretId = 5;                               // stale, out of [2,4)

        // Run under a 5 s watchdog: a regression spins forever → WhenAny returns
        // the Delay → clean failure instead of hanging the suite.
        var done = Task.Run(() => Srv.ChooseTurretSlot(boarder, smallCarrier));
        var completed = await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(done, completed), "ChooseTurretSlot must NOT hang on an out-of-range startMount (P313/H1)");
        short? result = await done;
        Assert.Equal((short)2, result);                     // revalidated into range, real free slot
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Board_with_equipped_turret_and_leave()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Board_without_equipped_turret_becomes_observer()
    {
        Require.Artwork();
        var server = new Srv();
        InjectBoardSpawn(server, equipTurret: false);         // slots [2,6) but nothing mounted
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Board_nonexistent_ship_nacks_only_requester()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Board_wrong_side_ship_nacks()
    {
        Require.Artwork();
        var server = new Srv();
        InjectBoardSpawn(server, equipTurret: true);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
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

    /// <summary>Watchdog pump: runs <paramref name="action"/> on a worker thread
    /// and fails cleanly (instead of hanging the whole suite) if it does not
    /// finish within <paramref name="timeoutSec"/>. Used to turn the P313/H1
    /// infinite-loop scenario into a fast, deterministic test failure if it ever
    /// regresses, while exercising the REAL synchronous sim path.</summary>
    private static void RunUnderWatchdog(Action action, int timeoutSec, string failMsg)
    {
        var finished = new ManualResetEventSlim(false);
        var t = Task.Run(() => { action(); finished.Set(); });
        Assert.True(finished.Wait(TimeSpan.FromSeconds(timeoutSec)), failMsg);
        t.Wait();
    }

    /// <summary>Three-ship, one-side, all-docked spawn for the P313 re-board
    /// scenario: <see cref="Big"/> = the LARGE carrier (hull 792, turret slots
    /// [2,6)); <see cref="Small"/> = the SMALL carrier (hull 504, slots [2,4));
    /// <see cref="Boarder"/> = the ship that will ride both. <see cref="Core"/>
    /// is captured for later equipment/occupancy setup.</summary>
    private sealed class ReboardSpawn
    {
        public Ship Big = null!, Small = null!, Boarder = null!;
        public StaticCore Core = null!;
    }

    private static ReboardSpawn InjectReboardSpawn(Srv server)
    {
        var sp = new ReboardSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sideId = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0).SideId;
            var station = P550TestDockHome.CapLandHome(mission, sideId);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            Ship ship;
            switch (n % 3)
            {
                case 0: ship = new Ship(id, core.HullTypes[CarrierHullId], StationPos(station) + new Vector3(25f, 0f, 0f), Vector3.Zero, Orientation.Identity); sp.Big = ship; break;
                case 1: ship = new Ship(id, core.HullTypes[BoarderHullId], StationPos(station) + new Vector3(35f, 0f, 0f), Vector3.Zero, Orientation.Identity); sp.Small = ship; break;
                default: ship = new Ship(id, core.HullTypes[BoarderHullId], StationPos(station) + new Vector3(45f, 0f, 0f), Vector3.Zero, Orientation.Identity); sp.Boarder = ship; break;
            }
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            sp.Core = core;
            n++;
            return (ship, sector);
        };
        return sp;
    }

    /// <summary>⭐ P313 / review H1 — LIVE PROOF of the exact player-triggerable
    /// infinite-loop scenario, exercised through the real
    /// <c>FM_C_BOARD_SHIP(103)</c> wire handler → <c>PerformBoard</c> →
    /// <c>ChooseTurretSlot</c> on the sim thread:
    /// <list type="number">
    /// <item>The boarder boards a LARGE carrier (hull 792, slots [2,6)) and
    /// lands on its top equipped slot 5 — so it now carries a TurretId (5) that
    /// is OUTSIDE a small carrier's range.</item>
    /// <item>STILL DOCKED and still parented (re-board WITHOUT leaving — legal
    /// in native, <c>FedSrv.CPP:2341-2345</c>), it boards a SMALL carrier (hull
    /// 504, slots [2,4)) whose every matching slot is occupied.</item>
    /// <item>The stale start 5 lies out of [2,4), so pre-fix the walk's wrap
    /// <c>turret &gt;= maxMount → minMount</c> never revisits 5 → the sim thread
    /// spins forever inside <c>TickSim</c>, freezing the whole server. Post-fix
    /// the start is clamped into range, the board resolves to observer, and the
    /// server keeps ticking normally.</item>
    /// </list>
    /// The ticking is guarded by a watchdog so a regression fails fast rather
    /// than hanging the suite.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_reboard_smaller_full_carrier_does_not_hang()
    {
        Require.Artwork();
        var server = new Srv();
        var sp = InjectReboardSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby
        ushort port = server.BoundPort;

        var big = new WireClient(); var small = new WireClient(); var boarder = new WireClient();
        big.Connect(port, "BIG"); small.Connect(port, "SMALL"); boarder.Connect(port, "BOARDER");
        big.Logon(Request("BIG")); small.Logon(Request("SMALL")); boarder.Logon(Request("BOARDER"));
        PumpUntil(server, () => big.ShipAssignment is not null && small.ShipAssignment is not null && boarder.ShipAssignment is not null, 4000);

        // Match each client to its ROLE by spawned ShipId (robust to socket order).
        short bSid = big.ShipAssignment!.Ship.ShipId, sSid = small.ShipAssignment!.Ship.ShipId, dSid = boarder.ShipAssignment!.Ship.ShipId;
        WireClient bigC = null!, smallC = null!, boarderC = null!;
        if (bSid == sp.Big.Id) bigC = big; else if (sSid == sp.Big.Id) bigC = small; else if (dSid == sp.Big.Id) bigC = boarder;
        if (bSid == sp.Small.Id) smallC = big; else if (sSid == sp.Small.Id) smallC = small; else if (dSid == sp.Small.Id) smallC = boarder;
        if (bSid == sp.Boarder.Id) boarderC = big; else if (sSid == sp.Boarder.Id) boarderC = small; else if (dSid == sp.Boarder.Id) boarderC = boarder;
        Assert.NotNull(bigC); Assert.NotNull(smallC); Assert.NotNull(boarderC);

        WaitDocked(server, (bigC, sp.Big), (smallC, sp.Small), (boarderC, sp.Boarder));

        // Equip the BIG carrier's TOP slot (MaxWeapons-1 = 5) so the boarder lands
        // there — giving it a stale TurretId OUT of the small carrier's [2,4) range.
        var weapon = sp.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
        sp.Big.MountPart(weapon, 1, (byte)(sp.Big.HullType.MaxWeapons - 1), 255);

        // Occupy the SMALL carrier's every in-range slot (2 and 3) so no free
        // equipped slot exists there → the walk must complete a full loop.
        var smallHull = sp.Small.HullType;
        var o1 = new Ship(8971, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        o1.TurretId = (short)smallHull.MaxFixedWeapons; o1.ParentShip = sp.Small; sp.Small.AddChildShip(o1);
        var o2 = new Ship(8972, smallHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        o2.TurretId = (short)(smallHull.MaxFixedWeapons + 1); o2.ParentShip = sp.Small; sp.Small.AddChildShip(o2);

        // ── 1) Board the BIG carrier → lands on slot 5, stays docked & parented ──
        boarderC.SendBoard(sp.Big.Id);
        RunUnderWatchdog(() => PumpUntil(server, () => ReferenceEquals(sp.Boarder.ParentShip, sp.Big) && sp.Boarder.TurretId == (short)(sp.Big.HullType.MaxWeapons - 1), PumpMax),
            10, "board onto big carrier did not set the stale high TurretId");
        Assert.Same(sp.Big, sp.Boarder.ParentShip);
        Assert.Equal((short)(sp.Big.HullType.MaxWeapons - 1), sp.Boarder.TurretId);   // stale slot 5

        // ── 2) Still docked/parented, board the SMALL carrier (slots all taken) ──
        //    Pre-fix this is the infinite-loop trigger; post-fix → observer, no hang.
        boarderC.SendBoard(sp.Small.Id);
        RunUnderWatchdog(() => PumpUntil(server, () => ReferenceEquals(sp.Boarder.ParentShip, sp.Small) && sp.Boarder.TurretId is null, PumpMax),
            10, "re-board onto the smaller full carrier must NOT hang (P313/H1) — observer expected");
        Assert.Same(sp.Small, sp.Boarder.ParentShip);                 // transferred WITHOUT leaving
        Assert.Null(sp.Boarder.TurretId);                             // observer — no free equipped slot
        Assert.DoesNotContain(sp.Big.ChildShips, c => ReferenceEquals(c, sp.Boarder));   // detached from big
        Assert.Contains(sp.Small.ChildShips, c => ReferenceEquals(c, sp.Boarder));       // now on small

        // ── 3) The server keeps ticking normally afterward — no residual stall ──
        RunUnderWatchdog(() => { for (int i = 0; i < 200; i++) { server.TickSim(); Thread.Sleep(1); } },
            10, "server failed to keep ticking after the re-board (P313/H1)");

        Console.WriteLine($"BOARD-H1-LIVE: boarder {sp.Boarder.Id} boarded big({sp.Big.Id}) slot {sp.Big.HullType.MaxWeapons - 1}, stayed docked, then re-boarded small({sp.Small.Id}) with all slots occupied -> observer, no hang, server kept ticking");
    }
}

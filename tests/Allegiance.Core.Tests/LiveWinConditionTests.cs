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
/// P178 — win-by-elimination (<c>CheckForVictoryByInactiveSides</c>,
/// <c>fsmission.cpp:3767-3788</c> + trigger site <c>:5371-5427</c>) and the
/// simplified <c>GAME_OVER</c>(62) announcement it produces. Layers:
///  (1) codec tests pin the exact bytes of GAME_OVER — most importantly the
///      <b>resolved <c>Color</c> width (16-byte RGBA float)</b>, the one open
///      question the task prompt left — plus a full round-trip;
///  (2) live loopback-in-process-server proofs for each elimination scenario:
///      two-sides-one-each declares a winner, three-sides-one-each delays the
///      call, the last player leaving is a draw, an already-over mission never
///      re-broadcasts, and a multi-mission server scopes the check per mission;
///  (3) the existing <c>mission.Stage != MissionStage.Started</c> gates are
///      confirmed (control + refusal) to refuse their action once the mission
///      is <see cref="MissionStage.Over"/>.
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveWinConditionTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // P108/P110 concrete hulls (measured from PCore001.igc):
    //   792 = a turret-capable carrier; 504 = a 500-credit normal hull (boarder)
    private const short CarrierHullId = 792;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Win Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigThreeSides() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Win Test 3", Map = "XXV_simple.igc", Teams = 3, CivIds = [1, 3, 2], SideNames = ["Alpha", "Bravo", "Charlie"] },
        ],
    };

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Game A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Game B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 901u, DwCookie = 0x901u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8860000000000000000ul, DrmHash = "wincond",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static bool InSector(Srv server, short id) =>
        server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().Any(s => s.Id == id);

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen side,
    /// so the multi-mission test can seat peers on mission B (the default
    /// spawner always uses <c>_missions[0]</c>). Peer 0 → mission 0 / side 0;
    /// peer 1 → mission 1 / side 0; peer 2 → mission 0 / side 1.</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);   // A0→A/s0, B0→B/s0, A1→A/s1
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 750f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    /// <summary>Plant a carrier and boarder inside a friendly station's dock
    /// radius (same-side), so the first ticks latch them docked — the same
    /// docked-spawn trick LiveLogoffTests/LiveSuicideShipDeleteTests use.</summary>
    private sealed class BoardSpawn { public Ship Carrier = null!, Boarder = null!; }

    private static BoardSpawn InjectDockedBoardSpawn(Srv server)
    {
        var sp = new BoardSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[n == 0 ? CarrierHullId : (short)504];
            var ship = new Ship(shipId, hull, StationPos(station) + new Vector3(25f + n * 10f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            if (n == 0)
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

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Spawn the peer on a <b>Lifepod</b> hull in open space (720,730,920),
    /// so a real SUICIDE is a genuine kill (native suicide on a non-lifepod ship
    /// routes to in-space eject, FedSrv.CPP:11779 — see LiveSuicideShipDeleteTests).
    /// Placed away from any station so the ship is flying (not docked), letting
    /// the suicide reach the kill branch through both of HandleSuicide's gates.</summary>
    private static void InjectLifepodSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var ship = new Ship(id, hull, new Vector3(720f, 730f, 920f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
    }

    private static void WaitDocked(Srv server, params Ship[] ships)
    {
        int n = PumpUntil(server, () => ships.All(s => s.StationId is not null), 400);
        Assert.True(n < 400, "ships did not dock");
    }

    // ── the wire client ────────────────────────────────────────────────────

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<GameOver> _gameOvers = new();
        private readonly List<ShipDelete> _deletes = new();
        private int _boardNacks;

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<GameOver> GameOvers { get { lock (_gate) return _gameOvers.ToArray(); } }
        public IReadOnlyList<ShipDelete> Deletes { get { lock (_gate) return _deletes.ToArray(); } }
        public int BoardNacks { get { lock (_gate) return _boardNacks; } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendSuicide() => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new Suicide().Encode() }), guaranteed: true);
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
                    switch (fmid)
                    {
                        case LogonAck.Fmid: _ack = LogonAck.Decode(m); break;
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); break;
                        case GameOver.Fmid: _gameOvers.Add(GameOver.Decode(m)); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                        case 178: _boardNacks++; break;   // BOARD_NACK
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    // ── codecs ─────────────────────────────────────────────────────────────

    /// <summary>P178 — GAME_OVER(62) round-trips the full payload: reason
    /// string, winner id, side count, eject flag, and all six side records
    /// (name / civ / 4-float color / seven stat counters).</summary>
    [Fact]
    public void GameOver_round_trips_full_payload()
    {
        var sides = new SideEndgameInfo[GameOver.MaxSides];
        for (int i = 0; i < GameOver.MaxSides; i++)
            sides[i] = new SideEndgameInfo(
                i == 0 ? "Alpha" : i == 1 ? "Bravo" : "<bug>",
                (short)(i == 0 ? 911 : i == 1 ? 912 : -1),
                0.1f, 0.2f, 0.3f, 1f,
                (short)(10 * i), (short)(11 * i), (short)(12 * i),
                (short)(13 * i), (short)(14 * i), (short)(15 * i), (short)(16 * i));

        var msg = new GameOver
        {
            Sides = sides,
            GameoverMessage = "Alpha won by outlasting all other sides.",
            SideWinner = 913,
            NumSides = 2,
            EjectPods = false,
        };

        var d = GameOver.Decode(msg.Encode());
        Assert.Equal(GameOver.Fmid, d.GetFmid());
        Assert.Equal("Alpha won by outlasting all other sides.", d.GameoverMessage);
        Assert.Equal((short)913, d.SideWinner);
        Assert.Equal((byte)2, d.NumSides);
        Assert.False(d.EjectPods);
        Assert.Equal("Alpha", d.Sides[0].SideName);
        Assert.Equal("Bravo", d.Sides[1].SideName);
        Assert.Equal((short)911, d.Sides[0].CivId);
        Assert.Equal(0.2f, d.Sides[1].G);
        Assert.Equal((short)40, d.Sides[4].CKills);   // 10 × 4
        Assert.Equal((short)-1, d.Sides[5].CivId);    // <bug> placeholder keeps civID NA
    }

    /// <summary>P178 — pins the resolved wire layout, especially the previously
    /// OPEN <c>Color</c> width: 4× <c>float</c> (16 bytes, RGBA), which with
    /// natural alignment makes <c>SideEndgameInfo</c> 60 bytes (1-byte pad at
    /// 25 aligning <c>civID</c> short to even, Color at 28, 2 tail-pad bytes at
    /// 58). Fixed message region is 372 bytes. Offsets asserted discretely so a
    /// regression names the exact field that drifted.</summary>
    [Fact]
    public void GameOver_layout_pins_Color_width_and_pads()
    {
        Assert.Equal(60, SideEndgameInfo.Size);
        Assert.Equal(372, GameOver.FixedSize);

        var sides = new SideEndgameInfo[GameOver.MaxSides];
        sides[0] = new SideEndgameInfo("A", 7, 1f, 0.5f, 0f, 1f, 1, 2, 3, 4, 5, 6, 7);
        for (int i = 1; i < GameOver.MaxSides; i++)
            sides[i] = new SideEndgameInfo("<bug>", -1, 0.5f, 0.5f, 0.5f, 1f, 0, 0, 0, 0, 0, 0, 0);

        var msg = new GameOver { Sides = sides, GameoverMessage = "A", SideWinner = 907, NumSides = 2, EjectPods = false };
        byte[] b = msg.Encode();
        Assert.Equal(372 + 2, b.Length);   // blob = "A"(1) + NUL(1)

        Assert.Equal(b.Length, BinaryPrimitives_ReadU16(b, 0)); // cbmsg
        Assert.Equal(62, BinaryPrimitives_ReadU16(b, 2));       // fmid
        Assert.Equal(372, BinaryPrimitives_ReadU16(b, 4));      // ib (blob at 372)
        Assert.Equal(2, BinaryPrimitives_ReadU16(b, 6));        // cb (blob length incl NUL)

        // First side record starts at 8; spot-check the alignment-sensitive spots.
        Assert.Equal(0, b[33]);                                   // record0 pad @33 aligning civID short to even
        Assert.Equal(7, BinaryPrimitives_ReadI16(b, 34));         // civID @34 (rec-offset 26)
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), BinaryPrimitives_ReadU32(b, 36));    // Color.r @36
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.5f), BinaryPrimitives_ReadU32(b, 40));  // Color.g @40
        Assert.Equal(BitConverter.SingleToUInt32Bits(0f), BinaryPrimitives_ReadU32(b, 44));    // Color.b @44
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), BinaryPrimitives_ReadU32(b, 48));    // Color.a @48
        Assert.Equal(1, BinaryPrimitives_ReadI16(b, 52));         // cKills @52
        Assert.Equal(7, BinaryPrimitives_ReadI16(b, 64));         // cArtifacts @64
        Assert.Equal(0, b[66]); Assert.Equal(0, b[67]);           // tail pad → stride 60

        // Second record begins at 8 + 60.
        Assert.Equal("<bug>", SideEndgameInfo.Read(b.AsSpan(68, 60)).SideName);

        // Tail fixed region.
        Assert.Equal(907, BinaryPrimitives_ReadI16(b, 368));      // iSideWinner @368
        Assert.Equal(2, b[370]);                                   // nNumSides @370
        Assert.Equal(0, b[371]);                                   // bEjectPods @371
        Assert.Equal((byte)'A', b[372]);                           // blob @372
        Assert.Equal(0, b[373]);                                   // trailing NUL
    }

    // ── live: win-by-elimination scenarios ────────────────────────────────

    /// <summary>⭐ Two sides, one player each: the second player disconnects →
    /// the first side's remaining connection receives a real GAME_OVER(62)
    /// with <c>iSideWinner</c> = its own side and the mission is now
    /// <see cref="MissionStage.Over"/>.</summary>
    [Fact]
    public void Second_player_disconnect_declares_first_side_winner()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var alpha = new WireClient();
        using var bravo = new WireClient();
        alpha.Connect(server.BoundPort, "ALPHA"); bravo.Connect(server.BoundPort, "BRAVO");
        alpha.Logon(Request("ALPHA")); bravo.Logon(Request("BRAVO"));
        int got = PumpUntil(server, () => alpha.ShipAssignment is not null && bravo.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no assignments");
        short alphaId = alpha.ShipAssignment!.Ship.ShipId;
        short bravoId = bravo.ShipAssignment!.Ship.ShipId;
        Assert.NotEqual(alphaId, bravoId);

        short alphaSide = server.Ships.First(s => s.Ship.Id == alphaId).Ship.SideId!.Value;

        // P439 — the shared game-over path rotates _missions[0] to a fresh
        // NotStarted mission, so the "mission is Over" assertions target the
        // ORIGINAL mission object (captured before the disconnect), which retains
        // Stage=Over exactly as native's restartable game-over.
        var mission0 = server.Missions[0];

        bravo.Dispose();                                     // abrupt drop → ReleasePeer
        int seen = PumpUntil(server, () => alpha.GameOvers.Count > 0 && mission0.Stage == MissionStage.Over, 800);
        Assert.True(seen < 800, "survivor did not receive GAME_OVER / stage not Over");

        var go = Assert.Single(alpha.GameOvers);
        Assert.Equal(alphaSide, go.SideWinner);
        Assert.Equal((byte)2, go.NumSides);
        Assert.Equal(MissionStage.Over, mission0.Stage);

        Console.WriteLine($"WIN-LIVE: dropped Bravo ship {bravoId}; survivor {alphaId} (side {alphaSide}) got " +
                          $"GAME_OVER iSideWinner={go.SideWinner} ({go.GameoverMessage}) after {seen} ticks");
    }

    /// <summary>⭐ Three sides, one player each: one disconnects → two sides
    /// still have players, so no GAME_OVER yet and the mission stays Started.</summary>
    [Fact]
    public void One_of_three_disconnect_defers_the_decision()
    {
        using var server = new Srv();
        server.Boot(ConfigThreeSides());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var clients = new[] { new WireClient(), new WireClient(), new WireClient() };
        try
        {
            clients[0].Connect(server.BoundPort, "P0"); clients[1].Connect(server.BoundPort, "P1"); clients[2].Connect(server.BoundPort, "P2");
            for (int i = 0; i < 3; i++) clients[i].Logon(Request($"P{i}"));
            int got = PumpUntil(server, () => clients.All(c => c.ShipAssignment is not null), 4000);
            Assert.True(got < 4000, "no assignments");

            clients[2].Dispose();                            // side 2 empties; sides 0 & 1 remain
            int settled = PumpUntil(server,
                () => !server.Ships.Any(s => s.Ship.Id == clients[2].ShipAssignment!.Ship.ShipId), 800);
            Assert.True(settled < 800, "dropped peer not released");

            Assert.Equal(MissionStage.Started, server.Missions[0].Stage);
            Assert.Empty(clients[0].GameOvers);
            Assert.Empty(clients[1].GameOvers);

            Console.WriteLine("WIN-LIVE: 3 sides, dropped one — no GAME_OVER, stage still Started (correct)");
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    /// <summary>⭐ The last remaining player disconnects: now ZERO sides have
    /// players → a draw ("All sides were destroyed"), <c>Stage</c> becomes
    /// <c>Over</c>, and the GAME_OVER is broadcast to nobody (there is no one
    /// left to receive it — the honest outcome). Assert on the stage transition
    /// and on the codec producing the draw payload, not on a peer receipt.</summary>
    [Fact]
    public void Last_player_disconnect_is_a_draw()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var solo = new WireClient();
        solo.Connect(server.BoundPort, "SOLO");
        solo.Logon(Request("SOLO"));
        int got = PumpUntil(server, () => solo.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no assignment");
        short soloId = solo.ShipAssignment!.Ship.ShipId;
        Assert.Contains(server.Ships, s => s.Ship.Id == soloId);

        // P439 — rotation replaces _missions[0]; the "draw set it Over" assertions
        // target the ORIGINAL mission object.
        var mission0 = server.Missions[0];

        solo.Dispose();                                      // the very last player leaves
        int settled = PumpUntil(server, () => mission0.Stage == MissionStage.Over, 800);
        Assert.True(settled < 800, "draw did not set stage Over");

        // No one remains to receive it — assert the payload the codec would
        // have broadcast (draw: iSideWinner == NA) is the honest one.
        var draw = GameOver.ForMission(mission0, GameOver.NA, "All sides were destroyed");
        Assert.Equal(GameOver.NA, draw.SideWinner);
        Assert.Equal("All sides were destroyed", draw.GameoverMessage);

        Console.WriteLine($"WIN-LIVE: last player {soloId} left → draw, stage Over after {settled} ticks (no recipients remain)");
    }

    /// <summary>⭐ Idempotency: after a mission is already Over (game decided),
    /// a later disconnect must NOT re-broadcast GAME_OVER — the surviving peer
    /// that witnessed the first one gets exactly one, and Stage stays Over.</summary>
    [Fact]
    public void No_second_game_over_after_already_over()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var alpha = new WireClient();
        using var bravo = new WireClient();
        alpha.Connect(server.BoundPort, "ALPHA"); bravo.Connect(server.BoundPort, "BRAVO");
        alpha.Logon(Request("ALPHA")); bravo.Logon(Request("BRAVO"));
        int got = PumpUntil(server, () => alpha.ShipAssignment is not null && bravo.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no assignments");
        short alphaId = alpha.ShipAssignment!.Ship.ShipId;

        // P439 — rotation replaces _missions[0] on the first game-over; the
        // idempotency ("no second GAME_OVER / stage stays Over") is verified on
        // the ORIGINAL mission object, which retains Stage=Over.
        var mission0 = server.Missions[0];

        bravo.Dispose();                                     // triggers the win
        int first = PumpUntil(server, () => alpha.GameOvers.Count > 0, 800);
        Assert.True(first < 800, "no first GAME_OVER");
        Assert.Equal(MissionStage.Over, mission0.Stage);

        alpha.Dispose();                                     // the survivor also leaves — must NOT re-fire
        int settled = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == alphaId), 800);
        Assert.True(settled < 800, "survivor not released");
        Assert.Equal(MissionStage.Over, mission0.Stage);
        Assert.Single(alpha.GameOvers);                      // still exactly one

        Console.WriteLine("WIN-LIVE: second disconnect after Over did not re-broadcast GAME_OVER (idempotent)");
    }

    /// <summary>⭐ Multi-mission server (two Games): a disconnect in mission A's
    /// last-standing-side scenario ends mission A but leaves mission B untouched —
    /// its Stage stays Started and its peer receives no GAME_OVER.</summary>
    [Fact]
    public void Multi_mission_disconnect_scopes_per_mission()
    {
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        Assert.Equal(2, server.Missions.Count);
        // P439 — rotation replaces _missions[0] when mission A ends; assert on
        // the ORIGINAL missionA object, which retains Stage=Over. Mission B is
        // untouched.
        var missionA = server.Missions[0];

        using var a0 = new WireClient();   // mission A, side 0
        using var b0 = new WireClient();   // mission B, side 0
        using var a1 = new WireClient();   // mission A, side 1
        a0.Connect(server.BoundPort, "A0"); b0.Connect(server.BoundPort, "B0"); a1.Connect(server.BoundPort, "A1");
        a0.Logon(Request("A0")); b0.Logon(Request("B0")); a1.Logon(Request("A1"));
        int got = PumpUntil(server, () => a0.ShipAssignment is not null && b0.ShipAssignment is not null && a1.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no assignments");
        short a1Id = a1.ShipAssignment!.Ship.ShipId;

        a1.Dispose();                                        // empties mission A's side 1 → mission A over
        int seen = PumpUntil(server, () => missionA.Stage == MissionStage.Over, 800);
        Assert.True(seen < 800, "mission A did not end");

        // Mission A ended; mission B is untouched.
        Assert.Equal(MissionStage.Over, missionA.Stage);
        Assert.Equal(MissionStage.Started, server.Missions[1].Stage);
        Assert.Contains(a0.GameOvers, g => g.SideWinner == missionA.Sides[0].Id);
        Assert.Empty(b0.GameOvers);                          // mission B's peer heard nothing
        Assert.Contains(server.Ships, s => s.Ship.Id == b0.ShipAssignment!.Ship.ShipId);

        Console.WriteLine($"WIN-MULTI-LIVE: dropped {a1Id} in mission A — A over (winner {missionA.Sides[0].Id}), " +
                          $"mission B stage {server.Missions[1].Stage}, B peer gameOvers={b0.GameOvers.Count}");
    }

    // ── live: existing Stage gates refuse once Over ────────────────────────

    /// <summary>Control for the gate tests: while the mission is Started, a
    /// real SUICIDE reaches the kill path — the ship is removed and the peer
    /// sees the SDR_KILLED delete. Proves the message/handler reachability the
    /// refusal test then negates.</summary>
    [Fact]
    public void Suicide_kills_the_ship_while_started()
    {
        using var server = new Srv();
        InjectLifepodSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var client = new WireClient();
        client.Connect(server.BoundPort, "GUINEA");
        client.Logon(Request("GUINEA"));
        int got = PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no assignment");
        short myId = client.ShipAssignment!.Ship.ShipId;

        client.SendSuicide();
        int killed = PumpUntil(server,
            () => client.Deletes.Any(d => d.ShipId == myId && d.Sdr == ShipDelete.SdrKilled),
            800);
        Assert.True(killed < 800, "suicide did not kill the ship while Started");
        Assert.Contains(client.Deletes, d => d.ShipId == myId && d.Sdr == ShipDelete.SdrKilled);

        Console.WriteLine($"WIN-GATE-LIVE: suicide while Started killed ship {myId} (control)");
    }

    /// <summary>⭐ Representative Stage gate #1: once the mission is Over, the
    /// SUICIDE handler's <c>mission.Stage != MissionStage.Started</c> gate
    /// (Server.cs, P103, FedSrv.CPP:6337) refuses — the ship is NOT killed and
    /// no SDR_KILLED delete goes out.</summary>
    [Fact]
    public void Suicide_refused_once_mission_over()
    {
        using var server = new Srv();
        InjectLifepodSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var client = new WireClient();
        client.Connect(server.BoundPort, "SURVIVOR");
        client.Logon(Request("SURVIVOR"));
        int got = PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no assignment");
        short myId = client.ShipAssignment!.Ship.ShipId;

        server.Missions[0].Stage = MissionStage.Over;        // end the mission behind the gate
        client.SendSuicide();
        int settled = PumpUntil(server, () => server.Ships.Any(s => s.Ship.Id == myId), 100);
        // Give any (wrong) kill a chance to propagate, then assert refusal.
        for (int i = 0; i < 100 && client.Deletes.Any(d => d.ShipId == myId); i++) server.TickSim();

        Assert.Contains(server.Ships, s => s.Ship.Id == myId);   // ship not killed
        Assert.DoesNotContain(client.Deletes, d => d.ShipId == myId && d.Sdr == ShipDelete.SdrKilled);

        Console.WriteLine("WIN-GATE-LIVE: suicide once mission Over was refused — ship survived");
    }

    /// <summary>⭐ Representative Stage gate #2: the BOARD_SHIP handler's gate
    /// (Server.cs, FedSrv.CPP:6388) refuses once the mission is Over — the
    /// boarder's request is answered with BOARD_NACK and its ParentShip stays
    /// null. Control (Started) shown by the docked pair boarding successfully.</summary>
    [Fact]
    public void Board_refused_once_mission_over()
    {
        // ── Control leg: boarding succeeds while Started ──────────────────
        using (var control = new Srv())
        {
            var sp = InjectDockedBoardSpawn(control);
            control.Boot(Config());
            foreach (var __m in control.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
            using var carrier = new WireClient();
            using var boarder = new WireClient();
            carrier.Connect(control.BoundPort, "CARRIER"); boarder.Connect(control.BoundPort, "BOARDER");
            carrier.Logon(Request("CARRIER")); boarder.Logon(Request("BOARDER"));
            int got = PumpUntil(control, () => carrier.ShipAssignment is not null && boarder.ShipAssignment is not null, 4000);
            Assert.True(got < 4000, "no assignments");
            short carId = sp.Carrier.Id;
            WaitDocked(control, sp.Carrier, sp.Boarder);

            boarder.SendBoard(carId);
            int boarded = PumpUntil(control, () => sp.Boarder.ParentShip is not null, 1200);
            Assert.True(boarded < 1200, "control: board should succeed while Started");
            Assert.Equal(0, boarder.BoardNacks);
        }

        // ── Refusal leg: the same setup, but the mission is Over ──────────
        using (var server = new Srv())
        {
            var sp = InjectDockedBoardSpawn(server);
            server.Boot(Config());
            foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
            using var carrier = new WireClient();
            using var boarder = new WireClient();
            carrier.Connect(server.BoundPort, "CARRIER"); boarder.Connect(server.BoundPort, "BOARDER");
            carrier.Logon(Request("CARRIER")); boarder.Logon(Request("BOARDER"));
            int got = PumpUntil(server, () => carrier.ShipAssignment is not null && boarder.ShipAssignment is not null, 4000);
            Assert.True(got < 4000, "no assignments");
            WaitDocked(server, sp.Carrier, sp.Boarder);

            server.Missions[0].Stage = MissionStage.Over;    // end the mission behind the gate
            boarder.SendBoard(sp.Carrier.Id);
            int nacked = PumpUntil(server, () => boarder.BoardNacks > 0, 800);
            Assert.True(nacked < 800, "boarder should get BOARD_NACK once the mission is Over");
            Assert.Null(sp.Boarder.ParentShip);              // refused — not boarded

            Console.WriteLine("WIN-GATE-LIVE: BOARD once mission Over was refused with BOARD_NACK (parent stayed null)");
        }
    }

    private static ushort BinaryPrimitives_ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static short BinaryPrimitives_ReadI16(byte[] b, int o) => unchecked((short)(b[o] | (b[o + 1] << 8)));
    private static uint BinaryPrimitives_ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}

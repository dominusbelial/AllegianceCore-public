using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Srv = global::Allegiance.Server.Server;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P775 — the native PLAYERINFO(10) moments. A native server sends
/// FM_S_PLAYERINFO from exactly three call sites of SendPlayerInfo
/// (FedSrv/FedSrv.CPP:2056-2135): (1) a player joins the mission → the whole
/// mission (fsmission.cpp:408-411, iSide = SIDE_TEAMLOBBY so no announcement
/// fires yet, appmsg.cpp:2286-2290); (2) the joiner gets one PLAYERINFO per
/// ship already in the mission — every ship, drones included, every sector
/// (fsmission.cpp:3958-3968); (3) a drone is created → the whole mission
/// (FedSrv.CPP:2269). Nothing is sent per sighting, per rank change or on a
/// side switch (FM_S_JOIN_SIDE is its own message, fsmission.cpp:621-624, and
/// native never re-sends PLAYERINFO there). <see cref="NativeJoinAnnouncer"/>
/// replays one recipient's captured stream under native's client rules
/// (clintlib/appmsg.cpp:2217-2290, clintlib/clintlib.cpp:4101-4160) and counts
/// the "X has joined your team / side" announcements plus the JOIN_SIDE-before-
/// PLAYERINFO dereference violation (appmsg.cpp:2912-2914).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P775PlayerInfoMomentsTests
{
    /// <summary>Replays one recipient's captured stream with native's rules:
    /// tracks the stage from MISSION_STAGE, own ship/side from JOINED_MISSION,
    /// the own PLAYERINFO and own JOIN_SIDE; keeps a PlayerInfo table keyed by
    /// ship id; counts an announcement for each PLAYERINFO of another human
    /// (DtidDrone == NA, ISide &gt;= 0) and each JOIN_SIDE of another ship while
    /// the stage is Started and the own side is a real side
    /// (clintlib.cpp:4142-4160); flags a violation when a JOIN_SIDE arrives for
    /// a ship with no PlayerInfo yet (appmsg.cpp:2912-2914 — native would
    /// dereference null).</summary>
    internal sealed class NativeJoinAnnouncer
    {
        private short? _ownShip;
        private short? _ownSide;
        private int _stage = MissionStageMessage.StageNotStarted;
        private readonly HashSet<short> _playerInfos = new();
        // Native's MISSIONDEF carries the mission's live stage
        // (Inc/Messages.h:301 `STAGE stage`, from m_misdef.stage), so a native
        // joiner already knows "Started" when its join burst's PLAYERINFOs
        // arrive. The port's MISSIONDEF always says 0 (Server.Lobby.cs
        // CompleteJoinAndLaunch) and the real stage follows later as
        // MISSION_STAGE, which would hide every join-burst announcement from
        // this replay. A test that checks the join-burst ORDER passes the live
        // stage here, so MISSIONDEF is replayed as native would fill it.
        private readonly int? _missionDefStage;
        // Native never sends JOIN_SIDE to a connection that has not joined the
        // current mission — its messages to a connection begin with that
        // connection's own join burst. The port's game-over rotation replays
        // JOIN_SIDEs (phase 2) to migrants whose fresh join burst (MISSIONDEF +
        // JOINED_MISSION, phase 3) has not landed yet. The ONLY JOIN_SIDE such a
        // recipient may get without a PlayerInfo is its OWN fresh ship's (the
        // announce helper skips the subject's own connection) — but its fresh
        // ship id is unknown until JOINED_MISSION. So JOIN_SIDEs without a
        // PlayerInfo that arrive before JOINED_MISSION are held, and at
        // JOINED_MISSION every held one for ANOTHER ship is a violation.
        // Epoch: reset at GAME_OVER(62) / MISSION_STAGE(Over), set at
        // JOINED_MISSION(95).
        private bool _joinedCurrentMission;
        private readonly List<short> _heldJoinSides = new();

        public NativeJoinAnnouncer(int? missionDefStage = null) => _missionDefStage = missionDefStage;

        public int Announcements { get; private set; }
        public List<string> Violations { get; } = new();

        public void Feed(IEnumerable<byte[]> packets)
        {
            foreach (var packet in packets)
                foreach (var message in FedPacket.Unpack(packet))
                    FeedMessage(message);
        }

        public void FeedMessage(byte[] message)
        {
            if (message.Length < 4) return;
            ushort fmid = BitConverter.ToUInt16(message, 2);
            switch (fmid)
            {
                case MissionStageMessage.Fmid:
                    _stage = MissionStageMessage.Decode(message).Stage;
                    if (_stage == MissionStageMessage.StageOver)
                    {
                        _joinedCurrentMission = false;
                        // The port client wipes its ship table at Over
                        // (ClientWorld BeginNextMission) — a fresh mission's
                        // JOIN_SIDE needs a fresh PLAYERINFO.
                        _playerInfos.Clear();
                    }
                    break;
                case GameOver.Fmid:
                    _joinedCurrentMission = false;
                    break;
                case MissionDef.Fmid:
                    // Not an epoch reset: the port sends MISSIONDEF twice per
                    // join burst (before AND after JOINED_MISSION), so a reset
                    // here would silently stop judging every later JOIN_SIDE.
                    if (_missionDefStage is { } liveStage) _stage = liveStage;
                    break;
                case JoinedMission.Fmid:
                    _ownShip = JoinedMission.Decode(message).ShipId;
                    _joinedCurrentMission = true;
                    foreach (var held in _heldJoinSides)
                        if (held != _ownShip && !_playerInfos.Contains(held))
                            Violations.Add($"JOIN_SIDE for ship {held} with no PLAYERINFO yet, before JOINED_MISSION (appmsg.cpp:2912-2914)");
                    _heldJoinSides.Clear();
                    break;
                case PlayerInfo.Fmid:
                {
                    var info = PlayerInfo.Decode(message);
                    if (info.ShipId == _ownShip) { _ownSide = info.ISide; break; }   // F — the own-ship side carrier
                    _playerInfos.Add(info.ShipId);
                    // clintlib.cpp:4142-4160: a human on a real side while
                    // Started and we hold a real side → announce.
                    if (info.DtidDrone == PlayerInfo.NA && info.ISide >= 0
                        && _stage == MissionStageMessage.StageStarted
                        && _ownSide is { } s && s >= 0 && info.ShipId != _ownShip)
                        Announcements++;
                    break;
                }
                case JoinSide.Fmid:
                {
                    var join = JoinSide.Decode(message);
                    if (join.ShipId == _ownShip) { _ownSide = join.SideId; break; }
                    // appmsg.cpp:2912-2914: FindPlayer → AddPlayerToSide with
                    // NO null check — a native client needs the PlayerInfo first.
                    if (!_playerInfos.Contains(join.ShipId))
                    {
                        if (_joinedCurrentMission)
                            Violations.Add($"JOIN_SIDE for ship {join.ShipId} with no PLAYERINFO yet (appmsg.cpp:2912-2914)");
                        else
                            _heldJoinSides.Add(join.ShipId);   // judged at JOINED_MISSION (own fresh ship exempt)
                    }
                    if (_stage == MissionStageMessage.StageStarted
                        && _ownSide is { } s && s >= 0)
                        Announcements++;
                    break;
                }
            }
        }
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 919u, DwCookie = 0x919u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9100000000000000000ul, DrmHash = "p775",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Logon + seat. P852 — the explicit POSITIONREQ send is gone:
    /// with the native-faithful RequestPosition park (fsmission.cpp:4749-4751),
    /// a post-seat explicit side request on a leader-vetted side (AutoAccept
    /// off) now legitimately parks the bot in the team lobby, changing the
    /// announcement stream this file measures. The seat itself always came
    /// from the bot's lazy POSITIONREQ(NA) auto-pick drain (round-robin sides;
    /// the explicit send never moved anyone — a pending request used to keep
    /// the requester seated, the SIM-0608 divergence P852 fixed), so dropping
    /// it preserves the seating this file's moment assertions were written
    /// against.</summary>
    private static FltBot Seat(Srv server, string name, short sideId)
    {
        var bot = new FltBot();
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int got = PumpUntil(server, () => bot.Ack is not null, 4000);
        Assert.True(got < 4000, $"{name}: no logon ack");
        got = PumpUntil(server, () => bot.ShipAssignment is not null, 6000);
        Assert.True(got < 6000, $"{name}: no ship assignment");
        return bot;
    }

    /// <summary>Ordered outbound capture of EVERY connection (SendPeer lane —
    /// PLAYERINFO/JOIN_SIDE/EXPORT are all ordered sends), indexed per test so
    /// "after this point" windows are sliceable.</summary>
    private sealed class Capture
    {
        public readonly Dictionary<uint, List<byte[]>> ByConn = new();
        public void Attach(Srv server)
        {
            server._testSendCapture = (conn, packet) =>
            {
                lock (ByConn)
                {
                    if (!ByConn.TryGetValue(conn.Value, out var list)) ByConn[conn.Value] = list = new();
                    list.Add(packet);
                }
            };
        }
        public List<byte[]> Stream(Srv server, FltBot bot)
        {
            var conn = server.TestSnapshotPeers().Single(p => p.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Conn;
            lock (ByConn) return ByConn.TryGetValue(conn.Value, out var l) ? l.ToList() : new();
        }
        public List<byte[]> StreamFrom(Srv server, FltBot bot, int from)
            => Stream(server, bot).Skip(from).ToList();
    }

    private static List<(int Index, ushort Fmid, short ShipId, int Stage)> Decode(IEnumerable<byte[]> packets)
    {
        var list = new List<(int Index, ushort Fmid, short ShipId, int Stage)>();
        int index = 0;
        foreach (var packet in packets)
            foreach (var message in FedPacket.Unpack(packet))
            {
                if (message.Length >= 4)
                {
                    ushort fmid = BitConverter.ToUInt16(message, 2);
                    if (fmid == PlayerInfo.Fmid)
                        list.Add((index, fmid, PlayerInfo.Decode(message).ShipId, 0));
                    else if (fmid == ExportShip.Fmid)
                    {
                        // Only OT_ship exports carry the ship id; other object
                        // types (sides, stations, …) are not ship sends.
                        int objectType = BitConverter.ToInt32(message, 8);
                        list.Add((index, fmid, objectType == ExportShip.OtShip ? ExportShip.Decode(message).ShipId : (short)-1, 0));
                    }
                    else if (fmid == JoinSide.Fmid)
                        list.Add((index, fmid, JoinSide.Decode(message).ShipId, 0));
                    else if (fmid == MissionStageMessage.Fmid)
                        list.Add((index, fmid, (short)-1, MissionStageMessage.Decode(message).Stage));
                    else
                        list.Add((index, fmid, (short)-1, 0));
                }
                index++;
            }
        return list;
    }

    private static Srv Boot(bool miners = true)
    {
        var server = new Srv { SpawnInitialMinersAtBoot = miners };
        server.Boot(new Cfg
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Games = [new Gm { Name = "P775", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // P180: bypass ready-up
        return server;
    }

    private static Sector OtherSector(Mission mission, Sector notThis)
        => mission.Sectors.First(s => !ReferenceEquals(s, notThis));

    /// <summary>Test 1 — mid-game sighting of a joined enemy sends no
    /// PLAYERINFO: A (side 0) and B (side 1) join in different sectors; B then
    /// moves into A's sector and the cross-side EXPORT fires; A must still have
    /// gotten its one PLAYERINFO for B at B's join, before B's JOIN_SIDE and
    /// before any EXPORT of B, and the announcer counts exactly one
    /// announcement (the JOIN_SIDE) with no violation.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mid_game_sighting_of_a_joined_enemy_sends_no_playerinfo()
    {
        Require.Artwork();
        using var server = Boot();
        var capture = new Capture();
        capture.Attach(server);
        var mission = server.Missions[0];
        var side0 = mission.Sides[0].Id;
        var side1 = mission.Sides[1].Id;
        // P841: sides now start auto-accepting (native InitSide, fsmission.cpp:58).
        // The port config-seats a joiner, so Seat()'s POSITIONREQ names the side the
        // ship already holds; with auto-accept native would re-seat it
        // (RequestPosition, fsmission.cpp:4747-4760) and the extra own JOIN_SIDE
        // changes this replay's announcement count. Keep the request pending, as
        // before P841; this test is about the PLAYERINFO moments, not re-seating.
        mission.Sides[0].AutoAccept = false;
        mission.Sides[1].AutoAccept = false;

        using var a = Seat(server, "Ace", side0);
        using var b = Seat(server, "Bee", side1);
        PumpUntil(server, () => false, 200);   // let both join bursts finish

        short aShip = a.ShipAssignment!.Ship.ShipId;
        short bShip = b.ShipAssignment!.Ship.ShipId;
        var aSector = server.Ships.First(s => s.Ship.Id == aShip).Ship.Sector!;
        // Move B far away first (in case the map seats the two sides together),
        // let the broadcast pass settle, then bring B into A's sector.
        mission.PlaceShip(server.Ships.First(s => s.Ship.Id == bShip).Ship, OtherSector(mission, aSector));
        PumpUntil(server, () => false, 100);

        int marker = capture.Stream(server, a).Count;
        mission.PlaceShip(server.Ships.First(s => s.Ship.Id == bShip).Ship, aSector);
        // Pump until the cross-side EXPORT for B reaches A (the "sighting").
        PumpUntil(server, () => Decode(capture.StreamFrom(server, a, marker))
            .Any(m => m.Fmid == ExportShip.Fmid && m.ShipId == bShip), 4000);
        PumpUntil(server, () => false, 100);

        var events = Decode(capture.Stream(server, a));
        var infosForB = events.Where(m => m.Fmid == PlayerInfo.Fmid && m.ShipId == bShip).ToList();
        Assert.Single(infosForB);   // exactly one PLAYERINFO for B over the whole run
        int joinSideIdx = events.FindIndex(m => m.Fmid == JoinSide.Fmid && m.ShipId == bShip);
        int exportIdx = events.FindIndex(m => m.Fmid == ExportShip.Fmid && m.ShipId == bShip);
        Assert.True(joinSideIdx >= 0, "A must receive B's JOIN_SIDE");
        Assert.True(infosForB[0].Index < joinSideIdx, "the PLAYERINFO must precede B's JOIN_SIDE (native fsmission.cpp:408-411 then :621-624)");
        if (exportIdx >= 0) Assert.True(infosForB[0].Index < exportIdx, "the PLAYERINFO must precede any EXPORT of B");

        var announcer = new NativeJoinAnnouncer();
        announcer.Feed(capture.Stream(server, a));
        Assert.Equal(1, announcer.Announcements);   // the JOIN_SIDE, nothing else
        Assert.Empty(announcer.Violations);
    }

    /// <summary>Test 2 — a mid-game joiner receives every mission ship's
    /// PLAYERINFO in its join burst, including same-side and enemy drones in
    /// other sectors (native fsmission.cpp:3958-3968: the loop is over
    /// pMission-&gt;GetShips(), every ship, humans and drones, every sector, no
    /// visibility test), and nothing more arrives afterwards.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mid_game_joiner_receives_every_ship_playerinfo_including_enemy_drones_in_other_sectors()
    {
        Require.Artwork();
        using var server = Boot(miners: false);
        var capture = new Capture();
        capture.Attach(server);
        var mission = server.Missions[0];
        var side0 = mission.Sides[0].Id;
        var side1 = mission.Sides[1].Id;
        short droneType = mission.Core.DroneTypes.Keys.First();

        using var c = Seat(server, "Cee", side1);      // enemy human, own sector
        var cShip = server.Ships.First(s => s.Ship.Id == c.ShipAssignment!.Ship.ShipId).Ship;
        var far = OtherSector(mission, cShip.Sector!);
        var allyDrone = server.SpawnNpcShip(mission, far, side0, new Vector3(100, 0, 0), droneType);
        var enemyDrone = server.SpawnNpcShip(mission, far, side1, new Vector3(200, 0, 0), droneType);
        PumpUntil(server, () => false, 50);

        using var d = Seat(server, "Dee", side0);      // the mid-game joiner
        short dShip = d.ShipAssignment!.Ship.ShipId;
        PumpUntil(server, () => false, 50);            // the join burst has fully drained

        var stream = capture.Stream(server, d);
        var events = Decode(stream);
        var infos = events.Where(m => m.Fmid == PlayerInfo.Fmid && m.ShipId != dShip)
            .GroupBy(m => m.ShipId).ToDictionary(g => g.Key, g => g.ToList());
        short[] expected = [cShip.Id, allyDrone.Id, enemyDrone.Id];
        Assert.Equal(expected.OrderBy(x => x), infos.Keys.OrderBy(x => x));   // every mission ship except its own, nothing else
        foreach (var (_, list) in infos)
            Assert.Single(list);                        // exactly one per ship
        PlayerInfo InfoFor(short shipId) => PlayerInfo.Decode(stream
            .SelectMany(FedPacket.Unpack)
            .First(m => m.Length >= 4 && BitConverter.ToUInt16(m, 2) == PlayerInfo.Fmid
                && PlayerInfo.Decode(m).ShipId == shipId));
        Assert.Equal(droneType, InfoFor(allyDrone.Id).DtidDrone);   // FedSrv.CPP:2088-2091
        Assert.Equal(droneType, InfoFor(enemyDrone.Id).DtidDrone);
        Assert.Equal(PlayerInfo.NA, InfoFor(cShip.Id).DtidDrone);   // a human

        // Nothing more arrives over 200 further ticks (no per-sighting resend).
        int count = capture.Stream(server, d).Count;
        PumpUntil(server, () => false, 200);
        var later = Decode(capture.StreamFrom(server, d, count))
            .Where(m => m.Fmid == PlayerInfo.Fmid).ToList();
        Assert.Empty(later);
    }

    /// <summary>Test 3 — a drone created mid-game reaches every mission peer
    /// exactly once at creation (native CreateStockDrone → SendPlayerInfo to
    /// GetGroupMission(), FedSrv.CPP:2269), never again at its later NPC /
    /// cross-side EXPORTs.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Drone_created_mid_game_reaches_every_mission_peer_once_at_creation()
    {
        Require.Artwork();
        using var server = Boot(miners: false);
        var capture = new Capture();
        capture.Attach(server);
        var mission = server.Missions[0];
        var side0 = mission.Sides[0].Id;
        var side1 = mission.Sides[1].Id;
        short droneType = mission.Core.DroneTypes.Keys.First();

        using var a = Seat(server, "Ace", side0);
        using var b = Seat(server, "Bee", side1);
        PumpUntil(server, () => false, 100);
        var aShip = server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship;
        int marker = capture.Stream(server, a).Count;

        var drone = server.SpawnNpcShip(mission, aShip.Sector!, side0, aShip.Position + new Vector3(50, 0, 0), droneType);
        PumpUntil(server, () => false, 100);           // the creation tick + first broadcast passes

        foreach (var bot in new[] { a, b })
        {
            var stream = capture.Stream(server, bot);
            var infos = Decode(stream)
                .Where(m => m.Fmid == PlayerInfo.Fmid && m.ShipId == drone.Id).ToList();
            Assert.Single(infos);                       // exactly one, in the creation burst
        }
        var droneInfo = PlayerInfo.Decode(capture.Stream(server, a)
            .SelectMany(FedPacket.Unpack)
            .First(m => m.Length >= 4 && BitConverter.ToUInt16(m, 2) == PlayerInfo.Fmid
                && PlayerInfo.Decode(m).ShipId == drone.Id));
        Assert.Equal(droneType, droneInfo.DtidDrone);
        // None at its later NPC / cross-side EXPORTs.
        int count = capture.Stream(server, a).Count;
        PumpUntil(server, () => false, 200);
        Assert.DoesNotContain(Decode(capture.StreamFrom(server, a, count)),
            m => m.Fmid == PlayerInfo.Fmid && m.ShipId == drone.Id);
    }

    /// <summary>Test 4 — a side switch sends no PLAYERINFO (native QUIT_SIDE +
    /// JOIN_SIDE only, fsmission.cpp:1131/:621-624; no PLAYERINFO is resent).
    /// Every recipient hears exactly one announcement — the JOIN_SIDE.
    /// <para>Fixture (P775 review): the server balances the requested sides,
    /// so the test reads the ACTUAL seats and switches the lone ship onto the
    /// pair's side (the first version asked for side 1 and got side 0, so its
    /// TryJoinSide was a no-op and it passed on the unfixed code). Before the
    /// switch the switcher is moved into the pair's sector and the test pumps
    /// until the cross-side EXPORT lands — the pre-P775 lazy paths (E on that
    /// sighting, then A/B on the first teammate EXPORT after the switch) all
    /// get their chance to fire, so the unfixed code resends PLAYERINFO after
    /// the switch.</para></summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side_switch_sends_no_playerinfo()
    {
        Require.Artwork();
        using var server = Boot(miners: false);
        var capture = new Capture();
        capture.Attach(server);
        var mission = server.Missions[0];

        using var a = Seat(server, "Ace", mission.Sides[0].Id);
        using var b = Seat(server, "Bee", mission.Sides[0].Id);
        using var c = Seat(server, "Cee", mission.Sides[1].Id);
        PumpUntil(server, () => false, 100);

        Ship ShipOf(FltBot bot) => server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
        var bots = new[] { a, b, c };
        // Three seats on two sides: exactly one side holds two of them.
        var bySide = bots.GroupBy(bot => ShipOf(bot).SideId!.Value).ToList();
        Assert.Equal(2, bySide.Count);
        var pair = bySide.Single(g => g.Count() == 2).ToArray();
        var switcher = bySide.Single(g => g.Count() == 1).Single();
        short targetSide = ShipOf(pair[0]).SideId!.Value;
        var switcherShip = ShipOf(switcher);
        Assert.NotEqual(targetSide, switcherShip.SideId);

        // Bring the switcher into the pair's sector and let the cross-side
        // EXPORT (and, on the unfixed code, its lazy PLAYERINFO seed) land.
        mission.PlaceShip(switcherShip, ShipOf(pair[0]).Sector!);
        int got = PumpUntil(server, () => Decode(capture.Stream(server, pair[0]))
            .Any(m => m.Fmid == ExportShip.Fmid && m.ShipId == switcherShip.Id), 4000);
        Assert.True(got < 4000, "the pair must see the switcher (cross-side EXPORT) before the switch");
        PumpUntil(server, () => false, 50);

        // Baseline announcements from each bot's pre-switch stream: a seated
        // peer hears each LATER joiner's JOIN_SIDE (fsmission.cpp:621-624 →
        // appmsg.cpp:2909-2915), while a joiner announces none of the humans
        // already present (their PLAYERINFOs precede its own, fsmission.cpp:406
        // vs :411). The assertion is on the DELTA the switch produces.
        var state = new Dictionary<FltBot, (NativeJoinAnnouncer Ann, int Before, int Marker)>();
        foreach (var bot in bots)
        {
            var full = capture.Stream(server, bot);
            var ann = new NativeJoinAnnouncer();
            ann.Feed(full);
            state[bot] = (ann, ann.Announcements, full.Count);
        }
        Assert.True(server.TryJoinSide(switcherShip, targetSide), "the side switch must seat");
        Assert.Equal(targetSide, switcherShip.SideId);
        PumpUntil(server, () => false, 200);

        foreach (var bot in bots)
        {
            var (ann, before, marker) = state[bot];
            var after = capture.Stream(server, bot).Skip(marker).ToList();
            var laterInfos = Decode(after).Where(m => m.Fmid == PlayerInfo.Fmid).ToList();
            Assert.Empty(laterInfos);                    // no PLAYERINFO after the switch
            ann.Feed(after);
            // The switcher hears its own JOIN_SIDE (never announced to itself,
            // ClientWorld.Apply(JoinSide)); each of the pair hears the switch's
            // JOIN_SIDE once.
            int expected = ReferenceEquals(bot, switcher) ? 0 : 1;
            Assert.Equal(before + expected, ann.Announcements);
            Assert.Empty(ann.Violations);
        }
    }

    /// <summary>Test 4b (P775 review) — a native joiner announces NONE of the
    /// humans already in the mission: native sends the mission's ships'
    /// PLAYERINFOs in SendLobbyMissionInfo (fsmission.cpp:406, loop at
    /// :3958-3968) while the joiner's own side is still unset, and only then
    /// the joiner's own PLAYERINFO (:408-411), so clintlib.cpp:4142-4160 (which
    /// needs the client's own side) fires for nobody. The replay feeds
    /// MISSIONDEF with the live stage, as native fills it (Messages.h:301).
    /// The unfixed code sent the joiner's own-side PLAYERINFO (F) first, so a
    /// native client heard every existing human "join" again.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_joiner_announces_none_of_the_humans_already_present()
    {
        Require.Artwork();
        using var server = Boot(miners: false);
        var capture = new Capture();
        capture.Attach(server);
        var mission = server.Missions[0];

        using var a = Seat(server, "Ace", mission.Sides[0].Id);
        using var b = Seat(server, "Bee", mission.Sides[1].Id);
        PumpUntil(server, () => false, 50);
        using var d = Seat(server, "Dee", mission.Sides[0].Id);    // the mid-game joiner
        short dShip = d.ShipAssignment!.Ship.ShipId;
        PumpUntil(server, () => false, 50);

        var stream = capture.Stream(server, d);
        var events = Decode(stream);
        int own = events.FindIndex(m => m.Fmid == PlayerInfo.Fmid && m.ShipId == dShip);
        Assert.True(own >= 0, "the joiner must get its own PLAYERINFO");
        var others = events.Where(m => m.Fmid == PlayerInfo.Fmid && m.ShipId != dShip).ToList();
        Assert.Equal(2, others.Count);                    // Ace and Bee
        Assert.All(others, m => Assert.True(m.Index < own,
            $"ship {m.ShipId}'s PLAYERINFO must precede the joiner's own (fsmission.cpp:406 before :411)"));

        var announcer = new NativeJoinAnnouncer(missionDefStage: MissionStageMessage.StageStarted);
        announcer.Feed(stream);
        Assert.Equal(0, announcer.Announcements);
        Assert.Empty(announcer.Violations);
    }

    /// <summary>Test 4c (P775 review) — a ship id reused after a removal path
    /// that did not prune the PLAYERINFO latch (e.g. a constructor that became
    /// a station; AllocateShipId reuses ids) still reaches every existing
    /// peer: the genuinely-new-ship sites drop the id's stale latch entries
    /// before announcing (AnnounceFreshShip), as native announces every new
    /// drone (FedSrv.CPP:2269).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reused_drone_id_after_unpruned_removal_still_reaches_existing_peers()
    {
        Require.Artwork();
        using var server = Boot(miners: false);
        var capture = new Capture();
        capture.Attach(server);
        var mission = server.Missions[0];
        var side0 = mission.Sides[0].Id;
        short droneType = mission.Core.DroneTypes.Keys.First();

        using var a = Seat(server, "Ace", side0);
        using var b = Seat(server, "Bee", mission.Sides[1].Id);
        PumpUntil(server, () => false, 50);
        var sector = server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship.Sector!;

        var first = server.SpawnNpcShip(mission, sector, side0, new Vector3(300, 0, 0), droneType);
        PumpUntil(server, () => false, 20);
        // Remove it the way a constructor-becomes-station path does: out of
        // the sector with no PLAYERINFO-latch prune (Sector.Remove raises no
        // death path).
        Assert.True(sector.Remove(first));
        // Make AllocateShipId hand the same id out again.
        typeof(Srv).GetField("_nextShipId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(server, (int)first.Id);
        var second = server.SpawnNpcShip(mission, sector, side0, new Vector3(400, 0, 0), droneType);
        Assert.Equal(first.Id, second.Id);
        PumpUntil(server, () => false, 20);

        foreach (var bot in new[] { a, b })
        {
            var infos = Decode(capture.Stream(server, bot))
                .Where(m => m.Fmid == PlayerInfo.Fmid && m.ShipId == first.Id).ToList();
            Assert.Equal(2, infos.Count);   // one per drone that carried the id
        }
    }

    /// <summary>Test 5 — rotation sends exactly one PLAYERINFO per (peer,
    /// fresh-mission ship), all before the fresh match starts, and nothing is
    /// announced after Started. Native would re-send nothing (its mission
    /// survives game over, fsmission.cpp:3483-3490); the port's fresh ids and
    /// cleared client tables make one send per fresh pair the native-moment
    /// equivalent (join announce + join burst, both while NotStarted).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rotation_sends_one_playerinfo_per_pair_before_the_match_starts()
    {
        Require.Artwork();
        using var server = Boot();
        var capture = new Capture();
        capture.Attach(server);
        var side0 = server.Missions[0].Sides[0].Id;
        var side1 = server.Missions[0].Sides[1].Id;

        var bots = new List<FltBot>();
        for (int i = 0; i < 3; i++) bots.Add(Seat(server, $"Alpha{i}", side0));
        for (int i = 0; i < 3; i++) bots.Add(Seat(server, $"Bravo{i}", side1));
        PumpUntil(server, () => false, 200);

        var markers = bots.ToDictionary(b => b, b => capture.Stream(server, b).Count);
        var oldMission = server.Missions[0];
        typeof(Srv).GetMethod("DeclareGameOver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(server, [oldMission, (short)GameOver.NA, "P775 rotation test"]);
        // Every migrant is re-seated into the fresh mission (fresh join burst).
        PumpUntil(server, () =>
        {
            var fresh = server.Missions[0];
            return !ReferenceEquals(fresh, oldMission) && bots.All(b =>
                b.ShipAssignment is { } sa
                && server.Ships.Any(s => s.Ship.Id == sa.Ship.ShipId && ReferenceEquals(s.Ship.Mission, fresh)));
        }, 12000);
        foreach (var b in bots) b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
        PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 12000);
        PumpUntil(server, () => false, 100);

        var fresh = server.Missions[0];
        var freshHumans = server.Ships.Where(s => ReferenceEquals(s.Ship.Mission, fresh)).Select(s => s.Ship.Id).ToHashSet();
        var freshMiners = fresh.Sectors.SelectMany(s => s.Models).OfType<Ship>().Where(s => s.IsNpc).Select(s => s.Id).ToHashSet();
        Assert.NotEmpty(freshMiners);   // the fresh mission spawned its initial miners
        var expectedShips = freshHumans.Union(freshMiners).ToHashSet();

        foreach (var bot in bots)
        {
            var marker = markers[bot];
            var ownShip = bot.ShipAssignment!.Ship.ShipId;
            var events = Decode(capture.StreamFrom(server, bot, marker));
            var infos = events.Where(m => m.Fmid == PlayerInfo.Fmid && m.ShipId != ownShip).ToList();
            var byShip = infos.GroupBy(m => m.ShipId).ToDictionary(g => g.Key, g => g.ToList());
            Assert.Equal(expectedShips.Where(id => id != ownShip).OrderBy(x => x),
                byShip.Keys.OrderBy(x => x));
            foreach (var (_, list) in byShip) Assert.Single(list);

            // All of them precede that peer's first MISSION_STAGE
            // Starting/Started (the ready-up transition — NOT the game-over
            // Over stage), none follow it.
            int firstStart = events.FindIndex(m => m.Fmid == MissionStageMessage.Fmid
                && (m.Stage == MissionStageMessage.StageStarting || m.Stage == MissionStageMessage.StageStarted));
            Assert.True(firstStart >= 0, "the fresh match must start on the wire");
            Assert.DoesNotContain(infos, i => i.Index > firstStart);

            var announcer = new NativeJoinAnnouncer();
            announcer.Feed(capture.StreamFrom(server, bot, marker));
            Assert.Equal(0, announcer.Announcements);    // nothing announced after the restart
            Assert.Empty(announcer.Violations);
        }
    }
}

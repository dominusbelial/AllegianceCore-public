using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P188 — the visible start countdown, the manual START_GAME trigger, and the
/// PING echo.
///   1. <c>SET_START_TIME(14)</c> + a real <c>STAGE_STARTING</c> countdown stage:
///      <c>StartCountdown</c> (fed by both the ready-up auto-start and the manual
///      <c>START_GAME</c>) moves the mission NotStarted → Starting, broadcasts
///      <c>SET_START_TIME</c> whole-mission, and only after the countdown elapses
///      (a later tick) crosses Starting → Started with the existing P180
///      <c>ENTER_GAME</c> broadcast — the countdown genuinely delays the
///      transition.
///   2. <c>START_GAME(157)</c>: the mission owner's manual start trigger, gated on
///      ownership + NotStarted + every-occupied-side-ready (the port's own
///      simplified readiness check), refused otherwise.
///   3. <c>PING(35)</c>: a client keepalive/latency probe echoed back to the SAME
///      connection with a real server timestamp, regardless of mission/side state.
/// Scenarios: codec bytes, owner-START_GAME-through-countdown (must-fail-if-
/// removed delay proof), the refusal matrix, PING echo-to-sender-only, and
/// multi-mission scoping of the SET_START_TIME/countdown-completion broadcasts.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class LiveCountdownStartPingTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Countdown Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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
            new Gm { Name = "Count A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Count B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 921u, DwCookie = 0x921u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9220000000000000000ul, DrmHash = "cnt",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static uint ReadU32(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
    private static int ReadI32(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4));

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static Side SideOf(Srv server, Mission mission, short sideId) =>
        mission.Sides.First(s => s.Id == sideId);

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen
    /// side (same pattern LiveTeamLobbyTests/InjectMultiMissionSpawn). Peer 0 →
    /// mission 0 / side 0; peer 1 → mission 1 / side 0 (SAME numeric side id);
    /// peer 2 → mission 0 / side 1.</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 750f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    // ── codec bytes ───────────────────────────────────────────────────────

    /// <summary>SET_START_TIME(14), START_GAME(157) and PING(35) round-trip and
    /// pin their exact natural-alignment wire layouts (no pragma pack in
    /// Messages.h): SET_START_TIME = header + uint32 timeStart@4; START_GAME =
    /// header-only marker; PING = header + uint32 timeClient@4 + uint32
    /// timeServer@8 + int32 fmg@12.</summary>
    [Fact]
    public void P188_codecs_round_trip_exact_bytes()
    {
        Assert.Equal((ushort)14, SetStartTime.Fmid);
        Assert.Equal(8, SetStartTime.FixedSize);
        var st = new SetStartTime { TimeStart = 987654321u };
        byte[] b = st.Encode();
        Assert.Equal(8, b.Length);
        Assert.Equal(8, ReadU16(b, 0));          // cbmsg
        Assert.Equal(14, ReadU16(b, 2));         // fmid
        Assert.Equal(987654321u, ReadU32(b, 4)); // timeStart @4
        Assert.Equal(987654321u, SetStartTime.Decode(b).TimeStart);

        Assert.Equal((ushort)157, StartGame.Fmid);
        Assert.Equal(4, StartGame.FixedSize);
        byte[] sg = new StartGame().Encode();
        Assert.Equal(4, sg.Length);
        Assert.Equal(4, ReadU16(sg, 0));         // cbmsg
        Assert.Equal(157, ReadU16(sg, 2));       // fmid
        _ = StartGame.Decode(sg);

        Assert.Equal((ushort)35, Ping.Fmid);
        Assert.Equal(16, Ping.FixedSize);
        var p = new Ping { TimeClient = 0x01020304u, TimeServer = 0x05060708u, Fmg = 1 };
        byte[] pb = p.Encode();
        Assert.Equal(16, pb.Length);
        Assert.Equal(16, ReadU16(pb, 0));        // cbmsg
        Assert.Equal(35, ReadU16(pb, 2));        // fmid
        Assert.Equal(0x01020304u, ReadU32(pb, 4));  // timeClient @4
        Assert.Equal(0x05060708u, ReadU32(pb, 8));  // timeServer @8
        Assert.Equal(1, ReadI32(pb, 12));        // fmg @12
        var dp = Ping.Decode(pb);
        Assert.Equal(0x01020304u, dp.TimeClient);
        Assert.Equal(0x05060708u, dp.TimeServer);
        Assert.Equal(1, dp.Fmg);
    }

    // ── START_GAME happy path through the real countdown ──────────────────

    /// <summary>⭐ MUST-FAIL-IF-REMOVED (the countdown genuinely delays the
    /// transition): the mission owner's START_GAME while every occupied side is
    /// ready moves the mission NotStarted → Starting and broadcasts
    /// SET_START_TIME whole-mission — but does NOT yet transition to Started or
    /// send ENTER_GAME. Only once the countdown elapses on a later tick does it
    /// cross to Started with the existing P180 ENTER_GAME broadcast to both
    /// peers. (Readiness is arranged by setting Side.Ready directly, bypassing
    /// the ready-up auto-start, so the manual START_GAME's own NotStarted gate
    /// is exercisable.)</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void StartGame_owner_all_ready_goes_through_real_countdown()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "CG1");
        short bId = Seat(server, b, "CG2");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        Assert.NotEqual(side0, side1);

        // Establish A as side0's leader AND the mission owner (empty-side join),
        // the same P183 pattern the leadership tests use.
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.Equal(aId, SideOf(server, m, side0).LeaderShipId);
        Assert.Equal(side0, m.MissionOwnerSideId);

        // Arrange "every occupied side ready" directly, bypassing the ready-up
        // auto-start (which would otherwise consume the NotStarted state first).
        SideOf(server, m, side0).Ready = true;
        SideOf(server, m, side1).Ready = true;
        Assert.Equal(MissionStage.NotStarted, m.Stage);

        // Manual start by the owner → countdown begins: NotStarted → Starting,
        // SET_START_TIME broadcast whole-mission.
        a.SendStartGame();
        int began = PumpUntil(server, () => m.Stage == MissionStage.Starting && a.SetStartTimes.Count >= 1, 400);
        Assert.True(began < 400, "START_GAME did not begin the countdown");
        Assert.Equal(MissionStage.Starting, m.Stage);
        Assert.Contains(a.SetStartTimes, s => s.TimeStart > 0);
        Assert.Single(b.SetStartTimes);          // whole-mission: both peers saw the announcement

        // ⭐ The countdown is STILL in progress: NOT started, no ENTER_GAME yet.
        // This is what must fail if the countdown is removed (an instant start
        // would already be Started with ENTER_GAME sent).
        Assert.Empty(a.EnterGames);
        Assert.Empty(b.EnterGames);

        // Now drive past the 15s countdown (20 upd → ~300 ticks) → Started +
        // ENTER_GAME via the exact P180 broadcast path.
        int elapsed = PumpUntil(server,
            () => m.Stage == MissionStage.Started && a.EnterGames.Count >= 1 && b.EnterGames.Count >= 1, 800);
        Assert.True(elapsed < 800, "mission did not cross into Started after the countdown elapsed");
        Assert.Equal(MissionStage.Started, m.Stage);
        Assert.Single(a.EnterGames);
        Assert.Single(b.EnterGames);
    }

    // ── START_GAME refusal matrix ─────────────────────────────────────────

    /// <summary>⭐ A non-owner sending START_GAME is refused — the mission stays
    /// NotStarted and no SET_START_TIME is broadcast, even though all sides are
    /// ready.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void StartGame_from_non_owner_is_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "CG3");
        short bId = Seat(server, b, "CG4");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A is owner
        Assert.Equal(side0, m.MissionOwnerSideId);
        SideOf(server, m, side0).Ready = true;
        SideOf(server, m, side1).Ready = true;
        Assert.Equal(MissionStage.NotStarted, m.Stage);

        // B is NOT the owner → START_GAME refused outright.
        b.SendStartGame();
        PumpUntil(server, () => false, 30);
        Assert.Equal(MissionStage.NotStarted, m.Stage);
        Assert.Empty(a.SetStartTimes);
        Assert.Empty(b.SetStartTimes);
        Assert.Empty(a.EnterGames);
    }

    /// <summary>⭐ The owner's START_GAME while not every occupied side is ready
    /// is refused (the port's own simplified all-ready gate).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void StartGame_not_all_ready_is_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "CG5");
        short bId = Seat(server, b, "CG6");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A is owner
        SideOf(server, m, side0).Ready = true;
        SideOf(server, m, side1).Ready = false;   // B's side unready
        Assert.Equal(MissionStage.NotStarted, m.Stage);

        a.SendStartGame();
        PumpUntil(server, () => false, 30);
        Assert.Equal(MissionStage.NotStarted, m.Stage);
        Assert.Empty(a.SetStartTimes);
    }

    /// <summary>⭐ The owner's START_GAME is refused once the mission is already
    /// Starting (mid-countdown) or already Started — it never re-triggers the
    /// countdown or double-broadcasts SET_START_TIME.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void StartGame_refused_once_starting_or_started()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "CG7");
        short bId = Seat(server, b, "CG8");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A is owner
        SideOf(server, m, side0).Ready = true;
        SideOf(server, m, side1).Ready = true;

        // First START_GAME → Starting + one SET_START_TIME.
        a.SendStartGame();
        int began = PumpUntil(server, () => m.Stage == MissionStage.Starting && a.SetStartTimes.Count >= 1, 400);
        Assert.True(began < 400, "first START_GAME did not begin the countdown");
        Assert.Single(a.SetStartTimes);

        // A second START_GAME mid-countdown is refused — no extra broadcast.
        a.SendStartGame();
        PumpUntil(server, () => false, 30);
        Assert.Equal(MissionStage.Starting, m.Stage);
        Assert.Single(a.SetStartTimes);

        // After the countdown elapses → Started; a START_GAME now is refused too.
        int elapsed = PumpUntil(server, () => m.Stage == MissionStage.Started, 800);
        Assert.True(elapsed < 800, "countdown did not elapse");
        a.SendStartGame();
        PumpUntil(server, () => false, 30);
        Assert.Equal(MissionStage.Started, m.Stage);
        Assert.Single(a.SetStartTimes);
    }

    // ── PING echo ─────────────────────────────────────────────────────────

    /// <summary>⭐ A PING is echoed back to the SENDER ONLY (never broadcast to
    /// the mission), with a real server timestamp stamped into TimeServer and
    /// the client's own TimeClient preserved — regardless of mission/side state
    /// (here the mission is NotStarted with nobody readied).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ping_echoes_to_sender_only_with_server_timestamp()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        Seat(server, a, "CG9");
        Seat(server, b, "CG10");

        // Nobody readied — the mission is still NotStarted, exercising PING in a
        // pre-game state (the handler sits at the top of the dispatch, before
        // any mission lookup, so game state is irrelevant to it).
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        a.SendPing(timeClient: 666u, fmg: 1);
        int got = PumpUntil(server, () => a.Pings.Count >= 1, 400);
        Assert.True(got < 400, "sender did not receive its PING echo");
        Assert.Single(a.Pings);
        Assert.Equal(666u, a.Pings[0].TimeClient);   // echoed unchanged
        Assert.True(a.Pings[0].TimeServer > 0, "server did not stamp a real TimeServer");
        Assert.Equal(1, a.Pings[0].Fmg);             // fmg echoed unchanged

        // Echo is to the sender ONLY — B saw nothing.
        Assert.Empty(b.Pings);
    }

    // ── multi-mission scoping ─────────────────────────────────────────────

    /// <summary>⭐ Multi-mission scoping: the SET_START_TIME announcement and
    /// the countdown-completion ENTER_GAME for mission A reach only mission A's
    /// peers — a peer on mission B (sharing the same numeric side id) sees
    /// neither, and mission B's stage is untouched (the P180/P178 must-not-
    /// regress per-mission-scoping class).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_countdown_scopes_per_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id)
        using var a1 = new FltBot();   // mission A, side 1
        short a0Id = Seat(server, a0, "CMA0");
        short b0Id = Seat(server, b0, "CMB0");
        short a1Id = Seat(server, a1, "CMA1");

        Mission ma = server.Missions[0];
        Mission mb = server.Missions[1];
        short mA0 = ma.Sides[0].Id;
        Assert.Equal(mA0, mb.Sides[0].Id);   // same numeric side id across missions

        // Establish a0 as mission A's owner; ready A's two sides directly; leave
        // mission B untouched.
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), mA0));
        Assert.Equal(mA0, ma.MissionOwnerSideId);
        SideOf(server, ma, ma.Sides[0].Id).Ready = true;
        SideOf(server, ma, ma.Sides[1].Id).Ready = true;

        a0.SendStartGame();
        int began = PumpUntil(server, () => ma.Stage == MissionStage.Starting && a0.SetStartTimes.Count >= 1, 400);
        Assert.True(began < 400, "mission A countdown did not begin");
        Assert.Equal(MissionStage.Starting, ma.Stage);

        // Mission A's peers heard SET_START_TIME; mission B's peer heard nothing.
        Assert.Single(a1.SetStartTimes);
        Assert.Empty(b0.SetStartTimes);
        Assert.Equal(MissionStage.NotStarted, mb.Stage);

        // Countdown completes → mission A Started with ENTER_GAME to A's peers only.
        int elapsed = PumpUntil(server,
            () => ma.Stage == MissionStage.Started && a0.EnterGames.Count >= 1 && a1.EnterGames.Count >= 1, 800);
        Assert.True(elapsed < 800, "mission A countdown did not elapse");
        Assert.Equal(MissionStage.Started, ma.Stage);
        Assert.Equal(MissionStage.NotStarted, mb.Stage);
        Assert.Empty(b0.SetStartTimes);
        Assert.Empty(b0.EnterGames);
    }
}

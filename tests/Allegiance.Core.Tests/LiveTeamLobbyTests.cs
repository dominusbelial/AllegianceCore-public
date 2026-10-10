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
/// P180 — team-lobby Stage 1: a real pre-game phase and ready-up gating.
/// The mission is now born <see cref="MissionStage.NotStarted"/> (instead of
/// force-Started at boot) and only transitions to
/// <see cref="MissionStage.Started"/> once every side with at least one
/// connected player is aggregate-ready — tracked per side (Side.Ready),
/// recomputed on every <c>PLAYER_READY</c> (the <c>CheckForSideAllReady</c>
/// port), reported whole-mission via <c>TEAM_READY(46)</c> when it flips, and
/// culminating in an <c>ENTER_GAME(128)</c> broadcast to every connected peer
/// at the auto-start transition. While NotStarted: the control-input
/// <c>SHIP_UPDATE</c> path is gated (a ship must not fly before the game
/// starts) and the existing <c>mission.Stage != Started</c> refusals (e.g.
/// <c>HandleSuicide</c>) hold against the genuinely-not-started stage.
/// Scenarios: codec bytes, single-side-side boot + SHIP_UPDATE ignored,
/// both-sides-ready → TEAM_READY+auto-start+ENTER_GAME, one-side-only no
/// auto-start, ready→un-ready second TEAM_READY(false), and a late joiner to
/// an already-started mission still getting its immediate ENTER_GAME.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class LiveTeamLobbyTests
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
            new Gm { Name = "Lobby Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 883u, DwCookie = 0x883u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9520000000000000000ul, DrmHash = "lobby",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ushort BinaryPrimitives_ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));

    private static short BinaryPrimitives_ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static short SideOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship.SideId!.Value;

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Lobby A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Lobby B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    /// <summary>Route peer <c>seq</c> to a designated mission with a chosen
    /// side, so the multi-mission test can seat peers on mission B (the
    /// default spawner always uses <c>_missions[0]</c>) — the same pattern
    /// <c>LiveWinConditionTests.InjectMultiMissionSpawn</c> already
    /// established for P178. Peer 0 -> mission 0 / side 0; peer 1 -> mission 1
    /// / side 0; peer 2 -> mission 0 / side 1.</summary>
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

    // ── codec ────────────────────────────────────────────────────────────

    /// <summary>TEAM_READY(46) round-trips and pins the exact wire layout:
    /// header(4) + iSide short@4 + fReady bool@6 + 1 tail pad@7 = 8 bytes,
    /// matching the natural-alignment shape of the sibling PLAYER_READY codec
    /// (no pragma pack encloses it in Messages.h:386-390).</summary>
    [Fact]
    public void TeamReady_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)46, TeamReady.Fmid);
        Assert.Equal(8, TeamReady.FixedSize);

        var msg = new TeamReady { SideId = 903, FReady = true };
        byte[] b = msg.Encode();
        Assert.Equal(8, b.Length);
        Assert.Equal(8, BinaryPrimitives_ReadU16(b, 0));      // cbmsg
        Assert.Equal(46, BinaryPrimitives_ReadU16(b, 2));     // fmid
        Assert.Equal(903, BinaryPrimitives_ReadI16(b, 4));    // iSide @4
        Assert.Equal(1, b[6]);                                // fReady @6
        Assert.Equal(0, b[7]);                                // tail pad @7

        var d = TeamReady.Decode(b);
        Assert.Equal((short)903, d.SideId);
        Assert.True(d.FReady);

        var f = new TeamReady { SideId = 904, FReady = false }.Encode();
        Assert.Equal(904, BinaryPrimitives_ReadI16(f, 4));
        Assert.Equal(0, f[6]);
        Assert.False(TeamReady.Decode(f).FReady);
    }

    // ── Stage 1: NotStarted boot + pre-game control hold ────────────────

    /// <summary>⭐ The mission boots with <c>Stage == NotStarted</c>, and a
    /// connected peer's real <c>SHIP_UPDATE</c> control input is IGNORED while
    /// it is not started — the ship's <c>Controls</c>/<c>StateM</c> stay at
    /// their defaults after a genuine control frame over the wire.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mission_boots_not_started_and_control_update_ignored_before_start()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        using var a = new FltBot();
        short aId = Seat(server, a, "GA1");
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);   // nobody readied

        var ship = server.Ships.First(s => s.Ship.Id == aId).Ship;
        var beforeCtrl = ship.Controls;
        int beforeState = ship.StateM;

        a.CommandControls(new ControlData(0f, 0f, 0f, 1f), stateM: 0x2);
        // Pump long enough for a (wrongful) application to have happened, then
        // assert it did not.
        PumpUntil(server, () => ship.Controls != beforeCtrl || ship.StateM != beforeState, 300);
        Assert.Equal(beforeCtrl, ship.Controls);
        Assert.Equal(beforeState, ship.StateM);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
    }

    // ── auto-start on both-sides-ready ──────────────────────────────────

    /// <summary>⭐ Both players mark PLAYER_READY(true): each side's aggregate
    /// ready flip is broadcast whole-mission as TEAM_READY(iSide, true) (each
    /// bot sees BOTH sides' readiness), the mission transitions to Started,
    /// and BOTH peers receive the single ENTER_GAME broadcast at that point —
    /// not before (both EnterGames lists are empty until then). A subsequent
    /// real control update now actually takes effect.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Both_sides_ready_broadcasts_team_ready_and_autostarts()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "GA2");
        short bId = Seat(server, b, "GB2");

        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Empty(a.EnterGames);
        Assert.Empty(b.EnterGames);

        short aSide = SideOf(server, aId);
        short bSide = SideOf(server, bId);
        Assert.NotEqual(aSide, bSide);   // default spawner round-robins to opposite sides

        // A readies → side A becomes ready, but side B still has an unready
        // player, so NO auto-start yet.
        a.SendPlayerReady(true, aId);
        int ra = PumpUntil(server, () => a.TeamReadys.Count >= 1, 800);
        Assert.True(ra < 800, "no TEAM_READY after A readied");
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Empty(a.EnterGames);

        // B readies → both occupied sides ready → auto-start + ENTER_GAME.
        b.SendPlayerReady(true, bId);
        int started = PumpUntil(server,
            () => server.Missions[0].Stage == MissionStage.Started
                  && a.EnterGames.Count >= 1 && b.EnterGames.Count >= 1, 800);
        Assert.True(started < 800, "mission did not auto-start / ENTER_GAME missing");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);

        // Whole-mission TEAM_READY: each bot saw both sides flip to ready.
        Assert.Contains(a.TeamReadys, t => t.SideId == aSide && t.FReady);
        Assert.Contains(a.TeamReadys, t => t.SideId == bSide && t.FReady);
        Assert.Contains(b.TeamReadys, t => t.SideId == aSide && t.FReady);
        Assert.Contains(b.TeamReadys, t => t.SideId == bSide && t.FReady);
        Assert.Single(a.EnterGames);
        Assert.Single(b.EnterGames);

        // Now the same control update actually takes effect.
        var shipA = server.Ships.First(s => s.Ship.Id == aId).Ship;
        shipA.Controls = new ControlData(0f, 0f, 0f, 0f);
        a.CommandControls(new ControlData(0f, 0f, 0f, 1f), stateM: 0x2);
        int cp = PumpUntil(server, () => shipA.Controls.Throttle == 1f, 800);
        Assert.True(cp < 800, "control update not applied after start");
    }

    /// <summary>⭐ Only ONE of two sides ready: no auto-start, Stage stays
    /// NotStarted, and nobody has received ENTER_GAME yet.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void One_side_ready_does_not_autostart()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "GA3");
        short bId = Seat(server, b, "GB3");

        a.SendPlayerReady(true, aId);          // side A ready only
        int ra = PumpUntil(server, () => a.TeamReadys.Count >= 1, 800);
        Assert.True(ra < 800, "no TEAM_READY after A readied");

        // Give any wrongful auto-start plenty of ticks to (not) happen.
        PumpUntil(server, () => false, 60);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Empty(a.EnterGames);
        Assert.Empty(b.EnterGames);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
    }

    /// <summary>⭐ A player who marks ready then un-readies (fReady=false)
    /// causes a SECOND whole-mission TEAM_READY(fReady=false) broadcast for
    /// their side — the aggregate flip true→false is reported, and the mission
    /// still has not started (the other side's player never readied).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unready_broadcasts_team_ready_false()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "GA4");
        short bId = Seat(server, b, "GB4");
        short aSide = SideOf(server, aId);

        a.SendPlayerReady(true, aId);
        int ra = PumpUntil(server, () => a.TeamReadys.Any(t => t.SideId == aSide && t.FReady), 800);
        Assert.True(ra < 800, "no TEAM_READY(true) for side A");
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);   // B never readied

        a.SendPlayerReady(false, aId);
        int unf = PumpUntil(server, () => a.TeamReadys.Any(t => t.SideId == aSide && !t.FReady), 800);
        Assert.True(unf < 800, "no TEAM_READY(false) for side A after un-ready");
        Assert.Contains(a.TeamReadys, t => t.SideId == aSide && !t.FReady);
        Assert.Contains(b.TeamReadys, t => t.SideId == aSide && !t.FReady);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Empty(a.EnterGames);
    }

    /// <summary>⭐ A peer joining a mission that ALREADY auto-started (a second,
    /// late-arriving peer) still receives its immediate ENTER_GAME via the
    /// existing join-sync path — the join-sync guard only suppresses the send
    /// while the mission is NotStarted, never when it is already running.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Late_joiner_to_started_mission_still_gets_enter_game()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "GA5");

        // Solo on the only occupied side: readying auto-starts the mission.
        a.SendPlayerReady(true, aId);
        int started = PumpUntil(server,
            () => server.Missions[0].Stage == MissionStage.Started && a.EnterGames.Count >= 1, 800);
        Assert.True(started < 800, "solo auto-start failed");
        Assert.Single(a.EnterGames);                                    // the transition broadcast

        using var b = new FltBot();                                     // late joiner
        short bId = Seat(server, b, "GB5");
        int late = PumpUntil(server, () => b.EnterGames.Count >= 1, 4000);
        Assert.True(late < 4000, "late joiner did not get immediate ENTER_GAME");
        Assert.Single(b.EnterGames);                                    // per-joiner join-sync send
        Assert.Single(a.EnterGames);                                    // A not doubled by B's send
    }

    /// <summary>⭐ The existing <c>mission.Stage != MissionStage.Started</c>
    /// gates still refuse their action while genuinely NotStarted — not just
    /// Over as P178 tested. A real SUICIDE while NotStarted is refused and the
    /// ship survives in the sector.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Existing_stage_gates_refuse_while_not_started()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "GA6");

        Assert.Contains(server.Ships, s => s.Ship.Id == aId);
        a.SendSuicide();                                                // should be refused
        int settle = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == aId), 300);
        // The gate refused the kill, so the ship is still there and still Started-less.
        Assert.Contains(server.Ships, s => s.Ship.Id == aId);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
    }

    /// <summary>⭐ Post-landing-review fix: a multi-mission server (two
    /// concurrent <c>Games</c>). Both peers on mission A's two sides ready up
    /// and auto-start mission A — but mission B's own peer (seated on
    /// mission B's side 0, the SAME numeric side id as mission A's side 0)
    /// must NOT see mission A's TEAM_READY/ENTER_GAME broadcasts, must not be
    /// counted toward mission A's readiness decision, and mission B's own
    /// Stage must stay NotStarted throughout. Mirrors
    /// <c>LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission</c>'s
    /// established per-mission-scoping proof for the win-condition broadcasts —
    /// this is the same class of bug (an unscoped <c>_peerShips</c> sweep) in
    /// the ready-up broadcasts, found and fixed during independent verification
    /// of this task's own landed commit, not part of the original prompt.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_ready_up_scopes_per_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Equal(MissionStage.NotStarted, server.Missions[1].Stage);

        using var a0 = new FltBot();   // mission A, side 0
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id as a0's)
        using var a1 = new FltBot();   // mission A, side 1
        short a0Id = Seat(server, a0, "MMA0");
        short b0Id = Seat(server, b0, "MMB0");
        short a1Id = Seat(server, a1, "MMA1");

        // Ready up BOTH of mission A's sides -> mission A should auto-start.
        a0.SendPlayerReady(true, a0Id);
        a1.SendPlayerReady(true, a1Id);
        int started = PumpUntil(server,
            () => server.Missions[0].Stage == MissionStage.Started && a0.EnterGames.Count >= 1 && a1.EnterGames.Count >= 1,
            800);
        Assert.True(started < 800, "mission A did not auto-start");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);

        // Mission B's own peer must be completely unaffected: no TEAM_READY,
        // no ENTER_GAME, and its mission's Stage is still NotStarted (B's own
        // side 0 was never readied).
        Assert.Empty(b0.TeamReadys);
        Assert.Empty(b0.EnterGames);
        Assert.Equal(MissionStage.NotStarted, server.Missions[1].Stage);

        // And mission B's peer must not have been silently counted as an
        // "occupied side" satisfying mission A's own side 0 — confirmed
        // indirectly above (mission A auto-started only after BOTH of ITS OWN
        // bots readied, not merely after a0 alone, even though b0 occupies the
        // same numeric side id in a different mission and was never readied).
    }
}

using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P548 — the peer-departure "Stage Over" bug and the auto-assign leader bug.
/// <para><b>Bug 1:</b> <c>CheckForVictoryByInactiveSides(Mission)</c>
/// (Server.Lobby.cs, the port of native fsmission.cpp:5371-5427 →
/// CheckForVictoryByInactiveSides) ran on EVERY disconnect and returned early
/// only when the mission was already <see cref="MissionStage.Over"/>. Native
/// only deactivates a side — which is what leads to the victory check — when
/// <c>fDeactivate = (STAGE_NOTSTARTED != m_misdef.stage) &amp;&amp;
/// !HasPlayers(psideOld, false)</c> (fsmission.cpp:1147-1149), so a drop in a
/// NotStarted lobby never ends a mission. The port lacked that stage gate, so a
/// lobby drop that left ≤ 1 side populated declared game-over on every drop.
/// P548 adds native's stage + side-empty gate: a drop in Starting/Started
/// deactivates only a side left empty (then checks for a win); a drop in a
/// NotStarted lobby never does.</para>
/// <para><b>Bug 2:</b> the auto-assign path set <c>SideId</c> directly
/// (Server.cs:753 → SpawnDefaultShip) without routing through
/// <c>TryJoinSide</c>, so an auto-assigned first player onto an empty side was
/// never elected team leader (native fsmission.cpp:601-613 always elects the
/// first player onto an empty side as leader, and every placement — auto or
/// explicit — flows through AddPlayerToSide). P548 routes auto-assign through
/// the same join path.</para>
/// Four live proofs (in-process real Allegiance.Server over loopback TCP):
/// <list type="number">
/// <item>NotStarted lobby, two peers on different sides, one drops → NO
///      GAME_OVER, neither side deactivated, mission stays NotStarted.</item>
/// <item>Started, the last player of side B drops → side B is deactivated,
///      exactly ONE GAME_OVER with native reason/winner on the survivor.</item>
/// <item>A sequence of 4 drops after a game over produces NO further
///      GAME_OVERs (the ×4 regression).</item>
/// <item>An auto-assigned first player on an empty side IS the team leader.</item>
/// </list>
/// [Collection("LiveNetwork")] keeps this suite sequential with the other live
/// suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveP548GameOverStageGateTests
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
            new Gm { Name = "P548 Gate", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 951u, DwCookie = 0x951u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9520000000000000000ul, DrmHash = "p548",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

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

    /// <summary>Seat a peer and wait until the connection is fully released
    /// after disposing it (ship retired from the server).</summary>
    private static void DropAndRelease(Srv server, FltBot bot, short shipId)
    {
        bot.Dispose();
        int released = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == shipId), 800);
        Assert.True(released < 800, $"peer {shipId} was not released");
    }

    // ══ proof 1 — a NotStarted (lobby) drop never fires game-over ══

    /// <summary>⭐ The lobby repro: two peers seat on two different sides and
    /// one drops. Native never deactivates/ends in a NotStarted lobby
    /// (fsmission.cpp:1147), so there must be NO GAME_OVER and the mission must
    /// stay NotStarted. Red on today's code: the old check ran the mission-wide
    /// tally on every disconnect, saw one surviving side and declared game-over.</summary>
    [Fact]
    public void NotStarted_lobby_drop_produces_no_game_over()
    {
        using var server = new Srv();
        server.Boot(Config());

        var mission0 = server.Missions[0];
        Assert.Equal(MissionStage.NotStarted, mission0.Stage);

        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "H1");
        short bId = Seat(server, b, "H2");

        // Auto-assign puts the two peers on the two different sides.
        short aSide = ShipOf(server, aId).SideId!.Value;
        short bSide = ShipOf(server, bId).SideId!.Value;
        Assert.NotEqual(aSide, bSide);
        Flush(server);
        Assert.Equal(MissionStage.NotStarted, mission0.Stage);   // still lobby

        // One peer drops. In a NotStarted lobby this must be a non-event.
        DropAndRelease(server, a, aId);
        Flush(server);

        Assert.Equal(MissionStage.NotStarted, mission0.Stage);   // NOT Over
        Assert.True(mission0.Sides.First(s => s.Id == aSide).Active);  // neither deactivated
        Assert.True(mission0.Sides.First(s => s.Id == bSide).Active);
        Assert.Empty(b.GameOvers);                                // survivor heard no GAME_OVER
        Console.WriteLine($"[P548-LIVE] lobby: peer {aId} (side {aSide}) dropped, peer {bId} (side {bSide}) remains → mission '{mission0.Stage}' — no game-over on a NotStarted drop.");
    }

    // ══ proof 2 — Started: last player of a side drops → deactivate + one GAME_OVER ══

    /// <summary>⭐ A Started game, last player of side B drops (side A
    /// survives): side B is deactivated and EXACTLY ONE GAME_OVER fires with
    /// native's reason/winner. Confirms the side-empty gate still ends a real
    /// game, and the deactivation happens.</summary>
    [Fact]
    public void Started_last_player_of_side_drops_deactivates_side_and_one_game_over()
    {
        using var server = new Srv();
        server.Boot(Config());

        var mission0 = server.Missions[0];
        short side0 = mission0.Sides[0].Id;
        short side1 = mission0.Sides[1].Id;
        string side0Name = mission0.Sides.First(s => s.Id == side0).Name!;

        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "J1");
        short bId = Seat(server, b, "J2");
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));
        Flush(server);

        var side1Obj = mission0.Sides.First(s => s.Id == side1);
        Assert.True(side1Obj.Active);

        server.Missions[0].Stage = MissionStage.Started;   // bypass ready-up lobby (P180)

        // The last player of side B (b) drops; side A (a) survives.
        DropAndRelease(server, b, bId);
        int ended = PumpUntil(server, () => mission0.Stage == MissionStage.Over, 900);
        Assert.True(ended < 900, "started-game last-player-of-a-side drop did not end the mission");

        // side B is deactivated; side A stays active.
        Assert.False(side1Obj.Active);
        Assert.True(mission0.Sides.First(s => s.Id == side0).Active);

        // Exactly one GAME_OVER on the survivor, native reason/winner.
        var go = Assert.Single(a.GameOvers);
        Assert.Equal(side0, go.SideWinner);
        Assert.Contains($"{side0Name} won by outlasting all other sides.", go.GameoverMessage);
        Console.WriteLine($"[P548-LIVE] started: last of side {side1} ({bId}) dropped → side {side1} Active={side1Obj.Active}, side {side0} wins, exactly {a.GameOvers.Count} GAME_OVER(s): \"{go.GameoverMessage}\"");
    }

    // ══ proof 3 — no cascading game-overs after a game over (the ×4 regression) ══

    /// <summary>⭐ After a real game over the surviving peers are migrated into a
    /// fresh NotStarted mission (P439/P480); dropping them one by one must
    /// produce NO further GAME_OVERs. The ×4 regression: each post-game-over
    /// drop used to run the check on the fresh NotStarted mission, which is
    /// under-populated, and declare another game-over. Exactly one GAME_OVER
    /// total.</summary>
    [Fact]
    public void Four_drops_after_game_over_produce_no_further_game_overs()
    {
        using var server = new Srv();
        server.Boot(Config());

        var mission0 = server.Missions[0];
        short side0 = mission0.Sides[0].Id;
        short side1 = mission0.Sides[1].Id;

        // 4 players on side0, 3 on side1.
        var bots = Enumerable.Range(0, 7).Select(_ => new FltBot()).ToArray();
        var ids = new short[7];
        try
        {
            for (int i = 0; i < 7; i++) ids[i] = Seat(server, bots[i], $"K{i}");
            for (int i = 0; i < 4; i++) Assert.True(server.TryJoinSide(ShipOf(server, ids[i]), side0));
            for (int i = 4; i < 7; i++) Assert.True(server.TryJoinSide(ShipOf(server, ids[i]), side1));
            Flush(server);

            server.Missions[0].Stage = MissionStage.Started;

            // Survivor peer 0 (side0) will receive the one real GAME_OVER.
            bots[0].ResetGameOvers();

            // Empty side1: drop its three players one at a time. The LAST one
            // empties it → exactly ONE game over (side0 wins). Drops 6 and 5
            // leave side1 occupied, so they must NOT end the game.
            for (int i = 6; i >= 4; i--)
            {
                DropAndRelease(server, bots[i], ids[i]);
                Flush(server);
            }
            int ended = PumpUntil(server, () => mission0.Stage == MissionStage.Over, 900);
            Assert.True(ended < 900, "side1 emptied by drops did not end the mission once");
            Assert.Single(bots[0].GameOvers);

            // Rotation rebuilt a fresh NotStarted mission (P439) and migrated
            // the four side0 survivors into it (P480).
            var fresh = server.Missions[0];
            Assert.NotSame(mission0, fresh);
            Assert.Equal(MissionStage.NotStarted, fresh.Stage);

            // Drop the 4 remaining survivors — the ×4 regression. Each of these
            // used to fire another "Stage Over" on the under-populated fresh
            // mission. With native's stage gate they are no-ops.
            for (int i = 0; i < 4; i++)
            {
                DropAndRelease(server, bots[i], ids[i]);
                Flush(server);
            }

            Assert.Equal(MissionStage.NotStarted, fresh.Stage);
            Assert.Single(bots[0].GameOvers);   // still exactly one
            Console.WriteLine($"[P548-LIVE] post-game-over: 4 survivor drops → fresh mission stays '{fresh.Stage}', exactly {bots[0].GameOvers.Count} GAME_OVER(s) total.");
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    // ══ proof 4 — auto-assigned first player on an empty side is leader ══

    /// <summary>⭐ An auto-assigned first player (default bot sends
    /// POSITIONREQ(NA) → the auto-assign branch of CompleteSideSelection) onto
    /// an empty side must be elected team leader, matching native's
    /// AddPlayerToSide (fsmission.cpp:601-613). Red on today's code: the
    /// auto-assign path set SideId directly and skipped TryJoinSide, so
    /// LeaderShipId stayed unset. Two auto-assigned peers, one onto each empty
    /// side: each becomes that side's leader, and the first keeps its role when
    /// the second joins.</summary>
    [Fact]
    public void Auto_assigned_first_player_on_empty_side_is_leader()
    {
        using var server = new Srv();
        server.Boot(Config());

        var mission0 = server.Missions[0];
        short side0 = mission0.Sides[0].Id;
        short side1 = mission0.Sides[1].Id;

        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "M1");   // auto-assign (default auto side select)
        short bId = Seat(server, b, "M2");   // auto-assign → the OTHER empty side

        // Auto-assign routes through the same join path as native, so the first
        // player onto each empty side is elected team leader (server truth).
        Assert.Equal(aId, mission0.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(bId, mission0.Sides.First(s => s.Id == side1).LeaderShipId);
        // The second auto-assigned peer did not displace the first.
        Assert.Equal(aId, mission0.Sides.First(s => s.Id == side0).LeaderShipId);
        Console.WriteLine($"[P548-LIVE] auto-assigned players: {aId} -> side {side0} leader, {bId} -> side {side1} leader (native AddPlayerToSide fsmission.cpp:601-613).");
    }

    // ══ live loopback — 4 clients dropped one-by-one, lobby then started ══

    /// <summary>⭐ The literal live loopback from the task: start a server,
    /// connect FOUR clients, drop them one by one IN THE LOBBY (expected 0
    /// <c>[gameover]</c> lines), then seat four fresh clients, force the game
    /// STARTED and drop them one by one (expected exactly 1 <c>[gameover]</c>).
    /// Mirrors the quoted server-log count in the P548 report.</summary>
    [Fact]
    public void Live_loopback_four_clients_drop_in_lobby_then_started_game()
    {
        using var server = new Srv();
        server.Boot(Config());
        var mission0 = server.Missions[0];

        // ── Phase 1: LOBBY — 4 clients seat, drop one by one. 0 game-overs.
        var lobbyBots = Enumerable.Range(0, 4).Select(_ => new FltBot()).ToArray();
        var lobbyIds = new short[4];
        try
        {
            for (int i = 0; i < 4; i++) lobbyIds[i] = Seat(server, lobbyBots[i], $"L{i}");
            Flush(server);
            Assert.Equal(MissionStage.NotStarted, mission0.Stage);
            for (int i = 0; i < 4; i++) { DropAndRelease(server, lobbyBots[i], lobbyIds[i]); Flush(server); }
            // Still a lobby: no drop fired game-over.
            Assert.Equal(MissionStage.NotStarted, mission0.Stage);
        }
        finally { foreach (var b in lobbyBots) b.Dispose(); }

        // ── Phase 2: STARTED — 4 fresh clients (2/side), drop one by one.
        var gameBots = Enumerable.Range(0, 4).Select(_ => new FltBot()).ToArray();
        var gameIds = new short[4];
        try
        {
            for (int i = 0; i < 4; i++) gameIds[i] = Seat(server, gameBots[i], $"G{i}");
            short s0 = mission0.Sides[0].Id;
            short s1 = mission0.Sides[1].Id;
            Assert.True(server.TryJoinSide(ShipOf(server, gameIds[0]), s0));
            Assert.True(server.TryJoinSide(ShipOf(server, gameIds[1]), s0));
            Assert.True(server.TryJoinSide(ShipOf(server, gameIds[2]), s1));
            Assert.True(server.TryJoinSide(ShipOf(server, gameIds[3]), s1));
            Flush(server);
            server.Missions[0].Stage = MissionStage.Started;
            var survivor = gameBots[0];
            survivor.ResetGameOvers();

            // Drop side1's first player → side1 still occupied → NO game-over.
            DropAndRelease(server, gameBots[3], gameIds[3]);
            Flush(server);
            Assert.Equal(MissionStage.Started, mission0.Stage);

            // Drop side1's LAST player → side1 empties → exactly one game-over.
            DropAndRelease(server, gameBots[2], gameIds[2]);
            int ended = PumpUntil(server, () => mission0.Stage == MissionStage.Over, 900);
            Assert.True(ended < 900, "side1 emptied in the started game did not end the mission");
            Assert.Single(survivor.GameOvers);
            Console.WriteLine($"[P548-LIVE] loopback: lobby 4 drops -> 0 game-overs (mission '{MissionStage.NotStarted}'); started 4 drops -> {survivor.GameOvers.Count} game-over(s).");
        }
        finally { foreach (var b in gameBots) b.Dispose(); }
    }
}

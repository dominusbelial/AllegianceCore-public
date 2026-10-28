using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P864d — the client half of the team lobby (SIM-0554/0556's SIDE_TEAMLOBBY
/// pool, P864b): the team screen must list <b>who</b> is in the lobby, not a
/// count. Native gets that from the normal player-info traffic: a joining peer
/// is a mission member parked on SIDE_TEAMLOBBY (<c>fsmission.cpp:406-411</c>) and
/// the join burst's <c>SendLobbyMissionInfo</c> ends with one PLAYERINFO per ship
/// in the mission (<c>fsmission.cpp:3954-3967</c>), while side changes are the
/// JOIN_SIDE/QUIT_SIDE relays. This live test drives the real server over
/// loopback: an owner flushes its side (FlushSides, <c>FedSrv.CPP:6964-6997</c> →
/// <c>fsmission.cpp:5069-5119</c>) sending a teammate to the lobby, then a THIRD
/// peer joins and must learn that lobby player BY NAME, with side = -2, from
/// PLAYERINFO(10) — plus the SIDE_ROSTER lobby count that P864b added.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class P864dTeamLobbyClientListLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        // Native behaviour: a peer that never picks a side stays pending.
        SideSelectAutoSeatSeconds = null,
        Games =
        [
            new Gm
            {
                Name = "P864d lobby list", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
                // The flush/randomize messages are STAGE_NOTSTARTED-only
                // (FedSrv.CPP:6971-6973), so hold the mission there.
                AutoStart = false,
            },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 864u, DwCookie = 0x864u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8640000000000000000ul, DrmHash = "p864d",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Connect a bot and hold it at the side-select screen.</summary>
    private static void ConnectPending(Srv server, FltBot bot, string name)
    {
        bot.AutoSelectSideAfterLogon = false;
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        PumpUntil(server, () => bot.SideRosters.Count > 0, 400);
    }

    private static short Seat(Srv server, FltBot bot, string name, short sideId)
    {
        ConnectPending(server, bot, name);
        bot.SendPositionReq(sideId);
        int got = PumpUntil(server, () => bot.ShipAssignment is not null, 1200);
        Assert.True(got < 1200, $"{name} did not seat after picking a side");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_joining_peer_learns_the_lobby_players_by_name_from_the_player_info_burst()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        short side0 = server.Missions[0].Sides[0].Id;

        // The owner/leader of side 0 and one teammate on the same side.
        using var owner = new FltBot();
        using var teammate = new FltBot();
        short ownerId = Seat(server, owner, "P864D-OWNER", side0);
        short teammateId = Seat(server, teammate, "P864D-LOBBY", side0);

        Assert.Equal(side0, ShipOf(server, ownerId).SideId);
        Assert.Equal(side0, ShipOf(server, teammateId).SideId);
        short ownerShip = server.Missions[0].Sides.First(s => s.Id == side0).LeaderShipId ?? -1;
        Assert.Equal(ownerId, ownerShip);   // first player on the side leads it

        // Owner flushes the team: every NON-leader goes to SIDE_TEAMLOBBY, the
        // leader stays anchored (fsmission.cpp:5097-5117).
        owner.SendRandomizeTeams();
        int flushed = PumpUntil(server, () => ShipOf(server, teammateId).SideId == -2, 1200);
        Assert.True(flushed < 1200, "the flush never moved the teammate to the team lobby");
        Assert.Equal(side0, ShipOf(server, ownerId).SideId);   // leader kept its side

        // A third peer joins and stays at the side-select screen. Native gives it
        // the whole mission player list in the join burst; it must therefore know
        // the lobby player by name AND side, not just a count.
        using var newcomer = new FltBot();
        ConnectPending(server, newcomer, "P864D-NEW");

        var lobbyInfo = newcomer.PlayerInfos.FirstOrDefault(p => p.ShipId == teammateId);
        Assert.NotNull(lobbyInfo);
        Assert.Equal((short)-2, lobbyInfo!.ISide);                 // SIDE_TEAMLOBBY
        Assert.Equal("P864D-LOBBY", lobbyInfo.CharacterName);
        // The seated leader rides the same burst with its real side, so the
        // team screen can render the whole mission.
        var ownerInfo = newcomer.PlayerInfos.FirstOrDefault(p => p.ShipId == ownerId);
        Assert.NotNull(ownerInfo);
        Assert.Equal(side0, ownerInfo!.ISide);

        // P864b's roster scalar keeps agreeing with the wire-derived pool.
        Assert.Equal(1, newcomer.SideRosters[^1].LobbyPlayerCount);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_lobby_player_requests_a_side_and_the_join_side_relay_moves_it_out_of_the_pool()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var owner = new FltBot();
        using var teammate = new FltBot();
        short ownerId = Seat(server, owner, "P864D-OWNER2", side0);
        short teammateId = Seat(server, teammate, "P864D-LOBBY2", side0);

        owner.SendRandomizeTeams();
        Assert.True(PumpUntil(server, () => ShipOf(server, teammateId).SideId == -2, 1200) < 1200);

        // The lobby player asks for the other (leaderless) side: native
        // auto-accepts a request to a side with no leader (fsmission.cpp:4757)
        // and the lobby exemption of the no-defections gate lets it through
        // (fsmission.cpp:4583-4590).
        teammate.SendPositionReq(side1);
        Assert.True(PumpUntil(server, () => ShipOf(server, teammateId).SideId == side1, 1200) < 1200,
            "the lobby player never landed on the side it requested");

        // Everyone still in the mission hears the JOIN_SIDE relay that empties
        // the pool; the client's Apply(JoinSide) is what removes the row.
        Assert.Contains(owner.JoinSides, j => j.ShipId == teammateId && j.SideId == side1);
        Assert.DoesNotContain(owner.QuitSides, q => q.ShipId == ownerId);   // leader never left
    }
}

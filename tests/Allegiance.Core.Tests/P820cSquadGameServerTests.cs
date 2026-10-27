using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

// 'Allegiance.Server' is a namespace AND our type's namespace; the bare name
// 'Server' would bind to the namespace. Declared inside the namespace body so
// the using-alias reliably wins simple-name resolution (same as ServerTests.cs).
using Server = global::Allegiance.Server.Server;

/// <summary>
/// P820c — behavioral tests for the squad-game gate (fix 1), the call-site fix
/// (fix 2: squad only on leader join, kept on succession) and the real quit-side
/// boot (fix 3: QUIT_SIDE SquadChange). Booted headless server (real core+map),
/// peers seeded with squad-bearing join identities, driven through the internal
/// test seams. Run 3× alone (rules light gate); requires artwork so it skips
/// cleanly without it.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P820cSquadGameServerTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static ServerConfig Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new GameConfig
            {
                Name = "P820c Squad Mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static JoinIdentity Identity(params SquadClaim[] squads) => new(
        Iss: "https://lobby.test", Aud: "game", Sub: "00000000000000000000000000000001", Callsign: "Pilot",
        Iat: 1700000000, Exp: 1700086400, Jti: "11111111111111111111111111111111",
        squads.Length == 0 ? null : squads);

    private static ServerConfig ConfigLock() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new GameConfig
            {
                Name = "P842 Locked Mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
                LockTeamSettings = true,          // native bLockTeamSettings (igc.h:1138)
            },
        ],
    };

    private static List<(ConnectionId Peer, byte[] Msg)> CaptureSends(Server server)
    {
        var sent = new List<(ConnectionId, byte[])>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        { foreach (var m in FedPacket.Unpack(bytes)) sent.Add((peer, m)); };
        return sent;
    }

    private static bool IsQuitSide(byte[] msg, short shipId, QuitSideReason reason) =>
        (msg[2] | msg[3] << 8) == QuitSide.Fmid && QuitSide.Decode(msg) is { ShipId: var sid } q && sid == shipId && q.Reason == reason;

    private static bool IsSetTeamInfo(byte[] msg, short sideId) =>
        (msg[2] | msg[3] << 8) == SetTeamInfo.Fmid && SetTeamInfo.Decode(msg).SideId == sideId;

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NonSquadGame_has_no_side_squad_and_boots_nobody()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.SquadGame = false;   // default — a normal public server
        var side0 = mission.Sides.First(s => s.Id == 0);

        var leaderConn = new ConnectionId(700);
        var memberConn = new ConnectionId(701);
        var leaderShip = Seat(mission, 19000, side0.Id);
        var memberShip = Seat(mission, 19001, side0.Id);
        server.TestSeedPeers([(leaderConn, leaderShip, mission.Sectors[0]), (memberConn, memberShip, mission.Sectors[0])]);
        server.TestSetIdentity(leaderConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        server.TestSetIdentity(memberConn, Identity(new SquadClaim("bbbb", "Squad B", true)));
        side0.LeaderShipId = leaderShip.Id;

        var sent = CaptureSends(server);
        server.TestEstablishSideSquad(mission, side0);

        Assert.Null(side0.SquadId);                      // non-squad game: no side squad
        Assert.Equal(side0.Id, memberShip.SideId);       // member NOT booted
        Assert.Equal(side0.Id, leaderShip.SideId);
        Assert.DoesNotContain(sent, s => IsQuitSide(s.Msg, memberShip.Id, QuitSideReason.SquadChange));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SquadGame_sets_squad_when_leader_joins_and_boots_non_member_via_QuitSide()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.SquadGame = true;
        var side0 = mission.Sides.First(s => s.Id == 0);

        var leaderConn = new ConnectionId(700);
        var memberConn = new ConnectionId(701);
        var leaderShip = Seat(mission, 19000, side0.Id);
        var memberShip = Seat(mission, 19001, side0.Id);
        server.TestSeedPeers([(leaderConn, leaderShip, mission.Sectors[0]), (memberConn, memberShip, mission.Sectors[0])]);
        // Leader is on squad A (and can lead it); the teammate is on a DIFFERENT squad B.
        server.TestSetIdentity(leaderConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        server.TestSetIdentity(memberConn, Identity(new SquadClaim("bbbb", "Squad B", true)));
        side0.LeaderShipId = leaderShip.Id;

        var sent = CaptureSends(server);
        server.TestEstablishSideSquad(mission, side0);

        Assert.Equal("aaaa", side0.SquadId);             // squad derived from the leader's preferred squad
        Assert.Equal("Squad A", side0.SquadName);
        Assert.Equal(side0.Id, leaderShip.SideId);       // leader is a member — stays seated
        Assert.Equal((short?)-2, memberShip.SideId);   // SIDE_TEAMLOBBY — booted
        Assert.Contains(sent, s => IsQuitSide(s.Msg, memberShip.Id, QuitSideReason.SquadChange));  // real quit-side broadcast
        Assert.Contains(sent, s => IsSetTeamInfo(s.Msg, side0.Id));                                // SET_TEAM_INFO relay
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MidGameSuccession_keeps_the_squad_and_prefers_a_squad_capable_successor()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.SquadGame = true;
        mission.Stage = MissionStage.Started;   // mid-game: succession must KEEP the squad
        var side0 = mission.Sides.First(s => s.Id == 0);
        side0.SquadId = "aaaa"; side0.SquadName = "Squad A";

        var leaderConn = new ConnectionId(700);
        var capableConn = new ConnectionId(701);
        var incapableConn = new ConnectionId(702);
        var leaderShip = Seat(mission, 19000, side0.Id);
        var capableShip = Seat(mission, 19001, side0.Id);
        var incapableShip = Seat(mission, 19002, side0.Id);
        server.TestSeedPeers([(leaderConn, leaderShip, mission.Sectors[0]),
            (capableConn, capableShip, mission.Sectors[0]), (incapableConn, incapableShip, mission.Sectors[0])]);
        // Capable teammate can lead squad A; the other only leads squad C.
        server.TestSetIdentity(leaderConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        server.TestSetIdentity(capableConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        server.TestSetIdentity(incapableConn, Identity(new SquadClaim("cccc", "Squad C", true)));
        side0.LeaderShipId = leaderShip.Id;

        var sent = CaptureSends(server);
        server.TestHandleLeaderSuccession(leaderShip);   // the leader leaves a STARTED game

        Assert.Equal("aaaa", side0.SquadId);             // squad KEPT — not re-derived, not cleared
        Assert.Equal("Squad A", side0.SquadName);
        Assert.Equal(capableShip.Id, side0.LeaderShipId); // prefers the squad-A-capable successor (GetCanLeadSquad)
        Assert.DoesNotContain(sent, s => IsQuitSide(s.Msg, capableShip.Id, QuitSideReason.SquadChange));  // nobody booted mid-game
    }

    /// <summary>P842 / SIM-0510 — pre-start leader departure on a squad side:
    /// native RemovePlayerFromSide's tail runs MaintainSquadLeadership
    /// (fsmission.cpp:1154-1155 → 1683-1739). The successor (first remaining
    /// player) cannot lead the side's squad, so the side re-fits to the
    /// successor's own preferred squad (fsmission.cpp:1698-1722) and non-members
    /// are booted (SetSideSquad, fsmission.cpp:1623-1641) — including the
    /// deposed leader, who is still seeded on the side here. Must-fail-if-
    /// removed: before the fix the squad stayed "aaaa" and nobody was booted.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PreStartLeaderDeparture_refits_the_squad_to_the_successors_preference()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.SquadGame = true;               // pre-start (default NotStarted)
        var side0 = mission.Sides.First(s => s.Id == 0);
        side0.SquadId = "aaaa"; side0.SquadName = "Squad A";

        var leaderConn = new ConnectionId(700);
        var memberConn = new ConnectionId(701);
        var leaderShip = Seat(mission, 19000, side0.Id);
        var memberShip = Seat(mission, 19001, side0.Id);
        server.TestSeedPeers([(leaderConn, leaderShip, mission.Sectors[0]), (memberConn, memberShip, mission.Sectors[0])]);
        // Leader is on squad A; the successor only leads squad B.
        server.TestSetIdentity(leaderConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        server.TestSetIdentity(memberConn, Identity(new SquadClaim("bbbb", "Squad B", true)));
        side0.LeaderShipId = leaderShip.Id;

        var sent = CaptureSends(server);
        server.TestHandleLeaderSuccession(leaderShip);   // the leader leaves a NOT-STARTED squad game

        Assert.Equal(memberShip.Id, side0.LeaderShipId); // succession picked the only remaining player
        Assert.Equal("bbbb", side0.SquadId);             // squad re-fit to the successor's preferred squad
        Assert.Equal("Squad B", side0.SquadName);
        Assert.Contains(sent, s => IsQuitSide(s.Msg, leaderShip.Id, QuitSideReason.SquadChange));  // non-member booted
        Assert.Equal((short?)-2, leaderShip.SideId);     // SIDE_TEAMLOBBY
        Assert.Equal(side0.Id, memberShip.SideId);       // successor is a member — stays seated
    }

    /// <summary>P842 / SIM-0510 — pre-start leader departure when NO remaining
    /// player has a preferred squad: the squad is cleared (SetSideSquad(sid, NA),
    /// fsmission.cpp:1722) and every remaining player is booted
    /// (fsmission.cpp:1724-1737). Must-fail-if-removed: before the fix the
    /// squad stayed "aaaa" and the teammate kept the seat.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PreStartLeaderDeparture_clears_the_squad_and_boots_everyone_when_no_leader_exists()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.SquadGame = true;
        var side0 = mission.Sides.First(s => s.Id == 0);
        side0.SquadId = "aaaa"; side0.SquadName = "Squad A";

        var leaderConn = new ConnectionId(700);
        var anonConn = new ConnectionId(701);            // anonymous teammate: no squads at all
        var leaderShip = Seat(mission, 19000, side0.Id);
        var anonShip = Seat(mission, 19001, side0.Id);
        server.TestSeedPeers([(leaderConn, leaderShip, mission.Sectors[0]), (anonConn, anonShip, mission.Sectors[0])]);
        server.TestSetIdentity(leaderConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        side0.LeaderShipId = leaderShip.Id;

        var sent = CaptureSends(server);
        server.TestHandleLeaderSuccession(leaderShip);

        Assert.Null(side0.SquadId);                      // SetSideSquad(sid, NA)
        Assert.Equal((short?)-2, anonShip.SideId);       // everyone booted (fsmission.cpp:1724-1737)
        Assert.Contains(sent, s => IsQuitSide(s.Msg, anonShip.Id, QuitSideReason.SquadChange));
    }

    /// <summary>P842 / SIM-0509 — bLockTeamSettings: a side's squad still changes
    /// but the team NAME is preserved (native SetSideSquad passes
    /// m_misdef.rgszName[sid] to SetSideName, fsmission.cpp:1645-1668).
    /// Must-fail-if-removed: before the fix the relay used the squad name.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LockTeamSettings_keeps_the_side_name_when_the_squad_changes()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(ConfigLock());
        var mission = server.TestMission!;
        mission.SquadGame = true;
        var side0 = mission.Sides.First(s => s.Id == 0);

        var leaderConn = new ConnectionId(700);
        var memberConn = new ConnectionId(701);
        var leaderShip = Seat(mission, 19000, side0.Id);
        var memberShip = Seat(mission, 19001, side0.Id);
        server.TestSeedPeers([(leaderConn, leaderShip, mission.Sectors[0]), (memberConn, memberShip, mission.Sectors[0])]);
        server.TestSetIdentity(leaderConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        server.TestSetIdentity(memberConn, Identity(new SquadClaim("aaaa", "Squad A", true)));
        side0.LeaderShipId = leaderShip.Id;

        var sent = CaptureSends(server);
        server.TestEstablishSideSquad(mission, side0);

        Assert.Equal("aaaa", side0.SquadId);             // the squad itself still changes
        var info = sent.Select(s => SetTeamInfo.Decode(s.Msg))
                       .Where(t => t.SideId == side0.Id).ToList();
        Assert.NotEmpty(info);
        Assert.All(info, m => Assert.Equal("Alpha", m.SideName));   // name PRESERVED, not "Squad A"
    }

    private static Ship Seat(Mission mission, short id, short side)
    {
        var s = new Ship(id, mission.Core.HullTypes.Values.First(), new Vector3(0, 10000, 0),
            Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(s, mission.Sectors[0]);
        return s;
    }
}

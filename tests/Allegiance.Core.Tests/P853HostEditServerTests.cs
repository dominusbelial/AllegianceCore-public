using Allegiance.Core.Collision;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

// 'Allegiance.Server' is a namespace AND our type's namespace; the bare name
// 'Server' would bind to the namespace (same idiom as ServerTests.cs/P820c).
using Server = global::Allegiance.Server.Server;

/// <summary>
/// P853 — the live host-editing SERVER guards, driven through the real inbound
/// dispatcher (<c>Server.TestProcessDatagram</c>) on a booted headless server:
/// <list type="bullet">
/// <item>FM_CS_MISSIONPARAMS(113): owner-only (FedSrv.CPP:7529-7531),
/// Invalid(true) TrapHackBoot (:7534), NOTSTARTED-only (:7560), and the
/// whole-mission MISSIONDEF(38) re-advertisement (:7577);</item>
/// <item>FM_CS_SET_TEAM_INFO(15): NOTSTARTED + unlocked settings only
/// (:7433-7436), the four TrapHackBoots (:7439-7442), the leadership race
/// check (:7445-7448), the leader rename relay (fsmission.cpp:1564-1600) and
/// the squad-game re-point (:7451-7459);</item>
/// <item>the nTeams shrink boot (QSR_SideDestroyed, fsmission.cpp:1821-1835)
/// with its SIDE_INACTIVE(68) broadcast.</item>
/// </list>
/// Every test's assertion fails against master (the handlers did not exist —
/// the messages hit the unsupported-fmid refusal), so each guard is proven to
/// fail before the fix.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P853HostEditServerTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static ServerConfig Config(bool lockTeamSettings = false, int teams = 2) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new GameConfig
            {
                Name = "P853 Host Edit", Map = "XXV_simple.igc", Teams = teams,
                CivIds = new[] { 1, 3, 4, 5, 6, 7 }.Take(teams).ToArray(),
                SideNames = Enumerable.Range(0, teams).Select(i => $"Side{i}").ToArray(),
                LockTeamSettings = lockTeamSettings,
            },
        ],
    };

    private static JoinIdentity Identity(params SquadClaim[] squads) => new(
        Iss: "https://lobby.test", Aud: "game", Sub: "00000000000000000000000000000001", Callsign: "Pilot",
        Iat: 1700000000, Exp: 1700086400, Jti: "11111111111111111111111111111111",
        squads.Length == 0 ? null : squads);

    private static Ship Seat(Mission mission, short id, short side)
    {
        var s = new Ship(id, mission.Core.HullTypes.Values.First(), new Vector3(0, 10000, 0),
            Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(s, mission.Sectors[0]);
        return s;
    }

    private static List<(ConnectionId Peer, byte[] Msg)> CaptureSends(Server server)
    {
        var sent = new List<(ConnectionId, byte[])>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        { foreach (var m in FedPacket.Unpack(bytes)) sent.Add((peer, m)); };
        return sent;
    }

    private static bool IsMissionDef(byte[] msg) => (msg[2] | msg[3] << 8) == MissionDef.Fmid;
    private static bool IsSetTeamInfo(byte[] msg) => (msg[2] | msg[3] << 8) == SetTeamInfo.Fmid;
    private static bool IsSideInactive(byte[] msg) => (msg[2] | msg[3] << 8) == SideInactive.Fmid;
    private static bool IsQuitSide(byte[] msg) => (msg[2] | msg[3] << 8) == QuitSide.Fmid;

    /// <summary>Seat an owner (leader of the mission-owner side) plus a plain
    /// teammate, and return the owner's connection.</summary>
    private static (ConnectionId Owner, ConnectionId Mate, Ship OwnerShip, Ship MateShip) SeatOwnerAndMate(
        Server server, Mission mission, short sideId = 0)
    {
        var ownerConn = new ConnectionId(830);
        var mateConn = new ConnectionId(831);
        var ownerShip = Seat(mission, 19300, sideId);
        var mateShip = Seat(mission, 19301, sideId);
        server.TestSeedPeers([(ownerConn, ownerShip, mission.Sectors[0]), (mateConn, mateShip, mission.Sectors[0])]);
        server.TestSetIdentity(ownerConn, Identity());
        server.TestSetIdentity(mateConn, Identity());
        mission.MissionOwnerSideId = sideId;
        mission.Sides.First(s => s.Id == sideId).LeaderShipId = ownerShip.Id;
        return (ownerConn, mateConn, ownerShip, mateShip);
    }

    private static MissionParamsMsg Edit(int lives = 30, short maxRank = 40) => new()
    {
        StrGameName = "P853 Host Edit",
        NTems = 2,
        ILives = (short)lives,
        IMinRank = -1,
        IMaxRank = maxRank,
        NMinPlayersPerTeam = 1,
        NMaxPlayersPerTeam = 100,
        NTotalMaxPlayersPerGame = unchecked((short)0x7FFF),
        IGoalConquestPercentage = 100,          // a winning condition must exist
        IGoalTerritoryPercentage = 100,         // native's territory DISABLED sentinel (igc.h:1516)
    };

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owner_params_edit_relays_a_missiondef_every_client_sees()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);
        var sent = CaptureSends(server);

        server.TestProcessDatagram(owner, FedPacket.Pack([Edit(lives: 30).Encode()]));

        // The whole-mission MISSIONDEF(38) re-advertisement (FedSrv.CPP:7577)
        // carries the new params — every mission peer sees the edit.
        var defs = sent.Where(s => IsMissionDef(s.Msg)).Select(s => MissionDef.Decode(s.Msg)).ToList();
        Assert.NotEmpty(defs);
        Assert.All(defs, d => Assert.Equal(30, d.ILives));
        Assert.All(defs, d => Assert.Equal(40, d.IMaxRank));
        Assert.Contains(sent, s => s.Peer == owner && IsMissionDef(s.Msg));
        Assert.Contains(sent, s => s.Peer == new ConnectionId(831) && IsMissionDef(s.Msg));
        // The mission itself adopted the settings (the lobby advertisement
        // reads these — native SetLobbyIsDirty, FedSrv.CPP:7579).
        Assert.Equal(30, mission.Lives);
        Assert.Equal(40, server.TestParamsOf(mission).IMaxRank);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_owner_params_edit_is_refused()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, mate, _, _) = SeatOwnerAndMate(server, mission);
        var before = server.TestParamsOf(mission).ILives;
        var sent = CaptureSends(server);

        server.TestProcessDatagram(mate, FedPacket.Pack([Edit(lives: 77).Encode()]));   // native: GetOwner() != pfsPlayer → break

        Assert.DoesNotContain(sent, s => IsMissionDef(s.Msg));       // no re-advertisement
        Assert.Equal(before, server.TestParamsOf(mission).ILives);   // params untouched
        Assert.Equal(before, mission.Lives);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Invalid_params_trap_hack_boot_the_sender()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);
        var before = server.TestParamsOf(mission).ILives;
        var sent = CaptureSends(server);

        var bad = Edit(lives: 30);
        bad.NMaxPlayersPerTeam = 0;    // igc.h:1343-1348 (client leg: must be 1..100)
        server.TestProcessDatagram(owner, FedPacket.Pack([bad.Encode()]));

        Assert.DoesNotContain(sent, s => IsMissionDef(s.Msg));       // TrapHackBoot(Invalid(true) == NULL), FedSrv.CPP:7534
        Assert.Equal(before, server.TestParamsOf(mission).ILives);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Params_edit_on_a_started_mission_is_silently_ignored()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);
        mission.Stage = MissionStage.Started;     // native: stage != STAGE_NOTSTARTED → ignore
        var before = server.TestParamsOf(mission).ILives;
        var sent = CaptureSends(server);

        server.TestProcessDatagram(owner, FedPacket.Pack([Edit(lives: 77).Encode()]));

        Assert.DoesNotContain(sent, s => IsMissionDef(s.Msg));
        Assert.Equal(before, server.TestParamsOf(mission).ILives);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_rename_in_notstarted_relays_set_team_info()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, _, ownerShip, _) = SeatOwnerAndMate(server, mission);
        var sent = CaptureSends(server);

        server.TestProcessDatagram(owner, FedPacket.Pack([new SetTeamInfo
        {
            SideId = 0, SquadId = -1, SideName = "Renamed Team",   // non-squad game, squadID NA
        }.Encode()]));

        // fsmission.cpp:1564-1600 — the side is renamed and SET_TEAM_INFO(15)
        // goes to the whole mission with the new real name.
        Assert.Equal("Renamed Team", mission.Sides.First(s => s.Id == 0).Name);
        var relays = sent.Where(s => IsSetTeamInfo(s.Msg)).Select(s => SetTeamInfo.Decode(s.Msg)).ToList();
        Assert.Contains(relays, r => r.SideId == 0 && r.SideName == "Renamed Team");
        Assert.Contains(sent, s => s.Peer == new ConnectionId(831) && IsSetTeamInfo(s.Msg));  // the teammate sees it too
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_leader_rename_is_refused()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, mate, _, mateShip) = SeatOwnerAndMate(server, mission);
        var sent = CaptureSends(server);

        // The teammate sends the rename for its OWN side — but it is not that
        // side's leader (native FedSrv.CPP:7445-7448 race check).
        server.TestProcessDatagram(mate, FedPacket.Pack([new SetTeamInfo
        { SideId = 0, SquadId = -1, SideName = "MUTINY" }.Encode()]));

        Assert.Equal("Side0", mission.Sides.First(s => s.Id == 0).Name);   // unchanged
        Assert.DoesNotContain(sent, s => IsSetTeamInfo(s.Msg));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rename_on_a_started_mission_is_silently_ignored()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);
        mission.Stage = MissionStage.Started;      // native FedSrv.CPP:7433: STAGE_NOTSTARTED only
        var sent = CaptureSends(server);

        server.TestProcessDatagram(owner, FedPacket.Pack([new SetTeamInfo
        { SideId = 0, SquadId = -1, SideName = "Late Rename" }.Encode()]));

        Assert.Equal("Side0", mission.Sides.First(s => s.Id == 0).Name);
        Assert.DoesNotContain(sent, s => IsSetTeamInfo(s.Msg));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rename_with_locked_team_settings_is_silently_ignored()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config(lockTeamSettings: true));    // native bLockTeamSettings, igc.h:1138
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);
        var sent = CaptureSends(server);

        server.TestProcessDatagram(owner, FedPacket.Pack([new SetTeamInfo
        { SideId = 0, SquadId = -1, SideName = "Locked Out" }.Encode()]));

        Assert.Equal("Side0", mission.Sides.First(s => s.Id == 0).Name);
        Assert.DoesNotContain(sent, s => IsSetTeamInfo(s.Msg));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unterminated_side_name_trap_hack_boots()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);
        var sent = CaptureSends(server);

        // 25 chars with no terminator: native memchr(SideName, 0, c_cbName)
        // finds no NUL → TrapHackBoot (FedSrv.CPP:7441). The codec carries all
        // 25 bytes, so the server sees exactly what native would.
        server.TestProcessDatagram(owner, FedPacket.Pack([new SetTeamInfo
        { SideId = 0, SquadId = -1, SideName = new string('X', 25) }.Encode()]));

        Assert.Equal("Side0", mission.Sides.First(s => s.Id == 0).Name);
        Assert.DoesNotContain(sent, s => IsSetTeamInfo(s.Msg));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Squad_game_set_team_info_repoints_the_side_squad()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        mission.SquadGame = true;
        var (owner, _, ownerShip, _) = SeatOwnerAndMate(server, mission);
        server.TestSetIdentity(owner, Identity(new SquadClaim("aaaa", "Squad A", true)));
        var sent = CaptureSends(server);

        var wireSquad = SquadWireIdOf("aaaa");
        server.TestProcessDatagram(owner, FedPacket.Pack([new SetTeamInfo
        { SideId = 0, SquadId = wireSquad, SideName = "Squad A" }.Encode()]));

        // FedSrv.CPP:7451-7459 — a squad game re-points the side's squad
        // (SetSideSquad), which relays SET_TEAM_INFO with the squad's id.
        Assert.Equal("aaaa", mission.Sides.First(s => s.Id == 0).SquadId);
        Assert.Contains(sent, s => IsSetTeamInfo(s.Msg));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NTeams_shrink_boots_the_destroyed_sides_members()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config(teams: 3));
        var mission = server.TestMission!;
        var (owner, mate, _, _) = SeatOwnerAndMate(server, mission);
        var victimConn = new ConnectionId(832);
        var victimShip = Seat(mission, 19302, 2);            // seated on the side that will die
        // TestSeedPeers replaces the table, so the victim must join the same
        // seed call as the owner/mate pair — reseed all three together.
        var ownerConn = owner;
        var mateConn = mate;
        var ownerShip = server.Ships.First(s => s.Ship.Id == 19300).Ship;
        var mateShip2 = server.Ships.First(s => s.Ship.Id == 19301).Ship;
        server.TestSeedPeers([(ownerConn, ownerShip, mission.Sectors[0]), (mateConn, mateShip2, mission.Sectors[0]),
            (victimConn, victimShip, mission.Sectors[0])]);
        server.TestSetIdentity(ownerConn, Identity());
        server.TestSetIdentity(victimConn, Identity());
        var sent = CaptureSends(server);

        var edit = Edit(lives: 30);
        edit.NTems = 2;                                       // 3 -> 2 teams
        server.TestProcessDatagram(owner, FedPacket.Pack([edit.Encode()]));

        // fsmission.cpp:1821-1835 — everyone on each extra team is kicked
        // (RemovePlayerFromSide QSR_SideDestroyed) and the side is destroyed.
        Assert.Contains(sent, s => IsQuitSide(s.Msg) && QuitSide.Decode(s.Msg) is { } q
            && q.ShipId == victimShip.Id && q.Reason == QuitSideReason.SideDestroyed);
        Assert.Contains(sent, s => IsSideInactive(s.Msg) && SideInactive.Decode(s.Msg) is { } si
            && si.SideId == 2 && !si.BActive);
        var defs = sent.Where(s => IsMissionDef(s.Msg)).Select(s => MissionDef.Decode(s.Msg)).ToList();
        Assert.NotEmpty(defs);
        Assert.All(defs, d => Assert.Equal(2, d.NTems));      // the re-advertisement tells the new count
        Assert.Equal((short)2, server.TestParamsOf(mission).NTems);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NTeams_regrow_after_shrink_reactivates_the_side()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config(teams: 3));
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);

        var shrink = Edit(lives: 30);
        shrink.NTems = 2;                                     // 3 -> 2 teams
        server.TestProcessDatagram(owner, FedPacket.Pack([shrink.Encode()]));
        Assert.False(mission.Sides.First(s => s.Id == 2).Active);

        var sent = CaptureSends(server);
        var grow = Edit(lives: 30);
        grow.NTems = 3;                                       // 2 -> 3 teams (native InitSide, fsmission.cpp:1846-1852)
        server.TestProcessDatagram(owner, FedPacket.Pack([grow.Encode()]));

        Assert.True(mission.Sides.First(s => s.Id == 2).Active);
        Assert.Contains(sent, s => IsSideInactive(s.Msg) && SideInactive.Decode(s.Msg) is { } si
            && si.SideId == 2 && si.BActive);
        Assert.Equal((short)3, server.TestParamsOf(mission).NTems);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Params_edit_cannot_change_game_name_or_password()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, _, _, _) = SeatOwnerAndMate(server, mission);
        var before = server.TestParamsOf(mission);

        var edit = Edit(lives: 30);
        edit.StrGameName = "Renamed by client";
        edit.StrGamePassword = "hunter2";
        server.TestProcessDatagram(owner, FedPacket.Pack([edit.Encode()]));

        // FedSrv.CPP:7562-7572 (KGJV #114): name and password stay the server's.
        var after = server.TestParamsOf(mission);
        Assert.Equal(30, mission.Lives);
        Assert.Equal(before.StrGameName, after.StrGameName);
        Assert.Equal(before.StrGamePassword, after.StrGamePassword);
    }

    /// <summary>The same FNV-1a wire mapping Server.SquadWireId uses (the
    /// squadID int on the SET_TEAM_INFO wire is an opaque stable hash of the
    /// port's string squad guid, P820c).</summary>
    private static int SquadWireIdOf(string squadId)
    {
        unchecked
        {
            int h = unchecked((int)2166136261u);
            foreach (char c in squadId) { h ^= c; h *= 16777619; }
            return h;
        }
    }
}

using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Server = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

/// <summary>
/// P866 / SIM-0537 — the live half of native <c>CFSMission::QueueLobbyMissionInfo</c>
/// (<c>FedSrv/fsmission.cpp:3983-4096</c>): the fields the port previously did not
/// publish — player/noat counts, per-team caps, max-per-game, the slot-availability
/// scan (including the leaderless-auto-accept guaranteed bit and the lobby-lock /
/// per-team-cap branches), the armed countdown offset, squad game, eject pods,
/// invulnerable stations and the playing sides' squad ids.
/// <para>P866 / SIM-0572 — the same file proves the native DIRTY FLAG: every
/// <c>SetLobbyIsDirty</c> call site this port has bumps the version the heartbeat
/// reads (mission-param edit <c>FedSrv.CPP:7579</c>, stage change
/// <c>fsmission.cpp:1766</c>, player leaving <c>:523</c>, team-lobby change
/// <c>fsship.cpp:1134</c>), so a runtime change reaches the directory on the next
/// throttled tick instead of only the lease refresh.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P866LobbyPublicationTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int maxPerTeam = 2) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "P866 Lobby", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"], MaxPlayersPerTeam = maxPerTeam, MinPlayersPerTeam = 1,
            },
        ],
    };

    private static Allegiance.Core.Sim.Ship Seat(Allegiance.Core.Model.Mission mission, short id, short? side)
    {
        var ship = new Allegiance.Core.Sim.Ship(id, mission.Core.HullTypes.Values.First(),
            Allegiance.Core.Collision.Vector3.Zero, Allegiance.Core.Collision.Vector3.Zero,
            Allegiance.Core.Sim.Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    /// <summary>An owner-led two-side server plus an unseated (team-lobby) peer:
    /// the exact inputs QueueLobbyMissionInfo counts.</summary>
    private static (Server Server, Allegiance.Core.Model.Mission Mission, ConnectionId Owner) Boot(int maxPerTeam = 2)
    {
        var server = new Server();
        server.Boot(Config(maxPerTeam));
        var mission = server.TestMission!;
        var ownerShip = Seat(mission, 17000, 0);
        var otherShip = Seat(mission, 17001, 1);
        var lobbyShip = Seat(mission, 17002, null);
        var owner = new ConnectionId(950);
        server.TestSeedPeers([
            (owner, ownerShip, mission.Sectors[0]),
            (new ConnectionId(951), otherShip, mission.Sectors[0]),
            (new ConnectionId(952), lobbyShip, mission.Sectors[0]),
        ]);
        mission.MissionOwnerSideId = 0;
        mission.Sides.First(s => s.Id == 0).LeaderShipId = ownerShip.Id;
        mission.Sides.First(s => s.Id == 1).LeaderShipId = otherShip.Id;
        // Side.AutoAccept starts TRUE (native InitSide, fsmission.cpp:58); the
        // "no guaranteed slot" branch below requires both sides NOT auto-accepting.
        foreach (var side in mission.Sides) side.AutoAccept = false;
        return (server, mission, owner);
    }

    /// <summary>⭐ The published mission info carries native's counts, caps and the
    /// slot-availability scan — including each of its branches: available slots,
    /// guaranteed slots (a side auto-accepting, or leaderless with
    /// fAutoAcceptLeaders folded on), and the two "no" branches (lobby locked, a
    /// full per-team cap).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Published_mission_info_carries_counts_caps_and_the_slot_scan()
    {
        Require.Artwork();
        var (server, mission, _) = Boot();
        using var _server = server;

        var info = server.TestLobbyParams();
        Assert.Equal(3, info.NumPlayers);                       // GetCountOfPlayers(NULL,false)
        Assert.Equal(1, info.NumNoatPlayers);                   // SIDE_TEAMLOBBY players
        Assert.Equal((short)2, info.Teams);                     // misparms.nTeams
        Assert.Equal((short)1, info.MinPlayersPerTeam);
        Assert.Equal((short)2, info.MaxPlayersPerTeam);
        Assert.Equal(4, info.MaxPlayersPerGame);                // nTeams * nMaxPlayersPerTeam
        Assert.False(info.SquadGame);
        Assert.True(info.EjectPods);                            // native Reset default
        Assert.False(info.InvulnerableStations);
        Assert.False(info.CountdownStarted);
        Assert.Equal(0u, info.StartTimeOffsetMs);
        Assert.Null(info.SquadIds);

        // One seat open on each side (1 of 2 taken), both sides led and not
        // auto-accepting: slots exist, but none is guaranteed.
        Assert.True(info.AnySlotsAvailable);
        Assert.False(info.GuaranteedSlotsAvailable);

        // A side that auto-accepts makes a joiner's seat guaranteed (:4066-4072).
        mission.Sides.First(s => s.Id == 1).AutoAccept = true;
        info = server.TestLobbyParams();
        Assert.True(info.GuaranteedSlotsAvailable);

        // A leaderless side auto-accepts too (fAutoAcceptLeaders, always on here).
        mission.Sides.First(s => s.Id == 1).AutoAccept = false;
        mission.Sides.First(s => s.Id == 1).LeaderShipId = null;
        info = server.TestLobbyParams();
        Assert.True(info.GuaranteedSlotsAvailable);
        mission.Sides.First(s => s.Id == 1).LeaderShipId = 17001;

        // Locked lobby: the scan is gated out entirely (:4047-4048).
        mission.LockLobby = true;
        info = server.TestLobbyParams();
        Assert.False(info.AnySlotsAvailable);
        Assert.False(info.GuaranteedSlotsAvailable);

        // Unlocked but every team full: no slot anywhere (:4052-4057).
        mission.LockLobby = false;
        var full = Boot(maxPerTeam: 1);
        using var _full = full.Server;
        var fullInfo = full.Server.TestLobbyParams();
        Assert.False(fullInfo.AnySlotsAvailable, "a full per-team cap leaves no slot");
        Assert.False(fullInfo.GuaranteedSlotsAvailable);
    }

    /// <summary>⭐ The armed countdown and a squad game's playing-squad list are
    /// published, matching native's <c>dwStartTime</c>/<c>fCountdownStarted</c>
    /// (<c>fsmission.cpp:4036-4037,4084-4086</c>) and <c>rgSquadIDs</c>
    /// (<c>:3995-4013</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Published_mission_info_carries_the_countdown_offset_and_playing_squads()
    {
        Require.Artwork();
        var (server, mission, owner) = Boot();
        using var _server = server;
        mission.SquadGame = true;
        mission.Sides.First(s => s.Id == 0).SquadId = "squad-alpha";
        mission.Sides.First(s => s.Id == 1).SquadId = "squad-bravo";

        var info = server.TestLobbyParams();
        Assert.True(info.SquadGame);
        Assert.Equal(["squad-alpha", "squad-bravo"], info.SquadIds!);   // both sides have ships

        // START_GAME: not-started, owner, all occupied sides ready → 15 s countdown.
        foreach (var side in mission.Sides) side.Ready = true;
        server.TestProcessDatagram(owner, FedPacket.Pack([new StartGame().Encode()]));
        Assert.Equal(Allegiance.Core.Sim.MissionStage.Starting, mission.Stage);

        info = server.TestLobbyParams();
        Assert.True(info.CountdownStarted);
        Assert.InRange(info.StartTimeOffsetMs, 14_000u, 15_000u);     // armed at +15 s
        Assert.True(info.InProgress, "STAGE_STARTING is in progress (fInProgress)");
    }

    /// <summary>⭐ SIM-0572 — every native <c>SetLobbyIsDirty</c> call site the port
    /// has bumps the version the heartbeat consumes: a host mission-param edit, the
    /// START_GAME stage change, a player entering the team lobby / switch, and a
    /// player leaving the mission.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Runtime_changes_mark_the_lobby_dirty()
    {
        Require.Artwork();
        var (server, mission, owner) = Boot();
        using var _server = server;
        long version = server.LobbyDirtyVersion;
        Assert.True(version >= 1, "mission construction starts dirty (fsmission.cpp:90)");

        // Host mission-params edit → native SetLobbyIsDirty at FedSrv.CPP:7579.
        var edit = new MissionParamsMsg
        {
            StrGameName = "P866 Lobby", NTems = 2, ILives = 20, IMinRank = -1, IMaxRank = 30,
            NMinPlayersPerTeam = 1, NMaxPlayersPerTeam = 2, NTotalMaxPlayersPerGame = unchecked((short)0x7FFF),
            IGoalConquestPercentage = 100, IGoalTerritoryPercentage = 100,
        };
        server.TestProcessDatagram(owner, FedPacket.Pack([edit.Encode()]));
        Assert.True(server.LobbyDirtyVersion > version, "a mission-param edit must mark the lobby dirty");
        version = server.LobbyDirtyVersion;

        // Stage change (START_GAME → STAGE_STARTING) → fsmission.cpp:1766.
        foreach (var side in mission.Sides) side.Ready = true;
        server.TestProcessDatagram(owner, FedPacket.Pack([new StartGame().Encode()]));
        Assert.True(server.LobbyDirtyVersion > version, "a stage change must mark the lobby dirty");
        version = server.LobbyDirtyVersion;

        // A player leaving the mission → fsmission.cpp:523.
        var conns = server.TestSnapshotPeers().Select(p => p.Conn).ToList();
        var leaving = conns.First(c => c != owner);
        server.TestProcessDatagram(leaving, FedPacket.Pack([new Logoff().Encode()]));
        Assert.True(server.LobbyDirtyVersion > version, "a player leaving must mark the lobby dirty");
        version = server.LobbyDirtyVersion;

        // A real side switch (auto-accept POSITIONREQ) → fsship.cpp:1134; and it is
        // also the SIM-0608 leg. The requester is the owner, moving from side 0 to
        // the leaderless side 1 (auto-accept). Other occupied sides must be ready.
        var req = new PositionReq { SideId = 1 };
        var ownerShip = server.TestSnapshotPeers().Single(p => p.Conn == owner).Ship;
        server.TestProcessDatagram(owner, FedPacket.Pack([req.Encode()]));
        Assert.True(server.LobbyDirtyVersion > version,
            "a side switch (team-lobby transition) must mark the lobby dirty");
        Assert.Equal((short)1, ownerShip.SideId);
    }
}

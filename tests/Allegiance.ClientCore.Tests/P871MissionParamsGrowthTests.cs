using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P871 / fedmsg::113 MISSIONPARAMS — LIVE proof of the native side growth
/// branch: a mission owner raising <c>nTeams</c> beyond the boot-time count makes
/// the server CREATE the extra sides (<c>FedSrv/fsmission.cpp:1846-1852</c> →
/// <c>Igc/missionigc.cpp:3749-3779</c>: <c>rgCivID</c>, the native side-name
/// array, the slot palette) and re-publish the roster, where the pre-P871 port
/// clamped the request.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P871MissionParamsGrowthTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Games = [new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 944u, DwCookie = 0x944u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 87100000000000002ul, DrmHash = "p871grow",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A valid four-team MissionParams (conquest on, territory off —
    /// native's Reset defaults, so <c>Invalid(true)</c> passes).</summary>
    private static MissionParamsMsg FourTeams() => new()
    {
        StrGameName = "Grow",
        SzIgcStaticFile = "PCore001.igc",
        NTems = 4,
        IGoalConquestPercentage = 100,
        IGoalTerritoryPercentage = 100,
        Bits = { [MissionParamsMsg.BitObjectModelCreated] = true, [MissionParamsMsg.BitScoresCount] = true },
    };

    private static ConnectionSession Seat(Srv server, string name)
    {
        var session = new ConnectionSession();
        session.Connect(server.BoundPort, name);
        session.SendLogonRequest(Request(name));
        int ack = PumpUntil(server, () => session.Ack is not null, 4000);
        Assert.True(ack < 4000, "timed out waiting for LOGONACK");
        Assert.True(session.Ack!.FValidated, $"logon refused: {session.Ack.CharNameOrFailureReason}");
        // P763's 30s auto-seat keeps the pre-flag behaviour; pump it through.
        int seated = PumpUntil(server, () => session.World.MyShipId is not null && session.World.MySideId is not null, 8000);
        Assert.True(seated < 8000, "timed out waiting for the auto-seat");
        return session;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void The_mission_owner_can_grow_the_side_list_beyond_the_boot_count()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config("Growth"));
        var mission = Assert.Single(server.Missions);
        Assert.Equal(2, mission.Sides.Count);

        using var owner = Seat(server, "GROWOWNER");

        owner.SendMissionParams(FourTeams());
        int grown = PumpUntil(server, () => mission.Sides.Count == 4, 4000);
        Assert.True(grown < 4000, "the server did not create the extra sides");
        // Native InitSide names/palette/civ per slot (fsmission.cpp:44-61).
        Assert.Equal("Team 3", mission.Sides[2].Name);
        Assert.Equal("Team 4", mission.Sides[3].Name);
        Assert.Equal((short)2, mission.Sides[2].Id);
        Assert.Equal((short)3, mission.Sides[3].Id);
        Assert.NotEqual(mission.Sides[0].Civilization.CivilizationId, mission.Sides[2].Civilization.CivilizationId);
        Assert.True(mission.Sides[2].Active && mission.Sides[3].Active);

        // The roster is re-published, so the client's team table grows too.
        int roster = PumpUntil(server, () => owner.World.SideRoster?.Sides.Length == 4, 4000);
        Assert.True(roster < 4000, "SIDE_ROSTER was not re-published after the growth");
        Assert.Contains(owner.World.SideRoster!.Sides, s => s.SideId == 3 && s.Name == "Team 4");

        // Shrinking back destroys the extra sides (the existing branch).
        var shrink = FourTeams();
        shrink.NTems = 2;
        owner.SendMissionParams(shrink);
        int shrunk = PumpUntil(server, () => mission.Sides.Take(2).All(s => s.Active)
            && mission.Sides.Skip(2).All(s => !s.Active), 4000);
        Assert.True(shrunk < 4000, "the shrink did not deactivate the extra sides");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_player_who_is_not_the_mission_owner_cannot_grow_the_side_list()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config("Growth Guard"));
        var mission = Assert.Single(server.Missions);
        using var first = Seat(server, "OWNERONE");
        using var second = Seat(server, "NOTOWNER");
        Assert.Equal(2, mission.Sides.Count);

        second.SendMissionParams(FourTeams());
        PumpUntil(server, () => false, 300);   // give the request time to be processed
        Assert.Equal(2, mission.Sides.Count);
    }
}

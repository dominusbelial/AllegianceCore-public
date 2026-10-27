using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed partial class P832aGoalServerTests
{
    private static ServerConfig Config(MissionGoals goals) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
        Games = [new() { Name = "P832a", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false, Goals = goals }]
    };

    private static void Pump(Srv server, Func<bool> done)
    {
        for (int i = 0; i < 5000 && !done(); i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "P832a loopback condition timed out");
    }

    [SkippableTheory]
    [InlineData("conquest")] [InlineData("territory")]
    public void Configured_station_goal_ends_a_local_match_and_survives_rotation(string goal)
    {
        Require.Artwork();
        var goals = goal == "conquest" ? new MissionGoals { ConquestPercentage = 73 }
            : new MissionGoals { TerritoryPercentage = 70 };
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        // Exercise source-generated JSON -> actual boot, not a post-boot flag.
        var config = Config(goals);
        server.Boot(JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!);
        var mission = server.Missions[0];
        Assert.Equal(goals, mission.Goals);
        foreach (var sector in mission.Sectors)
        {
            foreach (var station in sector.StationRuntimes.ToArray()) sector.RemoveStation(station);
            var type = mission.Core.StationTypes.Values.First() with
                { SabmCapabilities = DataStationTypeIGC.SabmFlag };
            mission.PlaceStation(new Station(new DataStationIGC([0, 0, -1000], [0, 1, 0], [1, 0, 0],
                [0, 0, 0, 1], sector.Id, 0, (short)(20000 + sector.Id), type.StationTypeId, 1, 1, "Goal base"), type), sector);
        }
        mission.Stage = MissionStage.Started;
        // Run the real recompute and its production host callback. Actual
        // combat-capture trigger coverage remains in ConquestTerritoryLiveTests.
        var result = mission.RecomputeConquestAndTerritory(null);
        Assert.Equal((short)0, result.Winner?.Id);
        mission.Site.VictoryByStationsComputed(mission, result.Changed, result.Winner);
        Assert.Equal(MissionStage.Over, mission.Stage);
        Assert.NotSame(mission, server.Missions[0]);
        Assert.Equal(goals, server.Missions[0].Goals);
        Assert.NotSame(goals, server.Missions[0].Goals);
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Configured_countdown_ends_by_clock_and_absent_goals_keep_master_behavior(bool timed)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(timed ? new() { GameLengthSeconds = 1 } : new()));
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        mission.CountdownStart = mission.LastUpdate;
        for (int i = 0; i < 25; i++) server.TickSim();
        Assert.Equal(timed ? MissionStage.Over : MissionStage.Started, mission.Stage);
        Assert.Equal(!timed, mission.Goals.IsNoWinningConditionsGame);
    }

    [SkippableFact]
    public void Configured_goals_roundtrip_over_tcp_to_briefing_and_investment_catalog()
    {
        Require.Artwork();
        var goals = new MissionGoals { ConquestPercentage = 73, TerritoryPercentage = 60, TeamKills = 21,
            Flags = 4, Artifacts = 7, TeamMoney = 2.5f, GameLengthSeconds = 600 };
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(goals));
        using var client = new ConnectionSession();
        client.Connect(server.BoundPort, "P832a-goals");
        client.SendLogonRequest(new() { CharacterName = "P832a-goals", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamID = 832001 });
        Pump(server, () => client.Ack is not null);
        client.SendPositionReq(0);
        Pump(server, () => { lock (client.Gate) return client.World.MissionBriefing is not null; });
        lock (client.Gate)
        {
            Assert.Equal(goals, client.World.MissionGoals);
            var briefing = client.World.MissionBriefing!;
            Assert.False(briefing.NoWin);
            Assert.Equal(73, briefing.ConquestPercent);
            Assert.Equal(21, briefing.KillLimit);
            Assert.Equal(4, briefing.FlagsGoal);
            Assert.Equal(7, briefing.ArtifactGoal);
            Assert.Equal(600, briefing.CountdownSeconds);
            var mission = server.Missions[0];
            int price = (int)(2.5f * mission.Core.Constants.FloatConstants[12]);
            Assert.Equal(price, briefing.ProsperityMoney);
            foreach (var side in mission.Sides)
            {
                Assert.Equal(price, side.Buckets.Single(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId).Price);
                Assert.Equal(price, BucketCatalog.Describe(mission.Core, side.InitialTechs,
                    GlobalAttributeSet.FromArray(side.Civilization.GasBaseAttributes), client.World.MissionGoals.TeamMoney)
                    .Single(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId).Price);
            }
            var lobby = LobbyGameParams.FromMission(mission);
            Assert.True(lobby.GoalConquest && lobby.GoalTerritory && lobby.GoalDeathmatch && lobby.GoalProsperity
                && lobby.GoalArtifacts && lobby.GoalFlags && lobby.GoalCountdown);
        }
    }

    [SkippableFact]
    public void Configured_flags_and_artifacts_populate_real_objectives_at_boot()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(new() { Flags = 2, Artifacts = 3 }));
        var mission = server.Missions[0];
        foreach (var sector in mission.Sectors)
        {
            foreach (var home in sector.StationRuntimes.Where(s => s.Type.HasCapability(DataStationTypeIGC.SabmStart)))
            {
                var pedestals = sector.StationRuntimes.Where(s => s.SideId == home.SideId && s.Type.HasCapability(DataStationTypeIGC.SabmPedestal)).ToArray();
                Assert.Equal(2, pedestals.Length);
                Assert.Contains(pedestals, p => p.Center == home.Center + new Vector3(0, 0, 2000));
                Assert.Contains(pedestals, p => p.Center == home.Center - new Vector3(0, 0, 2000));
                foreach (var pedestal in pedestals)
                    Assert.Contains(sector.Models.OfType<Treasure>(), t => t.Code == TreasureCode.Flag
                        && t.Data.TreasureId == home.SideId && Math.Abs(t.Position.Z - pedestal.Center.Z) == 50 + pedestal.Type.Radius);
            }
            if (!sector.Cluster.HomeSector)
                Assert.Single(sector.Models.OfType<Treasure>(), t => t.Data.TreasureId == Side.ArtifactSentinelFlag);
        }
    }

    [SkippableFact]
    public void Joining_another_mission_receives_its_own_configured_goals()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config(new() { ConquestPercentage = 73 });
        var other = new MissionGoals { TeamMoney = 2.5f, TeamKills = 21 };
        config.Games.Add(new() { Name = "P832a second world", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Charlie", "Delta"], AutoStart = false, Goals = other });
        // Existing multi-mission test seam selects world B for this peer.
        server.ShipSpawner = (core, _, id) =>
        {
            var mission = server.Missions[1];
            var sector = mission.Sectors[0];
            var ship = new Ship(id, core.HullTypes.Values.First(), new Vector3(1000, 0, 0),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(config);
        using var client = new ConnectionSession();
        client.Connect(server.BoundPort, "P832a-world-B");
        client.SendLogonRequest(new() { CharacterName = "P832a-world-B", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamID = 832002 });
        Pump(server, () => client.Ack is not null);
        client.SendPositionReq(0);
        Pump(server, () => { lock (client.Gate) return client.World.MissionBriefing is not null; });
        lock (client.Gate)
        {
            Assert.Equal(other, client.World.MissionGoals);
            Assert.Equal("Charlie", client.World.MissionBriefing!.SideName);
            Assert.False(client.World.MissionBriefing.Conquest);
            Assert.Equal(21, client.World.MissionBriefing.KillLimit);
            Assert.Equal((int)(2.5f * server.Missions[1].Core.Constants.FloatConstants[12]),
                client.World.MissionBriefing.ProsperityMoney);
        }
    }
}

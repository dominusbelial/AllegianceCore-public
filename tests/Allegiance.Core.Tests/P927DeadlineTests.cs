using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P927DeadlineTests
{
    [SkippableFact]
    public void Three_side_deadline_uses_all_sides_station_kills_then_native_kills_deaths_and_draw()
    {
        Require.Artwork();
        var config = new ServerConfig
        {
            Port = 0, ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc",
            Games = [new() { Name = "P927 deadline", Teams = 3, CivIds = [18, 4, 5],
                SideNames = ["Gigacorp", "Phoenix", "Omicron"], AutoStart = false, SkirmishPreset = true,
                MapGen = new() { Type = "DiamondRing", Seed = 927 } }]
        };
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        var mission = server.Missions[0];
        var sector = mission.Sectors[0];
        var type = mission.Core.StationTypes.Values.First();
        var station = new Station(new DataStationIGC([0, 0, 0], [0, 1, 0], [1, 0, 0],
            [0, 0, 0, 1], sector.Id, 1, 30001, type.StationTypeId, 255, 255, "Deadline fixture"), type);
        var ship = new Ship(30002, mission.Core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 2 };
        mission.RecordStationDestruction(station, ship);
        mission.Stage = MissionStage.Started;
        mission.Sides[0].Kills = 20; mission.Sides[1].Kills = 30; mission.Sides[2].Kills = 1;
        var result = mission.EvaluateCountdown(0, deadlineReached: true, stationKillsCriterion: true);
        Assert.Equal((short)2, result.Winner!.Id); Assert.Equal("station-kills", result.Criterion);
        Assert.Equal("0:stations=0/kills=20/deaths=0,1:stations=0/kills=30/deaths=0,2:stations=1/kills=1/deaths=0", mission.CountdownValues(result.Criterion));
        ship.SideId = 0; mission.RecordStationDestruction(station, ship);
        result = mission.EvaluateCountdown(0, deadlineReached: true, stationKillsCriterion: true);
        Assert.Equal((short)1, result.Winner!.Id); Assert.Equal("kills", result.Criterion);
        foreach (var side in mission.Sides) { side.Kills = 30; side.Deaths = 1; }
        mission.Sides[2].Deaths = 0;
        Assert.Equal((short)2, mission.EvaluateCountdown(0, true, true).Winner!.Id);
        mission.Sides[2].Deaths = 1;
        Assert.Null(mission.EvaluateCountdown(0, true, true).Winner);
    }
}

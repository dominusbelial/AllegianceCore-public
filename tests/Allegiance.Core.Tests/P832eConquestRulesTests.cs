using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed partial class ConquestTerritoryTests
{
    [SkippableTheory]
    [InlineData(false, true)] [InlineData(true, false)]
    public void P832e_Starting_recompute_requires_allow_empty_teams(bool allow, bool unchanged)
    {
        Require.Artwork(); var mission = Assembly();
        mission.Stage = MissionStage.Starting; mission.AllowEmptyTeams = allow;
        mission.Goals.ConquestPercentage = 40;
        var result = mission.RecomputeConquestAndTerritory(null);
        Assert.Equal(!unchanged, result.Changed);
        Assert.Equal(unchanged, result.Winner is null);
    }

    [SkippableFact]
    public void P832e_One_active_team_suppresses_conquest_win_but_broadcasts_percent_changes()
    {
        Require.Artwork(); var mission = Assembly();
        mission.AllowEmptyTeams = true; mission.Sides[1].Active = false;
        mission.Goals.ConquestPercentage = 40;
        var result = mission.RecomputeConquestAndTerritory(null);
        Assert.True(result.Changed); Assert.Null(result.Winner);
        Assert.All(mission.Sides, side => Assert.Equal(50, side.ConquestPercent));
    }

    [SkippableFact]
    public void P832e_Station_events_forward_native_capture_kill_build_and_unattributed_reasons()
    {
        Require.Artwork(); var mission = Assembly(); var winner = mission.Sides[0];
        var station = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == mission.Sides[1].Id);
        var ship = new Ship(777, mission.Core.HullTypes.Values.First(), default, default,
            Orientation.Identity) { SideId = winner.Id };
        string name = station.Data.Name;
        Assert.Equal($"{winner.Name} won because Pilot captured {name}",
            mission.StationVictoryReason(winner, station, ship, "Pilot", captured: true));
        Assert.Equal($"{winner.Name} has destroyed {mission.Sides[1].Name}'s {name} in {mission.Sectors.Single(s => s.Id == station.ClusterId).Name}.",
            mission.StationVictoryReason(winner, station, ship));
        Assert.Equal($"{winner.Name} won by out-building their opponents", mission.StationVictoryReason(winner, station, ship, built: true));
        Assert.Equal($"{winner.Name} won by dominating their opponents", mission.StationVictoryReason(winner, station, null));
        mission.Sides[0].AllyGroup = mission.Sides[1].AllyGroup = 0;
        Assert.Equal($"{winner.Name} & {mission.Sides[1].Name} won by dominating their opponents", mission.StationVictoryReason(winner, station, null));
        mission.Sides[0].AllyGroup = mission.Sides[1].AllyGroup = null;
        mission.Goals.ConquestPercentage = 65;
        var added = AddStation(mission, mission.Sectors[0].Id, winner.Id, 7777, DataStationTypeIGC.SabmFlag);
        mission.NotifyStationVictory(added, ship, built: true);
        Assert.Contains(((RecordingIgcSite)mission.Site).Calls, c => c.StartsWith("VictoryByStationsComputed(") && c.EndsWith("StationVictory)"));
    }
}

public sealed partial class P832aGoalServerTests
{
    [SkippableFact]
    public void P832e_Join_sync_reports_native_rankings_before_any_station_event()
    {
        Require.Artwork();
        using var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(new() { ConquestPercentage = 75 }));
        var mission = server.Missions[0];
        mission.Sides[0].ConquestPercent = 66; mission.Sides[1].ConquestPercent = 33;
        mission.Sides[0].Flags = 2; mission.Sides[1].Artifacts = 3;
        using var client = new Allegiance.ClientCore.Session.ConnectionSession();
        client.Connect(server.BoundPort, "P832e-rankings");
        client.SendLogonRequest(new() { CharacterName = "P832e-rankings", FedsrvVer = Allegiance.Core.Wire.LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3], CDKey = "ABCDEFGHIJKL", Time = 1, DwCookie = 832, CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamID = 832005 });
        Pump(server, () => client.Ack is not null);
        client.SendPositionReq(0);
        Pump(server, () => { lock (client.Gate) return client.World.SideGameStates.Count == 6; });
        lock (client.Gate)
        {
            Assert.Equal((short)75, client.World.MissionGoals.ConquestPercentage);
            Assert.Equal((byte)66, client.World.SideGameStates[0].Conquest);
            Assert.Equal((byte)33, client.World.SideGameStates[1].Conquest);
            Assert.Equal((byte)2, client.World.SideGameStates[0].Flags);
            Assert.Equal((byte)3, client.World.SideGameStates[1].Artifacts);
        }
        Assert.Equal(MissionStage.NotStarted, mission.Stage);
    }
}

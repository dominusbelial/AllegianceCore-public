using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P745BLiveTests
{
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Deadline_ignores_armed_side_and_delivers_native_winner_or_draw_then_rotates(bool draw)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        var config = P733ResearchTests.Config(); config.Showcase.FinaleTimeoutSeconds = 1;
        config.LiveSettings.AutoNextMatch.Enabled = false;
        server.Boot(config);
        using var a = P660NextMatchLiveTests.Connect(server, "P745B-A", 745001);
        using var b = P660NextMatchLiveTests.Connect(server, "P745B-B", 745002);
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        mission.Goals.ConquestPercentage = 0; mission.Goals.TerritoryPercentage = 100; mission.Goals.TeamMoney = 0; mission.Goals.Artifacts = 0; mission.Goals.Flags = 0;
        short armed = a.World.MySideId!.Value;
        short expected = draw ? GameOver.NA : (short)(1 - armed);
        mission.Sides.Single(s => s.Id != armed).Kills = (short)(draw ? 0 : 9);
        a.SendShowcaseFinale(Guid.ParseExact(mission.ResearchEpoch, "N"), armed);
        P660NextMatchLiveTests.Pump(server, () => !ReferenceEquals(mission, server.Missions[0]));
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(a, () => a.World.GameOverResult is not null)
            && P660NextMatchLiveTests.Read(b, () => b.World.GameOverResult is not null));
        Assert.Equal(expected, a.World.GameOverResult!.SideWinner);
        Assert.Equal(expected, b.World.GameOverResult!.SideWinner);
        Assert.Equal(MissionStage.Over, mission.Stage);
        Assert.Contains(draw ? "draw" : "by kills", a.World.GameOverResult.GameoverMessage);
        var fresh = server.Missions[0]; for (int i = 0; i < 10; i++) server.TickSim();
        Assert.Same(fresh, server.Missions[0]); Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableFact]
    public void Human_join_receives_objective_at_two_to_one_restart_bases_and_kill_clears_it()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        var config = P733ResearchTests.Config(); config.LiveSettings.AutoNextMatch.Enabled = false;
        server.Boot(config);
        using var pilot = P660NextMatchLiveTests.Connect(server, "P745B-objective", 745003);
        var m = server.Missions[0]; m.Stage = MissionStage.Started;
        short enemy = (short)(1 - pilot.World.MySideId!.Value);
        var station = m.Sectors.SelectMany(s => s.StationRuntimes).Single(st => st.SideId == enemy
            && st.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        var sector = m.Sectors.Single(s => s.Id == station.Data.ClusterId);
        var second = new Station(station.Data with { StationId = 30000, Name = "Second restart" }, station.Type);
        m.PlaceStation(second, sector);
        // MissionStage must travel on the real wire too (the fixture starts the mission explicitly).
        typeof(Srv).GetMethod("BroadcastMissionStageToMission", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, [m, MissionStage.Started]);
        for (int i = 0; i < 20; i++) { server.TickSim(); Thread.Sleep(2); }
        Assert.DoesNotContain(pilot.World.LastBaseObjectives(), o => o.Target.SideId == enemy);
        sector.RemoveStation(second);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(pilot,
            () => pilot.World.LastBaseObjectives().Any(o => o.Target.StationId == station.StationId)));
        Assert.Contains("destroy it to win", pilot.World.LastBaseObjectives().First(o => o.Target.SideId == enemy).Text);
        sector.RemoveStation(station);
        typeof(Srv).GetMethod("OnSideLostRestartStation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server, [m, enemy]);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(pilot, () => pilot.World.GameOverResult is not null));
        Assert.Empty(pilot.World.LastBaseObjectives()); Assert.Equal(0, server.SimPassStageFailures);
    }
}

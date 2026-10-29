using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P873TrainingJoinTests
{
    [Theory]
    [InlineData(3, 3049, "tm_3_47")]
    [InlineData(4, 3049, "tm_3_49r")]
    [InlineData(2, 2000, "vo_sal_commencescan")]
    [InlineData(8, 8000, "vo_sal_commencescan")]
    [InlineData(10, 6002, "tm_6_02")]
    public void Narration_resolves_mission_local_ids_without_alias_collisions(int mission, int sound, string expected)
        => Assert.Equal(expected, TrainingSoundCatalog.Stem(mission, sound));

    [Fact]
    public void Missing_audio_does_not_leave_the_first_sound_goal_playing_forever()
    {
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new ClientTrainingFacilities { World = () => connection.World, Session = connection });
        var play = new PlaySoundAction(site, 2000);
        var done = new SoundFinishedCondition(play);
        Assert.False(done.Start());
        play.Execute();
        Assert.False(done.Evaluate()); // native started latch
        Assert.True(done.Evaluate());
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    public void Logon_replicates_the_host_trainee_in_space_before_the_real_update_starts_script(int id)
    {
        Require.Artwork();
        var variant = id switch
        {
            2 => TrainingHost.BasicFlight, 3 => TrainingHost.BasicWeaponry,
            4 => TrainingHost.EnemyEngagement, 5 => TrainingHost.CommandView,
            6 => TrainingHost.PracticeArena, 8 => TrainingHost.Nanite,
            _ => TrainingHost.FreeFlight,
        };
        using var host = TrainingHost.Start(variant);
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new ClientTrainingFacilities
        {
            World = () => connection.World, Session = connection, Host = host,
        });
        TrainingMission mission = id switch
        {
            2 => new Mission2(site), 3 => new Mission3(site), 4 => new Mission4(site),
            5 => new Mission5(site), 6 => new Mission6(site), 8 => new Mission8(site),
            _ => new Mission10(site),
        };
        var session = new TrainingSession(site, mission);
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        void Set(string name, object value) => typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
        void Update() => typeof(Game1).GetMethod("UpdateTraining", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [0.001]);
        Set("_trainingSession", session);
        Set("_trainingStartDeadline", 20.0);
        Set("_trainingMissionId", id);
        Update();
        Assert.False(session.Started);
        Assert.Equal(0, host.PlayerShipId); // headless API remains usable before login
        host.StartBackgroundTicker();
        connection.Connect(host.Port, "p873-trainee");
        connection.SendLogonRequest(new LogonRequest
        {
            CharacterName = "p873-trainee", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
            FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
            SteamAuthTicket = new byte[16], DrmHash = "p873",
        });
        var watch = Stopwatch.StartNew();
        while (connection.Ack is null && watch.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(10);
        Assert.True(connection.Ack?.FValidated, "training logon did not validate");
        Assert.False(session.Started);
        connection.SendPositionReq(0);
        while (!site.HasPlayerShip && watch.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(10);
        Assert.True(site.HasPlayerShip, "trainee pose never replicated in space");
        Assert.Equal(connection.Ack!.ShipID, host.PlayerShipId);
        Assert.Equal((short)host.PlayerShipId, connection.World.MyShipId!.Value);
        Assert.Null(host.PlayerShip.StationId);
        Assert.Equal(variant.PlayerHull, host.PlayerShip.HullType.HullId);
        var replicated = connection.World.Ships[connection.World.MyShipId!.Value];
        Assert.Equal(-1, replicated.StationId);
        Assert.Equal(variant.StartSector, replicated.SectorId);
        Assert.DoesNotContain(host.Mission.Sectors.SelectMany(s => s.Models), m => m is Allegiance.Core.Sim.Ship { Id: 0 });
        Assert.Equal(host.StartSectorId, host.Stations[host.HomeStationId].SectorId);
        Assert.Equal(0, host.PlayerShip.Position.Length(), 3);
        Assert.Equal(1, host.PlayerShip.Orientation.GetForward().X, 3);
        Update(); // actual Game1 pump, the order which used to crash or start docked
        Assert.True(session.Started);
        Assert.NotEqual("complete", mission.ActiveGoalPath);
        Assert.False(session.Finished);
        session.Terminate();
    }
}

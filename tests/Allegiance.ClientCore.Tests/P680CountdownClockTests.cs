using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P680CountdownClockTests
{
    private static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server,
            FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));

    [Fact]
    public void Encoded_definition_and_start_update_drive_both_native_rounding_branches()
    {
        using var session = new ConnectionSession();
        var world = session.World;
        Feed(session, new MissionDef { Stage = MissionStageMessage.StageStarting,
            GameLengthSeconds = 20, TimeStart = 100_000 });
        world.ObserveServerTime(90_000, 10_000);
        Assert.Equal(11, world.MissionCountdownSoundSeconds(10_000));
        Assert.Equal(10, world.MissionCountdownSoundSeconds(10_001));
        Assert.Equal(1, world.MissionCountdownSoundSeconds(20_000));
        Assert.Equal(1, world.MissionCountdownSoundSeconds(20_999));
        Assert.Equal(0, world.MissionCountdownSoundSeconds(21_000));

        Feed(session, new SetStartTime { TimeStart = 110_000 });
        Feed(session, new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        Assert.Equal(1, world.CountdownVersion);
        Feed(session, new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.Equal(20, world.Mission!.GameLengthSeconds);
        Assert.Null(world.CountdownRemainingSeconds(40_000)); // briefing HUD still ends
        Assert.Equal(11, world.MissionCountdownSoundSeconds(40_000));
        Assert.Equal(10, world.MissionCountdownSoundSeconds(40_001));
        Assert.Equal(1, world.MissionCountdownSoundSeconds(50_000));
        Assert.Equal(0, world.MissionCountdownSoundSeconds(50_001));
        Assert.Empty(session.UnknownFmids);
    }

    [Fact]
    public void Late_join_uses_definition_epoch_and_corrects_server_samples_with_dilation()
    {
        using var session = new ConnectionSession();
        var world = session.World;
        Feed(session, new MissionDef { Stage = MissionStageMessage.StageStarted,
            GameLengthSeconds = 600, TimeStart = 100_000 });
        world.TimeScale = .25;
        world.ObserveServerTime(689_001, 1_000);
        Assert.Equal(11, world.MissionCountdownSoundSeconds(1_000));
        Assert.Equal(10, world.MissionCountdownSoundSeconds(5_000));
        // A new clock sample, and then a live scale change, use the same epoch.
        world.TimeScale = .5;
        world.ObserveServerTime(691_001, 5_000);
        Assert.Equal(9, world.MissionCountdownSoundSeconds(5_000));
        Assert.Equal(8, world.MissionCountdownSoundSeconds(7_000));
        Assert.Null(world.CountdownStartLocalMs); // late join never needed SET_START_TIME
    }

    [Theory]
    [InlineData(MissionStageMessage.StageNotStarted, 20)]
    [InlineData(MissionStageMessage.StageOver, 20)]
    [InlineData(MissionStageMessage.StageStarted, 0)]
    public void Inactive_stages_and_untimed_games_are_silent(int stage, float duration)
    {
        var world = new ClientWorld();
        world.Apply(new MissionDef { Stage = stage, TimeStart = 100_000, GameLengthSeconds = duration });
        world.ObserveServerTime(110_001, 0);
        Assert.Null(world.MissionCountdownSoundSeconds(0));
    }

    [Fact]
    public void Reset_and_next_match_discard_old_epoch_and_new_definition_replaces_duration()
    {
        var world = new ClientWorld();
        world.Apply(new MissionDef { Stage = MissionStageMessage.StageStarted,
            TimeStart = 100_000, GameLengthSeconds = 20 });
        world.ObserveServerTime(110_001, 0);
        Assert.Equal(10, world.MissionCountdownSoundSeconds(0));
        world.BeginNextMission();
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        Assert.Null(world.MissionCountdownSoundSeconds(0));
        world.Apply(new SetStartTime { TimeStart = 120_000 });
        Assert.Equal(10, world.MissionCountdownSoundSeconds(0));
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        // Mission configuration persists until the new MISSIONDEF arrives.
        world.Apply(new MissionDef { Stage = MissionStageMessage.StageStarted, TimeStart = 120_000 });
        Assert.Null(world.MissionCountdownSoundSeconds(0));
        world.Reset();
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        Assert.Null(world.MissionCountdownSoundSeconds(0));
    }
}

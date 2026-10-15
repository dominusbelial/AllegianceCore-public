using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P660CountdownTests
{
    [Fact]
    public void Native_restart_deadline_is_replaced_by_briefing_and_tracks_dilation()
    {
        var world = new ClientWorld();
        world.Apply(new GameOver { SideWinner = 0 });
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageOver });
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageNotStarted });
        world.TimeScale = .25;
        long local = Environment.TickCount64;
        world.ObserveServerTime(2_636_000, local);
        world.Apply(new SetStartTime { TimeStart = 2_666_000 });
        Assert.True(world.IsIntermission);
        Assert.NotNull(world.GameOverResult);
        Assert.Equal(25, world.CountdownRemainingSeconds(local + 20_000));
        // A new server sample corrects the estimate without changing epochs.
        world.ObserveServerTime(2_666_000, local + 120_000);
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        world.Apply(new SetStartTime { TimeStart = 2_681_000 });
        Assert.False(world.IsIntermission);
        Assert.Null(world.GameOverResult);
        Assert.Equal(15, world.CountdownRemainingSeconds(local + 120_000));
        Assert.Equal(5, world.CountdownRemainingSeconds(local + 160_000));
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.Null(world.CountdownRemainingSeconds());
    }

    [Fact]
    public void Side_elimination_preserves_result_until_a_real_next_match_stage()
    {
        var world = new ClientWorld();
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        world.Apply(new GameOver { SideWinner = -1 });
        Assert.NotNull(world.GameOverResult);
        // A duplicate Started snapshot must not dismiss side-only elimination.
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.NotNull(world.GameOverResult);
        Assert.False(world.IsIntermission);
    }

    [Fact]
    public void Game_over_roster_accumulates_native_chunks_and_resets_for_the_next_result()
    {
        var world = new ClientWorld();
        var entries = Enumerable.Range(0, 51)
            .Select(i => new GameOverPlayerEntry($"player-{i}", (short)(i % 2), 0, i)).ToArray();
        world.Apply(new GameOver { SideWinner = 0 });
        world.Apply(new GameOverPlayers { Entries = entries[1..] });
        world.Apply(new GameOverPlayers { Entries = entries[..1] });
        Assert.Equal(entries.OrderBy(e => e.CharacterName), world.GameOverPlayersList.OrderBy(e => e.CharacterName));
        world.BeginNextMission();
        Assert.Equal(51, world.GameOverPlayersList.Count); // retain the end screen during intermission
        world.Apply(new GameOver { SideWinner = 1 });
        Assert.Empty(world.GameOverPlayersList);
        world.Apply(new GameOverPlayers { Entries = entries[..1] });
        Assert.Single(world.GameOverPlayersList);
        world.Reset();
        Assert.Empty(world.GameOverPlayersList);
    }
}

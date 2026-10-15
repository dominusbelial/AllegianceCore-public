using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P680CountdownSoundTests
{
    [Theory]
    [InlineData(10)] // SIM-0304, WinTrek.cpp:11173
    [InlineData(9)]  // SIM-0305, WinTrek.cpp:11177
    [InlineData(8)]  // SIM-0306, WinTrek.cpp:11181
    [InlineData(7)]  // SIM-0307, WinTrek.cpp:11185
    [InlineData(6)]  // SIM-0308, WinTrek.cpp:11189
    [InlineData(5)]  // SIM-0309, WinTrek.cpp:11193
    [InlineData(4)]  // SIM-0310, WinTrek.cpp:11197
    [InlineData(3)]  // SIM-0311, WinTrek.cpp:11201
    [InlineData(2)]  // SIM-0312, WinTrek.cpp:11205
    [InlineData(1)]  // SIM-0313, WinTrek.cpp:11209
    public void Each_native_wave_is_emitted_once_for_briefing_and_timed_game(int second)
    {
        // Exercise the real receive dispatcher and the planner Game1 consumes.
        using var session = new ConnectionSession();
        var world = session.World;
        var sound = new MissionCountdownSound();
        void Feed(IFedMessage msg) => ((ITransportSite)session).OnReceive(ConnectionId.Server,
            FedPacket.Pack([msg.Encode()]));
        Feed(new MissionDef { Stage = MissionStageMessage.StageStarting,
            TimeStart = 100_000, GameLengthSeconds = 60 });
        world.ObserveServerTime((uint)(100_001 - second * 1000), 0);
        Assert.Equal(new MissionCountdownCue(true, second), sound.Update(world, 0));
        Assert.Equal(default, sound.Update(world, 100));
        Feed(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.Null(sound.Update(world, 100).Second); // now the game has ~60s left
        world.ObserveServerTime((uint)(160_001 - second * 1000), 1_000);
        Assert.Equal(new MissionCountdownCue(false, second), sound.Update(world, 1_000));
        Assert.Equal(default, sound.Update(world, 1_100));
        Assert.Equal($"countdown{second}", CountdownSound.TickAssetFor(second));
    }

    [Fact]
    public void Slow_frames_skip_missed_cues_and_clock_corrections_only_emit_changed_values()
    {
        var world = new ClientWorld();
        world.Apply(new MissionDef { Stage = MissionStageMessage.StageStarted,
            TimeStart = 100_000, GameLengthSeconds = 60 });
        world.ObserveServerTime(149_001, 0);
        var sound = new MissionCountdownSound();
        Assert.Equal(default, sound.Update(world, 0)); // 11 seconds
        Assert.Equal(10, sound.Update(world, 1_000).Second);
        Assert.Equal(7, sound.Update(world, 4_000).Second); // no catch-up 9,8
        world.ObserveServerTime(154_001, 4_000);
        Assert.Equal(6, sound.Update(world, 4_000).Second);
        Assert.Equal(default, sound.Update(world, 4_001));
        Assert.Equal(default, sound.Update(world, 20_000)); // expired
    }

    [Fact]
    public void New_briefing_and_next_match_reset_cues_and_inactive_frames_stay_silent()
    {
        var world = new ClientWorld();
        var sound = new MissionCountdownSound();
        world.Apply(new MissionDef { Stage = MissionStageMessage.StageNotStarted, TimeStart = 100_000 });
        world.ObserveServerTime(90_001, 0);
        Assert.Equal(default, sound.Update(world, 0));
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        Assert.Equal(new MissionCountdownCue(true, 10), sound.Update(world, 0));
        Assert.Equal(default, sound.Update(world, 1));
        world.Apply(new SetStartTime { TimeStart = 100_000 });
        Assert.Equal(default, sound.Update(world, 0)); // epoch update is not OnMissionCountdown
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        Assert.Equal(new MissionCountdownCue(true, 10), sound.Update(world, 0));
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.Equal(default, sound.Update(world, 0)); // untimed
        world.BeginNextMission();
        world.Apply(new MissionDef { Stage = MissionStageMessage.StageStarting, TimeStart = 100_000 });
        Assert.Equal(new MissionCountdownCue(true, 10), sound.Update(world, 0));
    }

    [Fact]
    public void Every_countdown_wave_is_bundled_and_built_for_the_real_sound_player()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));
        string definitions = File.ReadAllText(Path.Combine(content, "sounddef.mdl"));
        for (int second = 1; second <= 10; second++)
        {
            string wave = CountdownSound.TickAssetFor(second)!;
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", wave + ".ogg")).Length > 0);
            Assert.Contains($"/build:Sounds/{wave}.ogg", manifest);
            Assert.Contains($"ImportWave(\"{wave}\")", definitions);
        }
    }
}

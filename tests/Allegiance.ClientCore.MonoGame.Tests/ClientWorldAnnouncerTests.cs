using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Tests for the two P216 client-world message handlers that back the mission
/// announcer: <c>GAME_OVER(62)</c> drives the existing
/// <see cref="MissionInfo.Stage"/> field to <see cref="MissionStageMessage.StageOver"/>
/// (the real "mission has ended" signal the server already broadcasts), and
/// <c>SET_START_TIME(14)</c> records the local countdown anchor that
/// <c>Game1</c> uses to time the per-second ticks.
/// </summary>
public class ClientWorldAnnouncerTests
{
    [Fact]
    public void GameOver_DrivesExistingMissionStageToOver()
    {
        var world = new ClientWorld();
        // The join burst pins the stage to Started (Server.cs:2138).
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });

        world.Apply(new GameOver { SideWinner = 0, GameoverMessage = "won" });

        Assert.NotNull(world.Mission);
        Assert.Equal(MissionStageMessage.StageOver, world.Mission!.Stage);
        // It is the SAME field the auto-undock already reads — no new "ended" state.
        Assert.Equal(MissionStage.Over, (MissionStage)world.Mission.Stage);
    }

    [Fact]
    public void GameOver_PreservesTheKnownStaticCoreFields()
    {
        var world = new ClientWorld();
        world.Apply(new MissionDef
        {
            SzIGCStaticFile = "PCore001",
            VerIgcCore = 808,
            DwCookie = 777,
            ISideMissionOwner = 3,
            Stage = MissionStageMessage.StageStarted,
        });

        world.Apply(new GameOver());

        Assert.Equal("PCore001", world.Mission!.StaticCoreFile);
        Assert.Equal(808, world.Mission.StaticCoreVersion);
        Assert.Equal(777u, world.Mission.Cookie);
        Assert.Equal((short)3, world.Mission.SideMissionOwner);
        Assert.Equal(MissionStageMessage.StageOver, world.Mission.Stage);
    }

    [Fact]
    public void GameOver_WithoutPriorMission_StillMintsAnOverSnapshot()
    {
        var world = new ClientWorld();
        world.Apply(new GameOver());
        // Mirrors Apply(MissionStageMessage)'s mint-if-absent: the mission is
        // created with stage Over and default core fields rather than throwing.
        Assert.NotNull(world.Mission);
        Assert.Equal(MissionStageMessage.StageOver, world.Mission!.Stage);
    }

    [Fact]
    public void SetStartTime_RecordsTheCountdownAnchorOnce()
    {
        var world = new ClientWorld();
        Assert.Null(world.CountdownStartLocalMs);

        world.Apply(new SetStartTime { TimeStart = 900000 });
        Assert.NotNull(world.CountdownStartLocalMs);

        // The countdown is one-shot per mission — a second SET_START_TIME must
        // not re-anchor (keeps Game1's per-second ticks continuous).
        long first = world.CountdownStartLocalMs!.Value;
        world.Apply(new SetStartTime { TimeStart = 950000 });
        Assert.Equal(first, world.CountdownStartLocalMs);
    }
}

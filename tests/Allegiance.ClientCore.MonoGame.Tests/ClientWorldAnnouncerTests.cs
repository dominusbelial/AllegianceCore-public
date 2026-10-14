using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Tests for the two P216 client-world message handlers that back the mission
/// announcer: <c>GAME_OVER(62)</c> retains the end-of-mission snapshot for the
/// result panel (P262/P481 — it no longer flips
/// <see cref="MissionInfo.Stage"/>; the real "mission ended" signal is the
/// server's <c>MISSION_STAGE Over</c> broadcast, which a full game-over sends
/// before the P439 rotation while the P455b side-only game-over does not), and
/// <c>SET_START_TIME(14)</c> records the local countdown anchor that
/// <c>Game1</c> uses to time the per-second ticks.
/// </summary>
public class ClientWorldAnnouncerTests
{
    [Fact]
    public void GameOver_RetainsSnapshot_AndStageStaysUntil_MISSION_STAGE_Over()
    {
        var world = new ClientWorld();
        // The join burst pins the stage to Started (Server.cs:2138).
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });

        world.Apply(new GameOver { SideWinner = 0, GameoverMessage = "won" });

        Assert.NotNull(world.Mission);
        // P481 — GAME_OVER no longer flips the stage: the P455b side-only
        // game-over keeps the mission running for the other sides, so the Back
        // button must not see Over here.
        Assert.Equal(MissionStageMessage.StageStarted, world.Mission!.Stage);
        Assert.NotNull(world.GameOverResult);

        // A real full game-over's MISSION_STAGE Over broadcast is what marks the
        // mission ended (native SetStage broadcasts MISSION_STAGE, fsmission.cpp:1750-1768).
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageOver });
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
        // P481 — the stage stays whatever MISSIONDEF/MISSION_STAGE last set
        // (Started here); GAME_OVER does not touch it.
        Assert.Equal(MissionStageMessage.StageStarted, world.Mission.Stage);
    }

    [Fact]
    public void GameOver_WithoutPriorMission_LeavesMissionAbsent()
    {
        var world = new ClientWorld();
        world.Apply(new GameOver());
        // P481 — GAME_OVER only retains the result snapshot; it no longer
        // synthesizes a MissionInfo. The stage comes solely from MISSION_STAGE /
        // MISSIONDEF, so with neither received Mission stays null (the Back
        // planner treats a null stage as "not Over" → team lobby).
        Assert.NotNull(world.GameOverResult);
        Assert.Null(world.Mission);
    }

    [Fact]
    public void SetStartTime_ReplacesTheRestartDeadlineWithTheBriefingDeadline()
    {
        var world = new ClientWorld();
        Assert.Null(world.CountdownStartLocalMs);

        world.Apply(new SetStartTime { TimeStart = 900000 });
        Assert.NotNull(world.CountdownStartLocalMs);

        // Native can reset SET_START_TIME between intermission and briefing.
        long now = Environment.TickCount64;
        world.ObserveServerTime(940000, now);
        world.Apply(new SetStartTime { TimeStart = 955000 });
        Assert.Equal(15, world.CountdownRemainingSeconds(now));

    }
}

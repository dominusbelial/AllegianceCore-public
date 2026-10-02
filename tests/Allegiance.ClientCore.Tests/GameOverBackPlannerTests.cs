using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P481 — pure, headless Back-branching for the game-over screen, mirroring
/// native <c>GameOverScreen::OnButtonBack</c> (gameoverscreen.cpp:609-625): a
/// <c>STAGE_OVER</c> mission leaves; any other stage (the two port cases —
/// the rotated next game at NotStarted, P480, and the side-only elimination
/// whose mission is still Started, P455b) goes to the team lobby, split into
/// ready-check (surviving side still active) vs side-select (eliminated side)
/// by the local side's active state.
/// </summary>
public class GameOverBackPlannerTests
{
    [Fact]
    public void Stage_Over_leaves_the_mission_regardless_of_side_activity()
    {
        Assert.Equal(GameOverBackAction.LeaveMission,
            GameOverBackPlanner.Plan(MissionStageMessage.StageOver, ownSideActive: true));
        Assert.Equal(GameOverBackAction.LeaveMission,
            GameOverBackPlanner.Plan(MissionStageMessage.StageOver, ownSideActive: false));
    }

    [Fact]
    public void Rotated_next_game_NotStarted_returns_to_ready_check()
    {
        // After a full game-over + P480 rotation the stage is NotStarted and the
        // player is re-seated on its (still active) side → the ready-check lobby.
        Assert.Equal(GameOverBackAction.TeamLobbyReadyCheck,
            GameOverBackPlanner.Plan(MissionStageMessage.StageNotStarted, ownSideActive: true));
    }

    [Fact]
    public void Eliminated_side_Started_returns_to_side_select()
    {
        // P455b side-only game-over: the mission is still Started for the other
        // sides and the eliminated player's own side is inactive → the side-select
        // picker to request a still-active side.
        Assert.Equal(GameOverBackAction.TeamLobbySideSelect,
            GameOverBackPlanner.Plan(MissionStageMessage.StageStarted, ownSideActive: false));
    }

    [Fact]
    public void Surviving_active_side_on_a_started_mission_returns_to_ready_check()
    {
        // Belt-and-suspenders: a not-Over stage with an active own side is the
        // lobby's ready-check (the normal, non-eliminated case).
        Assert.Equal(GameOverBackAction.TeamLobbyReadyCheck,
            GameOverBackPlanner.Plan(MissionStageMessage.StageStarted, ownSideActive: true));
    }

    [Fact]
    public void Unknown_stage_with_inactive_side_returns_to_side_select()
    {
        // No MISSION_STAGE seen yet but the own side is inactive → still the
        // eliminated-side lobby, never a spurious leave.
        Assert.Equal(GameOverBackAction.TeamLobbySideSelect,
            GameOverBackPlanner.Plan(null, ownSideActive: false));
    }
}

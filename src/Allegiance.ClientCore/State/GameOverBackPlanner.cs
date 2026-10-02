using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>P481 — what the game-over screen's Back action does, a pure (graphics-
/// free) mirror of native <c>GameOverScreen::OnButtonBack</c>
/// (<c>WinTrek/gameoverscreen.cpp:609-625</c>):
/// <list type="bullet">
/// <item>mission stage <c>== STAGE_OVER</c> → <see cref="LeaveMission"/> (native
/// <c>StartQuitMission()</c>);</item>
/// <item>otherwise (the two port cases that need the team lobby) → back to the
/// team lobby, whose overlay is picked by whether the local side is still
/// active: a player who survived into the rotated next game (stage
/// <c>NotStarted</c>, P480) goes to the ready-check roster
/// (<see cref="TeamLobbyReadyCheck"/>); an eliminated player on an inactive
/// side (stage still <c>Started</c>, P455b) goes to the side-select picker to
/// request a still-active side (<see cref="TeamLobbySideSelect"/>).</item>
/// </list></summary>
public enum GameOverBackAction
{
    /// <summary>Leave the mission entirely (native <c>StartQuitMission</c> —
    /// back to the server browser / outer lobby).</summary>
    LeaveMission,

    /// <summary>Team lobby: the ready-check roster for the current mission
    /// (player is seated on an active side and can ready up for the next game).</summary>
    TeamLobbyReadyCheck,

    /// <summary>Team lobby: the side-select picker so the player can request a
    /// position on a still-active side (inactive/eliminated sides excluded).</summary>
    TeamLobbySideSelect,
}

/// <summary>P481 — decides the game-over Back action from the live client world
/// state. Pure and headless-testable (no MonoGame, no graphics device).</summary>
public static class GameOverBackPlanner
{
    /// <summary>Resolve the Back action. <paramref name="missionStage"/> is the
    /// client's <see cref="MissionInfo.Stage"/> (an int in the
    /// <c>MissionStageMessage</c> constant family); <paramref name="ownSideActive"/>
    /// is <see cref="ClientWorld.IsSideActive"/> of the local player's side
    /// (<c>true</c> when the side is unknown/active). Faithful to native's
    /// single <c>GetStage() == STAGE_OVER</c> branch; the ready-check vs
    /// side-select split is the port's mapping of native's TeamScreen onto the
    /// two existing overlays.</summary>
    public static GameOverBackAction Plan(int? missionStage, bool ownSideActive)
    {
        if (missionStage == MissionStageMessage.StageOver)
            return GameOverBackAction.LeaveMission;

        // Not a full game-over: the team lobby for the current mission.
        return ownSideActive
            ? GameOverBackAction.TeamLobbyReadyCheck
            : GameOverBackAction.TeamLobbySideSelect;
    }
}

namespace Allegiance.ClientCore.MonoGame.Rendering;

public enum TeamConsoleSize { Hidden, Mini, Expanded }
public readonly record struct ConsolePaneState(bool GameState, bool Inventory, bool Nav, TeamConsoleSize Team);

/// <summary>WinTrek.cpp:9871-9890,10001-10023,10038-10081.
/// Nav and Inventory toggle independently; team modes banish other modal panes.</summary>
public static class ConsoleModeRules
{
    // WinTrek.cpp:10005-10014: loadout -> banish overlays -> hangar,
    // while a hangar switches directly back to the loadout screen.
    public static (bool HangarOnly, bool ClosePanes) DockedInventory(bool hangarOnly, bool banishablePanes) =>
        hangarOnly ? (false, false) : banishablePanes ? (false, true) : (true, false);

    public static ConsolePaneState Switch(ConsolePaneState state, string action) => action switch
    {
        "ConModeGameState" => state with { GameState = !state.GameState, Team = TeamConsoleSize.Hidden },
        "ConModeNav" => state with { Nav = !state.Nav },
        "ConModeInventory" => state with { Inventory = !state.Inventory },
        "TeamPane" => state with { GameState = false,
            Team = state.Team == TeamConsoleSize.Expanded ? TeamConsoleSize.Hidden : TeamConsoleSize.Expanded },
        "ConModeMiniTeam" => state with { GameState = false,
            Team = state.Team == TeamConsoleSize.Mini ? TeamConsoleSize.Hidden : TeamConsoleSize.Mini },
        "ConModeCombat" or "ViewCommand" or "ViewExternal" => state with { GameState = false, Team = TeamConsoleSize.Hidden },
        _ => state,
    };
}

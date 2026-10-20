namespace Allegiance.ClientCore.MonoGame.Rendering;

public enum EscapeUiAction
{
    None,
    Briefing,
    Chat,
    QuickChat,
    Controls,
    Help,
    CloseMainMenu,
    PlayerPings,
    Investment,
    Teleport,
    Loadout,
    Credits,
    ToggleMainMenu,
}

/// <summary>
/// WinTrek.cpp:9304-9320: MainMenu opens a menu, gated on composition.
/// Existing port cancel surfaces take precedence; quitting requires selecting
/// Quit. UX-1 adds the docked-screen panes (investment, teleport, loadout) to
/// the same precedence chain so Escape closes the topmost open pane and only
/// falls through to the main menu when none is open.
/// </summary>
public static class MainMenuRules
{
    /// <summary>
    /// Resolve the single pane/overlay Escape should close this frame. The first
    /// true flag wins (that is the "topmost" pane), so exactly one action is
    /// returned even when several panes are open. Precedence:
    /// briefing → chat → quick-chat → controls → help → open main menu (closes
    /// it, matching its "Esc resume" hint) → player pings → investment →
    /// teleport → loadout → credits → main menu. The new docked panes sit between help and
    /// credits because the docked screen is drawn above credits/key-bindings but
    /// below the help overlay (Game1.Rendering.cs draw order).
    /// </summary>
    public static EscapeUiAction Escape(
        bool briefing, bool chat, bool quickChat, bool controls, bool help, bool credits,
        bool mainMenuPressed, bool overrideView = false,
        bool investment = false, bool teleport = false, bool loadout = false,
        bool mainMenuOpen = false, bool playerPings = false) =>
        briefing ? EscapeUiAction.Briefing
        : chat ? EscapeUiAction.Chat
        : quickChat ? EscapeUiAction.QuickChat
        : controls ? EscapeUiAction.Controls
        : help ? EscapeUiAction.Help
        : mainMenuOpen ? EscapeUiAction.CloseMainMenu
        : playerPings ? EscapeUiAction.PlayerPings
        : investment ? EscapeUiAction.Investment
        : teleport ? EscapeUiAction.Teleport
        : loadout ? EscapeUiAction.Loadout
        : credits ? EscapeUiAction.Credits
        : mainMenuPressed && !overrideView ? EscapeUiAction.ToggleMainMenu
        : EscapeUiAction.None;
}

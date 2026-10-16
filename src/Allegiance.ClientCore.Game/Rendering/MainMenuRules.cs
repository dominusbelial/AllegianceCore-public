namespace Allegiance.ClientCore.MonoGame.Rendering;

public enum EscapeUiAction { None, Briefing, Chat, QuickChat, Controls, Help, Credits, ToggleMainMenu }

/// <summary>WinTrek.cpp:9304-9320: MainMenu opens a menu, gated on composition.
/// Existing port cancel surfaces take precedence; quitting requires selecting Quit.</summary>
public static class MainMenuRules
{
    public static EscapeUiAction Escape(bool briefing, bool chat, bool quickChat, bool controls, bool help, bool credits, bool mainMenuPressed, bool overrideView = false) =>
        briefing ? EscapeUiAction.Briefing : chat ? EscapeUiAction.Chat : quickChat ? EscapeUiAction.QuickChat
        : controls ? EscapeUiAction.Controls : help ? EscapeUiAction.Help : credits ? EscapeUiAction.Credits
        : mainMenuPressed && !overrideView ? EscapeUiAction.ToggleMainMenu : EscapeUiAction.None;
}

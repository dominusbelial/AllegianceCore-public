using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// UX-1 — Escape closes the topmost open modal pane; only with none open does
/// it keep its existing main-menu meaning. The precedence is pure and lives in
/// <see cref="MainMenuRules.Escape"/> so the behaviour can be pinned headlessly.
/// </summary>
public class UX1EscapeClosesPanesTests
{
    [Fact]
    public void Escape_closes_the_investment_pane_when_it_is_open()
    {
        Assert.Equal(
            EscapeUiAction.Investment,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: false, mainMenuPressed: true,
                investment: true));
    }

    [Fact]
    public void Escape_closes_the_teleport_pane_when_it_is_open()
    {
        Assert.Equal(
            EscapeUiAction.Teleport,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: false, mainMenuPressed: true,
                teleport: true));
    }

    [Fact]
    public void Escape_closes_the_loadout_shop_when_it_is_open()
    {
        Assert.Equal(
            EscapeUiAction.Loadout,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: false, mainMenuPressed: true,
                loadout: true));
    }

    [Fact]
    public void Escape_closes_only_help_when_help_and_investment_are_open()
    {
        Assert.Equal(
            EscapeUiAction.Help,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: true, credits: false, mainMenuPressed: true,
                investment: true));
    }

    [Fact]
    public void Escape_closes_only_investment_when_investment_and_credits_are_open()
    {
        Assert.Equal(
            EscapeUiAction.Investment,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: true, mainMenuPressed: true,
                investment: true));
    }

    [Fact]
    public void Escape_closes_only_teleport_when_teleport_and_credits_are_open()
    {
        Assert.Equal(
            EscapeUiAction.Teleport,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: true, mainMenuPressed: true,
                teleport: true));
    }

    [Fact]
    public void Escape_keeps_the_existing_main_menu_behaviour_when_no_pane_is_open()
    {
        // None open → Escape opens the main menu (the original meaning).
        Assert.Equal(
            EscapeUiAction.ToggleMainMenu,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: false, mainMenuPressed: true));

        // Override/lockdown still suppresses the main menu exactly as before.
        Assert.Equal(
            EscapeUiAction.None,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: false, mainMenuPressed: true,
                overrideView: true));
    }

    [Fact]
    public void Existing_composition_precedence_is_unchanged()
    {
        Assert.Equal(EscapeUiAction.Briefing, MainMenuRules.Escape(true, true, true, true, true, true, true));
        Assert.Equal(EscapeUiAction.Chat, MainMenuRules.Escape(false, true, true, true, true, true, true));
        Assert.Equal(EscapeUiAction.QuickChat, MainMenuRules.Escape(false, false, true, true, true, true, true));
        Assert.Equal(EscapeUiAction.Controls, MainMenuRules.Escape(false, false, false, true, true, true, true));
        Assert.Equal(EscapeUiAction.Help, MainMenuRules.Escape(false, false, false, false, true, true, true));
        Assert.Equal(EscapeUiAction.Credits, MainMenuRules.Escape(false, false, false, false, false, true, true));
    }

    [Fact]
    public void Escape_closes_an_open_main_menu_before_a_docked_pane_behind_it()
    {
        Assert.Equal(
            EscapeUiAction.CloseMainMenu,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: false, mainMenuPressed: true,
                loadout: true, mainMenuOpen: true));
    }

    [Fact]
    public void Escape_closes_the_player_pings_pane_before_the_docked_panes()
    {
        Assert.Equal(
            EscapeUiAction.PlayerPings,
            MainMenuRules.Escape(briefing: false, chat: false, quickChat: false,
                controls: false, help: false, credits: false, mainMenuPressed: true,
                investment: true, playerPings: true));
    }
}

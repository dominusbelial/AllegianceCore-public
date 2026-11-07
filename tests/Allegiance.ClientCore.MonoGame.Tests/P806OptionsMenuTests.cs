using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P806 — the intro screen's OPTIONS MENU is a native menu TREE
/// (<c>WinTrek::ShowOptionsMenu</c>, WinTrek.cpp:4429-4442; the submenus
/// <c>GetSubMenu</c> builds at :4502-4590): Sound Options and Game Options open
/// nested menus (with Chat/Mute/Filter one level deeper) instead of the port's
/// previous one-press toggles. <see cref="OptionsMenuModel"/> is the production
/// model <c>Game1.UpdateOptionsMenu</c>/<c>DrawPrivacySettings</c> drive.
/// </summary>
public sealed class P806OptionsMenuTests
{
    private static readonly OptionsMenuModel.OptionsState State = new(
        MusicOn: true, FilterChatsToAll: true, LinearControls: false,
        FlipY: false, VirtualJoystick: true, Deadzone: 0.1f);

    [Fact]
    public void Root_menu_has_the_four_native_groups_and_two_of_them_are_submenus()
    {
        var menu = new OptionsMenuModel();
        var rows = menu.Rows(State);
        Assert.Equal(4, rows.Count);
        Assert.Equal("Graphics Options", rows[0].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenSoundMenu, rows[1].Command);
        Assert.Equal("Sound Options >", rows[1].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenGameMenu, rows[2].Command);
        Assert.Equal("Game Options >", rows[2].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.MapKeysAndControls, rows[3].Command);

        // Activating a submenu row opens it (and returns no leaf command);
        // activating a leaf returns the command and keeps the page.
        menu.Row0 = 1;
        Assert.Null(menu.Activate(State));
        Assert.Equal(OptionsMenuModel.Page.Sound, menu.Current);
        menu.Reset();
        menu.Row0 = 0;
        Assert.Equal(OptionsMenuModel.OptionsCommand.GraphicsSettings, menu.Activate(State));
        Assert.Equal(OptionsMenuModel.Page.Root, menu.Current);
    }

    [Fact]
    public void Sound_submenu_lists_the_ports_real_music_controls()
    {
        var menu = new OptionsMenuModel();
        menu.Row0 = 1;
        menu.Activate(State);                            // open Sound Options
        var rows = menu.Rows(State);
        // P893 added the positional-3D switch (a port addition — native's sound
        // menu has no 3D toggle). P902 restores quality as a fifth row.
        Assert.Equal(5, rows.Count);
        Assert.Equal("Music: on", rows[0].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleMusic, rows[0].Command);
        Assert.Equal(OptionsMenuModel.OptionsCommand.MusicVolumeUp, rows[1].Command);
        Assert.Equal(OptionsMenuModel.OptionsCommand.MusicVolumeDown, rows[2].Command);
        Assert.Equal("Positional 3D Sound: on", rows[3].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.Toggle3DSound, rows[3].Command);

        // The labels reflect live state (native Get...MenuString).
        var off = State with { MusicOn = false, Sound3DOn = false };
        var offRows = menu.Rows(off);
        Assert.Equal("Music: off", offRows[0].Label);
        Assert.Equal("Positional 3D Sound: off", offRows[3].Label);
    }

    [Fact]
    public void Game_submenu_contains_the_native_switches_and_nests_chat_filter()
    {
        var menu = new OptionsMenuModel();
        menu.Row0 = 2;
        menu.Activate(State);                            // open Game Options
        var rows = menu.Rows(State);
        // P871 restored native's Force Feedback switch (idmToggleEnableFeedback
        // 'E', WinTrek.cpp:4599); P948 added native's Mouse Options submenu
        // (idmMouseOptions 'Q', :4541), so the Game submenu is the native eight rows.
        Assert.Equal(8, rows.Count);
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenChatMenu, rows[0].Command);
        Assert.Contains(rows, r => r.Command == OptionsMenuModel.OptionsCommand.ToggleLinearControls);
        Assert.Contains(rows, r => r.Command == OptionsMenuModel.OptionsCommand.CycleDeadzone);
        Assert.Contains(rows, r => r.Command == OptionsMenuModel.OptionsCommand.ToggleFlipY);
        Assert.Contains(rows, r => r.Command == OptionsMenuModel.OptionsCommand.ToggleVirtualJoystick);
        Assert.Contains(rows, r => r.Command == OptionsMenuModel.OptionsCommand.ToggleForceFeedback);
        Assert.Contains(rows, r => r.Command == OptionsMenuModel.OptionsCommand.OpenMouseMenu);
        Assert.Equal(OptionsMenuModel.OptionsCommand.MapKeysAndControls, rows[^1].Command);

        // Labels are state-bearing, like the native Get...MenuString helpers.
        Assert.Contains(rows, r => r.Label == "Linear Controls: off");
        Assert.Contains(rows, r => r.Label == "Flip Y: off");
        Assert.Contains(rows, r => r.Label == "Virtual Joystick: on");
        Assert.Contains(rows, r => r.Label == "Force Feedback: on");
        Assert.Contains(rows, r => r.Label.StartsWith("Mouse Dead Zone:"));
    }

    [Fact]
    public void Mouse_submenu_steps_sensitivity_with_native_labels_and_end_stops()
    {
        // P948 — native's Game Options → "Mouse Options" submenu (idmMouseOptions
        // 'Q', WinTrek.cpp:4541) with the Raise/Lower items (:4612-4613,
        // AdjustMouseSens :5512-5531) and GetMouseSensMenuString's wording
        // (:5599-5615).
        var menu = new OptionsMenuModel();
        menu.Row0 = 2;
        menu.Activate(State);                            // Game Options
        var gameRows = menu.Rows(State);
        int mouseRow = gameRows.ToList().FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenMouseMenu);
        Assert.True(mouseRow >= 0);
        Assert.Equal('Q', gameRows[mouseRow].Hotkey);
        menu.Row0 = mouseRow;
        Assert.Null(menu.Activate(State));               // submenu opens
        Assert.Equal(OptionsMenuModel.Page.Mouse, menu.Current);

        var rows = menu.Rows(State with { MouseSensitivity = 0.6f });
        Assert.Equal(2, rows.Count);
        Assert.Equal("Raise Mouse Sensitivity to 61 %", rows[0].Label);
        Assert.Equal("Lower Mouse Sensitivity to 59 %", rows[1].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.MouseSensitivityUp, rows[0].Command);
        Assert.Equal(OptionsMenuModel.OptionsCommand.MouseSensitivityDown, rows[1].Command);
        Assert.Equal('U', rows[0].Hotkey);
        Assert.Equal('D', rows[1].Hotkey);

        // End stops use native's "(highest)"/"(lowest)" wording.
        Assert.Equal("Raise Mouse Sensitivity (highest)",
            menu.Rows(State with { MouseSensitivity = 2.0f })[0].Label);
        Assert.Equal("Lower Mouse Sensitivity (lowest)",
            menu.Rows(State with { MouseSensitivity = 0.1f })[1].Label);

        // Activating a row returns its command and keeps the page open.
        menu.Row0 = 0;
        Assert.Equal(OptionsMenuModel.OptionsCommand.MouseSensitivityUp, menu.Activate(State));
        Assert.Equal(OptionsMenuModel.Page.Mouse, menu.Current);
    }

    [Fact]
    public void Chat_submenu_flips_the_native_filter_chats_to_all_label()
    {
        var menu = new OptionsMenuModel();
        menu.Row0 = 2;
        menu.Activate(State);                            // Game Options
        menu.Row0 = 0;
        menu.Activate(State);                            // Chat/Mute/Filter
        Assert.Equal(OptionsMenuModel.Page.Chat, menu.Current);
        var rows = menu.Rows(State);
        // P871b added native's per-player mute toggle to this page, so the page is
        // the filter row plus that one.
        var row = Assert.Single(rows, r => r.Command == OptionsMenuModel.OptionsCommand.ToggleFilterChatsToAll);
        // Native GetFilterChatsToAllMenuString (WinTrek.cpp:5664-5667): the label
        // names the ACTION, so it flips with the switch.
        Assert.Equal("Don't Filter Chats Sent To All", row.Label);
        Assert.Equal("Filter Chats Sent To All",
            menu.Rows(State with { FilterChatsToAll = false })
                .Single(r => r.Command == OptionsMenuModel.OptionsCommand.ToggleFilterChatsToAll).Label);
        var mute = Assert.Single(rows, r => r.Command == OptionsMenuModel.OptionsCommand.ToggleMuteChatSender);
        Assert.Equal('M', mute.Hotkey);   // native idmContextMutePlayer's menu key, WinTrek.cpp:4310
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleFilterChatsToAll, menu.Activate(State));
    }

    [Fact]
    public void Back_leaves_a_submenu_then_closes_the_menu_at_the_root()
    {
        var menu = new OptionsMenuModel();
        menu.Row0 = 2;
        menu.Activate(State);
        menu.Row0 = 0;
        menu.Activate(State);
        Assert.Equal(OptionsMenuModel.Page.Chat, menu.Current);
        Assert.True(menu.Back());                        // Chat → Root (native OnCancel)
        Assert.Equal(OptionsMenuModel.Page.Root, menu.Current);
        Assert.False(menu.Back());                       // Root → the screen closes the menu
    }

    [Fact]
    public void Native_hotkeys_activate_rows_on_the_open_page()
    {
        var menu = new OptionsMenuModel();
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenSoundMenu,
            menu.CommandForHotkey('S', State));
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenGameMenu,
            menu.CommandForHotkey('G', State));
        Assert.Equal(OptionsMenuModel.OptionsCommand.GraphicsSettings,
            menu.CommandForHotkey('O', State));

        menu.Row0 = 2;
        menu.Activate(State);
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleLinearControls,
            menu.CommandForHotkey('L', State));
        Assert.Equal(OptionsMenuModel.OptionsCommand.CycleDeadzone,
            menu.CommandForHotkey('Z', State));
        Assert.Equal(OptionsMenuModel.OptionsCommand.MapKeysAndControls,
            menu.CommandForHotkey('C', State));
        // P948 — 'Q' is native's Mouse Options submenu (idmMouseOptions,
        // WinTrek.cpp:4541); before P948 the port had no mouse page to open.
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenMouseMenu,
            menu.CommandForHotkey('Q', State));
    }

    [Fact]
    public void Move_wraps_inside_the_page_and_activation_resets_a_submenus_cursor()
    {
        var menu = new OptionsMenuModel();
        menu.Move(-1, State);                            // wrap to the last root row
        Assert.Equal(3, menu.Row0);
        menu.Move(1, State);                             // wrap back to the first
        Assert.Equal(0, menu.Row0);

        menu.Row0 = 2;
        menu.Activate(State);                            // open Game Options
        Assert.Equal(0, menu.Row0);
    }
}

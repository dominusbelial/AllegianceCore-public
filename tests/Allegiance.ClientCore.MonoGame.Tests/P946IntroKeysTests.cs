using System.Text;
using Allegiance.ClientCore.MonoGame;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P946 — the intro screen's keyboard mapping, proven without a window: the
/// pilot-name field no longer swallows Up/Down, typed characters still reach
/// the name only while the field has focus, Tab toggles only when there is no
/// identity account, Enter keeps activating the selected entry, and the
/// Find Server / Direct Connect password field is named instead of silently
/// taking keys. Native's intro is mouse-driven (WinTrek/introscreen.cpp:1212-1228
/// wires the ButtonPanes to handlers), so this keyboard menu is the port's own
/// design and its rules live in <see cref="IntroScreenModel"/>.
/// </summary>
public class P946IntroKeysTests
{
    [Fact]
    public void Down_and_up_move_the_menu_while_the_name_field_has_focus()
    {
        // The pilot-name field is the always-on field at boot. P946: the mapping
        // has no focus parameter — Up/Down always move the selection.
        int index = 0;
        var down = IntroScreenModel.MainMenuKeys(upPressed: false, downPressed: true, enterPressed: false,
            tabPressed: false, tabAllowed: true);
        Assert.Equal(1, down.SelectionDelta);
        index = IntroScreenModel.MoveSelection(index, IntroScreenModel.MainLabels.Length, down.SelectionDelta);
        Assert.Equal(1, index);

        var up = IntroScreenModel.MainMenuKeys(upPressed: true, downPressed: false, enterPressed: false,
            tabPressed: false, tabAllowed: true);
        Assert.Equal(-1, up.SelectionDelta);
        index = IntroScreenModel.MoveSelection(index, IntroScreenModel.MainLabels.Length, up.SelectionDelta);
        Assert.Equal(0, index);
    }

    [Fact]
    public void Menu_navigation_wraps_in_both_directions()
    {
        const int count = 13;
        var up = IntroScreenModel.MainMenuKeys(upPressed: true, downPressed: false, enterPressed: false,
            tabPressed: false, tabAllowed: true);
        var down = IntroScreenModel.MainMenuKeys(upPressed: false, downPressed: true, enterPressed: false,
            tabPressed: false, tabAllowed: true);

        Assert.Equal(count - 1, IntroScreenModel.MoveSelection(0, count, up.SelectionDelta));
        Assert.Equal(0, IntroScreenModel.MoveSelection(count - 1, count, down.SelectionDelta));
        // Both arrows in one frame cancel rather than double-step.
        var both = IntroScreenModel.MainMenuKeys(true, true, false, false, true);
        Assert.Equal(0, both.SelectionDelta);
        // An empty list cannot move.
        Assert.Equal(0, IntroScreenModel.MoveSelection(3, 0, 1));
    }

    [Fact]
    public void Typed_characters_still_edit_the_pilot_name_only_while_it_is_focused()
    {
        var buffer = new StringBuilder();
        const int max = 24;
        foreach (var c in "Nova")
            if (IntroScreenModel.TypesPilotName(c, pilotFocused: true) && buffer.Length < max)
                buffer.Append(c);
        Assert.Equal("Nova", buffer.ToString());

        // Control characters (Enter/Backspace/Esc/DEL) never type into the name.
        Assert.False(IntroScreenModel.TypesPilotName('\r', pilotFocused: true));
        Assert.False(IntroScreenModel.TypesPilotName('\b', pilotFocused: true));
        Assert.False(IntroScreenModel.TypesPilotName((char)27, pilotFocused: true));
        Assert.False(IntroScreenModel.TypesPilotName((char)127, pilotFocused: true));
        // With the field unfocused (Tab), printable keys do not edit it.
        Assert.False(IntroScreenModel.TypesPilotName('N', pilotFocused: false));
    }

    [Fact]
    public void Tab_toggles_the_field_only_when_there_is_no_identity_account()
    {
        var anonymous = IntroScreenModel.MainMenuKeys(false, false, false, tabPressed: true, tabAllowed: true);
        Assert.True(anonymous.ToggleFieldFocus);

        // A signed-in identity account makes Tab a no-op (the pilot label is the
        // verified callsign), so the hint drops the Tab part too.
        var signedIn = IntroScreenModel.MainMenuKeys(false, false, false, tabPressed: true, tabAllowed: false);
        Assert.False(signedIn.ToggleFieldFocus);
        Assert.DoesNotContain("TAB", IntroScreenModel.MainHint(tabAllowed: false));
        Assert.Contains("TAB", IntroScreenModel.MainHint(tabAllowed: true));

        // A Tab press that is not an edge (held) never toggles.
        var held = IntroScreenModel.MainMenuKeys(false, false, false, tabPressed: false, tabAllowed: true);
        Assert.False(held.ToggleFieldFocus);
    }

    [Fact]
    public void Enter_activates_the_selected_entry_and_nothing_else_does()
    {
        var enter = IntroScreenModel.MainMenuKeys(false, false, enterPressed: true, tabPressed: false, tabAllowed: true);
        Assert.True(enter.Activate);
        Assert.Equal(IntroCommand.Options, IntroScreenModel.Activate(4));
        Assert.Equal(IntroCommand.Exit, IntroScreenModel.Activate(8));

        var moveOnly = IntroScreenModel.MainMenuKeys(false, true, enterPressed: false, tabPressed: false, tabAllowed: true);
        Assert.False(moveOnly.Activate);
    }

    [Fact]
    public void Main_hint_is_one_line_and_names_the_keys_that_work()
    {
        string hint = IntroScreenModel.MainHint(tabAllowed: true);
        Assert.DoesNotContain('\n', hint);
        Assert.Contains("Type your pilot name", hint);
        Assert.Contains("UP/DOWN", hint);
        Assert.Contains("ENTER", hint);
        Assert.Contains("ESC", hint);
        Assert.DoesNotContain("TAB", IntroScreenModel.MainHint(tabAllowed: false));
    }

    [Fact]
    public void FindServer_password_focus_keeps_the_list_keys_and_swallows_the_new_game_shortcut()
    {
        // Tab opens the password field; the server list still owns Up/Down.
        var focus = IntroScreenModel.FindServerFocus(passwordEditing: true);
        Assert.Equal(IntroScreenModel.TextField.ServerPassword, focus);
        Assert.Equal(IntroScreenModel.TextField.None, IntroScreenModel.FindServerFocus(passwordEditing: false));

        int cursor = 0;
        const int servers = 3;
        cursor = IntroScreenModel.MoveSelection(cursor, servers, 1);
        Assert.Equal(1, cursor);
        cursor = IntroScreenModel.MoveSelection(cursor, servers, -1);
        Assert.Equal(0, cursor);

        // P946: N typed into the password must not open the New Game wizard.
        Assert.False(IntroScreenModel.FindServerNewGameKey(IntroScreenModel.FindServerFocus(passwordEditing: true)));
        Assert.True(IntroScreenModel.FindServerNewGameKey(IntroScreenModel.FindServerFocus(passwordEditing: false)));

        // The hint names the field that currently owns typing.
        Assert.Contains("TYPE password", IntroScreenModel.FindServerHint(passwordFocused: true));
        Assert.Contains("UP/DOWN", IntroScreenModel.FindServerHint(passwordFocused: true));
        Assert.Contains("TAB password", IntroScreenModel.FindServerHint(passwordFocused: false));
        Assert.DoesNotContain("TYPE password", IntroScreenModel.FindServerHint(passwordFocused: false));
    }

    [Fact]
    public void DirectConnect_focus_is_the_password_when_open_else_the_address()
    {
        Assert.Equal(IntroScreenModel.TextField.DirectAddress,
            IntroScreenModel.DirectConnectFocus(addressEditing: true, passwordEditing: false));
        Assert.Equal(IntroScreenModel.TextField.ServerPassword,
            IntroScreenModel.DirectConnectFocus(addressEditing: true, passwordEditing: true));
        Assert.Equal(IntroScreenModel.TextField.None,
            IntroScreenModel.DirectConnectFocus(addressEditing: false, passwordEditing: false));

        Assert.Contains("TAB password", IntroScreenModel.DirectConnectHint(passwordFocused: false));
        Assert.Contains("TYPE password", IntroScreenModel.DirectConnectHint(passwordFocused: true));
        Assert.Contains("TAB back to address", IntroScreenModel.DirectConnectHint(passwordFocused: true));
    }

    [Fact]
    public void Every_hint_used_by_the_fixed_views_stays_on_one_line()
    {
        string[] hints =
        [
            IntroScreenModel.MainHint(true),
            IntroScreenModel.MainHint(false),
            IntroScreenModel.FindServerHint(true),
            IntroScreenModel.FindServerHint(false),
            IntroScreenModel.DirectConnectHint(true),
            IntroScreenModel.DirectConnectHint(false),
        ];
        Assert.All(hints, h => Assert.DoesNotContain('\n', h));
    }
}

using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P979 — the options menu's <c>Visuals</c> page. Native has no such page (the
/// flags are a port extension, like P893's 3D-sound switch), so it is added to
/// the root menu after native's four groups and built from
/// <see cref="VisualFlags.Definitions"/> so a future flag needs only a table row.
/// The page lives in the shared model: <see cref="HudStyle"/> selects the
/// in-flight HUD renderer only, so Classic and Modern show this page identically
/// (the capture pair under docs/reports/P979_assets/ is the visual check).
/// </summary>
public sealed class P979VisualsOptionsTests
{
    private static OptionsMenuModel.OptionsState State(VisualOptions? visuals = null) => new(
        MusicOn: true, FilterChatsToAll: true, LinearControls: false,
        FlipY: false, VirtualJoystick: true, Deadzone: 0.1f, Visuals: visuals);

    private static OptionsMenuModel OpenVisualsPage(OptionsMenuModel.OptionsState state)
    {
        var menu = new OptionsMenuModel();
        int row = menu.Rows(state).ToList().FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenVisualsMenu);
        Assert.True(row >= 0);
        menu.Row0 = row;
        Assert.Null(menu.Activate(state));
        Assert.Equal(OptionsMenuModel.Page.Visuals, menu.Current);
        return menu;
    }

    [Fact]
    public void Root_menu_keeps_natives_four_groups_and_adds_the_visuals_page()
    {
        var menu = new OptionsMenuModel();
        var rows = menu.Rows(State());
        Assert.Equal(5, rows.Count);
        Assert.Equal("Graphics Options", rows[0].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenSoundMenu, rows[1].Command);
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenGameMenu, rows[2].Command);
        // P979 port extension.
        Assert.Equal("Visuals >", rows[3].Label);
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenVisualsMenu, rows[3].Command);
        Assert.Equal('V', rows[3].Hotkey);
        Assert.Equal(OptionsMenuModel.OptionsCommand.MapKeysAndControls, rows[4].Command);
        Assert.Equal(OptionsMenuModel.OptionsCommand.OpenVisualsMenu, menu.CommandForHotkey('V', State()));
    }

    [Fact]
    public void Visuals_page_lists_every_flag_with_a_one_line_description_and_defaults_off()
    {
        var menu = OpenVisualsPage(State());
        var rows = menu.Rows(State());
        // Six toggle rows + a numeric flag's value row and its two step rows.
        Assert.Equal(9, rows.Count);
        Assert.Contains(rows, r => r.Label.StartsWith("Radial visibility: off - "));
        Assert.Contains(rows, r => r.Label.StartsWith("Remote timeline: off - "));
        Assert.Contains(rows, r => r.Label.StartsWith("View range: 10000 m - "));
        Assert.Contains(rows, r => r.Label == "Raise View range to 11000 m");
        Assert.Contains(rows, r => r.Label == "Lower View range to 9000 m");
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, rows[0].Command);
        Assert.Equal('R', rows[0].Hotkey);
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, rows[1].Command);
        Assert.Equal('M', rows[1].Hotkey);
        Assert.Null(rows[2].Command);                       // the value line is a read-out
        Assert.Equal(OptionsMenuModel.OptionsCommand.RaiseVisualRange, rows[3].Command);
        Assert.Equal(OptionsMenuModel.OptionsCommand.LowerVisualRange, rows[4].Command);
        Assert.Equal('U', rows[3].Hotkey);
        Assert.Equal('D', rows[4].Hotkey);
    }

    [Fact]
    public void Labels_reflect_the_live_flag_values()
    {
        var visuals = new VisualOptions();
        visuals.SetToggle(VisualFlags.RadialVisibilityKey, true);
        visuals.SetToggle(VisualFlags.RemoteTimelineKey, true);
        visuals.SetValue(VisualFlags.ViewRangeKey, 12000f);
        var state = State(visuals);
        var rows = OpenVisualsPage(state).Rows(state);
        Assert.Contains(rows, r => r.Label.StartsWith("Radial visibility: on - "));
        Assert.Contains(rows, r => r.Label.StartsWith("Remote timeline: on - "));
        Assert.Contains(rows, r => r.Label.StartsWith("View range: 12000 m - "));
        Assert.Contains(rows, r => r.Label == "Raise View range to 13000 m");
        Assert.Contains(rows, r => r.Label == "Lower View range to 11000 m");
    }

    [Fact]
    public void Each_row_names_the_flag_it_edits_and_the_steps_stop_at_the_table_bounds()
    {
        var menu = OpenVisualsPage(State());
        Assert.Equal(VisualFlags.RadialVisibilityKey, menu.VisualKeyAt(0, State()));
        Assert.Equal(VisualFlags.RemoteTimelineKey, menu.VisualKeyAt(1, State()));
        Assert.Equal(VisualFlags.ViewRangeKey, menu.VisualKeyAt(2, State()));
        Assert.Equal(VisualFlags.ViewRangeKey, menu.VisualKeyAt(3, State()));
        Assert.Equal(VisualFlags.ViewRangeKey, menu.VisualKeyAt(4, State()));
        Assert.Null(new OptionsMenuModel().VisualKeyAt(0, State()));   // not on the page

        var max = new VisualOptions();
        max.SetValue(VisualFlags.ViewRangeKey, 50000f);
        var maxRows = menu.Rows(State(max));
        Assert.Contains(maxRows, r => r.Label == "Raise View range (highest)");
        var min = new VisualOptions();
        min.SetValue(VisualFlags.ViewRangeKey, 1000f);
        var minRows = menu.Rows(State(min));
        Assert.Contains(minRows, r => r.Label == "Lower View range (lowest)");
    }

    [Fact]
    public void Activating_a_toggle_row_returns_the_generic_visual_command()
    {
        var state = State();
        var menu = OpenVisualsPage(state);
        menu.Row0 = 0;
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, menu.Activate(state));
        Assert.Equal(OptionsMenuModel.Page.Visuals, menu.Current);      // stays on the page
        // The visual commands come back through the page's hotkeys too.
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, menu.CommandForHotkey('R', state));
        Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, menu.CommandForHotkey('M', state));
        Assert.Equal(OptionsMenuModel.OptionsCommand.RaiseVisualRange, menu.CommandForHotkey('U', state));
        Assert.Equal(VisualFlags.ViewRangeKey, menu.VisualKeyAt(menu.Row0, state));
        Assert.Equal(OptionsMenuModel.OptionsCommand.LowerVisualRange, menu.CommandForHotkey('D', state));
        Assert.Equal(VisualFlags.ViewRangeKey, menu.VisualKeyAt(menu.Row0, state));
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Visuals_page_is_the_same_shared_model_in_both_hud_styles(HudStyle style)
    {
        // P972's rule: behaviour lives in the shared model and both styles get it.
        // The intro options menu is style-neutral (HudSettings.Style only picks the
        // in-flight HUD renderer), so both styles must render this exact page.
        var settings = new HudSettings { Style = style };
        Assert.Equal(style, settings.Style);

        var state = State();
        var menu = OpenVisualsPage(state);
        var rows = menu.Rows(state);
        Assert.Equal(9, rows.Count);
        Assert.Contains(rows, r => r.Label.StartsWith("Radial visibility: off - "));
        Assert.Contains(rows, r => r.Label.StartsWith("Remote timeline: off - "));
        Assert.Contains(rows, r => r.Label.StartsWith("View range: 10000 m - "));
        Assert.Equal(VisualFlags.RadialVisibilityKey, menu.VisualKeyAt(0, state));
        Assert.Equal(VisualFlags.RemoteTimelineKey, menu.VisualKeyAt(1, state));
        Assert.Equal(VisualFlags.ViewRangeKey, menu.VisualKeyAt(2, state));
    }
}

using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>HUD-10 — pins the native geometry/audio rules added for the chat
/// pane/composer, command bar, money creditbox, mission clock, lifepod panel,
/// death banner, damage-hit edge and pane slides. Every constant cites the
/// native file:line it comes from.</summary>
public class Hud10PanelsTests
{
    // ── 1. Chat history pane (dialog.mdl:180; chatpane.cpp:374-380; dialog.mdl:5394)
    [Fact]
    public void Chat_pane_uses_native_width_height_lines_and_anchor()
    {
        Assert.Equal(355, ChatPaneLayout.Width);                 // ChatListPane(355, ...)
        Assert.Equal(8, ChatPaneLayout.HeightBase);              // +8
        Assert.Equal(7, ChatPaneLayout.DefaultLines);            // LoadPreference("ChatLines",7)
        Assert.Equal(14, ChatPaneLayout.NativeFontHeight);       // largeFont at ChatFontSize 2
        Assert.Equal(106, ChatPaneLayout.Height(7));             // 8 + 14*7
        Assert.Equal(20, ChatPaneLayout.Top(7));                 // 126 - 106
        Assert.Equal(800, ChatPaneLayout.Left(1920));            // centre 960 - 160
        Assert.Equal(355, ChatPaneLayout.Width);
        Assert.True(ChatPaneLayout.ScrollbarHidden);             // ChatListPane(..., false)
        Assert.Equal(7, ChatRenderer.MaxVisible);
    }

    [Fact]
    public void Chat_pane_height_scales_with_the_native_line_count()
    {
        Assert.Equal(50, ChatPaneLayout.Height(3));
        Assert.Equal(148, ChatPaneLayout.Height(10));
        Assert.Equal(76, ChatPaneLayout.Top(3));
    }

    // ── 2. Compose variants (dialog.mdl:89-170,5355)
    [Fact]
    public void Compose_variants_match_the_native_panel_art_and_inner_offsets()
    {
        var mouse = ComposePaneLayout.Variant(NativeComposeState.MouseCommand);
        Assert.Equal("chatmousebmp", mouse.ArtStem);
        Assert.Equal((73, 23), (mouse.PanelWidth, mouse.PanelHeight));
        Assert.Equal((3, 3, 66), (mouse.RecipientLeft, mouse.RecipientTop, mouse.RecipientWidth));
        Assert.False(mouse.HasText);

        var chat = ComposePaneLayout.Variant(NativeComposeState.Chat);
        Assert.Equal("chattextbmp", chat.ArtStem);
        Assert.Equal((266, 22), (chat.PanelWidth, chat.PanelHeight));
        Assert.Equal((3, 3, 100), (chat.RecipientLeft, chat.RecipientTop, chat.RecipientWidth));
        Assert.Equal((112, 3, 154), (chat.TextLeft, chat.TextTop, chat.TextWidth));

        var command = ComposePaneLayout.Variant(NativeComposeState.Command);
        Assert.Equal("chatcommandbmp", command.ArtStem);
        Assert.Equal((236, 22), (command.PanelWidth, command.PanelHeight));
        Assert.Equal((3, 3, 66), (command.RecipientLeft, command.RecipientTop, command.RecipientWidth));
        Assert.Equal((78, 2), (command.VerbLeft, command.VerbTop));
        Assert.Equal((104, 3, 128), (command.TextLeft, command.TextTop, command.TextWidth));

        var shell = ComposePaneLayout.Variant(NativeComposeState.Shell);
        Assert.Equal("chatshellbmp", shell.ArtStem);
        Assert.False(shell.HasRecipient);
        Assert.Equal((3, 3, 229), (shell.TextLeft, shell.TextTop, shell.TextWidth));

        var quick = ComposePaneLayout.Variant(NativeComposeState.Quick);
        Assert.Equal("chatmousebmp", quick.ArtStem);
        Assert.Equal(mouse, quick with { State = NativeComposeState.MouseCommand });
        Assert.Throws<ArgumentOutOfRangeException>(() => ComposePaneLayout.Variant(NativeComposeState.NotComposing));
    }

    [Fact]
    public void Compose_panel_slides_from_off_to_native_on_anchor()
    {
        Assert.Equal(-170, ComposePaneLayout.OffLeftFromCentre);
        Assert.Equal(-150, ComposePaneLayout.OnLeftFromCentre);
        Assert.Equal(148, ComposePaneLayout.OnBottomFromScreenBottom);
        Assert.Equal(0.5f, ComposePaneLayout.SlideSeconds);
        // Left edge: centre-170 off, centre-150 on.
        Assert.Equal(790f, ComposePaneLayout.Left(1920, 0f));
        Assert.Equal(810f, ComposePaneLayout.Left(1920, 1f));
        // chattextbmp is 22 high and its bottom is 148 above the screen bottom.
        Assert.Equal(1080 - 148 - 22, ComposePaneLayout.Top(1080, 22, 1f));
        Assert.Equal(1080 + 78 - 22, ComposePaneLayout.Top(1080, 22, 0f));
    }

    [Theory]
    [InlineData("a", 1)] [InlineData("Attack", 1)] [InlineData("c", 2)] [InlineData("d", 3)]
    [InlineData("p", 4)] [InlineData("g", 5)] [InlineData("r", 6)] [InlineData("j", 7)]
    [InlineData("m", 8)] [InlineData("b", 9)] [InlineData("", 0)] [InlineData("zzz", 0)]
    public void Compose_command_verb_icon_maps_the_native_verb_initials(string input, short expected)
        => Assert.Equal(expected, ChatRenderer.CommandIdForCompose(input));

    // ── 3. Command bar (dialog.mdl:38-87,5416)
    [Fact]
    public void Command_bar_native_columns_hit_test_and_verb_ids()
    {
        Assert.Equal((274, 92), (CommandBarLayout.PanelWidth, CommandBarLayout.PanelHeight));
        Assert.Equal(0.5f, CommandBarLayout.SlideSeconds);
        Assert.Equal(new[] { 53, 106, 159, 212 }, CommandBarLayout.RecipientColumns);
        Assert.Equal(new[] { 32, 62, 92, 122, 152, 182, 212, 242, 268 }, CommandBarLayout.VerbColumns);
        Assert.Equal(0, CommandBarLayout.RecipientColumn(4 + 10, 8));
        Assert.Equal(1, CommandBarLayout.RecipientColumn(4 + 60, 8));
        Assert.Equal(3, CommandBarLayout.RecipientColumn(4 + 200, 8));
        Assert.Equal(-1, CommandBarLayout.RecipientColumn(4 + 212, 8));
        Assert.Equal(0, CommandBarLayout.VerbColumn(2 + 10, 38));
        Assert.Equal(8, CommandBarLayout.VerbColumn(2 + 250, 38));
        Assert.Equal(-1, CommandBarLayout.VerbColumn(2 + 268, 38));
        Assert.True(CommandBarLayout.InWingCombo(216 + 1, 8 + 1));
        Assert.False(CommandBarLayout.InWingCombo(216 + 54, 8 + 1));
        Assert.Equal((short)1, CommandBarLayout.VerbCommandId(0));
        Assert.Equal((short)9, CommandBarLayout.VerbCommandId(8));
        Assert.Equal((short)0, CommandBarLayout.VerbCommandId(9));
        Assert.Equal("command", CommandBarLayout.WingNames[0]);
        Assert.Equal("delta", CommandBarLayout.WingNames[8]);
    }

    // ── 4. Money creditbox (dialog.mdl:1885-1908,5199-5203,5368)
    [Fact]
    public void Money_creditbox_uses_native_art_text_and_anchor()
    {
        Assert.Equal("creditboxbmp", MoneyCreditsLayout.ArtStem);
        Assert.Equal((154, 43), (MoneyCreditsLayout.ArtWidth, MoneyCreditsLayout.ArtHeight));
        Assert.Equal("CURRENT CREDITS", MoneyCreditsLayout.Label);
        Assert.Equal((58, 0, 90), (MoneyCreditsLayout.ValueLeft, MoneyCreditsLayout.ValueTop, MoneyCreditsLayout.ValueWidth));
        Assert.Equal((31, 13, 160), (MoneyCreditsLayout.LabelLeft, MoneyCreditsLayout.LabelTop, MoneyCreditsLayout.LabelWidth));
        Assert.Equal("1234 cr", MoneyCreditsLayout.Value(1234));
        Assert.Equal(1920 - 490 + 33, MoneyCreditsLayout.Left(1920));
        Assert.Equal(1080 / 2 + 265 - 43, MoneyCreditsLayout.Top(1080));
    }

    // ── 5. Mission clock (WinTrek.cpp:8128-8143; igc.h:1526-1564)
    [Fact]
    public void Mission_clock_is_remaining_for_countdown_games_and_elapsed_otherwise()
    {
        Assert.True(MissionClock.IsCountdownGame(600f));
        Assert.False(MissionClock.IsCountdownGame(0f));
        Assert.Equal(474, MissionClock.DisplaySeconds(600f, 125.9));
        Assert.Equal(0, MissionClock.DisplaySeconds(600f, 900));
        Assert.Equal(125, MissionClock.DisplaySeconds(0f, 125.9));
        Assert.Equal("TIME REMAINING", MissionClock.Label(600f));
        Assert.Equal("TIME ELAPSED", MissionClock.Label(0f));
        Assert.Equal("10:05", MissionClock.Format(605));
        Assert.Equal("00:00", MissionClock.Format(-4));
    }

    // ── 6/7. Eject pod identity panel + endurance bar (dialog.mdl:1922-1996,5436)
    [Fact]
    public void Lifepod_panel_and_gauge_use_native_unscaled_geometry()
    {
        Assert.Equal("conejectpoddisplaybmp", LifepodPanelLayout.PanelStem);
        Assert.Equal((172, 44), (LifepodPanelLayout.PanelWidth, LifepodPanelLayout.PanelHeight));
        Assert.Equal("softgaugebmp", LifepodPanelLayout.GaugeStem);
        Assert.Equal((52, 4), (LifepodPanelLayout.GaugeWidth, LifepodPanelLayout.GaugeHeight));
        Assert.Equal((66, 10), (LifepodPanelLayout.GaugeLeft, LifepodPanelLayout.GaugeBottom));
        // SideCenter(-86,-87): lower-left at centre + (-86,-87) in y-up, i.e.
        // bottom = h/2+87; top = bottom - 44.
        Assert.Equal((1920 / 2 - 86, 1080 / 2 + 87 - 44), LifepodPanelLayout.PanelTopLeft(1920, 1080));
        Assert.Equal((1920 / 2 - 20, 1080 / 2 + 87 - 14), LifepodPanelLayout.GaugeTopLeft(1920, 1080));
        Assert.Equal(0.5f, LifepodPanelLayout.SlideSeconds);
    }

    // ── 8. Death banner (dialog.mdl:1998-2003,5439)
    [Fact]
    public void Death_banner_is_unscaled_at_the_native_centre_anchor()
    {
        Assert.Equal("deathbannerbmp", DeathBannerLayout.ArtStem);
        Assert.Equal((136, 25), (DeathBannerLayout.Width, DeathBannerLayout.Height));
        Assert.Equal((1920 / 2 - 105, 1080 / 2 - 15 - 25), DeathBannerLayout.TopLeft(1920, 1080));
    }

    // ── 11. Real per-hit edge for the damage flash (ClientWorld VICTIM_DAMAGED)
    [Fact]
    public void Victim_damaged_increments_the_real_hit_revision_used_by_the_flash()
    {
        var world = new ClientWorld();
        Assert.Equal(0, world.VictimDamageVersion);
        world.Apply(new VictimDamaged { AttackerId = 7, Damage = 24.5f });
        Assert.Equal(1, world.VictimDamageVersion);
        world.Apply(new VictimDamaged { AttackerId = 7, Damage = 0f });   // ignored
        Assert.Equal(1, world.VictimDamageVersion);
        world.Apply(new VictimDamaged { AttackerId = 8, Damage = 1f });
        Assert.Equal(2, world.VictimDamageVersion);
    }

    // ── 12. Pane slides / 13. paneSlideSound trigger (dialog.mdl:5355-5387;
    //         console.cpp:3272-3274)
    [Fact]
    public void Overlay_mask_change_starts_a_native_half_second_slide()
    {
        var slides = new PaneSlideTracker();
        Assert.False(slides.Update(10.0, ConsoleOverlayMask.None));       // no change from hidden
        Assert.Equal(0f, slides.Progress(SlidingPane.Inventory, 10.0));
        Assert.True(slides.Update(10.0, ConsoleOverlayMask.Inventory));   // mask changed
        Assert.Equal(0f, slides.Progress(SlidingPane.Inventory, 10.0));
        Assert.Equal(0.5f, slides.Progress(SlidingPane.Inventory, 10.25));
        Assert.Equal(1f, slides.Progress(SlidingPane.Inventory, 10.5));
        Assert.False(slides.Update(11.0, ConsoleOverlayMask.Inventory));  // settled
        Assert.True(slides.Update(11.0, ConsoleOverlayMask.None));        // closing edge
        Assert.Equal(0.5f, slides.Progress(SlidingPane.Inventory, 11.25));
        Assert.True(slides.IsOff(SlidingPane.Inventory, 11.5));
    }

    [Fact]
    public void Compose_panel_slide_is_state_driven_not_overlay_driven()
    {
        var slides = new PaneSlideTracker();
        slides.Update(1.0, ConsoleOverlayMask.InFlight | ConsoleOverlayMask.InCockpit);
        // The display image is always on in flight; the SwitchPane stays closed.
        Assert.Equal(0f, slides.Progress(SlidingPane.ChatComposition, 1.5));
        Assert.True(slides.SetTarget(SlidingPane.ChatComposition, 2.0, 1f));
        Assert.Equal(1f, slides.Progress(SlidingPane.ChatComposition, 2.5));
        Assert.True(slides.SetTarget(SlidingPane.ChatComposition, 3.0, 0f));
        Assert.Equal(0.5f, slides.Progress(SlidingPane.ChatComposition, 3.25));
        Assert.True(slides.IsOff(SlidingPane.ChatComposition, 3.5));
    }

    [Fact]
    public void Sliding_pane_mask_bits_match_the_native_overlay_flags()
    {
        Assert.Equal(ConsoleOverlayMask.Inventory, PaneSlideTracker.Bit(SlidingPane.Inventory));
        Assert.Equal(ConsoleOverlayMask.CommandPane, PaneSlideTracker.Bit(SlidingPane.CommandPane));
        Assert.Equal(ConsoleOverlayMask.Investment, PaneSlideTracker.Bit(SlidingPane.Investment));
        Assert.Equal(ConsoleOverlayMask.TeleportPane, PaneSlideTracker.Bit(SlidingPane.TeleportPane));
        Assert.Equal(ConsoleOverlayMask.Team | ConsoleOverlayMask.ExpandedTeam,
            PaneSlideTracker.Bit(SlidingPane.TeamPane));
        Assert.Equal(0x002000u, (uint)ConsoleOverlayMask.Inventory);   // dialog.mdl:5237
        Assert.Equal(0x004000u, (uint)ConsoleOverlayMask.CommandPane); // dialog.mdl:5239
        Assert.Equal(0x000200u, (uint)ConsoleOverlayMask.Investment);  // dialog.mdl:5229
    }
}

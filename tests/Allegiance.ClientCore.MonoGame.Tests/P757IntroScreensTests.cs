using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P757 — the intro/splash/help wave: the manual model (native HelpPane,
/// effect/help.cpp), the Find Server list rules (native FindGames,
/// introscreen.cpp:824-995) and the splash timeout constant
/// (splashscreen.cpp:34-46).
/// </summary>
public sealed class P757IntroScreensTests
{
    // ── HelpManualModel: native HelpPane navigation ────────────────────────

    [Fact]
    public void Manual_opens_on_the_start_topic_with_empty_history()
    {
        var m = new HelpManualModel();
        Assert.Equal("hlpStart", m.CurrentTopic);
        Assert.Equal(1, m.HistoryDepth);   // native Back is disabled at the root
        Assert.Equal(0, m.Scroll);
    }

    [Fact]
    public void Topic_switch_pushes_history_and_resets_scroll()
    {
        // Native OnMainTopic (help.cpp:280-288) pushes the topic on the history
        // front; DoSetTopic resets both scroll positions (:234-235).
        var m = new HelpManualModel();
        m.ScrollBy(4, 6);
        Assert.True(m.Scroll > 0);
        m.GoTo("hlpFlight");
        Assert.Equal("hlpFlight", m.CurrentTopic);
        Assert.Equal(2, m.HistoryDepth);
        Assert.Equal(0, m.Scroll);         // topic change resets the page
    }

    [Fact]
    public void Back_pops_the_history_and_is_a_noop_at_the_root()
    {
        // Native OnButtonBack (help.cpp:270-277).
        var m = new HelpManualModel();
        m.Back();
        Assert.Equal("hlpStart", m.CurrentTopic);
        m.GoTo("hlpCombat");
        m.GoTo("hlpCommand");
        m.Back();
        Assert.Equal("hlpCombat", m.CurrentTopic);
        m.Back();
        Assert.Equal("hlpStart", m.CurrentTopic);
        Assert.Equal(1, m.HistoryDepth);
    }

    [Fact]
    public void Topic_selection_wraps_and_the_controls_topic_exists()
    {
        var m = new HelpManualModel();
        m.MoveTopicSelection(-1);   // wrap backwards from START → COMMAND
        Assert.Equal("hlpCommand", m.CurrentTopic);
        m.MoveTopicSelection(+1);
        m.MoveTopicSelection(+1);   // START → CONTROLS
        Assert.Equal("hlpControls", m.CurrentTopic);
        // The generated key reference lives inside the manual as the Controls
        // topic (native helpdata's controls pages).
        Assert.Contains(HelpManualModel.Topics, t => t.Id == "hlpControls");
        // Every topic has content and pure-ASCII lines (the HUD spritefonts).
        foreach (var t in HelpManualModel.Topics)
        {
            Assert.NotEmpty(t.Lines);
            foreach (var line in t.Lines)
                foreach (var ch in line)
                    Assert.InRange(ch, '\u0020', '\u007e');
        }
    }

    [Fact]
    public void Page_scroll_clamps_to_the_page_length()
    {
        var m = new HelpManualModel();
        m.GoTo("hlpFlight");
        m.ScrollBy(-100, 14);
        Assert.Equal(0, m.Scroll);
        m.ScrollBy(1000, 14);
        Assert.Equal(Math.Max(0, m.Current.Lines.Count - 14), m.Scroll);
    }

    // ── IntroScreenModel: the native button set ────────────────────────────

    [Fact]
    public void Intro_menu_has_the_native_replay_intro_and_help_buttons()
    {
        // Native introButtonPane (introscreen.cpp:1224, OnButtonIntro :1865 →
        // ScreenIDSplashScreen) and helpButtonPane (:1226, OnButtonHelp :1806).
        Assert.Equal("Replay Intro", IntroScreenModel.MainLabels[6]);
        Assert.Equal(IntroCommand.ReplayIntro, IntroScreenModel.Activate(6));
        Assert.Equal("Help", IntroScreenModel.MainLabels[7]);
        Assert.Equal(IntroCommand.Help, IntroScreenModel.Activate(7));
        Assert.Equal("Exit", IntroScreenModel.MainLabels[8]);
        Assert.Equal(IntroCommand.Exit, IntroScreenModel.Activate(8));
    }

    // ── Splash timeout (splashscreen.cpp:34-46) ────────────────────────────

    [Fact]
    public void Splash_timeout_is_the_native_quickstart_value()
    {
        // The released native arms OnTimeout at 0.01f because the
        // `g_bQuickstart || true` REVIEW makes the quickstart branch
        // unconditional (the 6.85f branch is dead).
        Assert.Equal(0.01, Game1.SplashTimeoutSeconds, 3);
    }

    // ── Find Server list rules (introscreen.cpp:824-995) ───────────────────

    private static Discovery.DiscoveredServer Server(string name, int maxPlayers, string host = "10.0.0.1", int port = 2326)
        => new(name, host, port, Discovery.DiscoverySource.Lan, 0, "map", 0, maxPlayers, "", "");

    [Fact]
    public void FindServer_hides_single_player_and_zone_cap_games()
    {
        // Native: only nMaxPlayers > 1 && nMaxPlayers < 1000 are listed
        // (introscreen.cpp:979-983). The port's 0 = unknown/unbounded passes.
        Assert.False(IntroFindServerList.PassesMaxPlayerRule(1));    // single player
        Assert.False(IntroFindServerList.PassesMaxPlayerRule(0 + 1000));  // Zone cap
        Assert.True(IntroFindServerList.PassesMaxPlayerRule(2));
        Assert.True(IntroFindServerList.PassesMaxPlayerRule(50));
        Assert.True(IntroFindServerList.PassesMaxPlayerRule(999));
        Assert.True(IntroFindServerList.PassesMaxPlayerRule(0));     // unknown cap
    }

    [Fact]
    public void FindServer_dedups_enumeration_results_and_sorts_by_game_name()
    {
        // Native FindGames: DPlay8 EnumHosts duplicates are stripped
        // (introscreen.cpp:903-931) and the list is a SortedList by game name
        // (:841).
        var list = IntroFindServerList.FilterAndSort(new[]
        {
            Server("zeta game", 16, host: "10.0.0.9"),
            Server("alpha game", 8),
            Server("alpha game", 8, "10.0.0.2"),        // same name, different host: kept
            Server("alpha game", 8),                    // exact duplicate: dropped
            Server("solo session", 1),                  // hidden
            Server("zone server", 5000),                // hidden
        });
        Assert.Equal(3, list.Count);
        Assert.Equal("alpha game", list[0].Name);
        Assert.Equal("10.0.0.1", list[0].Address);      // stable tie order
        Assert.Equal("10.0.0.2", list[1].Address);
        Assert.Equal("zeta game", list[2].Name);
    }
}

using Allegiance.ClientCore.Browser.Pages;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>
/// P1070 — the Hangar state's HTML chrome gate. Once the hangar and the full ship loadout
/// are drawn and driven inside the game window, the HTML Launch/status/reason controls are
/// developer/proof only: absent for a player (no <c>?dev=1</c>), present unchanged for the
/// existing harnesses.
/// </summary>
public sealed class P1070DevGateTests
{
    [Fact]
    public void A_player_visit_renders_no_html_launch_controls_and_a_dev_visit_keeps_them()
    {
        Assert.False(BrowserHangarChrome.HtmlLaunchControls(dev: false));
        Assert.True(BrowserHangarChrome.HtmlLaunchControls(dev: true));
    }

    [Fact]
    public void The_hangar_state_says_where_the_ship_is_instead_of_showing_a_button()
    {
        Assert.Contains("game window", BrowserHangarChrome.CanvasNotice);
        Assert.DoesNotContain("launch", BrowserHangarChrome.CanvasNotice, StringComparison.OrdinalIgnoreCase);
    }
}

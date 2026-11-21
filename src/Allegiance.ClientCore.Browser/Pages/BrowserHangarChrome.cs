namespace Allegiance.ClientCore.Browser.Pages;

/// <summary>
/// P1070 — the browser Hangar state's chrome rule. Once the hangar and the full ship
/// loadout are drawn and driven inside the game window, the HTML launch controls are a
/// developer/proof surface only (the same switch the flight toolbar's proof controls
/// already use, <c>wwwroot/dev-switch.js</c> / P1059).
///
/// <para>Default off: a player in the Hangar state sees the game window and nothing to
/// click. <c>?dev=1</c> keeps <c>#online-launch</c>, <c>#launch-status</c> and
/// <c>#launch-reason</c> exactly as they are, so every existing harness
/// (P1051/P1058/P1065/P1066/P1067 all open <c>/play/menu.html?dev=1</c>) keeps working.
/// This type is pure (no Blazor type) so the rule is unit-tested without a browser
/// runtime.</para>
/// </summary>
public static class BrowserHangarChrome
{
    /// <summary>Whether the Hangar state renders its HTML launch button, busy status and
    /// refusal reason. Only the developer flag turns them on.</summary>
    public static bool HtmlLaunchControls(bool dev) => dev;

    /// <summary>What a player sees in the Hangar state instead: a one-line pointer at
    /// the game window, carrying no launch control.</summary>
    public const string CanvasNotice = "Your ship's hangar and loadout are in the game window.";
}

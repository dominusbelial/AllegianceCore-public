using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P1070 — the one wording of a blocked launch, shared by the browser's HTML launch
/// control and by the hangar screen drawn in the game window. Both ask
/// <see cref="HangarLaunch.Of"/> and then word the blocker with this, so the two
/// surfaces can never disagree about why the launch cannot start.
///
/// The texts are the ones the HTML launch control has carried since P1058
/// (<c>src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs</c>, <c>#launch-reason</c>);
/// this type only moves them next to the rule so the game window can use them.
/// </summary>
public static class HangarLaunchText
{
    public static string For(HangarLaunch.Blocker blocker) => blocker switch
    {
        HangarLaunch.Blocker.NoSession => "Not connected to the server. Return to the list to join again.",
        HangarLaunch.Blocker.Reconnecting => "Reconnecting to the server…",
        HangarLaunch.Blocker.NoShipAssigned => "Waiting for the server to assign your ship…",
        _ => "The launch cannot start yet.",
    };

    /// <summary>The busy line while a launch is on its way — the same wording the
    /// HTML panel's <c>#launch-status</c> shows (<c>Menu.Online.cs</c>
    /// <c>BusyStatus</c>), minus the countdown the page keeps to itself.</summary>
    public const string WaitingForServer = "Ready. Waiting for the server to start the mission…";
}

using Allegiance.ClientCore.MonoGame;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P571 — the P540 eject/pod autopilot must NOT survive a dock: entering the
/// docked hangar tears it down so a freshly launched hull never resumes
/// steering to the garrison after death (native DockWithStationEvent →
/// c_ssDocked, trekigc.cpp:4146 — the flight/goto interface, incl. the eject
/// autopilot at appmsg.cpp:235, is left behind). The rule is exposed as a pure
/// static predicate so the dock-transition invariant is headlessly testable;
/// Game1.HandleDockedScreen applies it (DisengageAutopilot("docked")) each time
/// the own ship enters the hangar. The end-to-end relaunch-after-death path
/// (die → pod autopilots home → dock → launch a new hull, no autopilot) is
/// proven in the live real-window proof.
/// </summary>
public class P571AutopilotDockClearTests
{
    /// <summary>⭐ P571 — once the own ship is docked, the pod autopilot must
    /// NOT stay engaged (the relaunched hull would fly back to the station).</summary>
    [Fact]
    public void Docked_own_ship_must_not_keep_the_pod_autopilot()
    {
        Assert.False(Game1.EjectAutopilotStaysEngaged(ownShipDocked: true));
    }

    /// <summary>⭐ P571 — while still an UNDOCKED pod in flight the autopilot
    /// stays (P540 home-steering preserved); only docking clears it.</summary>
    [Fact]
    public void Undocked_pod_keeps_the_autopilot()
    {
        Assert.True(Game1.EjectAutopilotStaysEngaged(ownShipDocked: false));
    }
}

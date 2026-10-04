using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P541 — the starmap left-click decision, made pure so the exact native
/// branch order (WinTrek/sectormap.cpp:1521-1571) is pinned by tests. Native's
/// <c>SectorMapPane::Button</c> on left-button-down over a cluster
/// (<c>GetClusterAtPoint</c>) plays the click sound, <c>SelectCluster</c>s it,
/// then picks exactly one branch:
/// <list type="number">
/// <item><b>ripcord</b> — if the player is ripcording and not already on
/// autopilot, set the ripcord target cluster (<c>RequestRipcord</c>,
/// sectormap.cpp:1527-1530).</item>
/// <item><b>teleport</b> — else if the teleport pane is open, try to teleport
/// to a station in the selected cluster (<c>AttemptTeleportTo</c>,
/// sectormap.cpp:1531-1536).</item>
/// <item><b>view</b> — else if docked (in a station, own cluster is null),
/// switch the view sector (<c>RequestViewCluster</c> + <c>vmCommand</c>,
/// sectormap.cpp:1539-1545).</item>
/// <item><b>none</b> — else if it is the ship's current cluster, do nothing
/// ("users find anything else confusing", sectormap.cpp:1546-1549).</item>
/// <item><b>goto</b> — else issue a goto order to the ship for that sector
/// (the <c>c_buoyCluster</c> buoy path, sectormap.cpp:1550-1567).</item>
/// </list>
/// A click on empty map (no cluster found) is <see cref="StarmapClickAction.None"/>
/// (native would start a drag, which this port has no use for).
/// </summary>
public static class StarmapClick
{
    /// <summary>Which of native's left-click branches a click on a cluster takes.</summary>
    public enum Action { None, Ripcord, Teleport, ViewCluster, Goto }

    /// <summary>
    /// Decide the left-click branch for a clicked cluster, exactly in native's
    /// order (sectormap.cpp:1521-1571). Pure so the five-way decision is
    /// unit-testable without a graphics device or a session.
    /// </summary>
    /// <param name="clusterFound">Whether the click hit a cluster at all (native
    /// <c>pClusterFound != NULL</c>).</param>
    /// <param name="ripcordActive">Whether the player's ship is ripcording
    /// (native <c>fRipcordActive()</c>).</param>
    /// <param name="autopilotEngaged">Whether the player is already on autopilot
    /// (native <c>GetAutopilot()</c>) — a ripcording pilot on autopilot falls
    /// through to the other branches.</param>
    /// <param name="teleportPaneOpen">Whether the teleport pane is open (native
    /// <c>ofTeleportPane</c> overlay flag).</param>
    /// <param name="docked">Whether the player is docked in a station (native
    /// <c>GetShip()-&gt;GetCluster() == NULL</c>).</param>
    /// <param name="isCurrentCluster">Whether the clicked cluster is the ship's
    /// current sector (native <c>pClusterFound == GetShip()-&gt;GetCluster()</c>).</param>
    public static Action Decide(
        bool clusterFound,
        bool ripcordActive, bool autopilotEngaged,
        bool teleportPaneOpen,
        bool docked,
        bool isCurrentCluster)
    {
        if (!clusterFound)
            return Action.None;
        if (ripcordActive && !autopilotEngaged)
            return Action.Ripcord;
        if (teleportPaneOpen)
            return Action.Teleport;
        if (docked)
            return Action.ViewCluster;
        if (isCurrentCluster)
            return Action.None;
        return Action.Goto;
    }

    /// <summary>
    /// The native cluster-goto buoy for the "other-sector" branch
    /// (sectormap.cpp:1553-1557): <c>position = (0,0,0)</c>,
    /// <c>clusterID</c> = the clicked sector, <c>type = c_buoyCluster</c> —
    /// byte-for-byte the <see cref="DataBuoyIGC"/> <c>IssueStarmapGoto</c> sends
    /// as a <c>FM_CS_CHATBUOY(108)</c> addressed to the player's own ship.
    /// Extracted here (rather than built inline) so the \"other-sector branch
    /// produces the native buoy\" requirement is pinned by a unit test.
    /// </summary>
    public static DataBuoyIGC BuildClusterGotoBuoy(short clusterId) => new(
        Position: [0f, 0f, 0f],
        ClusterId: clusterId,
        Type: DataBuoyIGC.CBuoyCluster,
        BuoyId: 0,
        Visible: false);
}

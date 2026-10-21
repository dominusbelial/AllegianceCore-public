namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P752 — pure, headless-testable port of native's starmap hover behaviour
/// (<c>SectorMapPane::MouseMove</c>, <c>WinTrek/sectormap.cpp:1413-1461</c>).
/// Native tracks two clusters on the map:
/// <list type="bullet">
/// <item><c>m_pClusterMouseOver</c> — the cluster under the pointer (or null),
/// reported to the info pane via <c>SelectMouseOverCluster</c>. The pane draws
/// the cluster's warning text only while this is non-null
/// (sectormap.cpp:517-534).</item>
/// <item><c>m_pClusterSel</c> — the pane's selected cluster. Hovering a cluster
/// <em>selects</em> it (<c>SelectCluster(pClusterFound)</c>); hovering empty
/// map space reverts the selection to the CHAT cluster
/// (<c>SelectCluster(trekClient.GetChatCluster())</c> — the viewed cluster, else
/// the ship's own cluster, else the docked station's cluster); the pointer
/// leaving the map entirely fires no event, so the selection is untouched.</item>
/// </list>
/// The port's keyboard cycle (<c>SelectSectorPrev/Next</c>) is an additive
/// navigation aid on top of the same selected-cluster state; this class owns
/// only the native mouse rule so it can be pinned by unit tests without a
/// graphics device.
/// </summary>
public static class StarmapHover
{
    /// <summary>
    /// Apply native's per-pointer-move rule and return the resulting
    /// (<c>selected</c>, <c>mouseOver</c>, <c>hoverCue</c>) triple.
    /// <list type="bullet">
    /// <item>Pointer inside the map over a cluster: the cluster is selected AND
    /// is the mouse-over cluster (the info pane follows the pointer).</item>
    /// <item>Pointer inside the map over no cluster: the selection reverts to
    /// the CHAT cluster (native <c>GetChatCluster()</c>, sectormap.cpp:1446 —
    /// not the own ship's sector; null keeps the current selection when the
    /// chat cluster is unknown) and the mouse-over cluster is null.</item>
    /// <item>Pointer outside the map: nothing changes except the mouse-over
    /// cluster, which is null (native fires no MouseMove there).</item>
    /// </list>
    /// <para><b>P764 — <paramref name="hoverCue"/></b> mirrors native
    /// <c>if (m_pClusterSel != pClusterFound) trekClient.PlaySoundEffect(mouseoverSound)</c>
    /// (sectormap.cpp:1440-1443): true exactly when the pointer is inside the
    /// map over a cluster that is NOT the current selection — a NEW cluster is
    /// being hovered. Hovering the same cluster again, empty map space and
    /// off-map moves never cue.</para>
    /// </summary>
    public static (short? Selected, short? MouseOver, bool HoverCue) Move(
        bool pointerInsideMap, short? hitCluster, short? chatCluster, short? currentSelected)
    {
        if (!pointerInsideMap)
            return (currentSelected, null, false);
        if (hitCluster is { } hit)
            return (hit, hit, hit != currentSelected);
        return (chatCluster ?? currentSelected, null, false);
    }
}

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P764 — pure, headless-testable port of native's starmap teleport attempt
/// (<c>SectorMapPane::AttemptTeleportTo</c>, <c>WinTrek/sectormap.cpp:1355-1390</c>),
/// the action behind a cluster click while the teleport-pane overlay is up
/// (sectormap.cpp:1531-1536). Native's branch order, verbatim:
/// <list type="number">
/// <item>the ship is not docked (<c>GetShip()-&gt;GetStation() == NULL</c>) —
/// a race condition, native does nothing (sectormap.cpp:1359-1361);</item>
/// <item>the docked station's cluster IS the clicked one —
/// "You are already in this cluster." + <c>errorSound</c>
/// (sectormap.cpp:1362-1364, SIM-0328);</item>
/// <item>the clicked cluster has no friendly station —
/// "You do not have an appropriate station in this cluster." +
/// <c>errorSound</c> (sectormap.cpp:1365-1368, SIM-0329);</item>
/// <item>otherwise the teleport: the DOCKED fm for the target station
/// (sectormap.cpp:1372-1388).</item>
/// </list>
/// </summary>
public static class StarmapTeleport
{
    /// <summary>Native's four AttemptTeleportTo outcomes.</summary>
    public enum OutcomeKind { NotDocked, AlreadyInCluster, NoStation, DockRequest }

    /// <summary>One attempt's decision: the outcome plus, for the error
    /// branches, the exact native feedback line (sectormap.cpp:1362/1366).</summary>
    public readonly record struct Outcome(OutcomeKind Kind, string? Message)
    {
        /// <summary>Whether native plays <c>errorSound</c> for this outcome
        /// (the two feedback branches only, sectormap.cpp:1362-1368).</summary>
        public bool PlaysError => Kind is OutcomeKind.AlreadyInCluster or OutcomeKind.NoStation;
    }

    /// <summary>
    /// Decide the AttemptTeleportTo branches (sectormap.cpp:1355-1390).
    /// </summary>
    /// <param name="dockedStationId">The local ship's station id, or -1 when
    /// not docked (native <c>GetShip()-&gt;GetStation()</c>).</param>
    /// <param name="stationClusterId">The docked station's cluster id, or -1
    /// when unknown (native <c>GetStation()-&gt;GetCluster()</c>).</param>
    /// <param name="clickedCluster">The clicked cluster's id.</param>
    /// <param name="friendlyStationId">A friendly station in the clicked
    /// cluster (native <c>FindFriendlyStation(pcluster, GetSide())</c>,
    /// sectormap.cpp:1357), or -1 when the cluster has none.</param>
    public static Outcome Decide(
        short dockedStationId, short stationClusterId, short clickedCluster, short friendlyStationId)
    {
        if (dockedStationId < 0)
            return new Outcome(OutcomeKind.NotDocked, null);          // race condition — nothing
        if (stationClusterId == clickedCluster)
            return new Outcome(OutcomeKind.AlreadyInCluster, "You are already in this cluster.");
        if (friendlyStationId < 0)
            return new Outcome(OutcomeKind.NoStation, "You do not have an appropriate station in this cluster.");
        return new Outcome(OutcomeKind.DockRequest, null);
    }
}

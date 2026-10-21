using Allegiance.Core.Wire;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    private readonly HashSet<short> _highlightedClusters = new();
    public IReadOnlySet<short> HighlightedClusters => _highlightedClusters;
    public short? ViewedClusterId { get; private set; }
    public Vector3? ViewedClusterPosition { get; private set; }
    public long ViewClusterVersion { get; private set; }

    // Native clintlib/appmsg.cpp:443-455. Viewing changes the scene, never MyShipId.
    private void Apply(ViewClusterReply message)
    {
        if (!_clusters.ContainsKey(message.ClusterId)) return;
        ViewedClusterId = message.ClusterId;
        ViewedClusterPosition = message.BUsePosition ? message.Position : null;
        ViewClusterVersion++;
    }

    // Native clintlib/appmsg.cpp:1749-1758; sectormap.cpp:1044. Unknown sectors
    // are ignored instead of dereferencing native's unchecked null cluster.
    private void Apply(HighlightCluster message)
    {
        if (!_clusters.ContainsKey(message.ClusterId)) return;
        if (message.Highlight)
        {
            _highlightedClusters.Add(message.ClusterId);
            RecordSoundEvent(ClientSoundEventKind.SectorHighlighted, message.ClusterId);
        }
        else _highlightedClusters.Remove(message.ClusterId);
    }

    // Native clintlib/appmsg.cpp:824-849; Igc/shipIGC.cpp:3509-3593.
    // Preserve each player's identity, transfer the hull/loadout/pose and all
    // remaining children, then demote the old pilot into the vacated seat.
    private void Apply(Promote message)
    {
        if (!_ships.TryGetValue(message.ShipidPromoted, out var promoted)
            || promoted.ParentId < 0
            || !_ships.TryGetValue(promoted.ParentId, out var parent)) return;
        MarkSnapshotDirty();
        promoted.ParentId = -1;
        promoted.HullId = parent.HullId;
        promoted.Loadout = parent.Loadout;
        promoted.Cargo = parent.Cargo;
        promoted.SectorId = parent.SectorId;
        promoted.StatusSectorKnown = parent.StatusSectorKnown;   // HUD-18
        promoted.StationId = parent.StationId;
        promoted.State = parent.State;
        promoted.IsDead = false;
        promoted.IsGone = false;
        if (parent.Latest is { } pose)
        {
            promoted.Latest = ServerShipUpdate.FromValues(promoted.ShipId, pose.StateM,
                pose.Controls, pose.Time, pose.Position, pose.Orientation, pose.Velocity,
                pose.TurnRates, pose.HullFraction, pose.ShieldFraction, pose.AmmoFraction,
                pose.FuelFraction, pose.EnergyFraction, pose.OreFraction, pose.Power);
            promoted.LastUpdateTick = parent.LastUpdateTick;
            promoted.LatestArrivalTime = parent.LatestArrivalTime;
        }
        foreach (var child in _ships.Values)
            if (child.ParentId == parent.ShipId) child.ParentId = promoted.ShipId;
        parent.ParentId = promoted.ShipId;
        parent.State = ShipStatusMessage.StateTurret;
        parent.Loadout = null;
        parent.Cargo = null;
        ReseedRemoteReckoner(promoted);
        // P508 resets the local simulation on the next update before reading
        // the new HUD gauges or sending flight controls from the promoted hull.
        if (promoted.IsMine) promoted.HasPendingReset = true;
        if (parent.IsMine) PostSystemLine("You have been demoted to a turret gunner");
        else if (promoted.IsMine) PostSystemLine("You have been promoted to pilot");
    }
}

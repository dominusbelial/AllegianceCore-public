using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private void BroadcastGeneratedTreasure(Treasure treasure)
    {
        // Sector IDs may repeat across games. Route by mission as well as sector.
        byte[] packet = FedPacket.Pack([new TreasureSpawn { Data = treasure.Data }.Encode()]);
        foreach (var (connection, live) in _peerShips)
            if (ReferenceEquals(live.Ship.Mission, treasure.OwnerMission) && live.Sector == treasure.OwnerSector)
            {
                SendPeer(connection, packet);
                // P797 — latch the discovery mark: the sweep must never re-send a
                // treasure this creation broadcast already delivered.
                _crossSideDiscoverySent.Add((connection, (byte)IgcObjectType.Treasure, treasure.Id));
            }
    }

    private void SendTreasureSnapshot(ConnectionId connection, Sector sector)
    {
        // Native exports the original trajectory on sector entry
        // (FedSrv/fsmission.cpp:4224-4231; Igc/treasureIGC.cpp:268-277).
        // P797 — per side: only the treasures THIS peer's side has seen
        // (fsmission.cpp:4228 `if (ptlink->data()->SeenBySide(pside))`). The
        // snapshot deliberately does NOT consult the discovery latch: the client
        // clears its whole treasure table on every SET_CLUSTER (ClientWorld's
        // Apply(SetCluster) models native's per-cluster object delete), so this
        // post-SET_CLUSTER snapshot is the ONLY way a re-entering peer re-learns
        // its sector's treasures — the latch below is written, never read here.
        // An unseated peer (no side) keeps the pre-P797 whole-sector behaviour —
        // it has no per-side view to honour yet.
        short? side = _peerShips.TryGetValue(connection, out var live) ? live.Ship.SideId : null;
        var treasures = sector.Models.OfType<Treasure>()
            .Where(t => side is not { } s || t.SeenBySide(s))
            .ToArray();
        if (treasures.Length == 0) return;
        // Only a moving late export needs an immediate clock sample. Static
        // ambient treasure must not inject samples into own-ship clock calibration.
        if (treasures.Any(t => t.Data.V0 is { } velocity && velocity.Any(v => v != 0)))
            SendPeer(connection, FedPacket.Pack([new Ping { TimeServer = (uint)(_simNow * 1000) }.Encode()]));
        foreach (var treasure in treasures)
        {
            SendPeer(connection, FedPacket.Pack([new TreasureSpawn { Data = treasure.Data }.Encode()]));
            if (side is not null)
                _crossSideDiscoverySent.Add((connection, (byte)IgcObjectType.Treasure, treasure.Id));
        }
    }
}

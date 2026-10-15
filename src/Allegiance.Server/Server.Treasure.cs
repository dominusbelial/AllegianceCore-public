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
                SendPeer(connection, packet);
    }

    private void SendTreasureSnapshot(ConnectionId connection, Sector sector)
    {
        // Native exports the original trajectory on sector entry
        // (FedSrv/fsmission.cpp:4224-4231; Igc/treasureIGC.cpp:268-277).
        var treasures = sector.Models.OfType<Treasure>().ToArray();
        if (treasures.Length == 0) return;
        // Only a moving late export needs an immediate clock sample. Static
        // ambient treasure must not inject samples into own-ship clock calibration.
        if (treasures.Any(t => t.Data.V0 is { } velocity && velocity.Any(v => v != 0)))
            SendPeer(connection, FedPacket.Pack([new Ping { TimeServer = (uint)(_simNow * 1000) }.Encode()]));
        foreach (var treasure in treasures)
            SendPeer(connection, FedPacket.Pack([new TreasureSpawn { Data = treasure.Data }.Encode()]));
    }
}

using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private void BroadcastShipWarnings()
    {
        // Small owner-only refresh also initializes late joiners and clears
        // dock/eject/respawn state without retaining a connection-id cache.
        foreach (var (peer, live) in _peerShips)
        {
            var ship = live.Ship;
            var mask = ship.StationId is null && ship.ParentShip is null && !ship.Dead
                ? ship.WarningMask : ShipWarningMask.None;
            SendPeer(peer, FedPacket.Pack(new ShipWarningUpdate { ShipId = ship.Id, Mask = mask }.Encode()));
        }
    }
}

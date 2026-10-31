using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>Embedded host lifetime ends with the player. Native removes players with
    /// QSR_ServerShutdown before destruction (FedSrv/fsmission.cpp:281-283).</summary>
    public void EndLanHost()
    {
        if (!RequireLanListeners || _shutdown) return;
        CompleteBroadcastSnapshot();
        FlushBroadcastSnapshots();
        foreach (var peer in _peerShips.ToArray())
        {
            SendPeer(peer.Key, FedPacket.Pack([new QuitMission
            {
                ShipId = peer.Value.Ship.Id, Reason = QuitSideReason.ServerShutdown,
                Message = "The host ended the LAN game."
            }.Encode()]));
            AfterQueuedSends(() => _host.Transport!.ClosePeerAfterFlush(peer.Key));
        }
    }
}

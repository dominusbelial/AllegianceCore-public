using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    // FedSrv.CPP:6749-6818: self quit or leader boot of a human teammate.
    // Steam moderator identity/bans and defection-side boots require native
    // infrastructure absent here; do not grant those privileges to an owner.
    private void HandleQuitMission(ConnectionId from, QuitMission request)
    {
        if (!_peerShips.TryGetValue(from, out var sender)
            || sender.Ship.Mission is not { } mission) return;
        var target = _peerShips.FirstOrDefault(kv => kv.Value.Ship.Id == request.ShipId
            && ReferenceEquals(kv.Value.Ship.Mission, mission));
        if (target.Value is null) return;
        bool self = target.Key == from;
        if (request.Reason != (self ? QuitSideReason.Quit : QuitSideReason.LeaderBooted)) return;
        if (!self && (sender.Ship.SideId is not { } side
            || target.Value.Ship.SideId != side
            || mission.Sides.FirstOrDefault(s => s.Id == side)?.LeaderShipId != sender.Ship.Id)) return;

        // ReleasePeer removes the recipient from the mission before broadcasting
        // to survivors. Deliver the same guaranteed notice to the leaver first.
        SendPeer(target.Key, FedPacket.Pack([new QuitMission
        { ShipId = request.ShipId, Reason = request.Reason }.Encode()]));
        ReleasePeerWithReason(target.Key, request.Reason);
    }
}

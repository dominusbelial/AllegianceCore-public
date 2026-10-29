using Allegiance.Core.Sim;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    // FedSrv/FedSrv.CPP:489-515: NA means first; unknown and Over refuse on the wire.
    private Mission? ResolveJoinMission(uint cookie) => cookie == uint.MaxValue
        ? _missions.FirstOrDefault() : _missions.FirstOrDefault(m => m.Cookie == cookie);

    private bool JoinMission(ConnectionId peer, uint cookie, short shipId, out Mission? mission, string? password = null)
    {
        mission = ResolveJoinMission(cookie);
        var reason = mission is null ? DelPositionReqReason.NoMission
            : mission.Stage == MissionStage.Over ? DelPositionReqReason.ServerPaused : (DelPositionReqReason?)null;
        // Native password gate follows mission lookup (FedSrv.CPP:648-661;
        // _Utility/Utility.h:1389-1392 uses case-sensitive lstrcmpA).
        if (reason is null && !string.IsNullOrEmpty(ParamsOf(mission!).StrGamePassword)
            && !string.Equals(ParamsOf(mission!).StrGamePassword, password ?? "", StringComparison.Ordinal))
            reason = DelPositionReqReason.BadPassword;
        if (reason is null) return true;
        SendPeer(peer, FedPacket.Pack([new DelPositionReq
        { ShipId = shipId, SideId = -1, Reason = reason.Value }.Encode()]));
        return false;
    }

    private Mission? ConnectionMission(ConnectionId peer) =>
        _peerShips.TryGetValue(peer, out var live) ? live.Ship.Mission
        : _pendingSideSelect.TryGetValue(peer, out var pending) ? pending.Mission : null;
}

using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly HashSet<ConnectionId> _botAuthenticated = [];
    private readonly Queue<Mission> _retiredMissions = new();
    private void DisposeRetiredMissions()
    {
        while (_retiredMissions.TryDequeue(out var mission)) mission.Dispose();
    }

    // FedSrv/FedSrv.CPP:7690-7715. Authentication is a separate role:
    // neither a character-name prefix nor admin/harness privileges imply it.
    private void HandleSetMissionOwner(ConnectionId from, SetMissionOwner request)
    {
        if (!_peerShips.TryGetValue(from, out var live) || live.Ship.Mission is not { } mission) return;
        if (!_botAuthenticated.Contains(from))
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id, "isBot == true", "FedSrv.CPP:7700");
            return;
        }
        if (live.Ship.SideId is not (>= 0 and < CSidesMax))
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id, "sid >= 0 && sid < c_cSidesMax", "FedSrv.CPP:7704");
            return;
        }
        var target = mission.GetSide(request.SideId);
        if (target is null || request.SideId < 0 || request.SideId >= CSidesMax || target.LeaderShipId is not { } leader) return;
        mission.MissionOwnerSideId = request.SideId;
        // CFSMission::SetOwner uses the target's actual leader, ignoring shipID.
        BroadcastSetMissionOwner(mission, request.SideId, leader);
        MarkLobbyDirty("mission-owner");
    }
}

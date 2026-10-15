using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    // Native CheckPositionRequest: FedSrv/fsmission.cpp:4575-4649.
    // Unported invitations, rank, bans and squads have no corresponding state.
    private DelPositionReqReason? CheckPositionRequest(Mission mission, short sideId, Ship? ship)
        => CheckPositionRequestCore(mission, sideId, ship,
            ship is not null && ConnOf(ship) is { } conn && HarnessGrantFor(conn) is not null);

    private DelPositionReqReason? CheckInitialPositionRequest(ConnectionId from, string? name, Mission mission, short sideId)
        => CheckPositionRequestCore(mission, sideId, null, IsHarnessPeer(from, name));

    private DelPositionReqReason? CheckPositionRequestCore(Mission mission, short sideId, Ship? ship, bool authenticatedBot)
    {
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null || !side.Active) return DelPositionReqReason.SideGone;
        if (mission.Stage == MissionStage.Started && ship?.SideId is not null && ship.SideId != sideId)
            return DelPositionReqReason.NoDefections;
        if (mission.LockSides) return DelPositionReqReason.SidesLocked;
        if (mission.Stage == MissionStage.Over) return DelPositionReqReason.ServerPaused;
        int index = _missions.IndexOf(mission);
        var game = index >= 0 && index < _config.Games.Count ? _config.Games[index] : new GameConfig();
        int Count(short sid) => _peerShips.Values.Count(l => ReferenceEquals(l.Ship.Mission, mission)
            && !ReferenceEquals(l.Ship, ship) && l.Ship.SideId == sid);
        int count = Count(sideId);
        if (count >= Math.Max(1, game.MaxPlayersPerTeam)) return DelPositionReqReason.TeamFull;
        if (mission.Stage != MissionStage.NotStarted)
        {
            if (!mission.Sectors.Any(s => s.Stations.Any(st => st.SideId == sideId))) return DelPositionReqReason.NoBase;
            // Native bots bypass the imbalance gate using the server's bot
            // authentication GUID (FedSrv/fsmission.cpp:4637-4642). Our equivalent
            // requires authenticated harness provenance, never a name alone.
            if (!authenticatedBot && game.MaxTeamImbalance != 32767 && mission.Sides.Where(s => s.Active)
                .Any(s => (long)count >= (long)Count(s.Id) + Math.Max(1, game.MaxTeamImbalance)))
                return DelPositionReqReason.TeamBalance;
        }
        return null;
    }
}

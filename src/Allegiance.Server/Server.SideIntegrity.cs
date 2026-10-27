using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    // Native CheckPositionRequest: FedSrv/fsmission.cpp:4575-4649.
    // The P854 invitation gate (fsmission.cpp:4605) and squads match are the parts
    // that consult player identity (callsign + active squads), so those thread
    // name/squads into the core check; the rest has no player state beyond the ship.
    private DelPositionReqReason? CheckPositionRequest(Mission mission, short sideId, Ship? ship)
        => CheckPositionRequestCore(mission, sideId, ship,
            ship is not null && ConnOf(ship) is { } conn && HarnessGrantFor(conn) is not null,
            ship is not null ? ConnOf(ship) : null, null);

    private DelPositionReqReason? CheckInitialPositionRequest(ConnectionId from, string? name, Mission mission, short sideId)
        => CheckPositionRequestCore(mission, sideId, null, IsHarnessPeer(from, name), from, name);

    /// <summary>P854 — a player's callsign for the invitation match (native
    /// <c>pfsPlayer-&gt;GetName()</c>, fsside.cpp:55): the logon name when given,
    /// else the peer's recorded name. Unknown → empty string, which no callsign
    /// entry matches (native: a name-less player matches no invitation).</summary>
    private string PlayerInvitationName(ConnectionId? peer, string? name)
        => name ?? (peer is { } c && _peerNames.TryGetValue(c, out var n) ? n ?? "" : "");

    /// <summary>P854 — a player's active squad names for the squad-game invitation
    /// match (native iterates <c>pfsPlayer-&gt;GetSquadMembershipList()</c>,
    /// fsside.cpp:40-51). Anonymous/bots have none → empty (no squad entry matches).</summary>
    private IReadOnlyList<string> PlayerInvitationSquads(ConnectionId? conn)
        => conn is { } c && PeerSquads(c) is { } sq
            ? sq.Select(s => s.Name ?? "").ToArray()
            : Array.Empty<string>();

    private DelPositionReqReason? CheckPositionRequestCore(Mission mission, short sideId, Ship? ship, bool authenticatedBot,
        ConnectionId? peer, string? name)
    {
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null || !side.Active) return DelPositionReqReason.SideGone;
        var game = GameSettingsSafe(mission);
        // Native's two no-defections gates (fsmission.cpp:4583-4590) both
        // carry an explicit SIDE_TEAMLOBBY exemption (branch 1: lastSide !=
        // TEAMLOBBY; branch 2: psideCurrent != TEAMLOBBY) — a player parked in
        // the team lobby (the port's SideTeamLobby park, P852) may always
        // request a side. Only the current-side branch (4587-4589) is portable:
        // the port keeps no GetLastSide() state (fsmission.cpp:4584-4585's
        // last-side branch is unportable — stated in the P852 report).
        if (mission.Stage == MissionStage.Started && !game.AllowDefections
            && ship?.SideId is { } currentSide && currentSide != SideTeamLobby && currentSide != sideId)
            return DelPositionReqReason.NoDefections;
        if (mission.LockSides) return DelPositionReqReason.SidesLocked;
        if (mission.Stage == MissionStage.Over) return DelPositionReqReason.ServerPaused;
        // P854 — native per-side invitation gate (fsmission.cpp:4605): in a private
        // mission a player may only join a side that invited them; anything else is
        // refused with DPR_PrivateGame. Runs before the capacity/balance checks, as
        // native does (4605 precedes nNumPlayers). Exact native placement after the
        // banned-side check (4598-4602); the port has no banned mask here.
        if (mission.RequiresInvitation && !mission.IsSideInvited(sideId, PlayerInvitationName(peer, name), PlayerInvitationSquads(peer)))
            return DelPositionReqReason.PrivateGame;
        int Count(short sid) => _peerShips.Values.Count(l => ReferenceEquals(l.Ship.Mission, mission)
            && !ReferenceEquals(l.Ship, ship) && l.Ship.SideId == sid);
        int count = Count(sideId);
        // Native checks loss of base before capacity (fsmission.cpp:4626-4635).
        if (mission.Stage != MissionStage.NotStarted &&
            !mission.Sectors.Any(s => s.Stations.Any(st => st.SideId == sideId)))
            return DelPositionReqReason.NoBase;
        if (count >= game.MaxPlayersPerTeam) return DelPositionReqReason.TeamFull;
        if (mission.Stage != MissionStage.NotStarted)
        {
            var active = mission.Sides.Where(s => s.Active).ToArray();
            long maxPlayers = game.MaxPlayersPerTeam;
            if (game.MaxTeamImbalance != 0x7fff)
                foreach (var other in active)
                    maxPlayers = Math.Min(maxPlayers, (long)Count(other.Id) + game.MaxTeamImbalance);
            int rank = _peerShips.Values.FirstOrDefault(l => ReferenceEquals(l.Ship, ship))?.PlayerScore.Rank ?? 0;
            // Native allows two extra low-rank players (default MaxNewbRank=4,
            // FedSrv.H:246); authenticated bots bypass this numeric gate only
            // (fsmission.cpp:4637-4642), never capacity or rank balance.
            if (count >= maxPlayers && !(rank <= 4 && count < maxPlayers + 2) && !authenticatedBot)
                return DelPositionReqReason.TeamBalance;
            if (game.MaxTeamImbalance == 0x7ffe)
            {
                // GetSideRankSum floors each player's rank at one, including
                // bots (fsmission.cpp:1352-1369). Rank mode accepts only a side
                // with the lowest current sum (fsmission.cpp:4646-4671).
                long RankSum(short sid) => _peerShips.Values
                    .Where(l => ReferenceEquals(l.Ship.Mission, mission) && !ReferenceEquals(l.Ship, ship) && l.Ship.SideId == sid)
                    .Sum(l => (long)Math.Max(1, (int)l.PlayerScore.Rank));
                if (active.Any(other => RankSum(sideId) > RankSum(other.Id)))
                    return DelPositionReqReason.TeamBalance;
            }
        }
        // P852 — native's squad-game join gates (fsmission.cpp:4708-4721),
        // evaluated after the capacity/balance gates: an OCCUPIED side only
        // accepts members of the side's squad (DPR_WrongSquad; a side with no
        // squad accepts everyone — native GetIsMemberOfSquad(NA) is true,
        // fsship.cpp:1359-1362), and an EMPTY side only accepts a player who
        // can lead a squad (DPR_CantLeadSquad; native
        // GetPreferredSquadToLead() == NA, fsship.cpp:1397-1417 — leader or
        // assistant-leader of some squad; the port collapses ASL to CanLead,
        // P820b's claim shape). Only gated on mission.SquadGame, exactly like
        // every other squad rule (P820c).
        if (mission.SquadGame)
        {
            if (Count(sideId) > 0)
            {
                if (ship is not null && ConnOf(ship) is { } memberPeer && !PeerIsMemberOfSquad(memberPeer, side.SquadId))
                    return DelPositionReqReason.WrongSquad;
            }
            else if (peer is not { } squadPeer || PeerSquads(squadPeer)?.Any(s => s.CanLead) != true)
                return DelPositionReqReason.CantLeadSquad;
        }
        return null;
    }

    // Native RequestPosition replies to the requester only with the reason
    // from CheckPositionRequest (FedSrv/fsmission.cpp:4734-4747).
    private void RefuseSideSelection(ConnectionId peer, short shipId, short sideId, DelPositionReqReason reason, string? name = null)
    {
        SendPeer(peer, FedPacket.Pack([new DelPositionReq { ShipId = shipId, SideId = sideId, Reason = reason }.Encode()]));
        Console.WriteLine($"[sideselect] refused peer {peer} '{Allegiance.Core.ConsoleLog.Sanitize(name ?? _peerNames.GetValueOrDefault(peer))}' ship {shipId} side {sideId}: {reason}");
    }
}

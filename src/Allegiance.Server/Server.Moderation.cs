using Allegiance.Core;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>
    /// P762 — FM_CS_QUIT_MISSION(124), the port of the native handler at
    /// <c>FedSrv/FedSrv.CPP:6749-6818</c>.
    /// <para><b>Moderators.</b> Native grants the Steam moderator/developer
    /// group the right to remove <em>any</em> player with a reason and a
    /// message (<c>:6751-6753,6769-6785</c>). The port's equivalent identity is
    /// <see cref="ServerConfig.ModeratorAccounts"/>; a matching sender takes
    /// that branch before the self/leader checks.</para>
    /// <para><b>Ordinary players.</b> Only a player quitting themselves or a
    /// side leader booting a teammate on their own side is accepted
    /// (<c>:6788-6801</c>). The reason must match the operation exactly
    /// (<c>TrapHackBoot</c>, <c>:6797</c>): <see cref="QuitSideReason.Quit"/>
    /// for self, <see cref="QuitSideReason.LeaderBooted"/> for a boot. A boot is
    /// refused while <see cref="GameConfig.LockTeamSettings"/> is set
    /// (<c>:6802</c>).</para>
    /// </summary>
    private void HandleQuitMission(ConnectionId from, QuitMission request)
    {
        if (!_peerShips.TryGetValue(from, out var sender)
            || sender.Ship.Mission is not { } mission) return;
        var target = _peerShips.FirstOrDefault(kv => kv.Value.Ship.Id == request.ShipId
            && ReferenceEquals(kv.Value.Ship.Mission, mission));
        if (target.Value is null) return;
        bool self = target.Key == from;

        // ── moderator/administrator branch (native Steam group) ────────────
        if (IsModerator(from))
        {
            SendPeer(target.Key, FedPacket.Pack([new QuitMission
            {
                ShipId = request.ShipId,
                Reason = request.Reason,
                Message = request.Message,
            }.Encode()]));
            Console.WriteLine($"[moderation] moderator {from} removed ship {target.Value.Ship.Id} " +
                              $"reason {request.Reason} message '{ConsoleLog.Sanitize(request.Message, 128)}'");
            ReleasePeerWithReason(target.Key, request.Reason);
            return;
        }

        // Native TrapHackBoot (:6797): only the exact reason for the operation.
        if (request.Reason != (self ? QuitSideReason.Quit : QuitSideReason.LeaderBooted))
        {
            ReportTrapHack(TrapHackKind.Boot, from, sender.Ship.Id, "pfmQuitMission->reason == (bIsBoot ? QSR_LeaderBooted : QSR_Quit)", "FedSrv.CPP:6799");
            return;
        }
        if (!self)
        {
            if (sender.Ship.SideId is not { } side
                || target.Value.Ship.SideId != side
                || mission.Sides.FirstOrDefault(s => s.Id == side)?.LeaderShipId != sender.Ship.Id) return;
            // Native :6802 — a locked team's settings forbid leader boots.
            if (GameSettings(mission).LockTeamSettings)
            {
                Console.WriteLine($"[moderation] leader boot of ship {target.Value.Ship.Id} refused — team settings locked");
                return;
            }
        }

        // ReleasePeer removes the recipient from the mission before broadcasting
        // to survivors. Deliver the same guaranteed notice to the leaver first.
        SendPeer(target.Key, FedPacket.Pack([new QuitMission
        { ShipId = request.ShipId, Reason = request.Reason, Message = request.Message }.Encode()]));
        ReleasePeerWithReason(target.Key, request.Reason);
    }

    /// <summary>
    /// Whether this connection presented a configured moderator secret.
    /// <para>Native's Steam group membership is keyed by the player's CD key
    /// (<c>CSteamUserGroupStatus::IsMemberOfModeratorsGroup(pfsPlayer-&gt;GetCDKey())</c>,
    /// <c>FedSrv.CPP:6751-6752</c>), which ASGS/Steam authenticates. This port
    /// has no account service: LOGONREQ carries a self-asserted character name,
    /// CD key and Steam ID, and <c>SteamAuthTicket</c> is not validated, so
    /// matching any of those identity <em>claims</em> would let any client grant
    /// itself moderator rights by claiming the name/id. Instead the configured
    /// entries are treated as <b>bearer secrets</b>, exactly the model P655
    /// already uses for the admin-cheat and test-harness grants: the peer must
    /// present the secret in <c>LOGONREQ.CDKey</c> and the comparison is
    /// fixed-time. Character names are never consulted.</para>
    /// <para>Operators who need per-person moderator identities should front
    /// the server with an authenticating lobby/ASGS and issue each moderator a
    /// distinct random secret; a plain character name is not a credential.</para>
    /// </summary>
    private bool IsModerator(ConnectionId from)
    {
        if (_config.ModeratorAccounts.Length == 0) return false;
        if (!_peerRequests.TryGetValue(from, out var req)) return false;
        string presented = req.CDKey ?? "";
        if (presented.Length == 0) return false;
        foreach (var account in _config.ModeratorAccounts)
        {
            if (string.IsNullOrWhiteSpace(account)) continue;
            if (FixedTimeEquals(account, presented)) return true;
        }
        return false;
    }
}

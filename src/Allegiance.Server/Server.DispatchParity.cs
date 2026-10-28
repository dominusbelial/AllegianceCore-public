using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>The non-ballot chat controls in OnAppMessage
    /// (FedSrv/FedSrv.CPP:5127-5246). True consumes the chat; false lets the
    /// normal relay run, including the native public echo of settings changes.</summary>
    private bool TryHandleLobbyChatCommand(ConnectionId from, Ship ship, ChatMessage chat)
    {
        if (ship.Mission is not { } mission || chat.MessageIsAbsent) return false;
        string text = chat.Message;
        // FedSrv.CPP:5127-5166: only the owner can ban, only from the team
        // lobby, case-insensitive exact name. Swallow the command even if no
        // matching lobby player remains. Steam moderation is a separate path.
        if (IsMissionOwnerPlayer(ship, mission) && text.StartsWith("#ban ", StringComparison.OrdinalIgnoreCase))
        {
            var target = _peerShips.FirstOrDefault(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                && kv.Value.Ship.SideId is < 0
                && _peerNames.TryGetValue(kv.Key, out var name)
                && string.Equals(name, text[5..], StringComparison.OrdinalIgnoreCase));
            if (target.Value is not null)
            {
                SendPeer(target.Key, FedPacket.Pack([new QuitMission
                { ShipId = target.Value.Ship.Id, Reason = QuitSideReason.OwnerBooted }.Encode()]));
                ReleasePeerWithReason(target.Key, QuitSideReason.OwnerBooted);
            }
            return true;
        }
        if (mission.Stage != MissionStage.Started || ship.SideId is not >= 0) return false;
        var side = mission.Sides.FirstOrDefault(s => s.Id == ship.SideId);
        if (side is null) return false;

        // FedSrv.CPP:5198-5204: unlike AUTO_ACCEPT, the chat control is usable
        // after launch. It forwards the original chat and then the mission def.
        if (side.LeaderShipId == ship.Id && IsBallotCommand(text, "#autoaccept"))
        {
            side.AutoAccept = !side.AutoAccept;
            if (side.AutoAccept) SweepAutoAcceptPending(mission, side.Id);
            SendMissionDef(mission);
        }
        // FedSrv.CPP:5207-5223: match the four prefixes explicitly; preserve
        // the current setting for an unrecognised parameter. Always publicise.
        else if (IsMissionOwnerPlayer(ship, mission)
            && text.Length >= "#autobalance  ".Length && IsBallotCommand(text, "#autobalance"))
        {
            int? value = IsBallotCommand(text, "#autobalance 1") ? 1
                : IsBallotCommand(text, "#autobalance 2") ? 2
                : IsBallotCommand(text, "#autobalance a") ? 0x7ffe
                : IsBallotCommand(text, "#autobalance n") ? 0x7fff : null;
            if (value is { } imbalance)
            {
                GameSettings(mission).MaxTeamImbalance = imbalance;
                ParamsOf(mission).IMaxImbalance = (short)imbalance;
            }
            chat.ChatTarget = (int)ChatTarget.Everyone;
            chat.OidRecipient = -1;
            SendMissionDef(mission);
        }
        // FedSrv.CPP:5227-5233: one private help reply, no public command echo.
        else if (IsBallotCommand(text, "#list"))
        {
            SendBallotReply(from, ship.Id, "#draw | #resign | #autoaccept | #autobalance [1|2|a|n] | #ban | #mutiny");
            return true;
        }
        return false;
    }

    // FlushSides (fsmission.cpp:5069-5119) lives in Server.Lobby.cs (P864b), shared with RebalanceSides.
}

using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>FedSrv/FedSrv.CPP:13652-13693 routes IIgcSite.SendChat through
    /// ForwardChatMessage. Scope by the sender's mission before selecting a group;
    /// no changes to broadcast scheduling or the player-chat dispatch.</summary>
    private void SendSiteChat(Ship? sender, ChatMessage message)
    {
        Mission? mission = sender?.Mission ?? (_missions.Count == 1 ? _missions[0] : null);
        if (mission is null) return;
        var peers = _peerShips.Where(p => ReferenceEquals(p.Value.Ship.Mission, mission));
        short? side = sender?.SideId;
        switch ((ChatTarget)message.ChatTarget)
        {
            case ChatTarget.Everyone: break;
            case ChatTarget.Team:
                // P760 — team chat with a command is only forwarded when the
                // sender belongs to that team (or there is no sender)
                // (FedSrv.CPP:3519-3526).
                if (message.CommandID != OrderChange.CidNone && side is not null && side != message.OidRecipient) return;
                peers = peers.Where(p => p.Value.Ship.SideId == message.OidRecipient); break;
            case ChatTarget.Group:
            case ChatTarget.GroupNoEcho:
                message.ChatTarget = (int)ChatTarget.Individual;
                goto case ChatTarget.Individual;
            case ChatTarget.Individual:
            case ChatTarget.IndividualNoFilter:
                // P760 — individual command chats are only delivered to same-side
                // or allied recipients (or from a null sender)
                // (FedSrv.CPP:3558-3562).
                if (message.CommandID != OrderChange.CidNone && side is not null)
                {
                    var recipientSide = peers.FirstOrDefault(p => p.Value.Ship.Id == message.OidRecipient).Value?.Ship.SideId;
                    if (recipientSide is not null && recipientSide != side && !AlliedSides(mission, side, recipientSide.Value)) return;
                }
                peers = peers.Where(p => p.Value.Ship.Id == message.OidRecipient); break;
            case ChatTarget.AllSector:
                peers = peers.Where(p => p.Value.Sector.Id == message.OidRecipient); break;
            case ChatTarget.FriendlySector:
                peers = peers.Where(p => p.Value.Sector.Id == message.OidRecipient && p.Value.Ship.SideId == side); break;
            case ChatTarget.Wing:
                peers = peers.Where(p => p.Value.Ship.SideId == side && p.Value.Ship.WingId == message.OidRecipient); break;
            case ChatTarget.Leaders:
                peers = peers.Where(p => mission.Sides.Any(s => s.LeaderShipId == p.Value.Ship.Id)); break;
            case ChatTarget.Allies:
                // P760 — send to the named side plus every side allied with it
                // (FedSrv.CPP:3429-3449). Commands are not forwarded to allies.
                if (message.CommandID != OrderChange.CidNone) return;
                peers = peers.Where(p => mission.Sides.Any(s => s.Id == message.OidRecipient
                    && (s.Id == p.Value.Ship.SideId
                        || (s.Id != p.Value.Ship.SideId && AlliedSides(mission, s.Id, p.Value.Ship.SideId))))); break;
            case ChatTarget.Ship:
                // P760 — send to the source ship (carrier when in a turret) and
                // its child ships (FedSrv.CPP:3481-3510).
                // P760b — the sender is NOT echoed (see ShipChatRecipients).
                {
                    var source = sender?.ParentShip ?? sender;
                    if (source is null) return;
                    var ids = ShipChatRecipients(sender, source);
                    peers = peers.Where(p => ids.Contains(p.Value.Ship.Id));
                }
                break;
            case ChatTarget.Admin:
                // P760 — already dropped at the ServerHost.SendChat site callback
                // (FedSrv.CPP:3473-3477); no peers reach this switch.
                return;
            default: return;
        }
        byte[] packet = FedPacket.Pack(new[] { message.Encode() });
        foreach (var peer in peers) SendPeer(peer.Key, packet);
    }

    /// <summary>P760b/SIM-0173 — the CHAT_SHIP recipient set, the exact filter
    /// <see cref="SendSiteChat"/> applies for <see cref="ChatTarget"/>.
    /// Ship: native forwards to the source ship only when it DIFFERS from the
    /// sender's own ship (FedSrv.CPP:3488-3493 — a gunner's chat reaches the
    /// carrier) and to each child ship EXCEPT the sender (FedSrv.CPP:3496-3505);
    /// the sender never receives its own CHAT_SHIP echo. Internal so the
    /// no-echo rule is pinned by a test.</summary>
    internal static HashSet<short> ShipChatRecipients(Ship? sender, Ship source)
    {
        var ids = new HashSet<short>();
        if (!ReferenceEquals(source, sender)) ids.Add(source.Id);
        foreach (var child in source.ChildShips)
            if (!ReferenceEquals(child, sender)) ids.Add(child.Id);
        return ids;
    }
}

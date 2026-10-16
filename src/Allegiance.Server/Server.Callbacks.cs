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
                peers = peers.Where(p => p.Value.Ship.SideId == message.OidRecipient); break;
            case ChatTarget.Group:
            case ChatTarget.GroupNoEcho:
                message.ChatTarget = (int)ChatTarget.Individual;
                goto case ChatTarget.Individual;
            case ChatTarget.Individual:
            case ChatTarget.IndividualNoFilter:
                peers = peers.Where(p => p.Value.Ship.Id == message.OidRecipient); break;
            case ChatTarget.AllSector:
                peers = peers.Where(p => p.Value.Sector.Id == message.OidRecipient); break;
            case ChatTarget.FriendlySector:
                peers = peers.Where(p => p.Value.Sector.Id == message.OidRecipient && p.Value.Ship.SideId == side); break;
            case ChatTarget.Wing:
                peers = peers.Where(p => p.Value.Ship.SideId == side && p.Value.Ship.WingId == message.OidRecipient); break;
            case ChatTarget.Leaders:
                peers = peers.Where(p => mission.Sides.Any(s => s.LeaderShipId == p.Value.Ship.Id)); break;
            default: return;
        }
        byte[] packet = FedPacket.Pack(new[] { message.Encode() });
        foreach (var peer in peers) SendPeer(peer.Key, packet);
    }
}

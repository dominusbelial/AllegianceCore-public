using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    // WinTrek/trekigc.cpp:3019-3067: activation is critical for every informed side;
    // destruction is critical only for the owner/allies. The native client's
    // asset-mask cache has no separate managed equivalent; probe presence is
    // already represented by ClientWorld's live probe set.
    // Use the existing feedback transport; no broadcast scheduling changes.
    private void BroadcastTeleportProbeNotice(Probe probe, bool active)
    {
        if (probe.OwnerMission is not { } mission) return;
        string sectorName = probe.OwnerSector.Name;
        string name = probe.ProbeType.LauncherName;
        string ownerName = mission.Sides.FirstOrDefault(s => s.Id == probe.SideId)?.Name ?? "";
        foreach (var (connection, peer) in _peerShips)
        {
            if (!ReferenceEquals(peer.Ship.Mission, mission)) continue;
            // Native notifications run on clients that know this probe, not
            // clients whose side has never discovered it (probeigc.cpp:153,
            // FedSrv/FedSrv.CPP:10750-10778). Ownership is always known.
            if (peer.Ship.SideId != probe.SideId &&
                (peer.Ship.SideId is not { } seeingSide || !probe.SeenBySide(seeingSide))) continue;
            bool friendly = probe.SideId == peer.Ship.SideId ||
                probe.SideId is { } ownerSide && peer.Ship.SideId is { } viewerSide &&
                (mission.AlliedSidePairs.Contains((ownerSide, viewerSide)) || mission.AlliedSidePairs.Contains((viewerSide, ownerSide)) ||
                 mission.Sides.Any(s => s.Id == ownerSide && s.AllyGroup is { } group &&
                     mission.Sides.Any(v => v.Id == viewerSide && v.AllyGroup == group)));
            var message = new ChatMessage
            {
                SidSender = -1, ChatTarget = (int)ChatTarget.Individual, OidRecipient = peer.Ship.Id,
                // Critical/noncritical native notification sounds are already
                // resolved by the client's feedback cue planner.
                VoiceOver = active || friendly ? NotificationCue.Critical : NotificationCue.NonCritical,
                CommandID = OrderChange.CidNone, OidTarget = -1, OtTarget = -1,
                Message = active
                    ? $"{(friendly ? "" : ownerName + " ")}{name} active in {sectorName}"
                    : $"{(friendly ? "" : ownerName + " ")}{name} in {sectorName} was destroyed"
            };
            SendPeer(connection, FedPacket.Pack([message.Encode()]));
        }
    }
}

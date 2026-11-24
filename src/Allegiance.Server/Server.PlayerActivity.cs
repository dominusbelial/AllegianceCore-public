using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    // Native distribution checks the PLAYER's GetReady(), not the team's ready
    // or force-ready aggregate (FedSrv/fsmission.cpp:5228-5241). Unknown retains
    // the existing opt-in auto-seat behavior until the client reports a value.
    private bool IsExplicitlyAfk(ConnectionId peer) => _playerReady.TryGetValue(peer, out bool ready) && !ready;
    private bool TimedAutoSeatEnabled => _config.SideSelectAutoSeatSeconds is { } seconds && seconds > 0 && double.IsFinite(seconds);

    private void HandlePlayerReady(ConnectionId from, PlayerReady ready)
    {
        var mission = ConnectionMission(from);
        if (mission is null) return;
        short shipId;
        if (_peerShips.TryGetValue(from, out var live)) shipId = live.Ship.Id;
        else if (_pendingSideSelect.TryGetValue(from, out var pending)) shipId = pending.ShipId;
        else return;
        if (!TryChargeChatBudget(from)) return;

        // Sender owns only its own ready flag. Pending side-select peers are
        // mission members too; native relays to GetGroupMission(), including
        // NOAT (FedSrv/FedSrv.CPP:6990-7026). P281 defers their ship creation.
        ready.ShipId = shipId;
        _playerReady[from] = ready.FReady;
        var packet = FedPacket.Pack([ready.Encode()]);
        foreach (var peer in _peerShips.Where(p => ReferenceEquals(p.Value.Ship.Mission, mission)).Select(p => p.Key)
            .Concat(_pendingSideSelect.Where(p => ReferenceEquals(p.Value.Mission, mission)).Select(p => p.Key)))
            SendPeer(peer, packet);
        Console.WriteLine($"[ready] {from} ship {shipId} fReady={ready.FReady}");
        if (live is not null) RecomputeSideReady(live.Ship);
    }
}

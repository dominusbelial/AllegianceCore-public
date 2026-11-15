using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private void PromotePassengerOnSideDeparture(Ship ship)
    {
        if (ship.ChildShips.FirstOrDefault() is not { } passenger) return;
        _loadoutCommandDepth++;
        try { passenger.Promote(allowObserver: true); }
        finally { _loadoutCommandDepth--; }

        // RemovePlayerFromSide, fsmission.cpp:904-921: promote before QUIT_SIDE,
        // to the flying sector or, in a station, the old side.
        var message = new Promote { ShipidPromoted = passenger.Id }.Encode();
        if (ship.Sector is { } sector && ship.StationId is null)
            SendSectorWide(sector, message);
        else
        {
            var packet = FedPacket.Pack([message]);
            foreach (var peer in _peerShips)
                if (peer.Value.Ship.Mission == ship.Mission && peer.Value.Ship.SideId == ship.SideId)
                    SendPeer(peer.Key, packet);
        }
    }

    private void DockSideJoiner(Ship ship, Mission mission, Side side)
    {
        // GetBase selects the first owned restart station, not an enemy or
        // an arbitrary station (FedSrv/fsmission.cpp:1433-1447,791-796).
        var home = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .FirstOrDefault(s => !s.Dead && s.SideId == side.Id
                && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        if (home is null) return;
        var sector = mission.Sectors.First(s => s.StationRuntimes.Contains(home));
        _loadoutCommandDepth++;
        try
        {
            if (mission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var pod))
                ship.ChangeHull(pod); // CFSShip::SetSide, fsship.cpp:155-158
        }
        finally { _loadoutCommandDepth--; }
        MoveShipToSector(ship, sector, announceExit: false);
        ship.Position = home.Center;
        ship.Velocity = Vector3.Zero;
        ship.StationId = home.StationId;
        ship.LastDockTime = _simNow;
        ship.StateM = 0;
        UpdatePeerSectorSnapshot(ship, sector);
        if (ConnOf(ship) is not { } conn || LiveOf(ship) is not { } live) return;
        SendSideWorldSnapshot(conn, mission, ship, sector, PeerName(ship) ?? "");
        SendPeer(conn, FedPacket.Pack([
            new BuyLoadoutAck { HullId = ship.HullType.HullId, FBoughtEverything = false,
                FLaunch = false, Parts = ship.ExportInventoryForAck() }.Encode(),
            new DockedNotify { StationId = home.StationId }.Encode(),
            MakeOwnShipStatus(ship.Id, live).Encode(),
        ]));
        SendStatusDumpToJoiner(conn, mission, ship.Id, live);
        if (mission.Stage == MissionStage.Started)
            SendPeer(conn, FedPacket.Pack([new EnterGame().Encode()]));
    }
}

using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>Complete the native wire observation after Sector's real build. Previously only
    /// late joiners learned the station, and the consumed rock/builder remained ghosts.
    /// FedSrv.CPP:11431-11462,12025; completion chat buildingeffectigc.h:319-321.</summary>
    private void OnStationBuilt(Asteroid rock, Ship builder, Station station)
    {
        if (station.Mission is not { } mission) return;
        var destruction = new AsteroidDestroyed { AsteroidId = rock.Data.AsteroidId,
            ClusterId = rock.Data.ClusterId, ExplodeF = false }.Encode();
        var consumed = new ShipDelete { ShipId = builder.Id, Sdr = ShipDelete.SdrTerminate }.Encode();
        var completion = FedPacket.Pack(new[] { destruction, ExportStation.FromStation(station.Data).Encode(), consumed });
        var retirement = FedPacket.Pack(new[] { destruction, consumed });
        foreach (var peer in _peerShips)
            if (ReferenceEquals(peer.Value.Ship.Mission, mission))
                SendPeer(peer.Key, peer.Value.Ship.SideId == station.SideId ? completion : retirement);
        BroadcastTeamChat(mission, station.SideId, station.Type.CompletionSound, $"Finished building {station.Type.Name}");
    }

    /// <summary>P703 read-only identity sync. Native CFSDrone owns the type/base data
    /// (FedSrv.CPP:2220-2236); the port's player-shaped NPC exports omit it.</summary>
    private static ResearchProduction ProductionIdentity(Ship ship) => new()
    {
        ShipId = ship.Id, SideId = ship.SideId ?? -1, DroneTypeId = ship.DroneTypeId,
        StationTypeId = ship.BuilderStationType?.StationTypeId ?? -1,
    };

    private void BroadcastResearchProduction(Mission mission, Ship ship)
    {
        if (!mission.ResearchPlans.ContainsKey(ship.SideId ?? -1)) return;
        var packet = FedPacket.Pack(ProductionIdentity(ship).Encode());
        foreach (var peer in _peerShips)
            if (ReferenceEquals(peer.Value.Ship.Mission, mission) && peer.Value.Ship.SideId == ship.SideId)
                SendPeer(peer.Key, packet);
    }
}

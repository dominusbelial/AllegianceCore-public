using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    // Native ShowObject exports a newly seen predictable object to the seeing
    // side (FedSrv/FedSrv.CPP:10750-10778,10945-10949). Health-only station
    // updates cannot instantiate a station built after a client's map export.
    private readonly HashSet<(ConnectionId Recipient, Mission Mission, short StationId, short TypeId)> _stationIdentitySent = [];

    private void ExportDiscoveredStations(short sideId, IReadOnlyList<ConnectionId> peers, Station[] stations)
    {
        _stationIdentitySent.RemoveWhere(entry => !_peerShips.TryGetValue(entry.Recipient, out var peer)
            || !ReferenceEquals(peer.Ship.Mission, entry.Mission));
        foreach (var station in stations)
        {
            if (station.Mission is not { Stage: MissionStage.Starting or MissionStage.Started } mission) continue;
            byte[]? packet = null;
            int recipients = 0;
            foreach (var peer in peers)
                if (_peerShips.TryGetValue(peer, out var live) && ReferenceEquals(live.Ship.Mission, mission)
                    && _stationIdentitySent.Add((peer, mission, station.StationId, station.Data.StationTypeId)))
                {
                    packet ??= FedPacket.Pack(ExportStation.FromStation(station.Data).Encode());
                    SendPeer(peer, packet);
                    recipients++;
                }
            if (recipients > 0)
                Console.WriteLine($"[station-export] side={sideId} station={station.StationId}:{station.Data.Name} sector={station.Data.ClusterId} -> {recipients} newly informed peer(s)");
        }
    }

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
            {
                bool ownSide = peer.Value.Ship.SideId == station.SideId;
                SendPeer(peer.Key, ownSide ? completion : retirement);
                if (ownSide) _stationIdentitySent.Add((peer.Key, mission, station.StationId, station.Data.StationTypeId));
            }
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

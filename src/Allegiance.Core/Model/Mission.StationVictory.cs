using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed record StationVictory(Station Station, Allegiance.Core.Sim.Model? Launcher, bool Captured, bool Built, short OldSide = -1);

public sealed partial class Mission
{
    /// <summary>Station-event recompute and native event-specific victory text.
    /// Capture: FedSrv/fsship.cpp:686-691; kill: FedSrv/FedSrv.CPP:12344-12378;
    /// build: FedSrv/FedSrv.CPP:11509-11515. Called after the world mutation.</summary>
    public void NotifyStationVictory(Station station, Allegiance.Core.Sim.Model? launcher, bool captured = false, bool built = false, short oldSide = -1)
    {
        var (winner, changed) = RecomputeConquestAndTerritory(null);
        if (changed || winner is not null || captured) Site.VictoryByStationsComputed(this, changed, winner,
            new StationVictory(station, launcher, captured, built, oldSide));
    }

    public string StationVictoryReason(Side winner, Station station, Allegiance.Core.Sim.Model? launcher, string? pilotName = null, bool captured = false, bool built = false)
    {
        if (built) return $"{winner.Name} won by out-building their opponents";
        if (captured && launcher is Ship captor)
            return $"{winner.Name} won because {pilotName ?? $"ship{captor.Id}"} captured {station.Data.Name}";
        if (launcher is Ship ship && ship.SideId is { } side && AreSidesFriendly(side, winner.Id))
        {
            string? attacker = Sides.First(s => s.Id == side).Name;
            string? owner = Sides.First(s => s.Id == station.SideId).Name;
            string sector = Sectors.First(s => s.Id == station.ClusterId).Name;
            return $"{attacker} has destroyed {owner}'s {station.Data.Name} in {sector}.";
        }
        string teams = winner.Name + string.Concat(Sides.Where(s => s.Id != winner.Id && AreSidesFriendly(s.Id, winner.Id))
            .Select(s => " & " + s.Name));
        return $"{teams} won by dominating their opponents";
    }

}

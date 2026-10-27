using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    /// <summary>Native PopulateCluster's flag setup (Igc/common.cpp:2895-3001):
    /// two pedestal/flag pairs above and below every Start-capable home station.
    /// Runs once on server assembly, for file and generated maps alike.</summary>
    public void PopulateGoalObjects(double now)
    {
        if (Goals.IsFlagsGame)
        {
            var pedestal = Core.StationTypes.Values.LastOrDefault();
            if (!pedestal.HasCapability(DataStationTypeIGC.SabmPedestal))
                throw new InvalidDataException("flags goal requires the native core's final station type to be a flag pedestal");
            foreach (var sector in Sectors)
            {
                // Snapshot: creating pedestals appends stations to this list.
                foreach (var home in sector.StationRuntimes.Where(s =>
                    s.Type.HasCapability(DataStationTypeIGC.SabmStart)).Reverse().ToArray())
                {
                    foreach (float dz in new[] { 1f, -1f })
                    {
                        var position = home.Center + new Vector3(0, 0, dz * 2000);
                        short id;
                        do { id = GenerateNewStationID(); }
                        while (Sectors.Any(s => s.StationRuntimes.Any(st => st.StationId == id)));
                        var data = new DataStationIGC([position.X, position.Y, position.Z],
                            [1, 0, 0], [0, 0, dz], [0, 0, dz, 0], sector.Id, home.SideId,
                            id, pedestal.StationTypeId, 1, 0, pedestal.Name);
                        PlaceStation(new Station(data, pedestal), sector);
                        var flagPosition = position + new Vector3(0, 0, dz * (50 + pedestal.Radius));
                        var flag = new DataTreasureIGC([flagPosition.X, flagPosition.Y, flagPosition.Z],
                            [0, 0, 0], 10 * 24 * 3600, (uint)(now * 1000), NextObjectId(),
                            home.SideId, sector.Id, 0, TreasureCode.Flag, false);
                        sector.Add(new Treasure(flag.ObjectId, sector, flag, now, this));
                    }
                }
            }
        }

        // Native PopulateCluster creates a neutral artifact in each neutral
        // sector when populating treasures (Igc/common.cpp:3090-3095); loaded
        // sectors with existing treasures skip population (missionigc.cpp:4048-4051).
        if (Goals.IsArtifactsGame)
            foreach (var sector in Sectors.Where(s => !s.Cluster.HomeSector && !s.Models.OfType<Treasure>().Any()))
                GenerateTreasure(now, sector, -2);

        // Native GenerateMission initializes rankings without declaring a
        // winner (Igc/missionigc.cpp:3859-3896,4074-4101). Keep the historical
        // port defaults when neither ranking goal is selected.
        if (Goals.IsConquestGame && Sides.Count > 0)
            foreach (var side in Sides) side.ConquestPercent = (byte)(100 / Sides.Count);
        if (Goals.IsTerritoryGame)
            foreach (var side in Sides)
                side.TerritoryCount = (byte)Sectors.Count(s => s.StationRuntimes.Count > 0
                    && s.StationRuntimes.All(station => station.SideId == side.Id));
    }
}

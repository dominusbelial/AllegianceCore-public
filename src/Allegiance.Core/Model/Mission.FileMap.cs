using Allegiance.Core.MapGen;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    /// <summary>File-map station compatibility (Igc/missionigc.cpp:4011-4049).
    /// Only the original station's local/effect techs join development techs;
    /// other buildings and candidate stations cannot unlock a replacement.</summary>
    public void ReplaceUnavailableStationTypes()
    {
        foreach (var sector in Sectors)
            foreach (var station in sector.StationRuntimes.Reverse().ToArray())
            {
                var side = Sides.FirstOrDefault(s => s.Id == station.SideId);
                if (side is null) continue;
                var original = station.Type;
                var available = side.DevelopmentTechs.Or(new TechTreeBitMask(original.TtbmLocal))
                    .Or(new TechTreeBitMask(original.TtbmEffects));
                if (new TechTreeBitMask(original.TtbmRequired).IsSubsetOf(available)) continue;
                foreach (var candidate in Core.StationTypes.Values)
                    if (candidate.ClassId == original.ClassId
                        && new TechTreeBitMask(candidate.TtbmRequired).IsSubsetOf(available))
                    {
                        station.SetBaseStationType(candidate);
                        sector.MoveStationToEnd(station);
                        break;
                    }
            }
    }

    /// <summary>Native custom-map generation (missionigc.cpp:3977-4055).
    /// Capture independent empty-list gates before PopulateCluster creates flags.
    /// Existing asteroid/treasure populations are preserved, even if below target.</summary>
    public void PopulateFileMap(double now, MapGenParams parameters)
    {
        var emptyTreasures = Sectors.Where(s => !s.Models.OfType<Treasure>().Any()).ToArray();
        ReplaceUnavailableStationTypes();
        PopulateGoalObjects(now, populateArtifacts: false);
        MissionMapGenerator.PopulateFileMapAsteroids(this, parameters);
        foreach (var sector in emptyTreasures)
        {
            bool home = sector.Cluster.HomeSector;
            if (!home && Goals.IsArtifactsGame) GenerateTreasure(now, sector, -2);
            int count = home ? parameters.PlayerSectorTreasures : parameters.NeutralSectorTreasures;
            short set = home ? TreasureParameters.PlayerStartSet : TreasureParameters.NeutralStartSet;
            for (int i = 0; i < count; i++) GenerateTreasure(now, sector, set);
        }
    }
}

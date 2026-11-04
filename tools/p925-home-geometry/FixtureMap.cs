using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;

namespace Allegiance.Tools.P925;

internal static class FixtureMap
{
    // BenchmarkSeed pins server/warp randomness, but file-map top-up currently
    // uses an independent ambient RNG. Pin ONLY this measurement fixture.
    internal static void PinRocks(Mission mission, MissionMap authored)
    {
        var ids = authored.Asteroids.Select(a => a.AsteroidId).ToHashSet();
        foreach (var sector in mission.Sectors)
            foreach (var asteroid in sector.Mineable.Where(a => !ids.Contains(a.Data.AsteroidId)).ToArray())
                sector.RemoveAsteroid(asteroid);
        MissionMapGenerator.PopulateFileMapAsteroids(mission, new MapGenParams
        {
            Seed = 925, Teams = 2,
            PlayerSectorAsteroids = 20, NeutralSectorAsteroids = 20,
            PlayerSectorMineableAsteroids = 2, NeutralSectorMineableAsteroids = 4,
            PlayerSectorSpecialAsteroids = 1, NeutralSectorSpecialAsteroids = 1,
            PlayerSectorTreasures = 0, NeutralSectorTreasures = 5,
        });
    }
}

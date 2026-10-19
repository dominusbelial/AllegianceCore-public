using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class MapGenAsteroidClearanceTests
{
    private static readonly Lazy<StaticCore> Core = new(MapGenTestCore.LoadRepresentative);

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(MapGenType.SingleRing)]
    [InlineData(MapGenType.SnowFlake)]
    [InlineData(MapGenType.HiLo)]
    [InlineData(MapGenType.Grid)]
    [InlineData(MapGenType.Brawl)]
    public void Asteroids_keep_native_clearance_from_existing_sector_models(MapGenType type)
    {
        Require.Artwork();
        var core = Core.Value;
        for (int sample = 0; sample < 4; sample++)
        {
            var map = MissionMapGenerator.Generate(new MapGenParams { Type = type, Teams = 2, CivIds = [1, 3] }, core, 0);
            AssertNativeClearance(core, map, $"{type} sample {sample}");
        }
    }

    internal static void AssertNativeClearance(StaticCore core, MissionMap map, string context)
    {
        foreach (var cluster in map.Clusters)
        {
            var placed = new List<(string Name, Vector3 Position, float Radius)>();
            placed.AddRange(map.Warps.Where(w => w.ClusterId == cluster.ClusterId)
                .Select(w => ($"warp {w.WarpId}", Pos(w.Position!), (float)w.Radius)));
            placed.AddRange(map.Stations.Where(s => s.ClusterId == cluster.ClusterId)
                .Select(s => ($"station {s.StationId}", Pos(s.Position), core.StationTypes[s.StationTypeId].Radius)));
            foreach (var asteroid in map.Asteroids.Where(a => a.ClusterId == cluster.ClusterId))
            {
                var position = Pos(asteroid.Position);
                foreach (var model in placed)
                {
                    // Independent native contract: common.cpp:2805–2831.
                    float separation = Math.Max(550f, asteroid.Radius) + Math.Max(550f, model.Radius);
                    Assert.True((position - model.Position).LengthSquared() >= 2f * separation * separation,
                        $"{context}: asteroid {asteroid.AsteroidId} overlaps native clearance of {model.Name}");
                }
                placed.Add(($"asteroid {asteroid.AsteroidId}", position, asteroid.Radius));
            }
        }
    }

    private static Vector3 Pos(float[] p) => new(p[0], p[1], p[2]);
}

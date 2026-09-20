using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P294 — pure-domain tests (no sockets, no server) for the building-effect
/// surface the MonoGame client reads: the EXPORT(26)/OT_buildingEffect table
/// (<c>ClientWorld.BuildingEffects</c>), its removal by
/// BUILDINGEFFECT_DESTROYED(181) and by completion self-expiry
/// (<see cref="ClientWorld.PruneExpiredBuildingEffects"/>), and the
/// <see cref="BuildingEffectEntity.ProgressFraction"/> reveal. Messages are
/// constructed directly (exactly like the other <c>ClientWorld*Tests</c>) and
/// fed through <see cref="ClientWorld.Apply"/>.
/// </summary>
public class ClientWorldBuildingEffectTests
{
    private static ExportBuildingEffect Export(short asteroid, short cluster,
        float radiusAsteroid, float radiusStation, Vector3 stop) =>
        new()
        {
            Effect = new DataBuildingEffectExport(
                TimeStart: 12u,
                RadiusAsteroid: radiusAsteroid,
                RadiusStation: radiusStation,
                PositionStart: Vector3.Zero,
                PositionStop: stop,
                ClusterId: cluster,
                AsteroidId: asteroid,
                ColorR: 0.2f, ColorG: 0.11f, ColorB: 0.57f, ColorA: 1f),
        };

    [Fact]
    public void ExportBuildingEffect_UpsertsIntoTheBuildingEffectsTable()
    {
        var world = new ClientWorld();

        world.Apply(Export(7, cluster: 3, 100f, 302.5f, new Vector3(1f, 2f, 3f)));
        world.Apply(Export(8, cluster: 3, 90f, 302.5f, new Vector3(4f, 5f, 6f)));

        Assert.Equal(2, world.BuildingEffects.Count);
        Assert.True(world.BuildingEffects.TryGetValue(7, out var be7));
        Assert.Equal((short)7, be7.AsteroidId);
        Assert.Equal((short)3, be7.ClusterId);
        Assert.Equal(302.5f, be7.RadiusStation);
        Assert.Equal(new Vector3(1f, 2f, 3f), be7.PositionStop);
    }

    [Fact]
    public void ExportBuildingEffect_IsUpsertByKey_LastWriteWins()
    {
        var world = new ClientWorld();

        world.Apply(Export(7, cluster: 3, 100f, 302.5f, new Vector3(1f, 2f, 3f)));
        world.Apply(Export(7, cluster: 4, 120f, 850f, new Vector3(9f, 9f, 9f)));

        Assert.Single(world.BuildingEffects);
        Assert.Equal((short)4, world.BuildingEffects[7].ClusterId);
        Assert.Equal(850f, world.BuildingEffects[7].RadiusStation);
    }

    [Fact]
    public void BuildingEffectDestroyed_RemovesTheConstructionFromTheTable()
    {
        var world = new ClientWorld();
        world.Apply(Export(7, cluster: 3, 100f, 302.5f, Vector3.Zero));

        world.Apply(new BuildingEffectDestroyed { AsteroidId = 7 });

        Assert.Empty(world.BuildingEffects);
    }

    [Fact]
    public void ProgressFraction_RampsZeroToOneAcrossBuildDuration()
    {
        var world = new ClientWorld();
        world.Apply(Export(7, cluster: 3, 100f, 302.5f, Vector3.Zero));

        var be = world.BuildingEffects[7];
        long r = be.ReceivedLocalMs;

        Assert.Equal(0f, be.ProgressFraction(r), 3);
        Assert.Equal(0.5f, be.ProgressFraction(r + (long)(BuildingEffect.BuildDuration * 500)), 3);
        Assert.Equal(1f, be.ProgressFraction(r + (long)(BuildingEffect.BuildDuration * 1000)), 3);
        // Past completion clamps at 1 (and the caller prunes on >= 1).
        Assert.Equal(1f, be.ProgressFraction(r + (long)(BuildingEffect.BuildDuration * 2000)), 3);
    }

    [Fact]
    public void PruneExpiredBuildingEffects_RemovesOnlyCompletedBuilds()
    {
        var world = new ClientWorld();
        world.Apply(Export(7, cluster: 3, 100f, 302.5f, Vector3.Zero));
        world.Apply(Export(8, cluster: 3, 90f, 302.5f, new Vector3(1f, 1f, 1f)));

        long r7 = world.BuildingEffects[7].ReceivedLocalMs;
        long r8 = world.BuildingEffects[8].ReceivedLocalMs;
        long mid = Math.Min(r7, r8) + (long)(BuildingEffect.BuildDuration * 500);
        long done = Math.Max(r7, r8) + (long)(BuildingEffect.BuildDuration * 1000) + 1;

        world.PruneExpiredBuildingEffects(mid);
        Assert.Equal(2, world.BuildingEffects.Count);   // neither finished at midpoint

        world.PruneExpiredBuildingEffects(done);
        Assert.Empty(world.BuildingEffects);            // both completed → both gone
    }
}

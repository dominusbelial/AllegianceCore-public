using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P997ConstructionTests
{
    private static readonly Vector3 Start = new(0, 0, 100);
    private static readonly Vector3 Stop = new(100, 0, 0);
    internal static ExportBuildingEffect Effect(short id = 7, short sector = 3) => new()
    {
        Effect = new(0, 50, 100, Start, Stop, sector, id, 1, 0, 0, 1),
    };
    private static DataStationIGC Station(short sector = 3, float x = 100) =>
        DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 8, ClusterId = sector, Position = [x, 0, 0] };

    // Igc/buildingeffectigc.h:19-22,160-225. Literal native reference
    // samples exercise each boundary rather than deriving expectations from At.
    [Theory]
    [InlineData(-1, 0, 100, 1, .5f, 1, false)]
    [InlineData(0, 0, 100, 1, .5f, 1, false)]
    [InlineData(5, 50, 50, 25.5f, .5f, 1, false)]
    [InlineData(9.999, 99.99f, .01f, 49.9951f, .5f, 1, false)]
    [InlineData(10, 100, 0, 50, .5f, 1, false)]
    [InlineData(15, 100, 0, 80, .5f, 1, false)]
    [InlineData(20, 100, 0, 110, .5f, 1, false)]
    [InlineData(25, 100, 0, 110, .75f, 1, false)]
    [InlineData(30, 100, 0, 110, 1, 1, false)]
    [InlineData(35, 100, 0, 105, .5f, .5f, false)]
    [InlineData(39.999, 100, 0, 100.001f, .0001f, .0001f, false)]
    [InlineData(40, 100, 0, 100, 0, 0, true)]
    [InlineData(41, 100, 0, 100, 0, 0, true)]
    public void Native_phase_boundaries_and_interpolated_shell_values(double elapsed, float x, float z,
        float radius, float alpha, float intensity, bool expired)
    {
        var phase = ConstructionPhase.At(elapsed, Start, Stop, 50, 100);
        Assert.Equal(x, phase.Position.X, 3); Assert.Equal(z, phase.Position.Z, 3);
        Assert.Equal(radius, phase.Radius, 3); Assert.Equal(alpha, phase.InnerAlpha, 4);
        Assert.Equal(intensity, phase.InnerIntensity, 4); Assert.Equal(intensity, phase.OuterIntensity, 4);
        Assert.Equal(expired, phase.Expired);
    }

    [Fact]
    public void Radius_envelops_the_larger_asteroid_and_shrinks_to_smaller_station()
    {
        Assert.Equal(220, ConstructionPhase.At(20, Start, Stop, 200, 100).Radius, 3);
        Assert.Equal(160, ConstructionPhase.At(35, Start, Stop, 200, 100).Radius, 3);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Asteroid_retirement_removes_the_visual_in_the_receipt_tick(bool explode)
    {
        var world = new ClientWorld();
        world.Apply(new EnterGame());
        world.Apply(ExportAsteroid.FromAsteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
            { AsteroidId = 7, ClusterId = 3, Position = [100, 0, 0], Radius = 50 }));
        world.Apply(Effect()); Assert.Single(world.ConstructionVisuals);
        world.Apply(new AsteroidDestroyed { AsteroidId = 7, ClusterId = 3, ExplodeF = explode });
        Assert.Empty(world.ConstructionVisuals); Assert.Empty(world.Asteroids);
        Assert.Equal(!explode, world.BuildingEffects.ContainsKey(7)); // MCP's previous semantics
    }

    [Fact]
    public void Retirement_without_cached_rock_still_removes_visual_but_wrong_sector_does_not()
    {
        var world = new ClientWorld(); world.Apply(new EnterGame()); world.Apply(Effect());
        world.Apply(new AsteroidDestroyed { AsteroidId = 7, ClusterId = 4 });
        Assert.Single(world.ConstructionVisuals);
        world.Apply(new AsteroidDestroyed { AsteroidId = 7, ClusterId = 3 });
        Assert.Empty(world.ConstructionVisuals);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Station_handoff_retires_visual_regardless_of_export_order_and_keeps_reservation(bool stationFirst)
    {
        var world = new ClientWorld();
        var station = ExportStation.FromStation(Station());
        if (stationFirst) world.Apply(station);
        world.Apply(Effect());
        if (!stationFirst) { Assert.Single(world.ConstructionVisuals); world.Apply(station); }
        Assert.Empty(world.ConstructionVisuals);
        Assert.Single(world.BuildingEffects);
    }

    [Fact]
    public void Nearby_station_or_identical_position_in_other_sector_does_not_retire_build()
    {
        var world = new ClientWorld(); world.Apply(Effect());
        world.Apply(ExportStation.FromStation(Station(sector: 4)));
        world.Apply(ExportStation.FromStation(Station(x: 101)));
        Assert.Single(world.ConstructionVisuals);
    }

    [Fact]
    public void Interruption_clears_both_visual_and_reservation()
    {
        var world = new ClientWorld(); world.Apply(Effect());
        world.Apply(new BuildingEffectDestroyed { AsteroidId = 7 });
        Assert.Empty(world.ConstructionVisuals); Assert.Empty(world.BuildingEffects);
    }

    [Theory]
    [InlineData("sector")] [InlineData("reset")] [InlineData("over")] [InlineData("stage")]
    public void Scene_boundaries_clear_visuals(string boundary)
    {
        var world = new ClientWorld(); world.Apply(Effect());
        switch (boundary)
        {
            case "sector": world.Apply(new SetCluster { SectorId = 4, Ship = ServerShipUpdate.FromValues(1, 0, default, 0, Vector3.Zero, Orientation.Identity, Vector3.Zero, new float[3], 1, 1, 1, 1, 1, 0, 1) }); break;
            case "over": world.Apply(new GameOver()); break;
            case "stage": world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageOver }); break;
            default: world.Reset(); break;
        }
        Assert.Empty(world.ConstructionVisuals);
        if (boundary is "sector" or "over") Assert.Single(world.BuildingEffects);
    }

    [Fact]
    public void Preassignment_exports_and_returning_to_a_live_build_restore_only_the_destination_shells()
    {
        var world = new ClientWorld(); world.Apply(new EnterGame());
        world.Apply(ExportAsteroid.FromAsteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
            { AsteroidId = 7, ClusterId = 3, Position = [100, 0, 0], Radius = 50 }));
        world.Apply(Effect());
        var ship = ServerShipUpdate.FromValues(1, 0, default, 0, Vector3.Zero, Orientation.Identity,
            Vector3.Zero, new float[3], 1, 1, 1, 1, 1, 0, 1);
        world.Apply(new SetCluster { SectorId = 3, Ship = ship }); Assert.Single(world.ConstructionVisuals);
        world.Apply(new SetCluster { SectorId = 4, Ship = ship }); Assert.Empty(world.ConstructionVisuals);
        world.Apply(new SetCluster { SectorId = 3, Ship = ship }); Assert.Single(world.ConstructionVisuals);
        world.Apply(new AsteroidDestroyed { AsteroidId = 7, ClusterId = 3 });
        world.Apply(new SetCluster { SectorId = 4, Ship = ship });
        world.Apply(new SetCluster { SectorId = 3, Ship = ship }); Assert.Empty(world.ConstructionVisuals);
        Assert.Single(world.BuildingEffects);
    }

    [Fact]
    public void Complete_observer_snapshot_removes_a_departed_build_visual()
    {
        var world = new ClientWorld();
        world.ApplyLocalObservation([Effect()], []);
        Assert.Single(world.ConstructionVisuals);
        world.ApplyLocalObservation([], []);
        Assert.Empty(world.ConstructionVisuals);
        Assert.Single(world.BuildingEffects); // observer refresh does not change reservations
    }

    [Fact]
    public void Late_export_cannot_revive_a_visual_after_game_over()
    {
        var world = new ClientWorld(); world.Apply(Effect()); world.Apply(new GameOver());
        world.Apply(Effect());
        Assert.Empty(world.ConstructionVisuals); Assert.Single(world.BuildingEffects);
        world.Reset(); world.Apply(Effect()); Assert.Single(world.ConstructionVisuals);
    }

    [Fact]
    public void Native_timeout_remains_a_backstop_for_both_collections()
    {
        var world = new ClientWorld(); world.Apply(Effect());
        long received = world.ConstructionVisuals[7].ReceivedLocalMs;
        world.PruneExpiredBuildingEffects(received + 39999); Assert.Single(world.ConstructionVisuals);
        world.PruneExpiredBuildingEffects(received + 40000);
        Assert.Empty(world.ConstructionVisuals); Assert.Empty(world.BuildingEffects);
    }
}

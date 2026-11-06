using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P935TimeConversionTests
{
    // Client simulation epoch is 900 seconds ahead of the server.
    private static OwnShipServerClock Clock()
    {
        var clock = new OwnShipServerClock();
        clock.AddSample(100_000, 1_000_000);
        return clock;
    }

    [Fact]
    public void Both_client_export_formats_convert_the_inner_time_and_roundtrip_the_wire()
    {
        var clock = Clock();
        IIgcSite site = clock;
        Assert.Equal(98, site.ServerTimeFromClientTime(998));
        Assert.Equal(998, site.ClientTimeFromServerTime(98));
        var pilot = ClientShipUpdate.FromLocalPose(site, 998, new(10, 20, 30), Vector3.Zero,
            Orientation.Identity, 0, default, [0, 0, 0], 1);
        var pilotWire = ShipUpdate.Decode(new ShipUpdate { Time = pilot.Time, Cookie = 3, Payload = pilot }.Encode());
        Assert.Equal(98_000u, pilotWire.Time);
        Assert.Equal(98_000u, pilotWire.Payload.Time);
        var gunner = ClientActiveTurretUpdate.FromLocalTime(site, 998, default, Orientation.Identity);
        var gunnerWire = ActiveTurretUpdate.Decode(new ActiveTurretUpdate { TimeUpdate = gunner.Time, Atu = gunner }.Encode());
        Assert.Equal(98_000u, gunnerWire.TimeUpdate);
        Assert.Equal(98_000u, gunnerWire.Atu.Time);
    }

    [Theory]
    [InlineData(1_000_000L)]
    [InlineData(9_000_000_000L)]
    public void Native_export_precision_survives_large_local_epochs(long epoch)
    {
        var clock = new OwnShipServerClock(); clock.AddSample(50, epoch);
        double local = clock.ClientTimeSeconds(epoch + 100);
        var pilot = ClientShipUpdate.FromLocalPose(clock, local, new(1, 0, 0), Vector3.Zero,
            Orientation.Identity, 0, default, [0, 0, 0], 1);
        var turret = ClientActiveTurretUpdate.FromLocalTime(clock, local, default, Orientation.Identity);
        Assert.Equal(150u, pilot.Time); Assert.Equal(150u, turret.Time);
        Assert.Equal(150u, clock.ServerTimeMs(epoch + 100));
        Assert.Equal(1u, IgcTime.ToMilliseconds(.0005));
        Assert.Equal(0u, IgcTime.ToMilliseconds(.00049));
    }

    private static ExportBuildingEffect Building(uint time = 98_000) => new()
    {
        Effect = new(time, 10, 30, Vector3.Zero, new(100, 0, 0), 1, 7, 1, 1, 1, 1),
    };
    private static CreateChaff Chaff(uint time = 98_000) => new()
    { Time0 = time, P0 = new(10, 20, 30), V0 = new(10, 0, 0), Etid = 1 };

    [Fact]
    public void All_native_object_imports_use_the_session_epoch_and_original_creation_instant()
    {
        var world = new ClientWorld { NowSeconds = () => 1000 };
        world.ObserveServerTime(100_000, 1_000_000);
        world.Apply(ExportBuildingEffect.Decode(Building().Encode()));
        world.Apply(CreateChaff.Decode(Chaff().Encode()));
        world.Apply(ExportProbe.Decode(new ExportProbe { Probe = new(Vector3.Zero, 98_000, 2, true, 1, 0, 1, 3, -1, -1, false) }.Encode()));
        world.Apply(ExportMine.Decode(new ExportMine { Mine = new(Vector3.Zero, 98_000, 4, true, 1, 1, 3, 0, 240, false) }.Encode()));
        world.Apply(TreasureSpawn.Decode(new TreasureSpawn
        { Data = new([0, 0, 0], [100, 0, 0], 60, 98_000, 5, 0, 1, 1, TreasureCode.Cash, false) }.Encode()));
        Assert.Equal(998, world.BuildingEffects[7].StartClientSeconds);
        Assert.Equal(.05f, world.BuildingEffects[7].ProgressFraction(1_000_000), 5);
        Assert.Equal(998, world.Chaff[0].StartClientSeconds);
        Assert.Equal(new Vector3(30, 20, 30), world.Chaff[0].PositionAt(1_000_000));
        Assert.Equal(998, world.Probes[2].Time0ClientSeconds);
        Assert.Equal(998, world.Mines[4].Time0ClientSeconds);
        Assert.Equal(180, world.Treasures[5].Position.X, 3);
        Assert.Equal(80, world.Treasures[5].Velocity.X, 3);
        world.PruneExpiredChaff(1_003_000, null);
        Assert.Empty(world.Chaff);
        world.PruneExpiredBuildingEffects(1_038_000);
        Assert.Empty(world.BuildingEffects);
    }

    [Fact]
    public void Exports_before_the_first_clock_sample_bind_when_the_session_epoch_arrives()
    {
        var world = new ClientWorld { NowSeconds = () => 1000 };
        world.Apply(Building());
        world.Apply(Chaff());
        world.Apply(new TreasureSpawn { Data = new([0, 0, 0], [100, 0, 0], 60, 98_000, 5, 0, 1, 1, TreasureCode.Cash, false) });
        world.ObserveServerTime(100_000, 1_000_000);
        Assert.Equal(.05f, world.BuildingEffects[7].ProgressFraction(1_000_000), 5);
        Assert.Equal(new Vector3(30, 20, 30), world.Chaff[0].PositionAt(1_000_000));
        Assert.Equal(180, world.Treasures[5].Position.X, 3);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Probe_and_mine_createNow_only_bypasses_conversion_for_exported_creation(bool export, bool createNow)
    {
        var world = new ClientWorld { NowSeconds = () => 1000 };
        world.ObserveServerTime(100_000, 1_000_000);
        world.Apply(new ExportProbe { Probe = new(Vector3.Zero, 98_000, 2, export, 1, 0, 1, 3, -1, -1, createNow) });
        world.Apply(new ExportMine { Mine = new(Vector3.Zero, 98_000, 4, export, 1, 1, 3, 0, 240, createNow) });
        double expected = export && createNow ? 1000 : 998;
        Assert.Equal(expected, world.Probes[2].Time0ClientSeconds);
        Assert.Equal(expected, world.Mines[4].Time0ClientSeconds);
    }

    [Fact]
    public void Imported_lifetimes_integrate_dilation_pause_and_resume_without_epoch_drift()
    {
        double local = 1000;
        var world = new ClientWorld { NowSeconds = () => local };
        world.ObserveServerTime(100_000, 1_000_000);
        world.Apply(Building()); world.Apply(Chaff());
        local = 1001;
        world.TimeScale = .5;
        local = 1003;
        world.TimeScale = 0;
        Assert.Equal(4, world.Chaff[0].ElapsedSeconds(1_100_000), 6);
        Assert.Equal(.1f, world.BuildingEffects[7].ProgressFraction(1_100_000), 5);
        local = 1100;
        world.TimeScale = 1;
        Assert.Equal(4.5, world.Chaff[0].ElapsedSeconds(1_100_500), 6);
        world.Reset();
        world.NowSeconds = () => 5000;
        world.ObserveServerTime(1000, 5_000_000);
        Assert.Equal(5000, world.TimeSite.ClientTimeFromServerTime(1));
    }

    [Fact]
    public void Server_pose_tick_indices_are_preserved_in_single_heavy_and_turret_records()
    {
        var world = new ClientWorld { NowSeconds = () => 1000 };
        world.ObserveServerTime(100_000, 1_000_000);
        var pose = ServerShipUpdate.FromValues(2, 0, default, 123, new(10, 20, 30),
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 1, 1);
        world.Apply(new SetCluster { SectorId = 1, Ship = pose });
        world.Apply(new SingleShipUpdate { OidTarget = 2, Ship = pose });
        Assert.Equal(123u, world.Ships[2].Latest!.Time);
        world.Apply(new HeavyShipsUpdate { TimeReference = 200, PositionReference = Vector3.Zero,
            HeavyShipEntries = [HeavyShipUpdateEntry.From(200, Vector3.Zero, 3, 0, default, 198,
                new(30, 40, 50), Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1)] });
        Assert.Equal(198u, world.Ships[3].Latest!.Time);
        world.Apply(new ExportShip { ShipId = 4, HullId = 445, SideId = 0 });
        world.Apply(new Allegiance.Core.Wire.LoadoutChange { SidShip = 3, HullId = 16, Parts = [],
            Passengers = [new PassengerData(4, 2)] });
        var turret = ServerActiveTurretUpdate.From(4, 200, 198, default, Orientation.Identity);
        var relay = HeavyShipsUpdate.Decode(new HeavyShipsUpdate { TimeReference = 200,
            HeavyShipEntries = [], ActiveTurretUpdates = [turret] }.Encode());
        world.Apply(relay);
        Assert.Equal(198u, relay.ActiveTurretUpdates[0].Time(relay.TimeReference));
        Assert.True(world.Ships[4].TurretFiring);

    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Model_exports_convert_creation_time_through_the_mission_site_at_a_nonzero_offset()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(P660NextMatchLiveTests.Config(false));
        var template = server.Missions[0];
        using var mission = Mission.Assemble(template.Core, template.Map,
            template.Sides.Select(s => new SideSpec(s.Id, s.Name, s.Civilization.CivilizationId)).ToArray(), Clock(), 1000);
        var rock = mission.GetAsteroids()[0];
        var builder = new Ship(30000, mission.Core.HullTypes.Values.First(), new(100, 0, 0), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(builder, mission.Sectors[0]);
        var effect = new BuildingEffect(rock, builder, mission.Core.StationTypes.Values.First(), 0, 1030);
        var wire = ExportBuildingEffect.FromBuildingEffect(effect, effect.PositionStart, 1, 1, 1, 1);
        Assert.Equal(100_000u, wire.Effect.TimeStart);
        // Export later: the timestamp stays the original start, not receipt/tick/now.
        effect.Update(1010);
        Assert.Equal(100_000u, ExportBuildingEffect.FromBuildingEffect(effect, effect.PositionStart, 1, 1, 1, 1).Effect.TimeStart);
        var treasure = new Treasure(30001, mission.Sectors[0],
            new([0, 0, 0], [100, 0, 0], 60, 98_000, 30001, 0, 1, 1, TreasureCode.Cash, false), 1000, mission);
        Assert.Equal(998, treasure.CreatedAt);
        Assert.Equal(180, treasure.Position.X, 3);
        Assert.Equal(98_000u, treasure.ExportData().Time0);
    }
}

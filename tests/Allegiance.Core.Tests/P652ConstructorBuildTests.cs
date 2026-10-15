using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>Socket-free constructor regressions through the production build-order path.</summary>
public sealed class P652ConstructorBuildTests
{
    private static object? Invoke(Srv server, string method, params object[] args) =>
        typeof(Srv).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, args);

    private static void Set(Srv server, string field, object value) =>
        typeof(Srv).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, value);

    private static Mission Mission(IIgcSite site)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        var mission = Allegiance.Core.Model.Mission.Assemble(core, map,
            [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], site);
        mission.Stage = MissionStage.Started;
        CvhFile? Load(string name)
        {
            var path = Path.Combine(TestArtRoot.Value, name + ".cvh");
            return File.Exists(path) ? CvhReader.Parse(File.ReadAllText(path)) : null;
        }
        foreach (var sector in mission.Sectors)
        {
            sector.StationLandingBays = new StationLandingBayCache(Load);
            sector.CollisionHulls = new CollisionHullCache(Load);
        }
        return mission;
    }

    private static Asteroid Rock(Mission mission, Sector sector, DataStationTypeIGC type, Vector3 position)
    {
        var rock = new Asteroid(new DataAsteroidIGC(1, [position.X, position.Y, position.Z],
            [0, 1, 0], [1, 0, 0], [0, 0, 0, 1], 0, 0, type.AabmBuild,
            6401, 0, 60, "roid.mdl", "", "", sector.Id, "Build target", 0));
        mission.PlaceAsteroid(rock, sector);
        return rock;
    }

    private static void Order(Srv server, Ship ship, Asteroid rock) =>
        Assert.True((bool)Invoke(server, "StartBuildIfLegal", ConnectionId.Me, (short)999, ship,
            new OrderChange { ShipID = ship.Id, CommandId = OrderChange.CidBuild,
                Command = OrderChange.CmdAccepted, ObjectType = IgcObjectType.Asteroid,
                ObjectID = rock.Data.AsteroidId })!);

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_build_reaches_rock_even_with_reserved_building_flag(bool reserved)
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var sector = mission.Sectors[0];
        var type = mission.Core.StationTypes.Values.First(t => t.ClassId == 0 &&
            (t.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);
        var drone = mission.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeBuilder);
        var position = new Vector3(615, 425, 455);
        var rock = Rock(mission, sector, type, position);
        var ship = server.SpawnNpcShip(mission, sector, 0, position + new Vector3(300, 0, 0), drone.DroneTypeId);
        ship.BuilderStationType = type;
        ship.WaitsForBuildOrder = true;
        ship.IsBuilding = reserved;
        Order(server, ship, rock);
        Assert.False(ship.IsBuilding); Assert.Same(rock, ship.BuilderAsteroidTarget);
        for (int i = 1; i <= 2000 && sector.BuildingEffects.Count == 0; i++) sector.Update(i * 0.05);
        var effect = Assert.Single(sector.BuildingEffects);
        Assert.Same(ship, effect.Builder); Assert.Same(rock, effect.Target);
        Assert.Equal(type, effect.StationType); Assert.True(ship.IsBuilding);
        Assert.False(ship.IsRunningAway); Assert.False(ship.IsMining); Assert.Equal(0f, ship.Ore);
        // Repeating an order during actual construction must not register a second effect.
        Order(server, ship, rock);
        sector.Update(sector.LastUpdate + 0.05);
        Assert.Same(effect, Assert.Single(sector.BuildingEffects));
        Assert.Null(ship.BuilderAsteroidTarget);
        for (int i = 1; i <= 900 && sector.BuildingEffects.Count > 0; i++) sector.Update(sector.LastUpdate + 0.05);
        Assert.Empty(sector.BuildingEffects);
        Assert.DoesNotContain(rock, sector.Mineable); Assert.DoesNotContain(ship, sector.Models);
        Assert.Contains(sector.StationRuntimes, s => s.Data.Position.SequenceEqual(rock.Data.Position) &&
            s.Type.StationTypeId == type.StationTypeId && s.SideId == 0);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Arrived_constructor_starts_build_on_rock_contested_by_enemy_miners()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var sector = mission.Sectors[0];
        var type = mission.Core.StationTypes.Values.First(t => t.ClassId == 0 &&
            (t.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);
        var drone = mission.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeBuilder);
        var position = new Vector3(615, 425, 455);
        var buildRock = Rock(mission, sector, type, position);
        sector.RemoveAsteroid(buildRock);
        var rock = new Asteroid(buildRock.Data with
        {
            AabmCapabilities = (short)(type.AabmBuild | Asteroid.CapMineHe3), Ore = 1000, OreMax = 1000,
        });
        mission.PlaceAsteroid(rock, sector);
        var hull = mission.Core.HullTypes.Values.First() with { Length = 10, Thrust = 0 };
        var friendly = new Ship(1, hull, position + new Vector3(-80, 0, 0), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeMiner, SideId = 0, MiningAsteroidTarget = rock };
        var enemy = new Ship(2, hull, position + new Vector3(0, 0, 80), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeMiner, SideId = 1, MiningAsteroidTarget = rock };
        mission.PlaceShip(friendly, sector); mission.PlaceShip(enemy, sector);
        var ship = server.SpawnNpcShip(mission, sector, 0,
            position + new Vector3(rock.Radius + 0.5f * mission.Core.HullTypes[drone.HullTypeId].Length + 20, 0, 0),
            drone.DroneTypeId);
        ship.BuilderStationType = type;
        Order(server, ship, rock);
        sector.Update(0.05);
        Assert.Same(rock, Assert.Single(sector.BuildingEffects).Target);
        Assert.True(ship.IsBuilding); Assert.Null(ship.BuilderAsteroidTarget);
        Assert.False(ship.IsMining); Assert.False(ship.IsRunningAway); Assert.Equal(0f, ship.Ore);
        Assert.Equal(0f, friendly.Ore); Assert.Equal(0f, enemy.Ore);
        Assert.Same(rock, friendly.MiningAsteroidTarget); Assert.Same(rock, enemy.MiningAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bucket_spawned_constructor_builds_after_waiting_at_home()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new global::Allegiance.Server.ServerHost());
        Set(server, "_missions", new List<Mission> { mission });
        Set(server, "_config", new global::Allegiance.Server.ServerConfig());
        var side = mission.Sides[0];
        var type = side.Buckets.First(b => b.ItemType == BucketItemType.BitStationType).StationType!.Value;
        Invoke(server, "CompleteStationType", mission, side, type, 0d);
        var ship = Assert.Single(mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()));
        var sector = ship.Sector!;
        void Tick(double now)
        {
            Set(server, "_simNow", now);
            Invoke(server, "RunNpcAutoLaunch", now);
            sector.Update(now);
        }
        for (int i = 1; i <= 200; i++) Tick(i * 0.05);
        Assert.True(ship.WaitsForBuildOrder); Assert.False(ship.IsBuilding);
        Assert.Empty(sector.BuildingEffects);
        // H-18: launch the waiting constructor before issuing BUILD; native
        // drone orders distinguish LAUNCH from construction (FedSrv.CPP:7281-7322).
        Invoke(server, "UndockShipState", ship, sector);
        Assert.Null(ship.StationId);
        var rock = Rock(mission, sector, type, ship.Position + new Vector3(400, 0, 0));
        Order(server, ship, rock);
        for (int i = 201; i <= 2200 && sector.BuildingEffects.Count == 0; i++) Tick(i * 0.05);
        Assert.True(sector.BuildingEffects.Count > 0,
            $"position={ship.Position}, velocity={ship.Velocity}, dock={ship.StationId}, " +
            $"target={ship.BuilderAsteroidTarget?.Data.AsteroidId}, rockPresent={sector.Mineable.Contains(rock)}, running={ship.IsRunningAway}");
        var effect = Assert.Single(sector.BuildingEffects);
        Assert.Same(ship, effect.Builder); Assert.Same(rock, effect.Target);
        Assert.True(ship.IsBuilding); Assert.False(ship.IsRunningAway); Assert.Equal(0f, ship.Ore);
    }
}

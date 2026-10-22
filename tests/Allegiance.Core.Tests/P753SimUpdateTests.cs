using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>P753 — simulation parity wave 16: updates bundle evidence.</summary>
public sealed class P753SimUpdateTests
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

    private static Sector StationSector(Mission mission) =>
        mission.Sectors.First(s => s.StationRuntimes.Any());

    private static Asteroid Rock(Mission mission, Sector sector, DataStationTypeIGC type, Vector3 position, bool mineable = false)
    {
        short caps = (short)((ushort)type.AabmBuild | (mineable ? (ushort)DataAsteroidIGC.AabmMineHe3 : (ushort)0));
        var rock = new Asteroid(new DataAsteroidIGC(1, [position.X, position.Y, position.Z],
            [0, 1, 0], [1, 0, 0], [0, 0, 0, 1], 1000, 1000, caps,
            6401, 0, 60, "roid.mdl", "", "", sector.Id, "Build target", 0));
        mission.PlaceAsteroid(rock, sector);
        return rock;
    }

    private static Ship Spawn(Srv server, Mission mission, Sector sector, byte pilotType, Vector3 position)
    {
        var drone = mission.Core.DroneTypes.Values.First(t => t.PilotType == pilotType);
        return server.SpawnNpcShip(mission, sector, 0, position, drone.DroneTypeId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void IsLegalCommand_gates_pilot_type_and_target()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var sector = StationSector(mission);
        var type = mission.Core.StationTypes.Values.First(t => t.ClassId == 0 &&
            (t.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);
        var rock = Rock(mission, sector, type, Vector3.Zero, mineable: true);
        var station = sector.StationRuntimes.First().Data;
        var enemyStation = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 1))
            .StationRuntimes.First().Data;
        var otherShip = Spawn(server, mission, sector, Ship.PilotTypeBuilder, new Vector3(500, 0, 0));
        var minerDrone = mission.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeMiner);
        var enemy = server.SpawnNpcShip(mission, sector, 1, new Vector3(600, 0, 0), minerDrone.DroneTypeId);

        var miner = Spawn(server, mission, sector, Ship.PilotTypeMiner, new Vector3(100, 0, 0));
        Assert.True(miner.IsLegalCommand(OrderChange.CidMine, rock));
        Assert.False(miner.IsLegalCommand(OrderChange.CidBuild, rock));
        Assert.False(miner.IsLegalCommand(OrderChange.CidAttack, enemy));   // miners never attack

        var builder = Spawn(server, mission, sector, Ship.PilotTypeBuilder, new Vector3(200, 0, 0));
        builder.BuilderStationType = type;
        Assert.True(builder.IsLegalCommand(OrderChange.CidBuild, rock));
        Assert.False(builder.IsLegalCommand(OrderChange.CidMine, rock));

        var wingman = Spawn(server, mission, sector, Ship.PilotTypeWingman, new Vector3(300, 0, 0));
        Assert.True(wingman.IsLegalCommand(OrderChange.CidAttack, enemy));
        Assert.False(wingman.IsLegalCommand(OrderChange.CidAttack, otherShip));   // friendly target
        Assert.False(wingman.IsLegalCommand(OrderChange.CidMine, rock));
        Assert.True(wingman.IsLegalCommand(OrderChange.CidAttack, enemyStation)); // attack on a station is legal

        // NPCs cannot goto an enemy station; a player-pilot ship can (shipIGC.h:1631-1636).
        Assert.True(miner.IsLegalCommand(OrderChange.CidGoto, station));
        Assert.False(miner.IsLegalCommand(OrderChange.CidGoto, enemyStation));
        var playerPilot = new Ship(99, miner.HullType, new Vector3(700, 0, 0), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypePlayer, SideId = 0 };
        mission.PlaceShip(playerPilot, sector);
        Assert.True(playerPilot.IsLegalCommand(OrderChange.CidGoto, enemyStation));

        // Null target is legal only for DoNothing; buildingMask gates every
        // other command to DoNothing as well (shipIGC.h:1607-1617).
        Assert.False(miner.IsLegalCommand(OrderChange.CidGoto, null));
        Assert.False(miner.IsLegalCommand(OrderChange.CidNone, null));
        Assert.True(miner.IsLegalCommand(OrderChange.CidDoNothing, null));
        builder.IsBuilding = true;
        Assert.False(builder.IsLegalCommand(OrderChange.CidGoto, station));
        Assert.True(builder.IsLegalCommand(OrderChange.CidDoNothing, station));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Illegal_plan_gets_cleared_and_legal_accepted_is_promoted()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var sector = StationSector(mission);
        var station = sector.StationRuntimes.First().Data;

        var miner = Spawn(server, mission, sector, Ship.PilotTypeMiner, new Vector3(100, 0, 0));
        // A mine command with a station target is illegal for any pilot type.
        miner.PlanTarget = station;
        miner.PlanCommandId = OrderChange.CidMine;
        miner.AcceptedTarget = station;
        miner.AcceptedCommandId = OrderChange.CidGoto;

        sector.Update(0.1);
        Assert.Equal(station, miner.PlanTarget);
        Assert.Equal(OrderChange.CidGoto, miner.PlanCommandId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Miner_prefers_cowardly_unload_station()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var sector = StationSector(mission);
        var type = mission.Core.StationTypes.Values.First(t => t.ClassId == 0 &&
            (t.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);
        var rock = Rock(mission, sector, type, new Vector3(0, 0, 0), mineable: true);
        rock.SetOreWithFraction(0.75f, false);

        var miner = Spawn(server, mission, sector, Ship.PilotTypeMiner, new Vector3(0, 0, 0));
        miner.MiningAsteroidTarget = rock;
        miner.Ore = miner.EffectiveMineCapacity();

        // Drive the decision logic directly (it normally runs from PickDefaultOrder
        // when the miner is idle).
        typeof(Ship).GetMethod("DecideMiningOrSelling", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(miner, [false]);
        Assert.NotNull(miner.PlanTarget);
        Assert.Equal(OrderChange.CidGoto, miner.PlanCommandId);
        // Rock still has plenty of ore, so the miner should keep its mine target.
        Assert.NotNull(miner.MiningAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void IsFriendlyCluster_uses_territory_and_weighted_seen_ships()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var home = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 0));
        var enemySector = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 1));
        enemySector.StationRuntimes.First(st => st.SideId == 1).SideVisibility.SetSideVisibility(0, true);

        // Territory gate: the side-0 station sector is side 0 territory; the
        // seen enemy base sector is not (common.cpp:388-391).
        Assert.True(home.IsTerritory(0));
        Assert.False(enemySector.IsTerritory(0));

        // With no ships the weighted count is 0 → friendly (common.cpp:424-431).
        Assert.True(home.IsFriendlyCluster(0));

        // An enemy carrier seen in the sector weighs -6 and flips it unfriendly.
        var hull = mission.Core.HullTypes.Values.First();
        var enemyCarrier = new Ship(301, hull, new Vector3(500, 0, 0), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeCarrier, SideId = 1 };
        mission.PlaceShip(enemyCarrier, home);
        enemyCarrier.SideVisibility.SetSideVisibility(0, true);
        Assert.False(home.IsFriendlyCluster(0));

        // Two friendly wingmen (+2 each) still leave -2 → unfriendly.
        for (short id = 302; id <= 303; id++)
        {
            var wing = new Ship(id, hull, new Vector3(id * 100, 0, 0), Vector3.Zero, Orientation.Identity)
                { IsNpc = true, PilotType = Ship.PilotTypeWingman, SideId = 0 };
            mission.PlaceShip(wing, home);
            wing.SideVisibility.SetSideVisibility(0, true);
        }
        Assert.False(home.IsFriendlyCluster(0));

        // Removing the carrier leaves +4 → friendly again.
        Assert.True(home.Remove(enemyCarrier));
        Assert.True(home.IsFriendlyCluster(0));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Idle_npc_complaint_uses_backoff()
    {
        Require.Artwork();
        using var server = new Srv();
        var site = new RecordingIgcSite();
        var mission = Mission(site);
        Set(server, "_missions", new List<Mission> { mission });
        var sector = StationSector(mission);
        var miner = Spawn(server, mission, sector, Ship.PilotTypeMiner, new Vector3(100, 0, 0));
        Assert.Equal(Ship.PilotTypeMiner, miner.PilotType);
        Assert.NotNull(miner.SideId);
        Assert.NotNull(miner.Mission);
        Assert.Same(site, miner.Mission!.Site);

        site.Reset();

        // Native initial idle-complaint interval is 30 s (shipIGC.cpp:2308).
        double t0 = 30.0;
        miner.ComplainIdle(t0);
        int afterFirst = site.Calls.Count(c => c.StartsWith("DroneChatEvent"));
        Assert.True(afterFirst > 0);

        miner.ComplainIdle(t0); // same time, should be ignored by backoff
        miner.ComplainIdle(t0 + 29.9); // still inside the randomized 1.5x-2.5x window
        Assert.Equal(afterFirst, site.Calls.Count(c => c.StartsWith("DroneChatEvent")));

        miner.ComplainIdle(t0 + 120.0); // well after the window
        Assert.Equal(afterFirst * 2, site.Calls.Count(c => c.StartsWith("DroneChatEvent")));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Explosion_falloff_uses_mine_collision_radius()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var sector = StationSector(mission);
        var mineType = mission.Core.MineTypes.Values.First();
        var launcher = server.SpawnNpcShip(mission, sector, 0, Vector3.Zero, null);
        var mine = new Mine(1, sector, mineType, Vector3.Zero, 0.0, launcher);
        sector.Add(mine);
        float initial = mine.Fraction;

        // Explosion centered at (mineRadius, 0, 0) with radius equal to mine radius.
        // Native subtracts the mine radius, so distance becomes 0 and the mine
        // takes full damage; without subtraction the distance would be mineRadius
        // and only partial damage would apply.
        sector.ResolveExplosion(new Explosion(mineType.DamageType, 100f, mineType.MineRadius, 0.1, new Vector3(mineType.MineRadius, 0, 0), null), 0.1);
        Assert.True(mine.Fraction < initial);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_npc_launch_forced_by_goto_not_mine()
    {
        Require.Artwork();
        using var server = new Srv();
        var mission = Mission(new RecordingIgcSite());
        Set(server, "_missions", new List<Mission> { mission });
        var sector = StationSector(mission);
        var station = sector.StationRuntimes.First();
        var droneType = mission.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeMiner);
        var ship = server.SpawnNpcShip(mission, sector, 0, station.Center, droneType.DroneTypeId);
        ship.LastDockTime = 0.0; // docked a while ago
        ship.StationId = station.StationId;
        ship.AcceptedCommandId = OrderChange.CidMine;

        Invoke(server, "RunNpcAutoLaunch", 5.0);
        Assert.NotNull(ship.StationId); // mine order does not force early launch

        ship.AcceptedCommandId = OrderChange.CidGoto;
        Invoke(server, "RunNpcAutoLaunch", 5.0);
        Assert.Null(ship.StationId); // goto forces launch even before 10s cooldown
    }
}

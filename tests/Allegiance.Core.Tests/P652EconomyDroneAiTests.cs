using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>Socket-free native economy/AI regressions, driven through real sector/ship ticks.</summary>
public sealed class P652EconomyDroneAiTests
{
    private static readonly Lazy<StaticCore> Core = new(() => StaticCore.Load(IgcReader.ParseFile(
        Path.Combine(TestArtRoot.Value, "PCore001.igc"))));

    private static Mission Mission(RecordingIgcSite? site = null)
    {
        var core = Core.Value;
        var cluster = new DataClusterIGC(1, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Test", "", "", 0, 0, 0, true, false);
        var civ = core.Civilizations.Values.First().CivilizationId;
        var warp = new DataWarpIGC(10, 11, 30, "", "", "Link", [9000, 9000, 9000], [0, 0, 1], [0, 0, 0, 1], 1, 1);
        var m = Allegiance.Core.Model.Mission.Assemble(core, MissionMap.FromLists(
            [cluster, cluster with { ClusterId = 2, Name = "Other" }],
            [warp, warp with { WarpId = 11, DestinationId = 10, ClusterId = 2 }], [], []),
            [new SideSpec(0, "Alpha", civ), new SideSpec(1, "Bravo", civ)], site ?? new RecordingIgcSite());
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        return m;
    }

    private static Asteroid Rock(Mission m, short cluster = 1, short id = 100, short radius = 100)
    {
        var rock = new Asteroid(new DataAsteroidIGC(1, [0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1],
            1000, 1000, Asteroid.CapMineHe3, id, 0, radius, "roid", "", "", cluster, "He3", 0));
        m.PlaceAsteroid(rock, m.Sectors.First(s => s.Id == cluster));
        return rock;
    }

    private static Ship Drone(Mission m, short id, byte pilot = Ship.PilotTypeMiner, short side = 0)
    {
        var hull = m.Core.HullTypes.Values.First() with { Length = 10, Thrust = 0 };
        var ship = new Ship(id, hull, new Vector3(120, 0, 0), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = pilot, SideId = side, WaitsForBuildOrder = true };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }

    private static Station Station(Mission m, short abilities, float x = 2000)
    {
        var type = m.Core.StationTypes.Values.First(t => t.HasCapability(abilities));
        var data = new DataStationIGC([x, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1], 1, 0, 200,
            type.StationTypeId, 1, 1, "Base");
        var station = new Station(data, type);
        m.PlaceStation(station, m.Sectors[0]);
        return station;
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Lone_player_mines_by_proximity_without_npc_claim(bool hasSide, bool hasTarget)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var hull = m.Core.HullTypes.Values.First() with { Length = 10 };
        var ship = new Ship(1, hull, new Vector3(120, 0, 0), Vector3.Zero, Orientation.Identity)
        {
            SideId = hasSide ? (short)0 : null,
            MiningAsteroidTarget = hasTarget ? rock : null,
            MiningRateOverridePerSecond = 100,
            MineCapacityOverrideUnits = 150,
        };
        m.PlaceShip(ship, m.Sectors[0]);
        m.Sectors[0].Update(0.25);
        Assert.Equal(25f, ship.Ore);
        Assert.Equal(975f, rock.OreRemaining);
        Assert.True(ship.IsMining); Assert.Same(rock, ship.MiningAsteroid);
        for (int i = 2; i <= 8; i++) m.Sectors[0].Update(i * 0.25);
        Assert.Equal(150f, ship.Ore);
        Assert.True(rock.OreRemaining < rock.OreMax); // later ticks also regenerate ore
        Assert.False(ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Geometric_player_participates_in_contested_rock_arbitration(bool enemy)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); Station(m, DataStationTypeIGC.SabmUnload);
        var drone = Drone(m, 1); drone.MiningAsteroidTarget = rock; drone.Ore = 10;
        var player = new Ship(2, drone.HullType, new Vector3(-120, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = enemy ? (short)1 : (short)0 };
        m.PlaceShip(player, m.Sectors[0]);
        m.Sectors[0].Update(0.1);
        Assert.Equal(10f, drone.Ore);
        Assert.Equal(!enemy, player.Ore > 0);
        Assert.Equal(enemy, drone.PlanTarget is null);
        Assert.Equal(enemy, player.MiningAsteroid is null);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lone_sideless_drone_with_mine_order_accrues_identity_rate()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); var ship = Drone(m, 1);
        ship.SideId = null; ship.MiningAsteroidTarget = rock;
        m.Sectors[0].Update(0.25);
        Assert.Equal(0.25f, ship.Ore);
        Assert.Same(rock, ship.MiningAsteroid);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_contention_geometry_keeps_stock_hull_miners_ready_and_locked_out()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m, radius: 60);
        var type = m.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmUnload));
        var data = new DataStationIGC([0, 380, 0], [0, 1, 0], [1, 0, 0], [0, 0, 0, 1], 1, 1, 200,
            type.StationTypeId, 1, 1, "SELL");
        m.PlaceStation(new Station(data, type), m.Sectors[0]);
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        float distance = rock.Radius + 0.5f * hull.Length + 20;
        Ship miner(short id, short side, Vector3 position)
        {
            var ship = new Ship(id, hull, position, Vector3.Zero, Orientation.Identity)
                { IsNpc = true, PilotType = Ship.PilotTypeMiner, SideId = side };
            m.PlaceShip(ship, m.Sectors[0]); return ship;
        }
        var poor = miner(1, 1, new Vector3(distance, 0, 0));
        var rich = miner(2, 1, new Vector3(-distance, 0, 0));
        var enemy = miner(3, 0, new Vector3(0, distance, 0));
        poor.Ore = 10; rich.Ore = 40; enemy.Ore = 25;
        foreach (var ship in new[] { poor, rich, enemy }) ship.MiningAsteroidTarget = rock;
        for (int i = 1; i <= 6000; i++)
        {
            m.Sectors[0].Update(i * 0.05);
            Assert.True(poor.Ore == 10 && rich.Ore == 40 && enemy.Ore == 25,
                $"tick={i}, ore={poor.Ore}/{rich.Ore}/{enemy.Ore}, positions={poor.Position}/{rich.Position}/{enemy.Position}, velocities={poor.Velocity}/{rich.Velocity}/{enemy.Velocity}");
        }
        Assert.Equal(10f, poor.Ore); Assert.Equal(40f, rich.Ore); Assert.Equal(25f, enemy.Ore);
        foreach (var ship in new[] { poor, rich, enemy })
        {
            Assert.Same(rock, ship.MiningAsteroidTarget);
            Assert.Null(ship.PlanTarget); Assert.Null(ship.MiningAsteroid);
            Assert.Null(ship.StationId); Assert.False(ship.IsRunningAway);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stock_constructor_reaches_accepted_build_rock_and_starts_construction()
    {
        Require.Artwork();
        var m = Mission();
        var type = m.Core.StationTypes.Values.First(t => t.AabmBuild != 0);
        var droneType = m.Core.DroneTypes[type.ConstructionDroneTypeId];
        var rock = Rock(m);
        var buildRock = new Asteroid(rock.Data with { AabmCapabilities = type.AabmBuild, AsteroidId = 101 });
        m.PlaceAsteroid(buildRock, m.Sectors[0]);
        var ship = new Ship(1, m.Core.HullTypes[droneType.HullTypeId], new Vector3(400, 0, 0),
            Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0 };
        m.PlaceShip(ship, m.Sectors[0]);
        ship.BuilderStationType = type; ship.WaitsForBuildOrder = true; ship.SetBuilderBuild(buildRock);
        for (int i = 1; i <= 2000 && !ship.IsBuilding; i++) m.Sectors[0].Update(i * 0.05);
        Assert.True(ship.IsBuilding, $"position={ship.Position}, velocity={ship.Velocity}, target={ship.BuilderAsteroidTarget?.Data.AsteroidId}");
        Assert.Same(buildRock, Assert.Single(m.Sectors[0].BuildingEffects).Target);
        Assert.False(ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_side_only_least_ore_ready_miner_accrues_regardless_of_insertion_order(bool poorFirst)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var station = Station(m, DataStationTypeIGC.SabmUnload);
        var first = Drone(m, 1); var second = Drone(m, 2);
        var poor = poorFirst ? first : second; var rich = poorFirst ? second : first;
        poor.Ore = 1; rich.Ore = 10;
        poor.MiningAsteroidTarget = rich.MiningAsteroidTarget = rock;
        m.Sectors[0].Update(0.1);
        Assert.True(poor.Ore > 1, $"ore={poor.Ore} pos={poor.Position} vel={poor.Velocity} target={poor.MiningAsteroidTarget?.Data.AsteroidId} plan={poor.PlanTarget?.StationId}");
        Assert.Equal(10f, rich.Ore);
        Assert.Same(rock, poor.MiningAsteroid);
        Assert.Null(rich.MiningAsteroid);
        Assert.Equal(station.StationId, rich.PlanTarget?.StationId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_ready_miners_block_all_accrual_and_keep_their_orders()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var a = Drone(m, 1); var b = Drone(m, 2, side: 1);
        a.MiningAsteroidTarget = b.MiningAsteroidTarget = rock;
        for (int i = 1; i <= 3; i++) m.Sectors[0].Update(i * 0.1);
        Assert.Equal(0f, a.Ore); Assert.Equal(0f, b.Ore);
        Assert.Same(rock, a.MiningAsteroidTarget); Assert.Same(rock, b.MiningAsteroidTarget);
        Assert.Null(a.MiningAsteroid); Assert.Null(b.MiningAsteroid);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Distant_enemy_with_same_order_does_not_displace_ready_miner()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var a = Drone(m, 1); var b = Drone(m, 2, side: 1);
        a.MiningAsteroidTarget = b.MiningAsteroidTarget = rock;
        b.Position = new Vector3(2000, 0, 0);
        m.Sectors[0].Update(0.1);
        Assert.True(a.Ore > 0, $"pos={a.Position} vel={a.Velocity} target={a.MiningAsteroidTarget?.Data.AsteroidId}"); Assert.Equal(0f, b.Ore);
        Assert.Same(rock, a.MiningAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Refinery_teleport_sells_without_docking_and_picks_new_order()
    {
        Require.Artwork();
        var site = new RecordingIgcSite(); var m = Mission(site); var rock = Rock(m);
        var refinery = Station(m, 0x800, 2000);
        var ship = Drone(m, 1); ship.Ore = 10;
        ship.Position = new Vector3(2000 + refinery.Radius + 54, 0, 0);
        ship.PlanTarget = refinery.Data; ship.PlanCommandId = OrderChange.CidGoto;
        long before = m.Sides[0].Money;
        m.Sectors[0].Update(0.1);
        Assert.Equal(0f, ship.Ore);
        Assert.Equal(before + (long)(10 * ship.EffectiveOreCreditPerUnit()), m.Sides[0].Money);
        Assert.Contains(site.Calls, c => c.StartsWith("PaydayEvent("));
        Assert.Null(ship.StationId); Assert.Null(ship.PlanTarget);
        Assert.Same(rock, ship.MiningAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(Ship.PilotTypeMiner)]
    [InlineData(Ship.PilotTypeBuilder)]
    public void Damaged_working_drone_interrupts_order_for_repair(byte pilot)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var repair = Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1, pilot);
        if (pilot == Ship.PilotTypeMiner) ship.MiningAsteroidTarget = rock;
        else ship.SetGoto(new GotoOrder(GotoKind.Waypoint, 1, new Vector3(4000, 0, 0)));
        ship.SetFraction(0.94f);
        ship.Update(31.01);
        Assert.Equal(repair.StationId, ship.PlanTarget?.StationId);
        Assert.Null(ship.MiningAsteroidTarget); Assert.Null(ship.GotoWaypoint);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(0f, 134.9f, true)]
    [InlineData(0f, 135f, false)]
    [InlineData(1f, 120f, false)]
    [InlineData(0.99f, 120f, true)]
    public void Readiness_uses_strict_radius_and_velocity_bounds(float speed, float distance, bool mines)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); var ship = Drone(m, 1);
        ship.MiningAsteroidTarget = rock;
        ship.Position = new Vector3(distance, 0, 0); ship.Velocity = new Vector3(0, speed, 0);
        // The accrual pass sees the final tick position; tiny dt isolates the strict boundaries.
        m.Sectors[0].Update(0.000001);
        Assert.Equal(mines, ship.Ore > 0);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(Ship.PilotTypeBuilder)]
    [InlineData(Ship.PilotTypeWingman)]
    [InlineData(Ship.PilotTypePlayer)]
    public void Non_miner_near_rock_cannot_accrue(byte pilot)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); var ship = Drone(m, 1, pilot);
        ship.MiningAsteroidTarget = rock;
        m.Sectors[0].Update(0.1);
        Assert.Equal(0f, ship.Ore);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_miners_share_least_ore_selection_without_enemy_lockout()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        foreach (var side in m.Sides) side.AllyGroup = 0;
        var rich = Drone(m, 1); var poor = Drone(m, 2, side: 1);
        rich.Ore = 10; poor.Ore = 1;
        rich.MiningAsteroidTarget = poor.MiningAsteroidTarget = rock;
        m.Sectors[0].Update(0.1);
        Assert.Equal(10f, rich.Ore); Assert.True(poor.Ore > 1);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Equal_ore_tie_uses_sector_order_and_loser_can_return_to_land_only_station()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var landOnly = m.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmLand) && !t.HasCapability(DataStationTypeIGC.SabmUnload));
        var data = new DataStationIGC([2000, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1], 1, 0, 200,
            landOnly.StationTypeId, 1, 1, "Base");
        m.PlaceStation(new Station(data with { StationTypeId = landOnly.StationTypeId }, landOnly), m.Sectors[0]);
        var first = Drone(m, 2); var second = Drone(m, 1);
        first.MiningAsteroidTarget = second.MiningAsteroidTarget = rock;
        m.Sectors[0].Update(0.1);
        Assert.True(first.Ore > 0); Assert.Equal(0f, second.Ore);
        Assert.NotNull(second.PlanTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(0.95f, false)]
    [InlineData(0.949f, true)]
    public void Miner_damage_threshold_is_strict_95_percent(float hull, bool retreats)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1); ship.MiningAsteroidTarget = rock; ship.SetFraction(hull);
        ship.Update(31);
        Assert.Equal(retreats, ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(0.75f, false)]
    [InlineData(0.749f, true)]
    public void Mounted_shield_uses_75_percent_threshold_even_with_full_hull(float shield, bool retreats)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1); ship.MiningAsteroidTarget = rock;
        ship.MountPart(m.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Shield), 1, 0, 255);
        ship.SetShieldFraction(shield);
        ship.Update(31);
        Assert.Equal(retreats, ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(Ship.PilotTypeMiner)]
    [InlineData(Ship.PilotTypeBuilder)]
    public void Repair_resumes_paused_work_only_after_ten_second_recheck(byte pilot)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); var repair = Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1, pilot); ship.Position = new Vector3(1000, 0, 0);
        var order = new GotoOrder(GotoKind.Waypoint, 1, new Vector3(4000, 0, 0));
        if (pilot == Ship.PilotTypeMiner) ship.MiningAsteroidTarget = rock;
        else ship.SetGoto(order);
        ship.SetFraction(0.94f); ship.Update(31);
        Assert.True(ship.IsRunningAway);
        ship.RepairAndRefuel(repair.Type);
        ship.Update(40.99); Assert.True(ship.IsRunningAway);
        ship.Update(41); Assert.False(ship.IsRunningAway);
        if (pilot == Ship.PilotTypeMiner) Assert.Same(rock, ship.MiningAsteroidTarget);
        else Assert.Equal(order, ship.GotoWaypoint);
        Assert.Null(ship.PlanTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void New_order_baseline_and_player_cooldown_are_honored()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1);
        ship.SetFraction(0.7f); ship.MiningAsteroidTarget = rock; // damaged before the order
        ship.Update(31); Assert.False(ship.IsRunningAway);
        ship.SetRunawayCheckCooldown(120); ship.SetFraction(0.69f);
        ship.Update(119.99); Assert.False(ship.IsRunningAway);
        ship.Update(120); Assert.True(ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(Ship.PilotTypeBuilder, 0.999f, true)]
    [InlineData(Ship.PilotTypeCarrier, 0.1f, false)]
    [InlineData(Ship.PilotTypePlayer, 0.1f, false)]
    public void Constructor_runs_on_any_new_damage_but_carriers_and_players_never_run(byte pilot, float hull, bool retreats)
    {
        Require.Artwork();
        var m = Mission(); Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1, pilot); ship.SetFraction(hull); ship.Update(31);
        Assert.Equal(retreats, ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(1000f, false)]
    [InlineData(500f, true)]
    public void Constructor_build_distance_exception_compares_with_local_repair(float stationX, bool retreats)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); Station(m, DataStationTypeIGC.SabmRepair, stationX);
        var ship = Drone(m, 1, Ship.PilotTypeBuilder);
        ship.Position = new Vector3(400, 0, 0); // build distance 400; repair distance 600 or 100
        ship.BuilderAsteroidTarget = rock; ship.SetFraction(0.8f); ship.Update(31);
        Assert.Equal(retreats, ship.IsRunningAway);
        Assert.Equal(retreats, ship.BuilderAsteroidTarget is null);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void No_repair_destination_preserves_work_and_new_order_cancels_retreat()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); var ship = Drone(m, 1);
        ship.MiningAsteroidTarget = rock; ship.SetFraction(0.8f); ship.Update(31);
        Assert.False(ship.IsRunningAway); Assert.Same(rock, ship.MiningAsteroidTarget);
        Station(m, DataStationTypeIGC.SabmRepair); ship.SetFraction(0.79f); ship.Update(31.1);
        Assert.True(ship.IsRunningAway);
        var order = new GotoOrder(GotoKind.Waypoint, 1, new Vector3(4000, 0, 0));
        ship.SetGoto(order); Assert.False(ship.IsRunningAway);
        ship.SetFraction(1); ship.Update(50);
        Assert.Equal(order, ship.GotoWaypoint); Assert.Null(ship.MiningAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(0x20, 0.59f, true)]
    [InlineData(0x20, 0.6f, false)]
    [InlineData(0x10, 0.29f, true)]
    [InlineData(0x10, 0.3f, false)]
    [InlineData(0, 0.1f, false)]
    public void Wingman_retreat_flags_use_native_thresholds(int flags, float hull, bool retreats)
    {
        Require.Artwork();
        var m = Mission(); Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1, Ship.PilotTypeWingman); ship.WingmanBehaviour = flags;
        ship.SetGoto(new GotoOrder(GotoKind.Waypoint, 1, new Vector3(4000, 0, 0)));
        ship.SetFraction(hull); ship.Update(31);
        Assert.Equal(retreats, ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    [InlineData(-0.001f)]
    public void Teleport_unload_strict_range_and_no_velocity_gate(float offset)
    {
        Require.Artwork();
        var m = Mission(); var refinery = Station(m, 0x800);
        var ship = Drone(m, 1); ship.Ore = 10;
        ship.PlanTarget = refinery.Data; ship.PlanCommandId = OrderChange.CidGoto;
        ship.Position = new Vector3(2000 + refinery.Radius + 105 + offset, 0, 0);
        ship.Velocity = new Vector3(0, 20, 0);
        // Exact radius is excluded; motion does not impose a stopped-ship gate.
        ship.Update(0.000001);
        Assert.Equal(offset < 0, ship.Ore == 0);
        Assert.Null(ship.StationId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false, OrderChange.CidGoto, Ship.PilotTypeMiner)]
    [InlineData(true, OrderChange.CidNone, Ship.PilotTypeMiner)]
    [InlineData(true, OrderChange.CidGoto, Ship.PilotTypeBuilder)]
    public void Teleport_requires_capability_goto_plan_and_miner(bool teleport, short command, byte pilot)
    {
        Require.Artwork();
        var site = new RecordingIgcSite(); var m = Mission(site);
        var station = Station(m, teleport ? (short)0x800 : DataStationTypeIGC.SabmRepair);
        if (!teleport) Assert.False(station.Type.HasCapability(0x800));
        var ship = Drone(m, 1, pilot); ship.Ore = 10;
        ship.PlanTarget = station.Data; ship.PlanCommandId = command;
        ship.Position = new Vector3(2000 + station.Radius + 54, 0, 0);
        ship.Update(0.1);
        Assert.Equal(10f, ship.Ore);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("PaydayEvent("));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Miner_repaired_in_another_sector_picks_local_work_instead_of_old_rock()
    {
        Require.Artwork();
        var m = Mission(); var oldRock = Rock(m); var localRock = Rock(m, 2, 101);
        var repair = Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1); ship.MiningAsteroidTarget = oldRock;
        ship.SetFraction(0.94f); ship.Update(31); Assert.True(ship.IsRunningAway);
        m.PlaceShip(ship, m.Sectors[1]); ship.RepairAndRefuel(repair.Type);
        ship.Update(41);
        Assert.False(ship.IsRunningAway); Assert.Same(localRock, ship.MiningAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Repair_search_uses_reachable_other_sectors()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var type = m.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRepair));
        var data = new DataStationIGC([2000, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1], 2, 0, 200,
            type.StationTypeId, 1, 1, "Remote Repair");
        m.PlaceStation(new Station(data, type), m.Sectors[1]);
        var ship = Drone(m, 1); ship.MiningAsteroidTarget = rock;
        ship.SetFraction(0.94f); ship.Update(31);
        Assert.True(ship.IsRunningAway); Assert.Equal((short)2, ship.PlanTarget?.ClusterId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Constructor_without_local_repair_keeps_accepted_build_even_with_remote_base()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var type = m.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRepair));
        var data = new DataStationIGC([2000, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1], 2, 0, 200,
            type.StationTypeId, 1, 1, "Remote Repair");
        m.PlaceStation(new Station(data, type), m.Sectors[1]);
        var ship = Drone(m, 1, Ship.PilotTypeBuilder); ship.Position = new Vector3(1000, 0, 0);
        ship.SetBuilderBuild(rock); ship.SetFraction(0.8f); ship.Update(31);
        Assert.False(ship.IsRunningAway); Assert.Same(rock, ship.BuilderAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Constructor_starts_drilling_only_after_arrival_and_does_not_flee_while_building()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); Station(m, DataStationTypeIGC.SabmRepair, 800);
        var type = m.Core.StationTypes.Values.First(t => t.AabmBuild != 0);
        var buildRock = new Asteroid(rock.Data with { AabmCapabilities = type.AabmBuild, AsteroidId = 101 });
        m.PlaceAsteroid(buildRock, m.Sectors[0]);
        var ship = Drone(m, 1, Ship.PilotTypeBuilder); ship.BuilderStationType = type;
        ship.Position = new Vector3(1000, 0, 0); ship.SetBuilderBuild(buildRock); ship.Update(0.1);
        Assert.False(ship.IsBuilding); Assert.Same(buildRock, ship.BuilderAsteroidTarget);
        ship.Position = new Vector3(120, 0, 0); ship.Update(0.2);
        Assert.True(ship.IsBuilding); Assert.Null(ship.BuilderAsteroidTarget);
        ship.SetFraction(0.1f); ship.Update(31);
        Assert.True(ship.IsBuilding); Assert.False(ship.IsRunningAway);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teleport_sale_scales_loaded_value_and_yield_and_pays_once()
    {
        Require.Artwork();
        var site = new RecordingIgcSite(); var m = Mission(site); Rock(m);
        var attributes = Enumerable.Repeat(1f, GlobalAttributeSet.Count).ToArray();
        attributes[GlobalAttributeSet.MiningYield] = 1.5f;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(attributes);
        var refinery = Station(m, 0x800);
        var ship = Drone(m, 1); ship.Ore = 10; ship.PlanTarget = refinery.Data;
        ship.PlanCommandId = OrderChange.CidGoto;
        ship.Position = new Vector3(2000 + refinery.Radius + 54, 0, 0);
        long before = m.Sides[0].Money;
        ship.Update(0.1); ship.Update(0.2);
        Assert.Equal(before + (long)(10 * m.Core.Constants.FloatConstants[8] * 1.5f), m.Sides[0].Money);
        Assert.Single(site.Calls, c => c.StartsWith("PaydayEvent("));
        Assert.Null(ship.StationId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_lockout_complains_with_native_voice_id_after_initial_backoff()
    {
        Require.Artwork();
        var site = new RecordingIgcSite(); var m = Mission(site); var rock = Rock(m);
        var a = Drone(m, 1); var b = Drone(m, 2, side: 1);
        a.MiningAsteroidTarget = b.MiningAsteroidTarget = rock;
        m.Sectors[0].Update(29.99);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("DroneChatEvent("));
        m.Sectors[0].Update(30);
        Assert.Equal(2, site.Calls.Count(c => c.StartsWith("DroneChatEvent(") && c.Contains("341") && c.Contains("Miner requesting unoccupied")));
        m.Sectors[0].Update(30.1);
        Assert.Equal(2, site.Calls.Count(c => c.StartsWith("DroneChatEvent(")));
        Assert.Equal(0f, a.Ore); Assert.Equal(0f, b.Ore);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("PCore001.igc")]
    [InlineData("PCore006.igc")]
    [InlineData("PCore014.igc")]
    public void Every_stock_teleport_refinery_can_unload(string coreName)
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, coreName)));
        var types = core.StationTypes.Values.Where(t => t.HasCapability(0x800)).ToArray();
        Assert.NotEmpty(types);
        Console.WriteLine($"P652-STOCK {coreName}: {types.Length} teleport-unload types");
        foreach (var type in types)
        {
            var m = Mission();
            // Reassemble with the selected stock core to exercise its actual type record.
            var map = MissionMap.FromLists(m.Map.Clusters, m.Map.Warps, [], []);
            var civ = core.Civilizations.Values.First().CivilizationId;
            m = Allegiance.Core.Model.Mission.Assemble(core, map, [new SideSpec(0, "Alpha", civ)], new RecordingIgcSite());
            m.Stage = MissionStage.Started;
            var data = new DataStationIGC([2000, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 1], 1, 0, 200,
                type.StationTypeId, 1, 1, "Refinery");
            m.PlaceStation(new Station(data, type), m.Sectors[0]);
            var ship = Drone(m, 1); ship.Ore = 10; ship.PlanTarget = data; ship.PlanCommandId = OrderChange.CidGoto;
            ship.Position = new Vector3(2000 + type.Radius + 54, 0, 0); ship.Update(0.1);
            Assert.Equal(0f, ship.Ore); Assert.Null(ship.StationId);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Traveling_miner_stops_in_native_mining_envelope_and_accrues()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var hull = m.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmMiner));
        var ship = new Ship(1, hull, new Vector3(2000, 0, 0), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeMiner, SideId = 0, MiningAsteroidTarget = rock };
        m.PlaceShip(ship, m.Sectors[0]);
        for (int i = 1; i <= 4000 && ship.Ore < 3; i++) m.Sectors[0].Update(i * 0.05);
        Assert.True(ship.Ore >= 3, $"ore={ship.Ore}, position={ship.Position}, velocity={ship.Velocity}");
        Assert.Equal(1f, ship.Fraction);
        Assert.Same(rock, ship.MiningAsteroid);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Repair_dock_keeps_paused_order_until_launch_and_recheck()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); var repair = Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1); ship.MiningAsteroidTarget = rock; ship.SetFraction(0.94f); ship.Update(31);
        var host = new global::Allegiance.Server.ServerHost();
        Assert.True(host.DockWithStationEvent(ship, new DockStation(repair.Data)));
        Assert.True(ship.IsRunningAway);
        ship.Update(41); Assert.True(ship.IsRunningAway); // still docked
        ship.RepairAndRefuel(repair.Type); ship.StationId = null; ship.Update(41.1);
        Assert.False(ship.IsRunningAway); Assert.Same(rock, ship.MiningAsteroidTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Working_miner_holds_position_inside_native_envelope()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var hull = m.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmMiner));
        var position = new Vector3(rock.Radius + 0.5f * hull.Length + 20, 0, 0);
        var ship = new Ship(1, hull, position, Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeMiner, SideId = 0, MiningAsteroidTarget = rock };
        m.PlaceShip(ship, m.Sectors[0]);
        for (int i = 1; i <= 20; i++) m.Sectors[0].Update(i * 0.05);
        Assert.True(ship.Ore >= 0.99f, $"ore={ship.Ore} position={ship.Position} velocity={ship.Velocity}");
        Assert.True((position - ship.Position).Length() < 0.01f);
        Assert.Same(rock, ship.MiningAsteroid);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Repair_ship_search_requires_land_on_carrier_hull(bool canLand)
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m);
        var hull = m.Core.HullTypes.Values.First() with { Thrust = 0, HabmCapabilities = canLand ? DataHullTypeIGC.HabmLandOnCarrier : (short)0 };
        var ship = new Ship(1, hull, new Vector3(120, 0, 0), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeMiner, SideId = 0, MiningAsteroidTarget = rock };
        var carrier = new Ship(2, hull with { HabmCapabilities = DataHullTypeIGC.HabmCarrier | 0x40 },
            new Vector3(1000, 0, 0), Vector3.Zero, Orientation.Identity)
            { IsNpc = true, PilotType = Ship.PilotTypeCarrier, SideId = 0 };
        m.PlaceShip(ship, m.Sectors[0]); m.PlaceShip(carrier, m.Sectors[0]);
        ship.SetFraction(0.94f); ship.Update(31);
        Assert.Equal(canLand, ship.IsRunningAway); Assert.Null(ship.PlanTarget);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void New_drone_in_old_mission_starts_runaway_clock_at_creation()
    {
        Require.Artwork();
        var m = Mission(); var rock = Rock(m); Station(m, DataStationTypeIGC.SabmRepair);
        var ship = Drone(m, 1); ship.InitClock(1000); ship.MiningAsteroidTarget = rock;
        ship.SetFraction(0.94f); ship.Update(1000.1);
        Assert.False(ship.IsRunningAway);
        ship.Update(1031); Assert.True(ship.IsRunningAway);
    }
}

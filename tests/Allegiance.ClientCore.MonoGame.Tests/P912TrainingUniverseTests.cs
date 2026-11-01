using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using V = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P912TrainingUniverseTests
{
    [Theory]
    [InlineData(Rendering.HudStyle.Classic)] [InlineData(Rendering.HudStyle.Modern)]
    public void Mission3_setup_selects_only_mount_zero_after_a_prior_group_mode(Rendering.HudStyle style)
    {
        using var connection = new ConnectionSession();
        var game = P910TrainingIntegrationTests.Headless(connection, style);
        P910TrainingIntegrationTests.Set(game, "_groupFire", true);
        P910TrainingIntegrationTests.Set(game, "_selectedMount", 2);
        P910TrainingIntegrationTests.Call(game, "SetupTrainingView", 3);
        Assert.False(P910TrainingIntegrationTests.Get<bool>(game, "_groupFire"));
        Assert.Equal(0, P910TrainingIntegrationTests.Get<int>(game, "_selectedMount"));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(4)] [InlineData(8)]
    public void Combat_universe_starts_with_the_trigger_released(int mission)
    {
        Require.Artwork();
        using var host = TrainingHost.Start(mission == 4 ? TrainingHost.EnemyEngagement : TrainingHost.Nanite);
        Assert.Equal(0, host.PlayerShip.StateM & ShipControlState.WeaponsMask);
    }

    [Fact]
    public void Training_start_checks_readiness_and_builds_goals_under_one_replicated_world_lock()
    {
        using var connection = new ConnectionSession();
        connection.World.Apply(new ExportShip { ShipId = 10, SideId = 0 });
        connection.World.Apply(new SetCluster { SectorId = 1051, Ship = ServerShipUpdate.FromValues(
            10, 0, default, 0, V.Zero, Orientation.Identity, V.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        TrainingSession? training = null;
        bool checking = false;
        int guardedReads = 0;
        var site = new ClientTrainingSite(new ClientTrainingFacilities
        {
            Session = connection,
            World = () =>
            {
                if (checking && training?.Started != true)
                {
                    Assert.True(Monitor.IsEntered(connection.Gate));
                    guardedReads++;
                }
                return connection.World;
            },
        });
        training = new TrainingSession(site, new P910TrainingIntegrationTests.ProbeMission(site));
        var game = P910TrainingIntegrationTests.Headless(connection, Rendering.HudStyle.Modern);
        P910TrainingIntegrationTests.Set(game, "_trainingSession", training);
        checking = true;
        P910TrainingIntegrationTests.Call(game, "UpdateTraining", .016);
        Assert.True(training.Started);
        Assert.True(guardedReads >= 2); // readiness and SetupTraining both read the world
        training.Terminate(); training.DetachClientHooks();
    }

    [Theory]
    [InlineData(Rendering.HudStyle.Classic)] [InlineData(Rendering.HudStyle.Modern)]
    public void Command_view_action_restores_the_bar_after_a_scripted_cockpit_mask(Rendering.HudStyle style)
    {
        using var connection = new ConnectionSession();
        var game = P910TrainingIntegrationTests.Headless(connection, style);
        P910TrainingIntegrationTests.Call(game, "SetTrainingDisplayMode", 0);
        Assert.False(P910TrainingIntegrationTests.Get<bool>(game, "_trainingCommandPane"));
        P910TrainingIntegrationTests.Call(game, "ApplyConsoleMode", "ViewCommand", false);
        Assert.True(P910TrainingIntegrationTests.Get<bool>(game, "_trainingCommandPane"));
        Assert.False(P910TrainingIntegrationTests.Get<Rendering.ConsolePaneState>(game, "_consolePanes").Inventory);
    }

    [Theory]
    [InlineData(4, Rendering.HudStyle.Classic)] [InlineData(4, Rendering.HudStyle.Modern)]
    [InlineData(8, Rendering.HudStyle.Classic)] [InlineData(8, Rendering.HudStyle.Modern)]
    public void Combat_lesson_setup_restores_native_group_fire_after_a_previous_single_weapon(int mission, Rendering.HudStyle style)
    {
        using var connection = new ConnectionSession();
        var game = P910TrainingIntegrationTests.Headless(connection, style);
        P910TrainingIntegrationTests.Set(game, "_groupFire", false);
        P910TrainingIntegrationTests.Call(game, "SetupTrainingView", mission);
        Assert.True(P910TrainingIntegrationTests.Get<bool>(game, "_groupFire"));
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Real_training_defend_chat_replaces_a_stale_goto_and_arms_the_live_escort()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.Nanite);
        using var connection = new ConnectionSession();
        host.StartBackgroundTicker();
        connection.Connect(host.Port, "p912-defend");
        connection.SendLogonRequest(new Allegiance.Core.Wire.LogonRequest
        {
            CharacterName = "p912-defend", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
            FedsrvVer = Allegiance.Core.Wire.LogonRequest.MsgVer, CrcFileList = -1,
            SteamAuthTicket = new byte[16], DrmHash = "p912",
        });
        Assert.True(connection.WaitForAck(15000).FValidated);
        connection.SendPositionReq(0);
        Assert.True(SpinWait.SpinUntil(() => connection.World.MyShipId is { } id
            && connection.World.Ships.TryGetValue(id, out var own) && own.Latest is not null, 15000));
        var site = new ClientTrainingSite(new ClientTrainingFacilities
        { World = () => connection.World, Session = connection, Host = host });
        site.AttachClientHooks(new Mission8(site));
        int leader = host.CreateDrone("escort charge", 4944, 604, 0, Ship.PilotTypePlayer);
        int guard = host.CreateDrone("ordered guard", 4945, 615, 0, Ship.PilotTypeWingman);
        host.PlaceDrone(leader, 1051, new(-2200, 2200, 0), System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -1);
        host.PlaceDrone(guard, 1051, new(-1200, 2200, 0), -System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -1);
        host.IssueCommand(guard, TrainingMissionIds.CommandAccepted, host.PlayerShipId, null, null, TrainingMissionIds.CmdGoto);
        Assert.NotNull(host.Drones[guard].GotoWaypoint);
        connection.SendChat("", ChatTarget.Individual, (short)guard, commandId: OrderChange.CidDefend,
            otTarget: (short)IgcObjectType.Ship, oidTarget: (short)leader);
        Assert.True(SpinWait.SpinUntil(() => host.Drones[guard].DefendTarget == host.Drones[leader], 5000),
            "The ordinary training CHAT order did not arm its native defend plan.");
        Assert.Null(host.Drones[guard].GotoWaypoint);
        Assert.Equal(OrderChange.CidDefend, host.Drones[guard].AcceptedShipCommandId);
        Assert.True(SpinWait.SpinUntil(() => (host.Drones[guard].Position - host.Drones[leader].Position).Length() < 850, 8000));
        site.DetachClientHooks();
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Mission2_bounds_are_private_and_Mission3_preserves_native_full_cargo_swap_bug()
    {
        Require.Artwork();
        using var normal = TrainingHost.Start(TrainingHost.BasicWeaponry);
        float radius = normal.Mission.Core.Constants.FloatConstants[1];
        using var flight = TrainingHost.Start(TrainingHost.BasicFlight);
        Assert.Equal(20000, flight.Mission.Core.Constants.FloatConstants[1]);
        Assert.Equal(100, flight.Mission.Core.Constants.FloatConstants[2]);
        Assert.Equal(radius, normal.Mission.Core.Constants.FloatConstants[1]);
        Assert.NotSame(normal.Mission.Core, flight.Mission.Core);
        float ordinaryLimit = radius * MathF.Sqrt(normal.Mission.Core.Constants.FloatConstants[2]);
        Assert.True(ordinaryLimit < 200000);
        var practicePosition = new V((ordinaryLimit + 200000) * .5f, 0, 0);
        normal.PlayerShip.Position = flight.PlayerShip.Position = practicePosition;
        normal.StepLight(.1f); flight.StepLight(.1f);
        Assert.True(normal.PlayerShip.Fraction < 1);
        Assert.Equal(1, flight.PlayerShip.Fraction);
        Assert.NotEqual(ShipWarningMask.None, normal.PlayerShip.WarningMask & ShipWarningMask.OutOfBounds);
        Assert.Equal(ShipWarningMask.None, flight.PlayerShip.WarningMask & ShipWarningMask.OutOfBounds);
        // trainingmission.cpp:819-820 -> clintlib.h:2233-2245 fills all
        // five slots (two ammo, two fuel, one dispenser). Native has no spare
        // magazine with this hull/core; do not invent cargo to hide the bug.
        Assert.Equal(new short[] { 14, 25, 25, 24, 24 }, normal.PlayerShip.CargoHold.Select(p => p!.PartId));
        Assert.DoesNotContain(normal.PlayerShip.Parts, p => p.EquipmentType == EquipmentType.Magazine);
        normal.SwapWeapon((int)EquipmentType.Magazine, 0);
        Assert.DoesNotContain(normal.PlayerShip.Parts, p => p.EquipmentType == EquipmentType.Magazine);
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Mission8_has_native_position_proximity_cargo_and_enemy_sniper_tech()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.Nanite);
        var ship = host.PlayerShip;
        Assert.Equal((short)685, ship.HullType.HullId);
        Assert.Equal(new V(-1500, 1500, 0), ship.Position);
        Assert.Equal(0, ship.StateM & ShipControlState.WeaponsMask);
        Assert.Contains(ship.Parts, p => p.EquipmentType == EquipmentType.Dispenser && p.PartId == 59);
        Assert.Equal(190, ship.CargoHold[0]!.PartId);
        Assert.All(ship.CargoHold.Skip(1), p => { Assert.NotNull(p); Assert.Equal(59, p.PartId); Assert.True(p.Amount > 0); });
        var sniper = host.Mission.Core.PartTypes[60];
        Assert.True(new TechTreeBitMask(sniper.TtbmEffects).IsSubsetOf(host.Mission.Sides[1].DevelopmentTechs));
        Assert.True(new TechTreeBitMask(sniper.TtbmRequired).IsSubsetOf(host.Mission.Sides[1].DevelopmentTechs));
        Assert.Equal(host.HomeStationId, host.CommanderShip.StationId);
        Assert.False(host.CommanderShip.Autopilot);
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Mission5_tableau_uses_native_station_warp_geometry_and_loadouts()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.CommandView);
        var site = new TrainingHostSite(host); var mission = new Mission5(site); mission.Start();
        var neptune = host.Mission.GetStation(1050)!;
        var mars = host.Mission.GetStation(1052)!;
        foreach (int id in new[] { mission.Builder1Id, mission.Builder2Id, mission.Builder3Id })
        {
            var ship = host.Drones[id]; var delta = ship.Position - neptune.Center;
            Assert.InRange(delta.X, -300, 300); Assert.InRange(delta.Y, -300, 300); Assert.InRange(delta.Z, -300, 300);
            Assert.Equal((short)87, ship.LayerLaidExpendableTypeId);
            Assert.Equal(TrainingMissionIds.CmdDoNothing, ship.CurrentCommandId);
            Assert.Equal(TrainingMissionIds.CmdDoNothing, ship.AcceptedShipCommandId);
        }
        foreach (int id in new[] { mission.Miner2Id, mission.Miner3Id, mission.Miner4Id })
        {
            var ship = host.Drones[id]; var delta = ship.Position - mars.Center;
            Assert.InRange(delta.X, -600, 600); Assert.InRange(delta.Y, -600, 600); Assert.InRange(delta.Z, -300, 300);
            Assert.Equal((short)1051, ship.Sector!.Id);
        }
        var before = host.Drones[mission.ScoutId].Position;
        for (int tick = 0; tick < 40; tick++) host.StepLight(.05f);
        Assert.Equal(before, host.Drones[mission.ScoutId].Position);
        Assert.Null(host.Drones[mission.Miner2Id].MiningAsteroidTarget);
        var warp = host.Mission.GetWarp(1052)!;
        var line = warp.Position - mars.Center;
        var scout = host.Drones[mission.ScoutId].Position - mars.Center;
        float fraction = scout * line / line.LengthSquared();
        Assert.InRange(fraction, .5f, .75f);
        Assert.True((scout - line * fraction).Length() < .01f);
        // Native DoNothing remains legal with a non-null target too; M8's
        // ending uses exactly that shape to stop its defenders.
        int enemyId = host.CreateDrone("idle target", 4940, 310, 1, Ship.PilotTypePlayer);
        host.PlaceDrone(enemyId, 1051, new(before.X + 500, before.Y, before.Z),
            System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -1);
        foreach (int slot in new[] { TrainingMissionIds.CommandCurrent, TrainingMissionIds.CommandAccepted })
            host.IssueCommand(mission.ScoutId, slot, enemyId, null, null, TrainingMissionIds.CmdDoNothing);
        for (int tick = 0; tick < 40; tick++) host.StepLight(.05f);
        Assert.Equal(before, host.Drones[mission.ScoutId].Position);
        Assert.Equal(1, host.Drones[enemyId].Fraction);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(Rendering.HudStyle.Classic)] [InlineData(Rendering.HudStyle.Modern)]
    public void Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI(Rendering.HudStyle style)
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.CommandView);
        using var connection = new ConnectionSession();
        var game = P910TrainingIntegrationTests.Headless(connection, style);
        P910TrainingIntegrationTests.Set(game, "_embeddedHost", host);
        var camera = new Rendering.CommandCamera();
        P910TrainingIntegrationTests.Set(game, "_commandCamera", camera);
        P910TrainingIntegrationTests.Set(game, "_shopCatalog", Rendering.ShopCatalog.FromCore(host.Mission.Core));
        var site = new ClientTrainingSite(game.CreateTrainingFacilities(5));
        site.AttachClientHooks(new Mission5(site));
        host.CreateDrone("remote miner", 4920, 438, 0, Ship.PilotTypeMiner);
        host.PlaceDrone(4920, 1051, new(500, 500, 500), System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -3);
        host.StartBackgroundTicker();
        connection.Connect(host.Port, "p912-view");
        connection.SendLogonRequest(new Allegiance.Core.Wire.LogonRequest
        {
            CharacterName = "p912-view", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
            FedsrvVer = Allegiance.Core.Wire.LogonRequest.MsgVer, CrcFileList = -1,
            SteamAuthTicket = new byte[16], DrmHash = "p912",
        });
        Assert.True(connection.WaitForAck(15000).FValidated);
        connection.SendPositionReq(0);
        Assert.True(SpinWait.SpinUntil(() => site.HasPlayerShip && site.PlayerDocked, 15000));
        camera.Center(new(4000, -3000, 0), 0);
        camera.Step(.1f, 1, 0, -10, 0);
        Assert.NotEqual(Microsoft.Xna.Framework.Vector3.Zero, camera.Displacement);
        site.SetupTraining(5);
        var home = host.Mission.GetStation(1050)!;
        Assert.Equal(new Microsoft.Xna.Framework.Vector3(home.Center.X, home.Center.Y, home.Center.Z), camera.Focus);
        Assert.Equal(Microsoft.Xna.Framework.Vector3.Zero, camera.Displacement);
        Assert.Equal(home.Radius * 2, camera.Distance);
        Assert.Equal((short)1050, host.PlayerShip.StationId);
        Assert.Equal(Rendering.CameraMode.Command, P910TrainingIntegrationTests.Get<Rendering.CameraMode>(game, "_cameraMode"));
        Assert.Equal(Rendering.RadarMode.All, P910TrainingIntegrationTests.Get<Rendering.RadarMode>(game, "_radarMode"));
        Assert.False(P910TrainingIntegrationTests.Get<bool>(game, "_dockedActive"));
        connection.SendViewClusterRequest(1051);
        Assert.True(SpinWait.SpinUntil(() => connection.World.ViewedClusterId == 1051, 3000));
        Assert.Equal((short)1051, P910TrainingIntegrationTests.Call(game, "RenderSector", (short)1050));
        Assert.True(new GetViewSectorCondition(site, 1051).Evaluate());
        Assert.True(SpinWait.SpinUntil(() => connection.World.Ships.TryGetValue(4920, out var remote)
            && remote.Latest is not null && remote.PoseSectorId == 1051 && !remote.IsGone, 5000));
        Assert.Equal((short)1050, connection.World.Ships[(short)host.PlayerShipId].SectorId);
        site.DetachClientHooks();
        camera.Step(.1f, 1, 0, 0, 0); // native enables displacement in command view
        Assert.NotEqual(Microsoft.Xna.Framework.Vector3.Zero, camera.Displacement);
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Mission3_guard_turns_outside_7500_and_clears_only_in_the_7000_to_7500_band()
    {
        Require.Artwork(); TrainingController.BeginMission();
        using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        var site = new TrainingHostSite(host); var mission = new Mission3(site); mission.Start();
        var ship = host.PlayerShip; var station = host.Mission.GetStation(host.HomeStationId)!;
        float radii = ship.Radius + station.Radius;
        ship.Position = station.Center + new V(0, radii + 7600, 0);
        mission.Frame();
        Assert.Contains(TrainingControlState.SetInputControls.Take(2), c => c != TrainingControlState.Na);
        ship.Position = station.Center + new V(0, radii + 7200, 0);
        mission.Frame();
        Assert.All(TrainingControlState.SetInputControls, c => Assert.Equal(TrainingControlState.Na, c));
        ship.Position = station.Center + new V(0, radii + 6900, 0);
        mission.Frame();
        Assert.All(TrainingControlState.SetInputControls, c => Assert.Equal(TrainingControlState.Na, c));
        mission.Terminate(); TrainingController.EndMission();
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(800)] [InlineData(4000)]
    public void Mission8_bomber_attacks_a_real_outpost_with_native_projectiles(float distance)
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.Nanite);
        var station = host.Mission.GetStation((short)Mission8.EnemyOutpostStationId)!;
        Assert.True(station.SeenBySide(0));
        int id = host.CreateDrone("bomber", 4930, 604, 0, Ship.PilotTypeWingman, CreateDroneAction.DefaultWingmanBehaviour, -1, 0);
        host.PlaceDrone(id, station.ClusterId, new(station.Center.X + distance, station.Center.Y, station.Center.Z),
            -System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -1);
        var ship = host.Drones[id];
        // Native removal tests part 200 only; the available-tech loadout upgrades it to 201.
        Assert.Equal(2, ship.Parts.Count(p => p.EquipmentType == EquipmentType.Weapon && p.PartId == 201));
        var sector = host.Mission.Sectors.First(s => s.Id == station.ClusterId);
        ship.SetWingmanAttackOrder(EquipmentTarget.Resolve(sector, IgcObjectType.Station, station.StationId)!);
        for (int tick = 0; tick < 6000 && !station.Dead; tick++) host.StepLight(.05f);
        Assert.True(station.Dead, $"Bomber never destroyed outpost; armour={station.ArmorFraction} pos={ship.Position}");
        Assert.True(ship.Fraction > 0);
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Nanite_escort_crosses_the_real_aleph_once_and_reaches_the_bomber()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.Nanite);
        var source = host.Mission.Sectors.First(s => s.Id == 1051);
        var mouth = source.Warps.First(w => w.Destination?.Id == 1052);
        var exit = mouth.Partner()!;
        static V Position(ResolvedWarp w) => new(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2]);
        static V Forward(ResolvedWarp w) => new(w.Warp.Forward[0], w.Warp.Forward[1], w.Warp.Forward[2]);
        var leaderId = host.CreateDrone("bomber", 4940, 604, 0, Ship.PilotTypePlayer);
        var escortId = host.CreateDrone("nan escort", 4941, 685, 0, Ship.PilotTypeWingman);
        var leaderPosition = Position(exit) + Forward(exit) * 600;
        var escortPosition = Position(mouth) + Forward(mouth) * 180;
        host.PlaceDrone(leaderId, 1052, new(leaderPosition.X, leaderPosition.Y, leaderPosition.Z),
            System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, -1);
        host.PlaceDrone(escortId, 1051, new(escortPosition.X, escortPosition.Y, escortPosition.Z),
            new(-Forward(mouth).X, -Forward(mouth).Y, -Forward(mouth).Z), System.Numerics.Vector3.UnitZ, null, -1);
        var escort = host.Drones[escortId];
        escort.SetGoto(new(GotoKind.Waypoint, 1052, leaderPosition,
            TargetType: IgcObjectType.Ship, TargetId: (short)leaderId));
        var transitions = new List<(short From, short To)>();
        // ServerHost is internal to the server; observe its existing public
        // event seam without adding test visibility to the shipping assembly.
        var site = host.Mission.Site;
        var warped = site.GetType().GetField("ShipWarped")!;
        var original = (Action<Ship, Sector, Sector>?)warped.GetValue(site);
        Action<Ship, Sector, Sector> observe = (ship, from, to) =>
        { if (ship.Id == escortId) transitions.Add((from.Id, to.Id)); };
        warped.SetValue(site, original + observe);
        // Run the real simulation directly: the detached legacy training
        // autopilot helper does not own this typed native ship-follow plan.
        for (int tick = 0; tick < 1000; tick++) host.Mission.Update(host.Mission.LastUpdate + .05);
        Assert.Equal((short)1052, escort.Sector!.Id);
        Assert.Single(transitions);
        Assert.Equal(((short)1051, (short)1052), transitions[0]);
        Assert.True((escort.Position - host.Drones[leaderId].Position).Length() < 300);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(false)] [InlineData(true)]
    public void Cluster_handoff_rebases_the_model_clock_and_stops_weapon_fire(bool npc)
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.Nanite);
        var source = host.Mission.Sectors.First(s => s.Id == 1051);
        var warp = source.Warps.First(w => w.Destination?.Id == 1052);
        var ship = npc ? host.Drones[host.CreateDrone("handoff", 4942, 685, 0, Ship.PilotTypeWingman)] : host.PlayerShip;
        source.Update(1); // destination has not run this chunk yet
        Assert.NotEqual(warp.Destination!.LastUpdate, ship.LastUpdate);
        ship.Position = new(warp.Warp.Position[0], warp.Warp.Position[1], warp.Warp.Position[2]);
        ship.Velocity = new(-50, 0, 0);
        ship.StateM = ShipControlState.WeaponsMask | ShipControlState.SelectedWeaponTwo;
        host.Mission.Site.HitWarpEvent(ship, warp);
        Assert.Equal(warp.Destination, ship.Sector);
        Assert.Equal(warp.Destination.LastUpdate, ship.LastUpdate);
        Assert.Equal(0, ship.StateM & ShipControlState.WeaponsMask);
        Assert.Equal(ShipControlState.SelectedWeaponTwo, ship.StateM & ShipControlState.SelectedWeaponMask);
    }

    private sealed class WarningMission(ITrainingSite site) : TrainingMission(site)
    {
        public Condition Warning(TargetRef target) => CreateTooCloseCondition(target);
        public override int GetMissionID() => 3;
        public override int GetStartSectorId() => 1010;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new FalseCondition();
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Too_close_warnings_rotate_with_native_range_rounding_and_delayed_two_second_cadence()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        
        // Native geometry from the real mission, independent of replicated interest.
        var real = new TrainingHostSite(host);
        var station = host.Mission.GetStation(host.HomeStationId)!;
        var ship = host.PlayerShip;
        ship.Position = station.Center + new V(station.Radius + ship.Radius * 5, 0, 0);
        ship.Velocity = new V(-.02f, 0, 0);
        // A host-bound site supplies the player's real identity for this primitive check.
        var mission = new WarningMission(real);
        var condition = mission.Warning(new(ObjectType.Station, host.HomeStationId));
        condition.Start();
        for (int i = 0; i < 7; i++)
        {
            host.StepLight(2.01f);
            ship.Velocity = new V(-.02f, 0, 0);
            condition.Evaluate(); condition.Evaluate();
        }
        Assert.True(real.Messages.Count >= 7);
        var messages = real.Messages.Take(7).ToArray();
        Assert.Equal($"Don't get too close to {station.Data.Name}!", messages[0]);
        float rounded = MathF.Floor((ship.Radius * 11 + station.Radius) * .02f) * 50;
        Assert.Equal(FormattableString.Invariant($"Stay at least {rounded:g} meters from {station.Data.Name}!"), messages[1]);
        Assert.Equal("A little close, don't ya think?", messages[2]);
        Assert.Equal("Watch your range!", messages[3]);
        Assert.Equal("Whoa! where are the parachutes?", messages[4]);
        Assert.Equal("You scratch my ship, I scratch your hide!", messages[5]);
        Assert.Equal(messages[0], messages[6]);
    }

    [Fact]
    public void Team_pane_includes_friendly_drones_and_filters_unseen_enemy_drones()
    {
        using var connection = new ConnectionSession(); var world = connection.World;
        world.Apply(new Allegiance.Core.Wire.ExportShip { ShipId = 10, SideId = 0 });
        world.Apply(new Allegiance.Core.Wire.PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "Cadet", DtidDrone = -1 });
        world.Apply(new Allegiance.Core.Wire.SetCluster { SectorId = 1050, Ship = Allegiance.Core.Wire.ServerShipUpdate.FromValues(
            10, 0, default, 0, V.Zero, Orientation.Identity, V.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new Allegiance.Core.Wire.PlayerInfo { ShipId = 11, ISide = 0, CharacterName = "Miner 01", DtidDrone = 40, PilotType = Ship.PilotTypeMiner });
        world.Apply(new Allegiance.Core.Wire.PlayerInfo { ShipId = 12, ISide = 1, CharacterName = "Hidden miner", DtidDrone = 40, PilotType = Ship.PilotTypeMiner });
        world.Apply(new Allegiance.Core.Wire.JoinSide { ShipId = 10, SideId = 0 });
        world.MarkApplied();
        var rows = Rendering.TeamPaneLayout.Entries(world);
        Assert.Contains(rows, r => r.CharacterName == "Miner 01");
        Assert.DoesNotContain(rows, r => r.CharacterName == "Hidden miner");
        Assert.DoesNotContain(world.ScoreRoster, r => r.CharacterName == "Miner 01");
        var controls = new Flight.TeamPaneControls();
        Assert.Contains(controls.BuildRoster(world), r => r.ShipId == 11);
    }

    [Fact]
    public void Command_position_drag_preserves_press_XY_and_uses_native_vertical_height()
    {
        var camera = new Rendering.CommandCamera();
        camera.Center(new(1000, -2000, 0), 0);
        var viewport = new Microsoft.Xna.Framework.Graphics.Viewport(0, 0, 800, 480);
        var press = new Microsoft.Xna.Framework.Vector2(400, 240);
        Assert.True(camera.TryPickPosition(press, press, viewport, out var click));
        Assert.InRange(Microsoft.Xna.Framework.Vector3.Distance(click, camera.Focus), 0, .01f);
        Assert.True(camera.TryPickPosition(press, new(400, 200), viewport, out var above));
        Assert.True(camera.TryPickPosition(press, new(400, 280), viewport, out var below));
        Assert.Equal(click.X, above.X); Assert.Equal(click.Y, above.Y);
        Assert.Equal(click.X, below.X); Assert.Equal(click.Y, below.Y);
        Assert.True(above.Z > 0); Assert.True(below.Z < 0);
        camera.TopDown = true;
        Assert.True(camera.TryPickPosition(press, new(400, 200), viewport, out var steep));
        Assert.Equal(0, steep.Z);
        Assert.Equal(100, Game1.PointerDragCancelSq);
    }

    [Fact]
    public void Training_quickcomm_accepts_native_tilde_without_changing_normal_bindings()
    {
        var map = new Rendering.ActionMap(Rendering.InputActionCatalog.Definitions);
        Assert.False(Game1.QuickChatKeyDown(new(Keys.OemTilde), map, training: false));
        Assert.True(Game1.QuickChatKeyDown(new(Keys.OemTilde), map, training: true));
        Assert.True(Game1.QuickChatKeyDown(new(Keys.R), map, training: false));
        map.ActionFilter = _ => false;
        Assert.False(Game1.QuickChatKeyDown(new(Keys.OemTilde), map, training: true));
    }

    [Fact]
    public void Training_inventory_accepts_native_Ctrl_1_without_changing_normal_bindings()
    {
        var map = new Rendering.ActionMap(Rendering.InputActionCatalog.Definitions);
        var input = new Rendering.EquipmentSwapInput();
        Assert.Null(input.Step(new(Keys.LeftControl, Keys.D1), map, false));
        Assert.Equal("SwapWeapon1", input.Step(new(Keys.LeftControl, Keys.D1), map, false, training: true));
        input.Step(default, map, false, training: true);
        map.ActionFilter = _ => false;
        Assert.Null(input.Step(new(Keys.LeftControl, Keys.D1), map, false, training: true));
    }
}

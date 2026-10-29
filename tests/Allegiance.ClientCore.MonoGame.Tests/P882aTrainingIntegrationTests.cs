using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using Vec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P882aTrainingIntegrationTests : IDisposable
{
    public P882aTrainingIntegrationTests() => TrainingClock.ResetForTests();
    public void Dispose()
    {
        TrainingClock.ResetForTests();
        KeyBindingStore.Instance.Map.ActionFilter = null;
        TrainingController.EndMission();
    }

    [Theory]
    [InlineData(HudStyle.Classic, 2, CameraMode.ExternalOrbit)]
    [InlineData(HudStyle.Modern, 2, CameraMode.ExternalOrbit)]
    [InlineData(HudStyle.Classic, 5, CameraMode.Command)]
    [InlineData(HudStyle.Modern, 5, CameraMode.Command)]
    public void Real_setup_selects_native_camera_radar_and_closes_panes(HudStyle style, int id, CameraMode camera)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_throttle", 1f);
        Set(game, "_minimapExpanded", true);
        Set(game, "_teamPaneVisible", true);
        Call(game, "SetupTrainingView", id);
        Assert.Equal(camera, Get<CameraMode>(game, "_cameraMode"));
        Assert.Equal(RadarMode.All, Get<RadarMode>(game, "_radarMode"));
        Assert.Equal(-1f, Get<float>(game, "_throttle"));
        Assert.False(Get<bool>(game, "_minimapExpanded"));
        Assert.False(Get<bool>(game, "_teamPaneVisible"));
    }

    [Theory]
    [InlineData(HudStyle.Classic, true)]
    [InlineData(HudStyle.Modern, true)]
    [InlineData(HudStyle.Classic, false)]
    [InlineData(HudStyle.Modern, false)]
    public void Ready_handoff_preserves_training_radar_and_throttle_while_normal_launch_keeps_defaults(HudStyle style, bool training)
    {
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new() { World = () => connection.World, Session = connection });
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        Set(game, "_hudSettings", new HudSettings { Style = style });
        if (training) Set(game, "_trainingSession", new TrainingSession(site, new ProbeMission(site, new ProbeCondition(() => false))));
        Set(game, "_throttle", 0.5f);
        Set(game, "_radarMode", RadarMode.Minimal);
        Call(game, "ResetInitialFlightState");
        Assert.Equal(training ? -1f : 1f, Get<float>(game, "_throttle"));
        Assert.Equal(training ? RadarMode.All : RadarMode.Default, Get<RadarMode>(game, "_radarMode"));
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Ordinary_rebound_keys_are_delivered_once_and_rejected_before_handlers(HudStyle style)
    {
        _ = style; // Both renderers consume the same input path.
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new() { World = () => connection.World, Session = connection });
        var mission = new Mission2(site);
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("FireBooster", new(Keys.B, InputModifiers.None), true);
        var gate = new TrainingInputGate(mission.RecordKeyPress);
        map.ActionFilter = gate.Approves;
        gate.Update(new(Keys.B), map);
        Assert.True(site.ConsumeKey(TrainingMissionIds.KeyFireBooster));
        gate.Update(new(Keys.B), map);
        Assert.False(site.ConsumeKey(TrainingMissionIds.KeyFireBooster));
        Assert.True(map.IsDownMostSpecific(new(Keys.B), "FireBooster"));
        foreach (string action in new[] { "Suicide", "TargetSelf", "RejectCommand", "ToggleAutopilot", "ViewCommand", "ToggleInvestmentPane", "ConModeGameState", "ToggleTeleportPane" })
        {
            map.SetBinding(action, new(Keys.F1, InputModifiers.None), true);
            gate.Update(default, map);
            gate.Update(new(Keys.F1), map);
            Assert.False(map.IsDownMostSpecific(new(Keys.F1), action), action);
        }
    }

    [Fact]
    public void Mission5_excludes_exact_native_camera_keys_but_accepts_command_and_acknowledgement()
    {
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new() { World = () => connection.World, Session = connection });
        var mission = new Mission5(site);
        foreach (int key in new[] { 67, 70, 107, 108, 30, 31, 32, 33, 34, 35, 36, 37 })
            Assert.False(mission.RecordKeyPress(key));
        foreach (int key in new[] { 38, 40, 43, 50, 68, 69 }) Assert.True(mission.RecordKeyPress(key));
    }

    [Fact]
    public void Frame_services_front_first_waits_without_advancing_goals_until_next_frame_and_freezes_when_paused()
    {
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new() { World = () => connection.World, Session = connection });
        var visited = new List<int>();
        var goal = new ProbeCondition(() => { visited.Add(3); return false; });
        var mission = new ProbeMission(site, goal);
        mission.Start();
        visited.Clear();
        mission.AddWaitCondition(new ProbeCondition(() => { visited.Add(1); return true; }));
        mission.AddWaitCondition(new ProbeCondition(() => { visited.Add(2); return true; }));
        TrainingClock.Pause();
        Assert.False(mission.Frame());
        Assert.Empty(visited);
        TrainingClock.Continue();
        Assert.False(mission.Frame());
        Assert.Equal(new[] { 2, 1 }, visited);
        mission.Frame();
        Assert.Equal(new[] { 2, 1, 3 }, visited);
        mission.Terminate();
        Assert.True(mission.Frame());
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Universe_has_six_native_sides_all_techs_real_equipment_and_commander(bool nanite)
    {
        Require.Artwork();
        using var host = TrainingHost.Start(nanite ? TrainingHost.Nanite : TrainingHost.BasicWeaponry);
        Assert.Equal(6, host.Mission.Sides.Count);
        Assert.Equal(new short[] { (short)(nanite ? 201 : 25), 35, 18, 25, 35, 18 }, host.Mission.Sides.Select(s => s.Civilization.CivilizationId));
        Assert.Equal(nanite ? "Rixian Unity" : "Iron League", host.Mission.Sides[0].Name);
        Assert.Equal(400, host.Mission.Sides[0].DevelopmentTechs.PopCount);
        Assert.Equal(400, host.Mission.Sides[0].BuildingTechs.PopCount);
        Assert.Equal("Cadet", host.PlayerShip.Name);
        Assert.Equal("Commander", host.CommanderShip.Name);
        Assert.Equal(412, host.CommanderShip.HullType.HullId);
        Assert.Contains(host.PlayerShip.Parts, p => p.EquipmentType == EquipmentType.Weapon);
        Assert.Equal(host.PlayerShip.Parts.Any(p => p.EquipmentType == EquipmentType.Shield) ? 1f : 0f,
            host.PlayerShip.ShieldFraction);
        Assert.Equal(host.PlayerShip.HullType.MaxAmmo, host.PlayerShip.Ammo);
        Assert.Equal(host.PlayerShip.EffectiveMaxEnergy(), host.PlayerShip.Energy);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void No_landing_station_loadout_mounts_preferred_equipment_and_native_five_cargo_slots()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        foreach (var sector in host.Mission.Sectors)
            foreach (var station in sector.StationRuntimes.ToArray()) sector.RemoveStation(station);
        var ship = new Ship(4220, host.PlayerShip.HullType, new(10000, 0, 0), Vec.Zero, Orientation.Identity) { SideId = 0 };
        host.Mission.PlaceShip(ship, host.Mission.Sectors[0]);
        typeof(TrainingHost).GetMethod("EquipDefaultTrainingLoadout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [ship]);
        Assert.Equal(new short[] { 24, 24, 25, 150, 150 }, ship.CargoHold.Select(p => p!.PartId));
        Assert.All(ship.Parts, part => Assert.Equal(ExpandedPartData.FullyMounted, part.FractionMounted));
        Assert.Contains(ship.Parts, p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0);
        Assert.Contains(ship.Parts, p => p.EquipmentType == EquipmentType.Afterburner);
        Assert.Equal(0f, ship.ShieldFraction); // native only charges an actual mounted shield
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
        Assert.Equal(ship.HullType.MaxFuel, ship.Fuel);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Offscreen_cluster_keeps_real_drone_motion_active()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.CommandView);
        int id = host.CreateDrone("Remote", 4221, 412, 1, Ship.PilotTypeWingman);
        host.PlaceDrone(id, 1051, new(10000, 0, 0), System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, null, 0);
        host.CreateWaypoint(4222, new(11000, 0, 0), 1051, 0, false);
        host.IssueCommand(id, TrainingMissionIds.CommandAccepted, null, 4222, null, TrainingMissionIds.CmdGoto);
        var ship = host.Drones[id];
        var before = ship.Position;
        long beforeTicks = ship.Sector!.UpdateCount;
        host.Step();
        Assert.Equal(1051, ship.Sector!.Id);
        Assert.NotEqual(host.PlayerShip.Sector, ship.Sector);
        Assert.True(ship.Position.X > before.X);
        Assert.True(ship.Sector.UpdateCount > beforeTicks); // actual sector tick, independent of the cruise helper
        Assert.Equal(ship.Sector.LastUpdate, ship.LastUpdate);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(8)] [InlineData(10)]
    public void Instructor_stays_docked_without_acquiring_or_firing_at_lesson_targets(int id)
    {
        Require.Artwork();
        var variant = id switch
        {
            3 => TrainingHost.BasicWeaponry, 4 => TrainingHost.EnemyEngagement,
            5 => TrainingHost.CommandView, 6 => TrainingHost.PracticeArena,
            8 => TrainingHost.Nanite, _ => TrainingHost.FreeFlight,
        };
        using var host = TrainingHost.Start(variant);
        for (int tick = 0; tick < 120; tick++) host.StepLight();
        Assert.Equal(host.HomeStationId, host.CommanderShip.StationId);
        Assert.True(host.CommanderShip.StayDocked);
        Assert.Equal(OrderChange.CidDoNothing, host.CommanderShip.CurrentCommandId);
        Assert.Equal(OrderChange.CidDoNothing, host.CommanderShip.AcceptedShipCommandId);
        Assert.Null(host.CommanderShip.AttackTarget);
        Assert.False(host.IsAutopilot(host.CommanderShipId));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Respawn_registration_strict_downtime_and_group_release_use_global_authoritative_presence()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.CommandView);
        using var connection = new ConnectionSession();
        double time = 1;
        var site = new ClientTrainingSite(new() { World = () => connection.World, Session = connection, Host = host, ClockSeconds = () => time });
        var mission = new ProbeMission(site, new ProbeCondition(() => false));
        mission.Start();
        CreateDroneAction Spawn(int id)
        {
            var action = new CreateDroneAction(site, "Respawn", id, 412, 1, Ship.PilotTypeWingman);
            action.SetCreatedLocation(1051, new(100, 0, 0)); // outside player's cluster
            return action;
        }
        mission.AddRespawn(Spawn(4100), 2, spawnOnAdd: true);
        var original = host.Drones[4100];
        time = 20;
        mission.Frame();
        Assert.Same(original, host.Drones[4100]); // no duplicate just because the client cannot see it
        Kill(host, original);
        Assert.False(site.TrainingShipExists(4100));
        mission.Frame(); // first missing observation timestamps the death
        time = 22;
        mission.Frame();
        Assert.False(site.TrainingShipExists(4100)); // strict >, not >=
        time = 22.01;
        mission.Frame();
        Assert.True(site.TrainingShipExists(4100));
        mission.AddRespawn(Spawn(4101), 2, 7);
        mission.AddRespawn(Spawn(4102), 4, 7);
        time = 25;
        mission.Frame();
        Assert.False(site.TrainingShipExists(4101));
        time = 26.02;
        mission.Frame();
        Assert.True(site.TrainingShipExists(4101));
        Assert.True(site.TrainingShipExists(4102));
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(10)]
    public void Real_lethal_damage_dispatches_death_camera_and_restore_policy(int id)
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        using var connection = new ConnectionSession();
        bool camera = false, restored = false;
        double time = 1;
        var site = new ClientTrainingSite(new() { World = () => connection.World, Session = connection, Host = host,
            ClockSeconds = () => time, DeathCamera = (_, _) => camera = true, RestoreCamera = () => restored = true });
        int goalReads = 0;
        TrainingMission mission = id switch
        {
            6 => new ActiveArenaMission(site), 10 => new ActiveFreeFlightMission(site),
            _ => new ProbeMission(site, new ProbeCondition(() => { goalReads++; return false; })),
        };
        var pump = new TrainingSession(site, mission);
        pump.Start();
        var ship = host.PlayerShip;
        var sector = ship.Sector;
        var hull = ship.HullType;
        ship.Position = new(10, 0, 0);
        ship.Velocity = new(100, 0, 0);
        var attackerId = host.CreateDrone("Attacker", 4110, 412, 1, Ship.PilotTypeWingman);
        var attacker = host.Drones[attackerId];
        attacker.Position = Vec.Zero;
        ship.ReceiveDamage(0, 1e10f, host.Mission.LastUpdate, ship.Position, ship.Position, attacker);
        Assert.Null(ship.Sector);
        Assert.Equal(hull, ship.HullType); // no multiplayer pod substitution
        Assert.Equal(50, ship.Velocity.Length(), 3);
        Assert.Equal(1.5f * (ship.Radius + attacker.Radius), ship.Position.Length(), 3);
        pump.Advance();
        Assert.True(camera);
        if (id == 2)
        {
            Assert.True(pump.Mission.RestoreShip());
            Assert.True(restored);
            Assert.Same(sector, ship.Sector);
            Assert.Equal(1, ship.Fraction);
            int reads = goalReads;
            time = 6; pump.Advance(); // strict boundary
            Assert.Equal(reads, goalReads);
            time = 6.01; pump.Advance(); // wait removal still owns this frame
            Assert.Equal(reads, goalReads);
            pump.Advance();
            Assert.Equal(reads + 1, goalReads);
        }
        else
        {
            Assert.False(mission.RestoreShip());
            Assert.Null(ship.Sector);
            Assert.False(restored);
            Assert.True(mission.Frame());
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Last_friendly_restart_death_clears_waits_posts_defeat_and_ends_after_strict_four_seconds()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        using var connection = new ConnectionSession();
        double time = 1;
        var site = new ClientTrainingSite(new() { World = () => connection.World, Session = connection, Host = host, ClockSeconds = () => time });
        var mission = new ProbeMission(site, new ProbeCondition(() => false));
        var pump = new TrainingSession(site, mission);
        pump.Start();
        mission.AddWaitCondition(new ProbeCondition(() => false));
        var station = host.Mission.Sectors.SelectMany(s => s.StationRuntimes).Single(s => s.SideId == 0);
        int attackerId = host.CreateDrone("Attacker", 4111, 412, 1, Ship.PilotTypeWingman);
        var sector = host.Mission.Sectors.Single(s => s.Id == station.ClusterId);
        var projectile = host.Mission.Core.ProjectileTypes.Values.First() with { Power = 1e10f, DamageType = 0, BlastPower = 0, Lifespan = 10 };
        sector.Add(new Projectile(4200, sector, projectile,
            station.Center + new Vec(station.Radius * 2 + 100, 0, 0), new Vec(-10000, 0, 0),
            new Vec(-1, 0, 0), host.Mission.LastUpdate, host.Drones[attackerId]));
        for (int i = 0; i < 10 && !station.Dead; i++) host.StepLight();
        Assert.True(station.Dead);
        pump.Advance();
        Assert.Equal(MissionStage.Started, host.Mission.Stage);
        Assert.Contains(connection.World.Events.Chats, line => line.Message.Contains("stations have all been destroyed"));
        time = 5;
        Assert.False(pump.Advance());
        time = 5.01;
        Assert.False(pump.Advance()); // terminate this frame; retire the next
        Assert.True(pump.Advance());
    }

    private static void Kill(TrainingHost host, Ship target)
    {
        // Real model damage, including the production server kill event and cleanup.
        target.ReceiveDamage(0, 1e10f, host.Mission.LastUpdate, target.Position, target.Position, host.PlayerShip);
        host.Tick();
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Probe_killer_separation_uses_the_actual_probe_radius()
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        var ship = host.PlayerShip;
        var type = host.Mission.Core.ProbeTypes.Values.First(p => p.Radius != ship.Radius);
        var probe = new Probe(4240, ship.Sector!, type, Vec.Zero, host.Mission.LastUpdate, 1);
        ship.Sector!.Add(probe);
        ship.Position = new(1, 0, 0);
        ship.Velocity = new(100, 0, 0);
        ship.ReceiveDamage(0, 1e10f, host.Mission.LastUpdate, ship.Position, ship.Position, probe);
        Assert.Null(ship.Sector);
        Assert.Equal(1.5f * (ship.Radius + type.Radius), ship.Position.Length(), 3);
        Assert.Equal(50, ship.Velocity.Length(), 3);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Production_Game1_death_facilities_save_camera_and_expiry_restores_the_authoritative_ship(HudStyle style)
    {
        Require.Artwork();
        using var host = TrainingHost.Start();
        using var connection = new ConnectionSession();
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_session", connection);
        Set(game, "_embeddedHost", host);
        var site = new ClientTrainingSite(game.CreateTrainingFacilities(2));
        var mission = new ProbeMission(site, new ProbeCondition(() => false));
        var pump = new TrainingSession(site, mission);
        Set(game, "_trainingSession", pump);
        pump.Start();
        Set(game, "_cameraMode", CameraMode.Cockpit);
        int id = host.CreateDrone("Enemy", 4230, 412, 1, Ship.PilotTypeWingman);
        var sector = host.PlayerShip.Sector;
        host.PlayerShip.ReceiveDamage(0, 1e10f, host.Mission.LastUpdate, Vec.Zero, Vec.Zero, host.Drones[id]);
        pump.Advance();
        Assert.Equal(CameraMode.ExternalOverride, Get<CameraMode>(game, "_cameraMode"));
        Assert.True(Get<bool>(game, "_trainingDeathCameraActive"));
        Assert.True(float.IsFinite(host.PlayerShip.Velocity.X)); // coincident launcher: playable native-bug ruling
        Assert.Null(host.PlayerShip.Sector);
        Set(game, "_nowSeconds", 5d);
        Call(game, "UpdateOverrideCameraExpiry");
        Assert.Equal(CameraMode.Cockpit, Get<CameraMode>(game, "_cameraMode"));
        Assert.False(Get<bool>(game, "_trainingDeathCameraActive"));
        Assert.Same(sector, host.PlayerShip.Sector);
        Assert.Equal(1f, host.PlayerShip.Fraction);
    }
    private sealed class ProbeCondition(Func<bool> evaluate) : Condition
    {
        public override bool Evaluate() => evaluate();
    }
    // Keep an active graph while exercising the actual arena lifecycle overrides;
    // the lesson's narrative is independent of its death termination policy.
    private sealed class ActiveArenaMission(ITrainingSite site) : Mission6(site)
    {
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new ProbeCondition(() => false);
    }
    private sealed class ActiveFreeFlightMission(ITrainingSite site) : Mission10(site)
    {
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new ProbeCondition(() => false);
    }
    private sealed class ProbeMission(ITrainingSite site, Condition condition) : TrainingMission(site)
    {
        public override int GetMissionID() => 2;
        public override int GetStartSectorId() => 1010;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => condition;
    }
    private static void Set(Game1 game, string name, object value) => typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
    private static void Call(Game1 game, string name, params object[] args) => typeof(Game1).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, args);
}

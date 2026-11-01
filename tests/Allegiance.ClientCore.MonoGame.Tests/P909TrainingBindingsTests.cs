using System.Reflection;
using static Allegiance.ClientCore.MonoGame.Game1;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.QuickChat;
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
using V = System.Numerics.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P909TrainingBindingsTests : IDisposable
{
    public P909TrainingBindingsTests() => TrainingClock.ResetForTests();
    public void Dispose() { TrainingClock.ResetForTests(); TrainingController.EndMission(); }

    private sealed class Client : IDisposable
    {
        public readonly TrainingHost Host;
        public readonly ConnectionSession Connection = new();
        public readonly Game1 Game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        public readonly ClientTrainingSite Site;
        public Client(HudStyle style, TrainingHost.TrainingVariant? variant = null)
        {
            Host = TrainingHost.Start(variant ?? TrainingHost.PracticeArena);
            Set(Game, "_session", Connection); Set(Game, "_embeddedHost", Host);
            Set(Game, "_hudSettings", new HudSettings { Style = style });
            Set(Game, "_commandCamera", new CommandCamera());
            Set(Game, "_ownSim", new Allegiance.ClientCore.Flight.OwnShipSimulator());
            Set(Game, "_shopCatalog", ShopCatalog.FromCore(Host.Mission.Core));
            Set(Game, "_trainingFrameDelta", 1f / 60f);
            Connection.World.PartTypeResolver = typeof(Game1).GetMethod("ResolveFlightPart",
                BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<short, DataPartTypeIGC?>>(Game);
            var state = typeof(Game1).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
            state.SetValue(Game, Enum.Parse(state.FieldType, "Flying"));
            Site = new(Game.CreateTrainingFacilities(6));
            Set(Game, "_trainingSession", new TrainingSession(Site, new ProbeMission(Site)));
            Host.StartBackgroundTicker();
            Connection.Connect(Host.Port, "p909");
            Connection.SendLogonRequest(new LogonRequest { CharacterName = "p909", ZoneTicket = [1, 2, 3, 4],
                CDKey = "ABCDEFGHIJKL", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                SteamAuthTicket = new byte[16], DrmHash = "p909" });
            Assert.True(Connection.WaitForAck(15000).FValidated);
            Connection.SendPositionReq(0);
            Assert.True(SpinWait.SpinUntil(() => Site.HasPlayerShip, 15000));
            Assert.True(SpinWait.SpinUntil(() => Connection.World.Ships[(short)Host.PlayerShipId].Loadout?.Count > 0, 3000));
            Host.Paused = true;
        }
        public void Dispose() { Connection.Dispose(); Host.Dispose(); }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Production_facilities_read_client_view_pick_and_apply_camera_panes_and_weapon_selection(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style, TrainingHost.Nanite);
        var target = new TargetRef(ObjectType.Ship, c.Host.PlayerShipId);
        Set(c.Game, "_commandee", new AutopilotTarget(TargetKind.Ship, (short)target.Id, "Cadet"));
        c.Host.SetPicked(null); c.Host.SetViewSector(-99); // diagnostic fields must not win
        Assert.True(new GetPickedCondition(c.Site, target.Type, target.Id).Evaluate());
        Assert.True(new GetViewSectorCondition(c.Site, c.Host.StartSectorId).Evaluate());
        Set(c.Game, "_commandee", null);
        Assert.False(c.Site.GetPicked(target));
        new SetDisplayModeAction(c.Site, 13).Execute();
        Assert.Equal(CameraMode.Command, Get<CameraMode>(c.Game, "_cameraMode"));
        Assert.True(Get<CommandCamera>(c.Game, "_commandCamera").TopDown);
        new SetDisplayModeAction(c.Site, 0).Execute();
        Assert.Equal(CameraMode.Cockpit, Get<CameraMode>(c.Game, "_cameraMode"));
        new ShowPaneAction(c.Site, TrainingMissionIds.PaneInventory).Execute();
        Assert.True(Get<ConsolePaneState>(c.Game, "_consolePanes").Inventory);
        Assert.Equal(TeamConsoleSize.Hidden, Get<ConsolePaneState>(c.Game, "_consolePanes").Team);
        Assert.False(Get<bool>(c.Game, "_trainingCommandPane"));
        new ShowPaneAction(c.Site, (int)ConsoleOverlayMask.CommandPane).Execute();
        Assert.True(Get<bool>(c.Game, "_trainingCommandPane"));
        new ShowEyeballAction(c.Site, true).Execute(); Assert.True(c.Connection.World.Ships[(short)c.Host.PlayerShipId].Detected);
        Set(c.Game, "_selectedMount", 1);
        new ToggleWeaponAction(c.Site, 0).Execute(); Assert.Equal(0, Get<int>(c.Game, "_selectedMount"));
        Assert.False(Get<bool>(c.Game, "_groupFire"));
        new ToggleWeaponAction(c.Site, 0).Execute(); Assert.True(Get<bool>(c.Game, "_groupFire"));
        Set(c.Game, "_autoPilotEngaged", false);
        c.Site.SetAutopilot(target, true); // native unconditional setter also permits no current target
        Assert.True(c.Site.GetAutopilot(target));
        Assert.True(c.Host.PlayerShip.Autopilot);
        Assert.Equal(c.Host.PlayerShip.CargoHold.Count(p => p is not null),
            c.Connection.World.Ships[(short)c.Host.PlayerShipId].Cargo!.Count);
        c.Site.SetCargoNan(target);
        var incoming = c.Host.PlayerShip.CargoHold.First(p => p?.EquipmentType == EquipmentType.Weapon)!;
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.Ships[(short)c.Host.PlayerShipId].Cargo!
            .Any(p => p.PartId == incoming.PartId), 3000));
        c.Site.SwapWeapon((int)EquipmentType.Weapon, 0);
        c.Host.Paused = false;
        Assert.True(SpinWait.SpinUntil(() => c.Host.PlayerShip.Parts.Any(p => p.MountId == 0 && p.PartId == incoming.PartId), 3000));
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.Ships[(short)c.Host.PlayerShipId].Loadout!
            .Any(p => p.MountId == 0 && p.PartId == incoming.PartId), 3000));
        c.Host.Paused = true;
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Real_models_supply_geometry_damage_scanner_and_both_command_slots(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        var site = c.Site; var host = c.Host;
        site.CreateDrone("target", 4900, 310, 1, Ship.PilotTypePlayer);
        site.PlaceDrone(4900, host.StartSectorId, new(5000, 0, 0), V.UnitX, V.UnitZ, null, -1);
        var target = new TargetRef(ObjectType.Ship, 4900);
        Assert.True(SpinWait.SpinUntil(() => site.Resolve(target) is not null, 3000));
        Assert.Equal(host.Drones[4900].Radius, site.Resolve(target)!.Radius);
        host.Drones[4900].SetSignature(1e6f);
        var sees = new ShipSeesObjectCondition(site, new(ObjectType.Ship, host.PlayerShipId), target);
        Assert.True(sees.Evaluate()); // beyond old fixed 1500
        host.Drones[4900].SetSignature(0);
        Assert.False(sees.Evaluate());
        host.Drones[4900].SetFraction(.25f);
        Assert.True(new GetShipIsDamagedCondition(site, ObjectType.Ship, 4900, 1).Evaluate());
        host.Drones[4900].SetCommand(OrderChange.CmdAccepted, null, 5);
        host.Drones[4900].SetCommand(OrderChange.CmdCurrent, null, 1);
        Assert.True(new GetCommandCondition(site, target, 5).Evaluate());
        Assert.True(new GetCommandCondition(site, target, 1).Evaluate());
        Assert.False(new GetCommandCondition(site, target, 2).Evaluate());
        var station = host.Mission.GetStation((short)host.HomeStationId)!;
        host.DamageStation(station.StationId, 12500);
        Assert.Equal(station.ArmorFraction, site.GetFraction(new(ObjectType.Station, station.StationId)));
        Assert.True(station.ArmorFraction < 1);
        var stationGeometry = site.Resolve(new(ObjectType.Station, station.StationId))!;
        Assert.Equal(station.Radius, stationGeometry.Radius);
        host.Drones[4900].SetFraction(0);
        Assert.True(new GetShipIsDestroyedCondition(site, target).Evaluate());
        Assert.Null(site.Resolve(new(ObjectType.Station, 32760)));
        var nativeBuoy = new Buoy(new DataBuoyIGC([700, 800, 900], (short)host.StartSectorId, 0, 4930, true));
        host.Mission.AddBuoy(nativeBuoy); // no authored waypoint-table entry
        Assert.Equal(new V(700, 800, 900), site.Resolve(new(ObjectType.Buoy, 4930))!.Position);
        nativeBuoy.Position = new(900, 800, 700);
        Assert.Equal(new V(900, 800, 700), site.Resolve(new(ObjectType.Buoy, 4930))!.Position);
        host.Mission.DeleteBuoy(nativeBuoy);
        Assert.Null(site.Resolve(new(ObjectType.Buoy, 4930)));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Drone_recipes_apply_upgrades_spawn_orientation_fallback_and_accepted_buoy_command(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        var site = c.Site; var host = c.Host;
        site.CreateWaypoint(4902, new(1000, 500, 0), host.StartSectorId, 0, true);
        var recipe = new CreateDroneAction(site, "upgraded", 4901, 310, 1, Ship.PilotTypePlayer);
        recipe.SetCreatedTechLevel(2);
        recipe.SetCreatedLocationAtShipID(host.PlayerShipId, new(10, 20, 30));
        recipe.SetCreatedCommand(4902, ObjectType.Buoy, 5);
        recipe.Execute();
        var drone = host.Drones[4901];
        Assert.Equal(host.PlayerShip.Position.X + 10, drone.Position.X);
        Assert.Equal(host.PlayerShip.Position.Y + 20, drone.Position.Y);
        Assert.Equal(host.PlayerShip.Orientation.GetUp(), drone.Orientation.GetUp());
        Assert.Equal((short)5, drone.AcceptedShipCommandId);
        Assert.IsType<Buoy>(drone.AcceptedShipTarget);
        Assert.Equal(2, host.BuoyConsumerCount(4902));
        site.SetAutopilot(new(ObjectType.Ship, drone.Id), true);
        site.SetAutopilot(new(ObjectType.Ship, drone.Id), false);
        Assert.Null(drone.GotoWaypoint); // shipIGC.cpp:3974-3981 clears the plan, preserving command slots
        Assert.Equal((short)5, drone.AcceptedShipCommandId);
        site.ReleaseConsumer(4902);
        Assert.NotNull(site.Resolve(new(ObjectType.Buoy, 4902)));
        Assert.Contains(drone.Parts, p => p.PartId is 71 or 121);
        recipe.SetCreatedLocationAtShipID(32760, V.Zero);
        recipe.SetCreatedLocation(host.StartSectorId, new(80, 90, 100));
        recipe.Execute();
        Assert.Equal(80, host.Drones[4901].Position.X);
        Assert.NotNull(c.Connection.World.CommandBuoys.GetValueOrDefault((short)4902).Position);
        site.CreateDrone("nanite", 4903, 310, 1, Ship.PilotTypePlayer, techLevel: -2);
        Assert.Contains(host.Drones[4903].Parts, p => p.PartId == 190 && p.MountId == 0);
        var type = host.Mission.Core.ProbeTypes.Values.First();
        site.CreateProbe(4904, type.ExpendableTypeId, new(100, 100, 100), 0, host.StartSectorId);
        Assert.NotNull(host.Mission.GetProbe(4904));
        Assert.Null(host.Mission.GetProbe(4904)!.Launcher);
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.Probes.ContainsKey(4904), 3000));
        Assert.Equal(type.Radius, site.Resolve(new(ObjectType.Probe, 4904))!.Radius);
        host.Drones[4903].StationId = host.Mission.GetStations().First(s => s.SideId == 1).StationId;
        Assert.Equal(-1, site.Resolve(new(ObjectType.Ship, 4903))!.SectorId);
        var dockedFallback = new CreateDroneAction(site, "docked fallback", 4905, 310, 1, Ship.PilotTypePlayer);
        dockedFallback.SetCreatedLocation(host.StartSectorId, new(77, 88, 99));
        dockedFallback.SetCreatedLocationAtShipID(4903, new(10000, 0, 0));
        dockedFallback.Execute();
        Assert.Equal(77, host.Drones[4905].Position.X);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Key_gate_commander_messages_quick_chat_ack_and_eliminated_target_use_real_client(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        var mission = new ProbeMission(c.Site);
        var key = new GetKeyCondition(c.Site, TrainingMissionIds.KeyFireBooster);
        key.Start();
        var secondKey = new GetKeyCondition(c.Site, TrainingMissionIds.KeyFireBooster);
        secondKey.Start();
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("FireBooster", new(Keys.J, InputModifiers.None), true);
        var gate = new TrainingInputGate(mission.RecordKeyPress);
        gate.Update(new(Keys.J), map);
        Assert.True(key.Evaluate()); Assert.False(key.Evaluate());
        Assert.True(secondKey.Evaluate()); Assert.False(secondKey.Evaluate());
        gate.Update(new(Keys.J), map); Assert.False(key.Evaluate());
        gate.Update(default, map); gate.Update(new(Keys.J), map);
        key.Start(); Assert.False(key.Evaluate()); // stale press discarded
        new MessageAction(c.Site, "Instructor message").Execute();
        var line = c.Connection.World.Events.Chats.Last();
        Assert.Equal((short)c.Host.CommanderShipId, line.SidSender);
        Assert.Equal((int)ChatTarget.Individual, line.ChatTarget);
        Assert.Equal(c.Connection.World.MyShipId, line.OidRecipient);
        Set(c.Game, "_qcMenuPath", new List<QuickChatMenu>());
        Set(c.Game, "_qcKeysPrev", new HashSet<Keys>());
        typeof(Game1).GetMethod("SendQuickChatLeaf", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(c.Game, [new QuickChatSendAction { CommandId = 5, Target = ChatTarget.Team,
                Entry = new QuickChatEntry { Index = 0, Caption = "Goto" } }]);
        Assert.True(new CommandAcknowledgedCondition(c.Site, 5).Evaluate());
        var a = new TargetRef(ObjectType.Ship, 4910);
        var b = new TargetRef(ObjectType.Ship, 4911);
        c.Site.CreateDrone("a", a.Id, 310, 1, Ship.PilotTypePlayer);
        c.Site.CreateDrone("b", b.Id, 310, 1, Ship.PilotTypePlayer);
        c.Host.PlayerShip.SetCommand(OrderChange.CmdCurrent, c.Host.Drones[a.Id], 1);
        var cached = new PlayerCurrentTarget(c.Site, true);
        Assert.Equal(a, cached.Resolve());
        c.Host.DestroyDrone(a.Id);
        c.Host.PlayerShip.SetCommand(OrderChange.CmdCurrent, c.Host.Drones[b.Id], 1);
        Assert.Equal(a, cached.Resolve());
        Assert.True(new GetShipIsDestroyedCondition(c.Site, cached).Evaluate());
        var empty = new PlayerCurrentTarget(c.Site, true);
        c.Host.PlayerShip.SetCommand(OrderChange.CmdCurrent, null, -1);
        Assert.Null(empty.Resolve());
        c.Host.PlayerShip.SetCommand(OrderChange.CmdCurrent, c.Host.Drones[b.Id], 1);
        Assert.Null(empty.Resolve()); // native caches the initial invalid TypeIDTarget too
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Respawn_recipe_takes_one_buoy_hold_at_registration(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        var mission = new ProbeMission(c.Site); mission.Start();
        c.Site.CreateWaypoint(4930, new(1000, 500, 0), c.Host.StartSectorId, 0, true);
        int created = c.Host.BuoyConsumerCount(4930);
        var recipe = new CreateDroneAction(c.Site, "carrier", 4931, 310, 1, Ship.PilotTypePlayer);
        recipe.SetCreatedLocation(c.Host.StartSectorId, new(1000, 0, 0));
        recipe.SetCreatedCommand(4930, ObjectType.Buoy, 5);
        new AddRespawnAction(mission, recipe, 1).Execute();   // trainingmission.cpp:304-313
        Assert.Equal(created + 1, c.Host.BuoyConsumerCount(4930));
        recipe.Execute();
        recipe.Execute();
        // The recipe's hold plus the accepted slot's own; neither grows per spawn.
        Assert.Equal(created + 2, c.Host.BuoyConsumerCount(4930));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Respawn_modifications_and_geometry_use_native_radii_and_live_identity(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        var mission = new ProbeMission(c.Site); mission.Start();
        var recipe = new CreateDroneAction(c.Site, "initial", 4920, 310, 1, Ship.PilotTypePlayer);
        recipe.SetCreatedLocation(c.Host.StartSectorId, new(1000, 0, 0));
        new AddRespawnAction(mission, recipe, 1, spawnOnAdd: true).Execute();
        var old = c.Host.Drones[4920];
        var modify = new ModifyCreateDroneActionAction(recipe);
        modify.ModifyCreatedName("replacement"); modify.ModifyCreatedTechLevel(3); modify.Execute();
        c.Host.DestroyDrone(4920);
        Set(c.Game, "_trainingClock", 10d); mission.Frame();
        Set(c.Game, "_trainingClock", 12d); mission.Frame();
        var current = c.Host.Drones[4920];
        Assert.NotSame(old, current); Assert.Equal("replacement", current.Name);
        Assert.Contains(current.Parts, p => p.PartId is 72 or 122);
        var own = new TargetRef(ObjectType.Ship, c.Host.PlayerShipId);
        var target = new TargetRef(ObjectType.Ship, current.Id);
        float separation = c.Site.Resolve(own)!.Radius + current.Radius;
        c.Site.PlaceDrone(current.Id, c.Host.StartSectorId, c.Site.Resolve(own)!.Position + new V(separation + 10, 0, 0), V.UnitX, V.UnitZ, null, -1);
        Assert.False(new ObjectWithinRadiusCondition(c.Site, own, target, 9).Evaluate());
        Assert.True(new ObjectWithinRadiusCondition(c.Site, own, target, 11).Evaluate());
        Assert.True(new ObjectPointingAtCondition(c.Site, own, target).Evaluate());
        c.Site.CreateWaypoint(4921, c.Site.Resolve(target)!.Position + new V(1000, 0, 0), c.Host.StartSectorId, 0, true);
        current.Velocity = new(1, 0, 0);
        var moving = new ObjectMovingTowardsCondition(c.Site, target, new(ObjectType.Buoy, 4921));
        Assert.True(moving.Evaluate());
        current.Velocity = new(-1, 0, 0); Assert.False(moving.Evaluate());
        c.Site.CreateWaypoint(4922, c.Site.Resolve(target)!.Position + new V(0, 1000, 0), c.Host.StartSectorId, 0, true);
        var correction = c.Site.TurnToFace(target, new(ObjectType.Buoy, 4922));
        Assert.True(MathF.Abs(correction.Yaw) + MathF.Abs(correction.Pitch) > 0);
        var turn = new TurnShipAboutCondition(c.Site, target); Assert.False(turn.Start());
        var orientation = Orientation.Identity; orientation.Set(new(0, 1, 0), new(0, 0, 1)); current.SetOrientation(orientation);
        Assert.True(turn.Evaluate());
        new SetCargoNanAction(c.Site, target).Execute();
        Assert.Equal((short)72, current.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0).PartId);
        Assert.Equal((short)190, current.CargoHold[0]!.PartId); // existing gatling stays mounted
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Suspended_sound_uses_real_site_playback_handles_and_wait_list(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        Set(c.Game, "_trainingSounds", new Dictionary<int, Microsoft.Xna.Framework.Audio.SoundEffectInstance>());
        var mission = new ProbeMission(c.Site); mission.Start();
        var field = typeof(TrainingMission).GetField("_waitConditions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var waits = (List<Condition>)field.GetValue(mission)!;
        new SuspendedPlaySoundAction(c.Site, mission, SoundId.None).Execute();
        Assert.Empty(waits);
        new SuspendedPlaySoundAction(c.Site, mission, 6002).Execute();
        Assert.Single(waits);
        mission.Frame(); Assert.Single(waits); // SoundFinished's first evaluation only latches Start
        mission.Frame(); Assert.Empty(waits); // no audio device: production playback returns a stopped handle
    }

    private sealed class ProbeMission(ITrainingSite site) : TrainingMission(site)
    {
        public override int GetMissionID() => 6;
        public override int GetStartSectorId() => 1051;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new Never();
    }
    private sealed class Never : Condition { public override bool Evaluate() => false; }

    private static void Set(Game1 game, string name, object? value) => typeof(Game1).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
}

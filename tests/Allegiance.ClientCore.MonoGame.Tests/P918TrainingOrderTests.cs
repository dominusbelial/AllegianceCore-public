using System.Reflection;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using V = System.Numerics.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P918TrainingOrderTests : IDisposable
{
    public P918TrainingOrderTests() => TrainingClock.ResetForTests();
    public void Dispose() { TrainingClock.ResetForTests(); TrainingController.EndMission(); }
    private static void Set(Game1 game, string field, object? value) => typeof(Game1).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string field) => (T)typeof(Game1).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
    private static TrainingSession StartLesson(P909TrainingBindingsTests.Client c, bool basic = false)
    {
        Set(c.Game, "_trainingSounds", new Dictionary<int, Microsoft.Xna.Framework.Audio.SoundEffectInstance>());
        Get<TrainingSession>(c.Game, "_trainingSession").DetachClientHooks();
        TrainingMission lesson = basic ? new Mission2(c.Site) : new Mission6(c.Site);
        c.Host.SetActiveMission(lesson);
        var session = new TrainingSession(c.Site, lesson);
        Set(c.Game, "_trainingSession", session);
        session.Start();
        return session;
    }
    private static TargetRef Drone(ClientTrainingSite site, TrainingHost host, int id, int pilot = Ship.PilotTypeWingman)
    {
        site.CreateDrone("P918 drone", id, 310, 0, pilot);
        site.PlaceDrone(id, host.StartSectorId, new(4000, 4000, 4000), V.UnitX, V.UnitZ, null, -1);
        return new(ObjectType.Ship, id);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Native_Mission2_waypoint_uses_real_slots_visibility_and_retirement(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style, TrainingHost.BasicFlight);
        var session = StartLesson(c, basic: true);
        Buoy? native = null;
        for (int frame = 1; frame <= 500 && native is null; frame++)
        {
            Set(c.Game, "_trainingClock", frame * .1d); session.Advance();
            native = c.Host.Mission.GetBuoys().FirstOrDefault(b => b.ConsumerCount > 0);
        }
        Assert.NotNull(native);
        Assert.IsType<Mission2>(session.Mission);
        Assert.StartsWith("2.", session.Mission.ActiveGoalPath);
        Assert.True(native.Visible); Assert.Equal(20, native.Radius);
        Assert.Equal(3, native.ConsumerCount); // accepted + current + plan, shipIGC.h:766-773
        var draw = new List<Buoy.Visual>(); c.Host.CopyVisibleBuoys(c.Host.StartSectorId, draw);
        Assert.Contains(draw, b => b.Id == native.Id && b.Radius == 20);
        c.Host.CommanderShip.SetCommand(OrderChange.CmdCurrent, null, OrderChange.CidNone);
        Assert.Equal(2, native.ConsumerCount);
        c.Host.CommanderShip.SetCommand(OrderChange.CmdAccepted, null, OrderChange.CidNone);
        Assert.Equal(0, native.ConsumerCount); Assert.Null(c.Host.Mission.GetBuoy(native.Id));
        c.Host.CopyVisibleBuoys(c.Host.StartSectorId, draw); Assert.DoesNotContain(draw, b => b.Id == native.Id);
        c.Site.CreateWaypoint(7500, new(1000, 1000, 1000), c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, false);
        var drone = Drone(c.Site, c.Host, 7501);
        new SetCommandAction(c.Site, drone, TrainingMissionIds.CommandQueued, new(ObjectType.Buoy, 7500), TrainingMissionIds.CmdGoto).Execute();
        Assert.Equal(2, c.Site.Resolve(new(ObjectType.Buoy, 7500))!.Radius);
        c.Host.CopyVisibleBuoys(c.Host.StartSectorId, draw); Assert.DoesNotContain(draw, b => b.Id == 7500);
        Assert.Equal(1, c.Host.BuoyConsumerCount(7500));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Native_Mission2_legacy_arrival_retires_all_matching_slots(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style, TrainingHost.BasicFlight);
        StartLesson(c, basic: true);
        var target = Drone(c.Site, c.Host, 7571);
        var drone = c.Host.Drones[target.Id];
        var point = new V(drone.Position.X + 150, drone.Position.Y, drone.Position.Z);
        c.Site.CreateWaypoint(7570, point, c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true);
        var buoy = c.Host.Mission.GetBuoy(7570)!;
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandAccepted,
            new(ObjectType.Buoy, 7570), TrainingMissionIds.CmdGoto).Execute();
        Assert.Equal(3, buoy.ConsumerCount);
        for (int tick = 0; tick < 200 && buoy.ConsumerCount > 0; tick++) c.Host.StepLight();
        Assert.Equal(0, buoy.ConsumerCount); Assert.Null(c.Host.Mission.GetBuoy(7570));
        Assert.Null(drone.CommandTarget); Assert.Null(drone.AcceptedShipTarget); Assert.Null(drone.PlanCommandTarget);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Real_death_repeated_spawn_and_recipe_removal_balance_native_holds(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style);
        var session = StartLesson(c);
        c.Site.CreateWaypoint(7510, new(1000, 1000, 1000), c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true);
        var recipe = new CreateDroneAction(c.Site, "respawn", 7511, 310, 1, Ship.PilotTypeWingman);
        recipe.SetCreatedLocation(c.Host.StartSectorId, new(5000, 5000, 5000));
        recipe.SetCreatedCommand(7510, ObjectType.Buoy, TrainingMissionIds.CmdGoto);
        session.Mission.AddRespawn(recipe, 30, -1, false);
        recipe.Execute(); var previous = c.Host.Drones[7511];
        Assert.Equal(4, c.Host.BuoyConsumerCount(7510));
        recipe.Execute(); var drone = c.Host.Drones[7511];
        Assert.NotSame(previous, drone); Assert.Equal(4, c.Host.BuoyConsumerCount(7510));
        Assert.Null(previous.AcceptedShipTarget); Assert.Null(previous.PlanCommandTarget);
        Assert.Equal(DamageResult.Killed, drone.ReceiveDamage(0, 1e9f, c.Host.Mission.LastUpdate,
            drone.Position, drone.Position + new Allegiance.Core.Collision.Vector3(1, 0, 0), c.Host.PlayerShip));
        Assert.Equal(1, c.Host.BuoyConsumerCount(7510)); // recipe survives a death
        Assert.NotNull(c.Site.Resolve(new(ObjectType.Buoy, 7510)));
        session.Mission.RemoveRespawn(7511);
        Assert.Equal(0, c.Host.BuoyConsumerCount(7510)); Assert.Null(c.Site.Resolve(new(ObjectType.Buoy, 7510)));
        c.Host.Dispose(); // mission end remains safe after ordinary death/recipe cleanup
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Queued_non_player_slot_and_real_server_replacement_retire_old_targets(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style);
        StartLesson(c);
        var target = Drone(c.Site, c.Host, 7521);
        c.Site.CreateWaypoint(7520, new(9000, 9000, 9000), c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true);
        var buoy = new TargetRef(ObjectType.Buoy, 7520);
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandQueued, buoy, TrainingMissionIds.CmdGoto).Execute();
        var drone = c.Host.Drones[7521];
        Assert.Equal(OrderChange.CidGoto, drone.QueuedCommandId);
        Assert.Equal((short)7520, drone.QueuedShipTarget!.Id);
        Assert.Null(drone.GotoWaypoint); Assert.Equal(1, c.Host.BuoyConsumerCount(7520));
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandCurrent, buoy, TrainingMissionIds.CmdGoto).Execute();
        Assert.Equal(2, c.Host.BuoyConsumerCount(7520));
        Assert.Null(drone.PlanCommandTarget); Assert.Null(drone.GotoWaypoint);
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandCurrent, null, TrainingMissionIds.CmdNone).Execute();
        Assert.Equal(1, c.Host.BuoyConsumerCount(7520));
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandAccepted, buoy, TrainingMissionIds.CmdGoto).Execute();
        Assert.Equal(4, c.Host.BuoyConsumerCount(7520));
        c.Host.Paused = false;
        c.Connection.SendOrderChange(drone.Id, OrderChange.CidGoto, OrderChange.CmdAccepted);
        Assert.True(SpinWait.SpinUntil(() => drone.AcceptedShipTarget is EquipmentTarget { Body: Station }, 5000),
            "The real server did not apply the NPC return-to-base order.");
        c.Host.Paused = true;
        Assert.Equal(0, c.Host.BuoyConsumerCount(7520)); Assert.Null(c.Host.Mission.GetBuoy(7520));
        Assert.Null(drone.QueuedShipTarget); Assert.Equal(OrderChange.CidNone, drone.QueuedCommandId);
        Assert.IsType<EquipmentTarget>(drone.CommandTarget); Assert.IsType<EquipmentTarget>(drone.PlanCommandTarget);
        Assert.Null(drone.GotoWaypoint); // the retired buoy cannot remain an execution goal
        Assert.Equal(((EquipmentTarget)drone.PlanCommandTarget!).Id, drone.PlanTarget!.Value.StationId);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Real_server_chat_reorder_uses_canonical_slots_and_retires_all_old_buoy_holds(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style);
        StartLesson(c);
        var target = Drone(c.Site, c.Host, 7561);
        c.Site.CreateWaypoint(7560, new(9000, 9000, 9000), c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true);
        var buoy = c.Host.Mission.GetBuoy(7560)!;
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandAccepted,
            new(ObjectType.Buoy, 7560), TrainingMissionIds.CmdGoto).Execute();
        Assert.Equal(3, buoy.ConsumerCount);
        var drone = c.Host.Drones[target.Id];
        c.Host.Paused = false;
        // Exercise the embedded server's ordinary command-chat handler rather
        // than the offline client's local training-order shortcut.
        lock (c.Connection.Gate)
        {
            var localOrder = c.Connection.World.TrainingDroneChat;
            c.Connection.World.TrainingDroneChat = null;
            try
            {
                c.Connection.SendChat("P918 reorder", ChatTarget.Individual, drone.Id,
                    commandId: OrderChange.CidGoto, objectModel: true,
                    oidTarget: (short)c.Host.PlayerShipId, otTarget: (short)IgcObjectType.Ship);
            }
            finally { c.Connection.World.TrainingDroneChat = localOrder; }
        }
        Assert.True(SpinWait.SpinUntil(() => ReferenceEquals(drone.AcceptedShipTarget, c.Host.PlayerShip), 5000));
        c.Host.Paused = true;
        Assert.Same(c.Host.PlayerShip, drone.CommandTarget); Assert.Same(c.Host.PlayerShip, drone.PlanCommandTarget);
        Assert.Equal(OrderChange.CidGoto, drone.PlanCommandId);
        Assert.Equal(0, buoy.ConsumerCount); Assert.Null(c.Host.Mission.GetBuoy(7560));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Tech_cache_survives_drone_death_and_restores_same_hull_at_real_carrier_site(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style);
        StartLesson(c);
        c.Site.CreateDrone("saved tech two", 7530, 310, 0, Ship.PilotTypePlayer, techLevel: 2);
        var saved = c.Host.Drones[7530].Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0);
        Assert.Contains(saved.PartId, new short[] { 71, 121 });
        c.Host.DestroyDrone(7530);
        var target = Drone(c.Site, c.Host, 7531, Ship.PilotTypePlayer);
        var lander = c.Host.Drones[target.Id];
        lander.TryRemovePart(EquipmentType.Weapon, 0, out _);
        for (int mount = -Ship.CargoCapacity; mount < 0; mount++) lander.TryRemoveCargoPart(mount, out _);
        c.Site.CreateDrone("carrier", 7532, 325, 0, Ship.PilotTypeCarrier);
        var carrier = c.Host.Drones[7532];
        Assert.True(c.Host.Mission.Site.LandOnCarrierEvent(carrier, lander, 0));
        Assert.Equal(saved.PartId, lander.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0).PartId);
        Assert.Contains(lander.CargoHold.OfType<MountedPart>(), p => p.PartId == 154);
        // Same base hull overwrites the client-wide cache, regardless of side.
        c.Site.CreateDrone("saved tech three", 7533, 310, 1, Ship.PilotTypePlayer, techLevel: 3);
        Assert.True(c.Host.Mission.Site.LandOnCarrierEvent(carrier, lander, 0));
        Assert.Equal(saved.PartId, lander.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon && p.MountId == 0).PartId);
        lander.TryRemovePart(EquipmentType.Weapon, 0, out _);
        Assert.True(c.Host.Mission.Site.LandOnCarrierEvent(carrier, lander, 0));
        Assert.Contains(lander.Parts, p => p.MountId == 0 && p.PartId is 72 or 122);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Site.CreateDrone("native invalid prox", 7534,
            310, 0, Ship.PilotTypePlayer, techLevel: -4));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Trainee_side_departure_and_mission_exit_retire_slots_and_registered_recipes(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style);
        var session = StartLesson(c);
        c.Site.CreateWaypoint(7540, new(1000, 1000, 1000), c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true);
        var sideBuoy = c.Host.Mission.GetBuoy(7540)!;
        new SetCommandAction(c.Site, new(ObjectType.Ship, c.Host.PlayerShipId), TrainingMissionIds.CommandQueued,
            new(ObjectType.Buoy, 7540), TrainingMissionIds.CmdGoto).Execute();
        Assert.Same(sideBuoy, c.Host.PlayerShip.QueuedShipTarget); Assert.Equal(1, sideBuoy.ConsumerCount);
        var server = typeof(TrainingHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Host)!;
        server.GetType().GetMethod("LeaveSideForSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [c.Host.Mission, c.Host.PlayerShip, (short)0, QuitSideReason.SwitchingSides]);
        Assert.Equal(0, sideBuoy.ConsumerCount); Assert.Null(c.Host.PlayerShip.QueuedShipTarget);
        Assert.Null(c.Host.Mission.GetBuoy(7540));

        c.Site.CreateWaypoint(7541, new(1000, 1000, 1000), c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true);
        var endBuoy = c.Host.Mission.GetBuoy(7541)!;
        var recipe = new CreateDroneAction(c.Site, "exit owner", 7542, 310, 1, Ship.PilotTypeWingman);
        recipe.SetCreatedLocation(c.Host.StartSectorId, new(5000, 5000, 5000));
        recipe.SetCreatedCommand(7541, ObjectType.Buoy, TrainingMissionIds.CmdGoto);
        session.Mission.AddRespawn(recipe, 30, -1, false); recipe.Execute();
        Assert.Equal(4, endBuoy.ConsumerCount);
        c.Host.Dispose();
        Assert.Equal(0, endBuoy.ConsumerCount); Assert.Empty(c.Host.Mission.GetBuoys());
        Assert.True(c.Host.Mission.IsDisposed);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Real_drone_sector_change_preserves_waypoint_then_replacement_releases_exactly_one_hold(HudStyle style)
    {
        Require.Artwork(); using var c = new P909TrainingBindingsTests.Client(style);
        StartLesson(c);
        var target = Drone(c.Site, c.Host, 7550);
        var drone = c.Host.Drones[target.Id];
        c.Site.SetAutopilot(target, false);
        var destination = c.Host.Mission.Sectors.First(s => s.Id != c.Host.StartSectorId);
        c.Site.CreateWaypoint(7551, new(1000, 0, 0), destination.Id, DataBuoyIGC.CBuoyWaypoint, false);
        c.Site.CreateWaypoint(7552, new(2000, 0, 0), c.Host.StartSectorId, DataBuoyIGC.CBuoyWaypoint, true);
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandAccepted, new(ObjectType.Buoy, 7551), TrainingMissionIds.CmdGoto).Execute();
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandQueued, new(ObjectType.Buoy, 7552), TrainingMissionIds.CmdGoto).Execute();
        Assert.Equal(1, c.Host.BuoyConsumerCount(7551)); Assert.Equal(1, c.Host.BuoyConsumerCount(7552));
        c.Site.PlaceDrone(target.Id, destination.Id, new(1000, 1000, 1000), V.UnitX, V.UnitZ, null, -1);
        // A drone is not trekClient.GetShip: changing cluster alone does not
        // clear its ordinary waypoint (trekigc.cpp:2746-2749,2773-2785).
        Assert.Equal(1, c.Host.BuoyConsumerCount(7551));
        drone.SetCommand(OrderChange.CmdAccepted, null, OrderChange.CidNone);
        Assert.Equal(0, c.Host.BuoyConsumerCount(7551)); Assert.Null(c.Host.Mission.GetBuoy(7551));
        Assert.Null(drone.AcceptedShipTarget); Assert.Null(drone.GotoWaypoint);
        Assert.Equal(1, c.Host.BuoyConsumerCount(7552)); Assert.Equal((short)7552, drone.QueuedShipTarget!.Id);
        // A cluster goal completes in the ordinary NPC planner after travel;
        // unlike an unrelated waypoint, its current/accepted/plan slots retire.
        c.Site.SetAutopilot(target, true);
        c.Site.CreateWaypoint(7553, new(3000, 0, 0), c.Host.StartSectorId, DataBuoyIGC.CBuoyCluster, true);
        new SetCommandAction(c.Site, target, TrainingMissionIds.CommandAccepted, new(ObjectType.Buoy, 7553), TrainingMissionIds.CmdGoto).Execute();
        Assert.Equal(3, c.Host.BuoyConsumerCount(7553));
        c.Site.PlaceDrone(target.Id, c.Host.StartSectorId, new(1000, 1000, 1000), V.UnitX, V.UnitZ, null, -1);
        Assert.Equal(3, c.Host.BuoyConsumerCount(7553));
        c.Host.StepLight();
        Assert.Equal(0, c.Host.BuoyConsumerCount(7553)); Assert.Null(c.Host.Mission.GetBuoy(7553));
        // The real Mission6 default-order hook may immediately supply its next
        // plan in this same tick; none of the retired cluster slots may remain.
        Assert.DoesNotContain(new[] { drone.CommandTarget, drone.AcceptedShipTarget, drone.PlanCommandTarget },
            t => t is Buoy b && b.Id == 7553);
        Assert.Equal(1, c.Host.BuoyConsumerCount(7552)); // independent queued target survives completion
        c.Host.DestroyDrone(target.Id);
        Assert.Null(c.Host.Mission.GetBuoy(7552));
    }
}

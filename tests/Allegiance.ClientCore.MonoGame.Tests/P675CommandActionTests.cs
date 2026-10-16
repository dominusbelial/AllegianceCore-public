using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P675CommandActionTests
{
    private static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(id, 0, default, 0, Vector3.Zero,
        Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);
    private static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(10) });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "me" });
        world.Apply(new ExportShip { ShipId = 11, SideId = 1, CharacterName = "enemy" });
        world.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 1, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        world.Apply(new ExportStation { Station = new DataStationIGC([0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 0], 2, 1, 20, 0, 255, 255, "Remote") });
        world.Apply(new ExportAsteroid { Asteroid = new DataAsteroidIGC(1, [0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0], 1, 1, 0x20, 30, 100, 100, "", "", "", 1, "Si1", 255) });
        world.Apply(new ExportWarp { Warp = new DataWarpIGC(40, 41, 100, "", "", "Aleph", [0, 0, 0], [0, 0, 1], [0, 0, 0], 1, 1) });
        return world;
    }
    private static ActionMap Map()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("ExecuteCommand", new(Keys.F16, InputModifiers.None), force: true);
        map.SetBinding("TargetCommand", new(Keys.F17, InputModifiers.None), force: true);
        return map;
    }
    private static void Queue(ClientWorld world, short id = 11, IgcObjectType type = IgcObjectType.Ship, short cid = OrderChange.CidGoto) =>
        world.Apply(new QueuedCommandUpdate { CommandId = cid, TargetType = type, TargetId = id });

    [Theory]
    [InlineData("ViewMissile", 32)] [InlineData("ViewChase", 34)] [InlineData("ViewTurret", 35)]
    [InlineData("TargetCommand", 89)] [InlineData("ExecuteCommand", 156)]
    public void Actions_are_rebindable_and_native_keyboard_defaults_are_unbound(string action, int tk)
    {
        var native = DefaultInputMap.ParseKeyMap(DefaultInputMap.EmbeddedText);
        Assert.DoesNotContain(native, b => b.TkNumber == tk);
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.Equal(Keys.None, map.BindingOf(action).Key);
        map.SetBinding(action, new(Keys.F18, InputModifiers.Control));
        Assert.True(map.IsDownMostSpecific(new(Keys.F18, Keys.LeftControl), action));
        Assert.False(map.IsDownMostSpecific(new(Keys.F18), action));
        var restored = new ActionMap(InputActionCatalog.Definitions); restored.ApplyJson(map.ToJson());
        Assert.Equal(map.BindingOf(action), restored.BindingOf(action));
    }

    [Theory]
    [InlineData((int)CameraMode.ExternalMissile)] [InlineData((int)CameraMode.Chase)] [InlineData((int)CameraMode.ExternalTurret)]
    public void Discrete_camera_presses_toggle_to_requested_mode_and_back_and_observe_suppressed_holds(int requested)
    {
        bool held = false; var mode = (CameraMode)requested;
        Assert.Equal(mode, Game1.CameraKeyTransition(CameraMode.Command, mode, true, ref held, false));
        Assert.Null(Game1.CameraKeyTransition(mode, mode, true, ref held, false));
        Assert.Null(Game1.CameraKeyTransition(mode, mode, false, ref held, false));
        Assert.Equal(CameraMode.Cockpit, Game1.CameraKeyTransition(mode, mode, true, ref held, false));
        Game1.CameraKeyTransition(mode, mode, false, ref held, false);
        Assert.Null(Game1.CameraKeyTransition(mode, mode, true, ref held, true));
        Assert.Null(Game1.CameraKeyTransition(mode, mode, true, ref held, false));
    }

    [Theory]
    [InlineData(false, 11, (int)IgcObjectType.Ship)]
    [InlineData(true, 11, (int)IgcObjectType.Ship)]
    [InlineData(true, 20, (int)IgcObjectType.Station)]
    [InlineData(true, 30, (int)IgcObjectType.Asteroid)]
    [InlineData(true, 40, (int)IgcObjectType.Warp)]
    public void Accept_and_execute_promote_arbitrary_targets_but_only_execute_engages(bool execute, short id, int type)
    {
        var world = World(); Queue(world, id, (IgcObjectType)type);
        var input = new CommandActionInput();
        var result = input.Step(new(execute ? Keys.F16 : Keys.Home), false, Map(), world);
        Assert.Equal(OrderChange.CmdQueued, result.Request!.Command);
        Assert.Equal(OrderChange.CidGoto, result.Request.CommandId);
        Assert.Null(world.MyQueuedCommand);
        Assert.Equal(id, world.MyAcceptedCommand!.Value.TargetId);
        Assert.Equal(id, Game1.CommandSelection(world, result.Selection)!.Value.Id);
        Assert.True(result.ChangeSelection); Assert.Equal(execute, result.Engage);
        Assert.Null(input.Step(new(execute ? Keys.F16 : Keys.Home), false, Map(), world).Request);
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public void Execute_accepts_while_docked_or_parented_but_does_not_engage(bool docked, bool parented)
    {
        var world = World();
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = docked ? (short)99 : (short)-1,
            ParentId = parented ? (short)12 : (short)-1, State = ShipStatusMessage.StateFlying });
        Queue(world);
        var result = new CommandActionInput().Step(new(Keys.F16), false, Map(), world);
        Assert.NotNull(world.MyAcceptedCommand);
        Assert.Equal(!docked && !parented, result.Engage);
    }

    [Fact]
    public void Target_command_uses_accepted_target_even_when_a_different_order_is_queued_and_clears_missing_selection()
    {
        var world = World(); var map = Map(); var input = new CommandActionInput();
        Queue(world, 20, IgcObjectType.Station);
        input.Step(new(Keys.Home), false, map, world);
        input.Step(default, false, map, world);
        Queue(world, 11);
        var result = input.Step(new(Keys.F17), false, map, world);
        Assert.Equal(20, Game1.CommandSelection(world, result.Selection)!.Value.Id);
        Assert.Equal(OrderChange.CidGoto, Game1.CommandSelection(world, result.Selection)!.Value.CommandId);
        Assert.False(result.Engage); Assert.Null(result.Request);
        Assert.NotNull(world.MyQueuedCommand);
        world.ClearAcceptedCommand(); input.Step(default, false, map, world);
        result = input.Step(new(Keys.F17), false, map, world);
        Assert.True(result.ChangeSelection); Assert.Null(result.Selection);
    }

    [Fact]
    public void Reject_keeps_accepted_clear_keeps_queue_and_reset_discards_both_slots()
    {
        var world = World(); var map = Map(); var input = new CommandActionInput(); Queue(world);
        input.Step(new(Keys.Home), false, map, world);
        Queue(world, 20, IgcObjectType.Station);
        var rejected = input.Step(new(Keys.End), false, map, world);
        Assert.Equal(OrderChange.CmdQueued, rejected.Request!.Command);
        Assert.Null(world.MyQueuedCommand); Assert.NotNull(world.MyAcceptedCommand);
        Queue(world, 20, IgcObjectType.Station);
        var clear = input.Step(new(Keys.Delete), false, map, world);
        Assert.Equal(OrderChange.CmdAccepted, clear.Request!.Command);
        Assert.Null(world.MyAcceptedCommand); Assert.NotNull(world.MyQueuedCommand);
        world.BeginNextMission(); Assert.Null(world.MyAcceptedCommand); Assert.Null(world.MyQueuedCommand);
    }

    [Fact]
    public void Invalid_or_stale_orders_do_not_promote_or_clear_and_composition_and_entry_held_keys_do_not_leak()
    {
        var world = World(); var map = Map(); var input = new CommandActionInput(); Queue(world, 999);
        Assert.Null(input.Step(new(Keys.F16), false, map, world).Request);
        Assert.NotNull(world.MyQueuedCommand); Assert.Null(world.MyAcceptedCommand);
        input.Step(default, false, map, world); Queue(world, 10);
        Assert.Null(input.Step(new(Keys.F16), false, map, world).Request); // self illegal
        input.Step(default, false, map, world); Queue(world);
        Assert.Null(input.Step(new(Keys.F16), true, map, world).Request);
        Assert.Null(input.Step(new(Keys.F16), false, map, world).Request);
        input.Prime(new(Keys.F16), map);
        Assert.Null(input.Step(new(Keys.F16), false, map, world).Request);
        input.Step(default, false, map, world);
        Assert.NotNull(input.Step(new(Keys.F16), false, map, world).Request);
    }

    [Fact]
    public void Join_order_changes_wing_without_targeting_or_overwriting_accepted_or_engaging()
    {
        var world = World(); var input = new CommandActionInput(); var map = Map();
        Queue(world, 20, IgcObjectType.Station); input.Step(new(Keys.Home), false, map, world);
        // Native SET_WINGID is a same-team relay; join a teammate's wing.
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(new ExportShip { ShipId = 11, SideId = 0, CharacterName = "wing leader" });
        world.Apply(new SetWingId { ShipID = 11, WingId = 4 }); Queue(world, cid: OrderChange.CidJoin);
        var result = input.Step(new(Keys.F16), false, map, world);
        Assert.Equal<short?>(4, result.JoinWing); Assert.Equal(4, world.Ships[10].WingId);
        Assert.False(result.ChangeSelection); Assert.False(result.Engage);
        Assert.Equal(20, world.MyAcceptedCommand!.Value.TargetId); Assert.Null(world.MyQueuedCommand);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Pickup_accept_selects_without_engaging_and_execute_engages_with_real_rescue_and_pod_hulls()
    {
        Require.Artwork(); var world = World();
        world.Apply(new MissionDef { SzIGCStaticFile = "PCore001" });
        world.Apply(new JoinedMission { ShipId = 10 });
        var hulls = world.MissionCore!.HullTypes.Values;
        short rescuer = hulls.First(h => (h.HabmCapabilities & DataHullTypeIGC.HabmRescue) != 0).HullId;
        short pod = hulls.First(h => (h.HabmCapabilities & DataHullTypeIGC.HabmLifepod) != 0).HullId;
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(10) });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, HullId = rescuer });
        world.Apply(new ExportShip { ShipId = 11, SideId = 0, HullId = pod });
        var input = new CommandActionInput(); var map = Map(); Queue(world, cid: OrderChange.CidPickup);
        Assert.False(input.Step(new(Keys.Home), false, map, world).Engage);
        Queue(world, cid: OrderChange.CidPickup);
        Assert.True(input.Step(new(Keys.F16), false, map, world).Engage);
    }
    private static FireMissile Missile(short id, short launcher, short target = -1) => new()
    {
        LauncherId = launcher, TargetId = target, TargetType = target >= 0 ? (int)IgcObjectType.Ship : -1, ClusterId = 1,
        Entries = [new MissileLaunchData(new Vector3(id, 0, 0), new Vector3(0, 0, 1), Vector3.Zero, id)],
    };

    [Fact]
    public void Missile_camera_tracks_only_source_ships_last_shot_preserves_target_through_updates_and_falls_back_after_expiry()
    {
        var world = World();
        world.Apply(new SingleShipUpdate { OidTarget = 11, Ship = ServerShipUpdate.FromValues(11, 0, default, 0, new Vector3(200, 0, 0),
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(Missile(1, 10, 11)); world.Apply(Missile(2, 11)); // newer enemy missile must never win
        world.Apply(new MissileUpdate { Records = [new MissileStateRecord(1, 1, 1, new Vector3(50, 0, 0), new Vector3(0, 0, 1))] });
        Assert.True(Game1.MissileCameraFrame(world, 10, out var missile, out var focus));
        Assert.Equal(new Vector3(50, 0, 0), missile); Assert.Equal(new Vector3(200, 0, 0), focus);
        Assert.Equal(10, world.Missiles[1].LauncherId); Assert.Equal(11, world.Missiles[1].TargetId);
        world.Apply(Missile(3, 10));
        world.Apply(new MissileDestroyed { MissileId = 3 });
        Assert.False(Game1.MissileCameraFrame(world, 10, out _, out _)); // don't revive older own shot #1
        Assert.Equal(2, world.Missiles.Count);
        world.Reset(); Assert.Null(world.LastMissileFiredBy(10));
    }

    [Fact]
    public void Gunner_missile_camera_uses_parent_source_and_missing_target_uses_source_position()
    {
        var world = World(); world.Apply(Missile(1, 11));
        world.Apply(new SingleShipUpdate { OidTarget = 11, Ship = Pose(11) });
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = -1, ParentId = 11, State = ShipStatusMessage.StateTurret });
        Assert.True(Game1.MissileCameraFrame(world, 10, out var missile, out var focus));
        Assert.Equal(new Vector3(1, 0, 0), missile); Assert.Equal(Vector3.Zero, focus);
        world.BeginNextMission(); Assert.Null(world.LastMissileFiredBy(11));
    }

    [Theory]
    [InlineData((int)CameraMode.ExternalMissile)] [InlineData((int)CameraMode.Chase)]
    public void Docked_discrete_views_return_to_hangar_cockpit_instead_of_activating_external_camera(int requested)
    {
        bool held = false;
        Assert.Equal(CameraMode.Cockpit, Game1.CameraKeyTransition(CameraMode.Command, (CameraMode)requested,
            true, ref held, false, docked: true));
        Assert.Null(Game1.CameraKeyTransition(CameraMode.Cockpit, (CameraMode)requested,
            true, ref held, false, docked: false)); // held through launch cannot fire again
    }

}

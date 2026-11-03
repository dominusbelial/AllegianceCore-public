using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

public sealed class P918CommandSlotsTests
{
    private static Mission World(bool training = true)
    {
        var body = new byte[DataConstantsIGC.Size];
        var core = StaticCore.Load(new IgcFile { Wrapper = IgcWrapper.VersionAndDataSize, Version = 1,
            Body = body, BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new(IgcObjectType.Constants, body.Length, 0)] });
        var map = MissionMap.FromLists([Cluster(0), Cluster(1), Cluster(2)], [], [], []);
        var m = Mission.Assemble(core, map, [], new NullIgcSite());
        m.IsTraining = training;
        m.NativeGotoPlanEnabled = true;
        return m;
    }
    private static DataClusterIGC Cluster(short id) => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = id };
    private static Ship Place(Mission m, bool player = false)
    {
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
        { HitPoints = 100, Length = 10, Mass = 1, Speed = 100, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1] };
        var ship = new Ship(50, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { IsNpc = !player, PilotType = player ? Ship.PilotTypePlayer : Ship.PilotTypeWingman, Autopilot = true, SideId = 0 };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }
    private static Buoy Waypoint(Mission m, short id = 100, byte kind = DataBuoyIGC.CBuoyWaypoint, short sector = 0, bool visible = true)
    {
        var buoy = new Buoy(new([1000, 0, 0], sector, kind, id, visible));
        m.AddBuoy(buoy); return buoy;
    }

    // Igc/shipIGC.h:731-782. These are independent native slot counts, not
    // host-issued order diagnostics. Every direct setter uses the same owner.
    [Fact]
    public void Four_slots_hold_once_and_identical_writes_cannot_retire_a_live_buoy()
    {
        using var m = World(); var ship = Place(m); var buoy = Waypoint(m);
        ship.SetCommand(OrderChange.CmdAccepted, buoy, OrderChange.CidGoto);
        Assert.Equal(3, buoy.ConsumerCount);
        Assert.Equal(OrderChange.CidGoto, ship.CurrentCommandId);
        Assert.Equal(OrderChange.CidGoto, ship.AcceptedShipCommandId);
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);
        Assert.Same(buoy, ship.CommandTarget); Assert.Same(buoy, ship.PlanCommandTarget);
        ship.SetCommand(OrderChange.CmdQueued, buoy, OrderChange.CidGoto);
        Assert.Equal(4, buoy.ConsumerCount);
        ship.SetCommand(OrderChange.CmdAccepted, buoy, OrderChange.CidGoto);
        Assert.Equal(4, buoy.ConsumerCount); Assert.Same(buoy, m.GetBuoy(100));
        ship.CommandTarget = null; Assert.Equal(3, buoy.ConsumerCount);
        ship.QueuedShipTarget = null; Assert.Equal(2, buoy.ConsumerCount);
        ship.AcceptedShipTarget = null; Assert.Equal(1, buoy.ConsumerCount);
        ship.PlanCommandTarget = null;
        Assert.Equal(0, buoy.ConsumerCount); Assert.Null(m.GetBuoy(100));
    }

    [Fact]
    public void Replacement_termination_and_mission_retirement_release_each_actual_slot_once()
    {
        using var m = World(); var ship = Place(m); var first = Waypoint(m); var second = Waypoint(m, 101);
        ship.SetCommand(OrderChange.CmdAccepted, first, OrderChange.CidGoto);
        ship.SetCommand(OrderChange.CmdQueued, first, OrderChange.CidGoto);
        ship.SetCommand(OrderChange.CmdAccepted, second, OrderChange.CidGoto);
        Assert.Equal(1, first.ConsumerCount); Assert.Equal(3, second.ConsumerCount);
        Assert.True(ship.Sector!.Terminate(ship));
        Assert.False(ship.Sector.Terminate(ship));
        Assert.Empty(m.GetBuoys()); Assert.Equal(0, first.ConsumerCount); Assert.Equal(0, second.ConsumerCount);
        m.PlaceShip(ship, m.Sectors[0]); var end = Waypoint(m, 102);
        ship.SetCommand(OrderChange.CmdAccepted, end, OrderChange.CidGoto);
        ship.SetCommand(OrderChange.CmdQueued, end, OrderChange.CidGoto);
        m.Dispose(); m.Dispose();
        Assert.Equal(0, end.ConsumerCount); Assert.Null(ship.AcceptedShipTarget);
        Assert.Null(ship.QueuedShipTarget); Assert.Null(ship.CommandTarget); Assert.Null(ship.PlanCommandTarget);
    }

    [Fact]
    public void Direct_sim_goto_and_autopilot_changes_retire_the_plan_hold_only()
    {
        using var m = World(); var ship = Place(m); var buoy = Waypoint(m);
        ship.SetCommand(OrderChange.CmdAccepted, buoy, OrderChange.CidGoto);
        ship.SetGoto(new(GotoKind.Waypoint, 0, buoy.Position, TargetType: IgcObjectType.Buoy, TargetId: buoy.Id));
        Assert.Equal(3, buoy.ConsumerCount); Assert.Single(m.GetBuoys()); // no duplicate lazy buoy
        ship.SetGoto(null);
        Assert.Equal(2, buoy.ConsumerCount); Assert.Null(ship.PlanCommandTarget);
        ship.Autopilot = true;
        Assert.Equal(3, buoy.ConsumerCount); Assert.Same(buoy, ship.PlanCommandTarget);
        ship.Autopilot = false;
        Assert.Equal(2, buoy.ConsumerCount); Assert.Same(buoy, ship.AcceptedShipTarget);
        ship.Sector!.Terminate(ship); Assert.Equal(0, buoy.ConsumerCount);
    }

    [Fact]
    public void Direct_accepted_replacement_updates_execution_and_preserves_other_command_slots()
    {
        using var m = World(); var ship = Place(m); var first = Waypoint(m); var next = Waypoint(m, 101);
        ship.SetCommand(OrderChange.CmdQueued, first, OrderChange.CidGoto);
        ship.SetCommand(OrderChange.CmdAccepted, first, OrderChange.CidGoto);
        Assert.Equal(first.Id, ship.GotoWaypoint!.Value.TargetId);
        ship.SetCommand(OrderChange.CmdAccepted, next, OrderChange.CidGoto);
        Assert.Equal(next.Id, ship.GotoWaypoint!.Value.TargetId);
        Assert.Equal(1, first.ConsumerCount); Assert.Equal(3, next.ConsumerCount);
        Assert.Same(first, ship.QueuedShipTarget);
        ship.SetCommand(OrderChange.CmdPlan, null, OrderChange.CidNone);
        Assert.Null(ship.GotoWaypoint); Assert.Same(next, ship.AcceptedShipTarget);
        Assert.Same(next, ship.CommandTarget); Assert.Equal(2, next.ConsumerCount);
        Assert.Equal(1, first.ConsumerCount);
    }

    // WinTrek/trekigc.cpp:2773-2785; Igc/shipIGC.cpp:3394-3440.
    // Side and cluster changes do not blindly discard ordinary waypoints.
    [Fact]
    public void Cluster_arrival_retires_matching_slots_and_side_changes_preserve_live_waypoints()
    {
        using var m = World(); var ship = Place(m, player: true);
        m.TrainingPlayerShipId = ship.Id;
        var arrival = Waypoint(m, kind: DataBuoyIGC.CBuoyCluster, sector: 1);
        var waypoint = Waypoint(m, 101);
        ship.SetCommand(OrderChange.CmdQueued, waypoint, OrderChange.CidGoto);
        ship.SetCommand(OrderChange.CmdAccepted, arrival, OrderChange.CidGoto);
        ship.SetCommand(OrderChange.CmdCurrent, arrival, OrderChange.CidGoto);
        ship.Autopilot = true;
        ship.Sector = m.Sectors[2]; Assert.Equal(3, arrival.ConsumerCount);
        ship.SideId = 1; Assert.Equal(3, arrival.ConsumerCount); Assert.Equal(1, waypoint.ConsumerCount);
        ship.Sector = m.Sectors[1];
        Assert.Null(m.GetBuoy(100)); Assert.Equal(0, arrival.ConsumerCount);
        Assert.Same(waypoint, ship.QueuedShipTarget); Assert.Equal(1, waypoint.ConsumerCount);
        ship.Mission = null;
        Assert.Equal(0, waypoint.ConsumerCount); Assert.Empty(m.GetBuoys());
    }

    [Fact]
    public void Npc_cluster_goto_completion_retires_every_matching_slot_after_real_sector_transfer()
    {
        using var m = World(); var ship = Place(m);
        var arrival = Waypoint(m, kind: DataBuoyIGC.CBuoyCluster, sector: 1);
        ship.SetCommand(OrderChange.CmdAccepted, arrival, OrderChange.CidGoto);
        ship.SetGoto(new GotoOrder(GotoKind.Cluster, 1, arrival.Position,
            TargetType: IgcObjectType.Buoy, TargetId: arrival.Id));
        Assert.Equal(3, arrival.ConsumerCount);
        ship.Sector!.Remove(ship); m.PlaceShip(ship, m.Sectors[1]);
        Assert.Equal(3, arrival.ConsumerCount); // NPC arrival is not the local GetShip callback
        typeof(Ship).GetMethod("ApplyNativeNpcAutopilot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(ship, [1d]); // normal planner completion, shipIGC.cpp:2795-2807
        Assert.Null(ship.CommandTarget); Assert.Null(ship.AcceptedShipTarget); Assert.Null(ship.PlanCommandTarget);
        Assert.Null(ship.GotoWaypoint); Assert.Equal(0, arrival.ConsumerCount); Assert.Null(m.GetBuoy(arrival.Id));
    }

    [Theory]
    [InlineData(true, 20f)] [InlineData(false, 2f)]
    public void Visibility_requires_a_consumer_and_preserves_native_geometry(bool visible, float radius)
    {
        using var m = World(); var ship = Place(m); var buoy = Waypoint(m, visible: visible);
        Assert.Equal(radius, buoy.Radius); Assert.False(buoy.Visible);
        ship.SetCommand(OrderChange.CmdQueued, buoy, OrderChange.CidGoto);
        Assert.Equal(visible, buoy.Visible);
        ship.SetCommand(OrderChange.CmdQueued, null, OrderChange.CidNone);
        Assert.False(buoy.Visible); Assert.Null(m.GetBuoy(100));
    }

    [Fact]
    public void Multiplayer_keeps_existing_two_slot_events_and_lazy_waypoint_ownership()
    {
        using var m = World(training: false); var ship = Place(m); var buoy = Waypoint(m);
        ship.SetCommand(OrderChange.CmdAccepted, buoy, OrderChange.CidGoto);
        Assert.Same(buoy, ship.AcceptedShipTarget); Assert.Null(ship.CommandTarget); Assert.Null(ship.PlanCommandTarget);
        ship.SetCommand(OrderChange.CmdQueued, buoy, OrderChange.CidGoto);
        Assert.Null(ship.QueuedShipTarget); Assert.Equal(OrderChange.CidNone, ship.QueuedCommandId);
        Assert.Equal(0, buoy.ConsumerCount);
        ship.SetGoto(new(GotoKind.Waypoint, 0, new(2000, 0, 0)));
        Assert.Equal(2, m.GetBuoys().Count);
        ship.SetGoto(null); Assert.Single(m.GetBuoys());
        ship.Sector!.Terminate(ship);
        // Existing multiplayer retirement retains generic slots and the authored
        // buoy, and only releases the lazy navigation buoy (P869).
        Assert.Same(buoy, ship.AcceptedShipTarget); Assert.Same(buoy, m.GetBuoy(100));
    }
}

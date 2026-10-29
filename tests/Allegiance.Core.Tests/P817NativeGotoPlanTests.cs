using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using G = Allegiance.Core.Sim.NativeGotoPlan;
using M = Allegiance.Core.Sim.DockApproach.Mask;

namespace Allegiance.Core.Tests;

/// <summary>Native rule tests; citations refer to the read-only Allegiance source.</summary>
public sealed class P817NativeGotoPlanTests
{
    private static G.Mover Mover(Vector3 position = default, Vector3 velocity = default) => new(
        position, velocity, Orientation.Identity, 5,
        new(100, 100, 2, 2, 100, 100, 0, 0, 1000), 2, 100);
    private static G.Target Target(float distance, G.Objective objective = G.Objective.Goto, G.Kind kind = G.Kind.Asteroid) =>
        new(7, 1, kind, objective, new(0, 0, -distance), 10);
    private static G.Result Execute(G.Mover mover, G.Target target, G.Obstacle[]? obstacles = null, bool boost = false) =>
        G.Execute(mover, target, [], obstacles ?? [], .05f, .5f, boost);

    // common.cpp:1936-1983: fixed numeric values from integrating reverse thrust + drag.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(.1f, 0)]
    [InlineData(10, 8.8392216f)]
    [InlineData(50, 153.42641f)]
    [InlineData(100, 450.69385f)]
    public void Reverse_drag_stopping_distance(float speed, float expected) =>
        Assert.Equal(expected, G.StoppingDistance(Mover(velocity: new(0, 0, -speed)), .5f), .001f);

    // common.cpp:1343-1379: current sphere danger, rest sphere, stop completion, free travel.
    [Theory]
    [InlineData(14, 0, 0, M.Pivot)]
    [InlineData(20, 0, 0, M.Pivot | M.Finished)]
    [InlineData(50, 10, 30, M.KillThrottle)]
    [InlineData(100, 10, 30, 0)]
    public void Goto_masks_preserve_current_and_rest_position_tests(float distance, float stopping, float drift, uint mask)
    {
        var mover = Mover();
        var step = G.GetGotoPosition(mover, Target(distance), [], stopping, new(0, 0, -drift));
        Assert.Equal(mask, step.Mask);
    }
    // common.cpp:1547-1555: a warp is a Fast entry, and cannot stop short.
    [Fact]
    public void Warp_entry_keeps_full_throttle_inside_stopping_distance()
    {
        var r = Execute(Mover(velocity: new(0, 0, -100)), Target(20, G.Objective.Enter, G.Kind.Warp));
        Assert.Equal(M.Fast, r.Step.Mask); Assert.Equal(1, r.Controls.Throttle); Assert.False(r.Done);
    }
    // common.cpp:1850-1862,1877-1919: invalidate warp on either cluster change.
    [Fact]
    public void Waypoint_replans_on_ship_and_target_cluster_changes_and_enter_completion()
    {
        G.Waypoints state = default;
        var target = Target(100) with { Cluster = 3 };
        Assert.True(state.Refresh(1, target)); state.SetWarp(10);
        Assert.False(state.Refresh(1, target)); Assert.Equal(3, state.Mask);
        Assert.True(state.Refresh(2, target)); Assert.Equal(1, state.Mask); Assert.Equal(-1, state.WarpId);
        state.SetWarp(20); Assert.True(state.Refresh(2, target with { Cluster = 2 })); Assert.Equal(1, state.Mask);
        var warp = target with { Kind = G.Kind.Warp, Objective = G.Objective.Enter, WarpDestination = 4 };
        state.Refresh(2, warp); Assert.False(state.Refresh(4, warp)); Assert.Equal(0, state.Mask);
    }
    // common.cpp:894-1011: visible exits only, sorted by initial distance, coward bypass and brave retry.
    [Fact]
    public void FindPath_obeys_visibility_initial_distance_and_coward_final_cluster_exception()
    {
        G.Edge[] edges = [new(1, 2, 10, new(0, 0, 90), true), new(1, 3, 20, new(0, 0, 10), true),
            new(2, 4, 11, default, true), new(3, 4, 21, default, true)];
        G.Cluster[] clusters = [new(1, true), new(2, true), new(3, false), new(4, false)];
        var work = new G.Path[edges.Length + 1]; var seen = new short[5];
        Assert.Equal(10, G.FindPath(1, 4, default, true, edges, clusters, work, seen));
        Assert.Equal(20, G.FindPath(1, 4, default, false, edges, clusters, work, seen));
        edges[0] = edges[0] with { Visible = false };
        Assert.Equal(-1, G.FindPath(1, 4, default, true, edges, clusters, work, seen));
        Assert.Equal(20, G.FindPath(1, 4, default, false, edges, clusters, work, seen));
        Assert.Equal(20, G.FindPath(1, 3, default, true, edges, clusters, work, seen));
    }
    // common.cpp:1693-1790,2000-2016: lateral buttons, goal facing retained, throttle killed.
    [Fact]
    public void Dodge_uses_rcs_not_away_facing_and_respects_skip_and_ships_only()
    {
        var mover = Mover(velocity: new(0, 0, -60));
        G.Obstacle rock = new(8, G.Kind.Asteroid, new(0, 0, -50), default, 10);
        var r = Execute(mover, Target(500), [rock]);
        Assert.Equal(-1, r.Controls.Throttle); Assert.Equal(0, r.Controls.Yaw); Assert.Equal(0, r.Controls.Pitch);
        Assert.NotEqual(0, r.Buttons & ShipControlState.MoveButtonsMask);
        Assert.False(G.Dodge(mover, [rock], 8, false, out _));
        Assert.False(G.Dodge(mover, [rock], -1, true, out _));
        Assert.False(G.Dodge(mover, [rock with { Ignore = true }], -1, false, out _));
    }
    // common.cpp:1723 and :2248-2304: mines ignored at <=50; cruise slow to -.5.
    [Fact]
    public void Mines_have_native_speed_gate_and_minefield_slowdown()
    {
        G.Obstacle mine = new(8, G.Kind.Mine, new(0, 0, -40), default, 10);
        Assert.False(G.Dodge(Mover(velocity: new(0, 0, -50)), [mine], -1, false, out _));
        Assert.True(G.Dodge(Mover(velocity: new(0, 0, -51)), [mine], -1, false, out _));
        var r = Execute(Mover(velocity: new(0, 0, -40)), Target(500), [mine]);
        Assert.Equal(-.5f, r.Controls.Throttle);
    }
    // common.cpp:1157-1320,1385-1429: constructor strips, NoDodge pivot, completion on charge.
    [Fact]
    public void Builder_strip_pivots_then_completes_without_dodging_rock()
    {
        var mover = Mover(position: new(0, 0, 25));
        var target = Target(0, G.Objective.Enter) with { Axis = new(0, 0, 1) };
        var r = Execute(mover, target, [new(7, G.Kind.Asteroid, default, default, 10)]);
        Assert.Equal(M.Enter | M.NoDodge | M.Finished, r.Step.Mask); Assert.True(r.Done);
        Assert.Equal(ControlData.Neutral, r.Controls); // Finished precedes Enter (common.cpp:2022).
        var reversed = mover with { Position = new(0, 0, -25) };
        r = Execute(reversed, target);
        Assert.Equal(M.Pivot | M.NoDodge, r.Step.Mask); Assert.False(r.Done); Assert.Equal(1, r.Controls.Yaw);
    }
    // common.cpp:1272-1280,1302-1318,1517-1542.
    [Fact]
    public void Station_no_bay_power_glides_and_bay_approach_keeps_native_masks()
    {
        var target = Target(0, G.Objective.Enter, G.Kind.Station);
        var mover = Mover(position: new(0, 0, 25));
        Assert.Equal(M.Fast, Execute(mover, target).Step.Mask);
        DockApproach.DockBay[] bays = [new(default, new(0, 0, 1))];
        var r = G.Execute(mover, target, bays, [], .05f, .5f);
        Assert.Equal(M.Enter | M.NoDodge | M.Roll, r.Step.Mask); Assert.Equal(1, r.Controls.Throttle); Assert.Equal(-1, r.Controls.Roll);
        var turning = mover with { Turn = mover.Turn with { RateYaw = .02f } };
        Assert.Equal(M.Pivot | M.NoDodge, G.Execute(turning, target, bays, [], .05f, .5f).Step.Mask);
        Assert.Equal(M.Enter | M.NoDodge | M.Roll, G.Execute(turning with { Experimental = true }, target, bays, [], .05f, .5f).Step.Mask);
    }
    // common.cpp:1430-1446: live pod intercept, Fast, target excluded from dodge.
    [Fact]
    public void Pickup_predicts_intercept_and_flies_through_target()
    {
        var target = Target(100, G.Objective.Enter, G.Kind.Ship) with { Pickup = true, Velocity = new(10, 0, 0) };
        var r = Execute(Mover(), target, [new(7, G.Kind.Ship, target.Position, target.Velocity, 10)]);
        Assert.Equal(M.Fast, r.Step.Mask); Assert.InRange(r.Step.Goal.X, 10, 11); Assert.False(r.Done);
    }
    // common.cpp:1813-1821: strict <2000 range, afterburner latch and completed-plan exception.
    [Theory]
    [InlineData(2001, true)]
    [InlineData(2000, true)]
    [InlineData(1999, false)]
    public void Boost_is_cancelled_only_inside_original_target_radius(float distance, bool expected)
    {
        var r = Execute(Mover(), Target(distance), boost: true);
        Assert.Equal(expected, r.WantBoost);
        Assert.Equal(expected, (r.Buttons & ShipControlState.AfterburnerButton) != 0);
    }
    // common.cpp:1873-1874, igc.h:4909-4913: keep an unseen/dead target
    // plan pending and clear travel controls without Dodge.
    [Fact]
    public void Unavailable_target_waits_without_completing_or_steering()
    {
        var target = Target(500) with { Present = false };
        var result = Execute(Mover(), target, [new(8, G.Kind.Asteroid, new(10, 0, 0), default, 20)]);
        Assert.False(result.Done); Assert.Equal(ControlData.Neutral, result.Controls);
        Assert.Equal(0, result.Buttons);
    }
    // common.cpp:152-254,2231-2235,2309: rear escape, normalized turn + independent RCS.
    [Fact]
    public void Wide_angle_turn_uses_native_yaw_escape_and_directional_rcs()
    {
        var r = Execute(Mover(), Target(-500));
        Assert.Equal(1, r.Controls.Yaw); Assert.Equal(0, r.Controls.Pitch);
        Assert.Equal(-1, r.Controls.Throttle); Assert.True((r.Buttons & ShipControlState.BackwardButton) != 0);
        var angular = Mover() with { Turn = Mover().Turn with { RateYaw = .5f } };
        Assert.True(Execute(angular, Target(500)).Controls.Yaw < 0); // counter angular drift
    }
    // common.cpp:2136-2226: blocking stationary models produce tangent paths; moving ships are skipped.
    [Fact]
    public void Divert_uses_tangent_path_around_stationary_blockers()
    {
        Vector3 path = new(0, 0, -1000);
        var o = new G.Obstacle(8, G.Kind.Asteroid, new(0, 0, -100), default, 20);
        var result = G.Divert(Mover(), path, [o], -1);
        Assert.True(MathF.Abs(result.X) + MathF.Abs(result.Y) > 1);
        Assert.Equal(path, G.Divert(Mover(), path, [o], 8));
        Assert.Equal(path, G.Divert(Mover(), path, [o with { Kind = G.Kind.Ship, Velocity = new(1, 0, 0) }], -1));
    }
    // common.cpp:2043-2063 and :2244-2262: two different speed-matching biases.
    [Fact]
    public void Moving_ship_targets_use_native_coasting_and_approach_speed_matches()
    {
        var mover = Mover(velocity: new(0, 0, -10));
        var coasting = Execute(mover, Target(30, kind: G.Kind.Ship) with { Velocity = new(0, 0, -50) });
        Assert.Equal(M.KillThrottle, coasting.Step.Mask);
        Assert.Equal(-.7976784f, coasting.Controls.Throttle, .0001f);
        var approach = Execute(mover, Target(100, kind: G.Kind.Ship) with { Velocity = new(0, 0, -20) });
        Assert.Equal(0u, approach.Step.Mask); Assert.Equal(-.59f, approach.Controls.Throttle, .0001f);
        Assert.False(Execute(Mover(), Target(20, kind: G.Kind.Ship)).Done); // ship follow plans never finish
    }
    // common.cpp:2090-2150: entry roll includes angular stopping drift.
    [Fact]
    public void Station_entry_roll_compensates_current_angular_velocity()
    {
        var mover = Mover(position: new(0, 0, 25)) with { RateRoll = .5f };
        var result = G.Execute(mover, Target(0, G.Objective.Enter, G.Kind.Station),
            [new(default, new(0, 0, 1))], [], 1f, .5f);
        Assert.Equal(-.8478982f, result.Controls.Roll, .0001f);
    }
    // common.cpp:1740-1751: strict earliest time and inclusive acceleration horizon.
    [Fact]
    public void Dodge_horizon_is_inclusive_and_equal_time_ties_keep_first_candidate()
    {
        // Powers-of-two values make the quadratic root and the equality
        // boundary exact in float: r=16, a=4, v=64, t=4.
        var mover = Mover(velocity: new(0, 0, -64));
        mover = mover with { Turn = mover.Turn with { Thrust = 400 } };
        G.Obstacle edge = new(8, G.Kind.Asteroid, new(0, 0, -272), default, 10);
        Assert.True(G.Dodge(mover, [edge], -1, false, out _));
        Assert.False(G.Dodge(mover, [edge with { Position = new(0, 0, -273) }], -1, false, out _));
        G.Obstacle right = new(9, G.Kind.Asteroid, new(10, 0, 0), default, 20);
        G.Obstacle left = right with { Key = 10, Position = new(-10, 0, 0) };
        Assert.True(G.Dodge(Mover(), [right, left], -1, false, out int buttons));
        Assert.Equal(ShipControlState.LeftButton, buttons);
        Assert.True(G.Dodge(Mover(), [left, right], -1, false, out buttons));
        Assert.Equal(ShipControlState.RightButton, buttons);
    }
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, "0", false)]
    [InlineData(true, "FALSE", false)]
    [InlineData(true, "off", false)]
    [InlineData(false, "1", true)]
    public void Flag_defaults_on_and_environment_overrides_config(bool config, string? env, bool expected)
    {
        Assert.True(new SimConfig().NativeGotoPlan);
        Assert.Equal(expected, SimConfig.ResolveNativeGotoPlan(config, env));
    }
    [Fact]
    public void Pure_control_and_route_loops_allocate_no_bytes_after_warmup()
    {
        var mover = Mover(velocity: new(0, 0, -60)); var target = Target(1000);
        G.Obstacle[] obstacles = [new(8, G.Kind.Asteroid, new(10, 0, -70), default, 10)];
        G.Edge[] edges = [new(1, 2, 10, default, true)]; G.Cluster[] clusters = [new(1, true), new(2, false)];
        var queue = new G.Path[2]; var seen = new short[3];
        for (int i = 0; i < 100; i++) { G.Execute(mover, target, [], obstacles, .05f, .5f); G.FindPath(1, 2, default, true, edges, clusters, queue, seen); }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { G.Execute(mover, target, [], obstacles, .05f, .5f); G.FindPath(1, 2, default, true, edges, clusters, queue, seen); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Production_adapter_allocates_no_bytes_and_excludes_player_pilots()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var cluster = new DataClusterIGC(1, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Test", "", "", 0, 0, 0, true, false);
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster, cluster with { ClusterId = 2, Name = "Unreachable" }], [], [], []),
            [new SideSpec(0, "Alpha", core.Civilizations.Values.First().CivilizationId)], new NullIgcSite());
        mission.NativeGotoPlanEnabled = true;
        var hull = core.HullTypes.Values.First();
        var ship = new Ship(1, hull, default, default, Orientation.Identity) { IsNpc = true, SideId = 0, PilotType = Ship.PilotTypeCarrier };
        mission.PlaceShip(ship, mission.Sectors[0]);
        ship.GotoWaypoint = new(GotoKind.Waypoint, 1, new(0, 0, -1000));
        for (int i = 0; i < 100; i++) Assert.True(ship.ApplyNativeNpcAutopilot(.05));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) ship.ApplyNativeNpcAutopilot(.05);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        // An unreachable cross-cluster plan retries native FindPath every tick.
        // Territory, visibility and search workspaces must also stay allocation-free.
        ship.GotoWaypoint = new(GotoKind.Waypoint, 2, new(0, 0, -1000));
        for (int i = 0; i < 100; i++) ship.ApplyNativeNpcAutopilot(.05);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) ship.ApplyNativeNpcAutopilot(.05);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var player = new Ship(2, hull, default, default, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(player, mission.Sectors[0]);
        Assert.False(player.ApplyNativeNpcAutopilot(.05));
        mission.NativeGotoPlanEnabled = false;
        Assert.False(ship.ApplyNativeNpcAutopilot(.05));
    }
    // Independent C++ fixture, tools/p817-gotoplan-proof/reference.cpp. The
    // control decisions AND integrated positions/velocities must agree at
    // every 50ms frame of mine -> warp -> sell (not just eventual arrival).
    [Fact]
    public void Mine_warp_sell_trajectory_matches_independent_native_formula_trace()
    {
        using var stream = typeof(P817NativeGotoPlanTests).Assembly.GetManifestResourceStream(
            "Allegiance.Core.Tests.Fixtures.P817NativeGotoTrace.csv")!;
        using var reader = new StreamReader(stream);
        reader.ReadLine();
        var orientation = Orientation.Identity;
        var rates = new float[3];
        Vector3 position = new(0, 0, 1000), velocity = default;
        int phase = 0, count = 0;
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            float Read(int index) => float.Parse(parts[index], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(phase, (int)Read(1));
            var mover = Mover(position, velocity);
            var target = phase == 0 ? Target(0) : phase == 1
                ? Target(1000, G.Objective.Enter, G.Kind.Warp)
                : Target(0, G.Objective.Enter, G.Kind.Station);
            var result = Execute(mover, target);
            Assert.Equal((uint)Read(2), result.Step.Mask);
            Assert.Equal(Read(3), result.Controls.Throttle);
            Assert.Equal(0, result.Buttons);
            var inputs = new ShipKinematics.Inputs(result.Controls, result.Buttons,
                1000, 100, 100, 1, .5f, 2, 2, 2, 100, 100, 100, 0, 0, false);
            ShipKinematics.Step(inputs, ref velocity, orientation, rates, .05f);
            position += velocity * .05f;
            Assert.Equal(Read(4), position.Z, .005f);
            Assert.Equal(Read(5), velocity.Z, .005f);
            if (phase == 0 && position.Z < 45 && velocity.LengthSquared() < 1) phase = 1;
            else if (phase == 1 && position.Z < -970) { phase = 2; position = new(0, 0, 1000); velocity = new(0, 0, -velocity.Length() - 15); }
            count++;
        }
        Assert.Equal(2, phase); Assert.True(count > 500); Assert.True(position.Z < 25);
    }

    // ResetWaypoint retains model identity (shipIGC.cpp:3990-4151), and an
    // explicit warp-enter finishes on changing clusters (common.cpp:1850-1862).
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Explicit_warp_order_enters_at_speed_boost_uses_original_goal_and_arrival_clears_order()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var cluster = new DataClusterIGC(1, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Test", "", "", 0, 0, 0, true, false);
        var warp = new DataWarpIGC(10, 11, 30, "", "", "Link", [0, 0, -5000], [0, 0, 1], [0, 0, 0, 1], 1, 1);
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster, cluster with { ClusterId = 2 }],
            [warp, warp with { WarpId = 11, DestinationId = 10, ClusterId = 2, Position = [0, 0, 5000] }], [], []),
            [new SideSpec(0, "Alpha", core.Civilizations.Values.First().CivilizationId)], new NullIgcSite());
        mission.NativeGotoPlanEnabled = true;
        foreach (var w in mission.Warps) w.SideVisibility.SetSideVisibility(0, true);
        var hull = core.HullTypes.Values.First() with { Thrust = 1000, Speed = 100, Mass = 100, Length = 10 };
        var ship = new Ship(1, hull, new(0, 0, -4990), new(0, 0, -100), Orientation.Identity)
            { IsNpc = true, SideId = 0, PilotType = Ship.PilotTypeCarrier };
        mission.PlaceShip(ship, mission.Sectors[0]);
        ship.GotoWaypoint = new(GotoKind.Waypoint, 1, new(warp.Position[0], warp.Position[1], warp.Position[2]),
            TargetType: IgcObjectType.Warp, TargetId: 10);
        Assert.True(ship.ApplyNativeNpcAutopilot(.05));
        Assert.Equal(1, ship.Controls.Throttle); // no early arrival brake even 10 units from warp
        ship.WantBoost = true;
        mission.Sectors[0].Remove(ship);
        mission.PlaceShip(ship, mission.Sectors[1]);
        Assert.True(ship.ApplyNativeNpcAutopilot(.05)); Assert.Null(ship.GotoWaypoint);
        Assert.True((ship.StateM & ShipControlState.AfterburnerButton) != 0); // completion keeps the requested boost this tick
        ship.ApplyNativeNpcAutopilot(.1);
        Assert.True(ship.WantBoost); Assert.Equal(0, ship.StateM & ShipControlState.AfterburnerButton); // idle plot clears maneuver/fire bits
        // A cross-cluster buoy's boost cutoff must compare to the buoy, not its warp.
        mission.Sectors[1].Remove(ship);
        mission.PlaceShip(ship, mission.Sectors[0]); ship.Position = default; ship.Velocity = default;
        ship.WantBoost = true;
        ship.GotoWaypoint = new(GotoKind.Waypoint, 2, new(0, 0, -1000));
        ship.ApplyNativeNpcAutopilot(.05); Assert.False(ship.WantBoost); Assert.Equal(10, ship.IntendedNextWarpId);
        ship.WantBoost = true;
        ship.GotoWaypoint = new(GotoKind.Waypoint, 2, new(0, 0, -10000));
        ship.ApplyNativeNpcAutopilot(.05); Assert.True(ship.WantBoost);
    }
    // Native SetStation, shipIGC.h:459-478, completes the entered target only.
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enter_station_goto_clears_on_docking_and_moving_ship_goto_reads_live_position()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var cluster = new DataClusterIGC(1, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Test", "", "", 0, 0, 0, true, false);
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            [new SideSpec(0, "Alpha", core.Civilizations.Values.First().CivilizationId), new SideSpec(1, "Bravo", core.Civilizations.Values.First().CivilizationId)], new NullIgcSite());
        mission.NativeGotoPlanEnabled = true;
        foreach (var side in mission.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        var hull = core.HullTypes.Values.First() with { Length = 10, HabmCapabilities = 0, Signature = 1, ScannerRange = 100 };
        var ship = new Ship(20, hull, default, default, Orientation.Identity) { IsNpc = true, SideId = 0, PilotType = Ship.PilotTypeCarrier };
        var target = new Ship(10, hull, new(0, 0, -1000), default, Orientation.Identity) { IsNpc = true, SideId = 0, PilotType = Ship.PilotTypeCarrier };
        mission.PlaceShip(ship, mission.Sectors[0]); mission.PlaceShip(target, mission.Sectors[0]);
        using var server = new Srv();
        var resolved = (GotoOrder)typeof(Srv).GetMethod("ResolveDroneGoto", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(server, [mission, target, IgcObjectType.Ship, target.Id])!;
        Assert.Equal(IgcObjectType.Ship, resolved.TargetType); Assert.Equal(target.Id, resolved.TargetId);
        ship.GotoWaypoint = resolved;
        // Personal-scanner rules (shipIGC.h:341-376), before a side sweep:
        // signature resistance, rear quarter range, and flag-carrier visibility.
        target.SideId = 1; target.Position = new(0, 0, -80);
        var attributes = Enumerable.Repeat(1f, GlobalAttributeSet.Count).ToArray();
        attributes[GlobalAttributeSet.Signature] = 2;
        mission.Sides[1].GlobalAttributes = new(attributes);
        ship.ApplyNativeNpcAutopilot(.05); Assert.Equal(ControlData.Neutral, ship.Controls);
        mission.Sides[1].GlobalAttributes = GlobalAttributeSet.Identity;
        ship.ApplyNativeNpcAutopilot(.05); Assert.True(ship.Controls.Throttle > -1);
        target.Position = new(0, 0, 80);
        ship.ApplyNativeNpcAutopilot(.05); Assert.Equal(ControlData.Neutral, ship.Controls);
        target.Flag = 1;
        ship.ApplyNativeNpcAutopilot(.05); Assert.Equal(1, ship.Controls.Yaw);
        target.Flag = null; target.SideId = 0; target.Position = new(0, 0, -1000);
        ship.ApplyNativeNpcAutopilot(.05); Assert.Equal(1, ship.Controls.Throttle);
        target.Position = new(0, 0, 1000); // behind us; the original point has not changed
        ship.ApplyNativeNpcAutopilot(.05); Assert.Equal(1, ship.Controls.Yaw); Assert.Equal(-1, ship.Controls.Throttle);
        // Ignore's lower-ID allied drone rule stops applying while it mines.
        target.Position = new(0, 0, -10);
        ship.ApplyNativeNpcAutopilot(.05); Assert.Equal(0, ship.StateM & ShipControlState.MoveButtonsMask);
        target.IsMining = true;
        ship.ApplyNativeNpcAutopilot(.05); Assert.NotEqual(0, ship.StateM & ShipControlState.MoveButtonsMask);
        target.SideId = 1; // native drones ignore enemy ships even when mining
        ship.ApplyNativeNpcAutopilot(.05); Assert.Equal(0, ship.StateM & ShipControlState.MoveButtonsMask);
        // Native cheat pilots use player road rules, unlike the heavier drones
        // (common.cpp:1679-1680 checks pilotType < c_ptPlayer).
        mission.Sectors[0].Remove(ship);
        var cheat = new Ship(30, hull, default, default, Orientation.Identity)
            { IsNpc = true, SideId = 0, PilotType = Ship.PilotTypeCheatPlayer };
        mission.PlaceShip(cheat, mission.Sectors[0]);
        cheat.GotoWaypoint = new(GotoKind.Waypoint, 1, new(0, 0, -1000));
        cheat.ApplyNativeNpcAutopilot(.05); Assert.NotEqual(0, cheat.StateM & ShipControlState.MoveButtonsMask);
        mission.Sectors[0].Remove(cheat); mission.PlaceShip(ship, mission.Sectors[0]);
        ship.GotoWaypoint = new(GotoKind.Waypoint, 1, default, TargetStationId: 42);
        ship.StationId = 41; Assert.NotNull(ship.GotoWaypoint);
        ship.StationId = 42; Assert.Null(ship.GotoWaypoint);
        mission.NativeGotoPlanEnabled = false;
        var legacy = (GotoOrder)typeof(Srv).GetMethod("ResolveDroneGoto", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(server, [mission, target, IgcObjectType.Ship, target.Id])!;
        Assert.Null(legacy.TargetType); Assert.Null(legacy.TargetId);
        ship.GotoWaypoint = new(GotoKind.Waypoint, 1, default, TargetStationId: 42);
        ship.StationId = 42; Assert.NotNull(ship.GotoWaypoint); // default-off path is unchanged
    }

    // Visible buoy radius: buoyigc.cpp:44-48 / igc.h:1921. Layer action only
    // on GotoPlan completion, before movement: shipIGC.cpp:2795-2828.
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_layer_waits_for_visible_buoy_stop_then_lays_before_movement_legacy_keeps_reach_radius()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var cluster = new DataClusterIGC(1, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Test", "", "", 0, 0, 0, true, false);
        var site = new RecordingIgcSite();
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            [new SideSpec(0, "Alpha", core.Civilizations.Values.First().CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        mission.NativeGotoPlanEnabled = true; mission.Sides[0].GlobalAttributes = GlobalAttributeSet.Identity;
        var hull = core.HullTypes.Values.First() with { Length = 10, HabmCapabilities = 0, Mass = 100, Thrust = 1000, Speed = 100, BackMultiplier = .5f };
        var ship = new Ship(1, hull, new(0, 0, 60), new(0, 0, -100), Orientation.Identity)
            { IsNpc = true, SideId = 0, PilotType = Ship.PilotTypeLayer, LayerLaidExpendableTypeId = core.MineTypes.Keys.First() };
        mission.PlaceShip(ship, mission.Sectors[0]);
        ship.SetLayerBuild(new(1, default, 70));
        ship.Update(.05);
        Assert.Empty(mission.Sectors[0].Models.OfType<Mine>()); Assert.NotNull(ship.LayerBuildOrder);
        ship.Position = new(0, 0, 30); ship.Velocity = default;
        ship.Update(.1);
        Assert.Null(ship.LayerBuildOrder);
        var mine = Assert.Single(mission.Sectors[0].Models.OfType<Mine>());
        Assert.Equal(30, mine.Position.Z); Assert.Equal(.05, mine.DeployTime, .0001);
        Assert.Contains(site.Calls, c => c.StartsWith("KillShipEvent("));
        // The old, default-off lane still lays on entering its broad reach radius.
        mission.NativeGotoPlanEnabled = false;
        ship.Position = new(0, 0, 60); ship.Velocity = new(0, 0, -100);
        ship.SetLayerBuild(new(1, default, 70)); ship.Update(.15);
        Assert.Null(ship.LayerBuildOrder); Assert.Equal(2, mission.Sectors[0].Models.OfType<Mine>().Count());
    }

    // Native wingman goto's retreat/ammo boost, shipIGC.cpp:2752-2754,2830-2833;
    // retreat-carrier entry objective, shipIGC.cpp:3999-4003.
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(Ship.PilotTypeWingman, true, false, false, true)]
    [InlineData(Ship.PilotTypeWingman, false, true, false, true)]
    [InlineData(Ship.PilotTypeWingman, false, false, false, false)]
    [InlineData(Ship.PilotTypeMiner, true, false, false, false)]
    [InlineData(Ship.PilotTypeWingman, true, false, true, true)]
    public void Wingman_retreat_or_ammo_goto_boosts_only_at_full_throttle_and_carrier_entry_does_not_brake(
        byte pilot, bool running, bool ammo, bool carrierGoal, bool expectedBoost)
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var cluster = new DataClusterIGC(1, [0, 1, 0], 0, 0, 0, 1, 0, 0, "Test", "", "", 0, 0, 0, true, false);
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            [new SideSpec(0, "Alpha", core.Civilizations.Values.First().CivilizationId)], new NullIgcSite());
        mission.NativeGotoPlanEnabled = true; mission.Sides[0].GlobalAttributes = GlobalAttributeSet.Identity;
        var hull = core.HullTypes.Values.First() with { Length = 10, HabmCapabilities = DataHullTypeIGC.HabmLandOnCarrier, Mass = 100, Thrust = 1000, Speed = 100 };
        var ship = new Ship(20, hull, default, carrierGoal ? new(0, 0, -100) : default, Orientation.Identity)
            { IsNpc = true, SideId = 0, PilotType = pilot };
        mission.PlaceShip(ship, mission.Sectors[0]);
        var type = core.StationTypes.Values.First();
        var data = DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 42, StationTypeId = type.StationTypeId, ClusterId = 1, SideId = 0,
                Position = [0, 0, -10000], Up = [0, 1, 0], Forward = [0, 0, 1] };
        mission.PlaceStation(new Station(data, type), mission.Sectors[0]);
        ship.PlanTarget = data; ship.PlanCommandId = Allegiance.Core.Wire.OrderChange.CidGoto;
        if (carrierGoal)
        {
            ship.PlanTarget = null;
            var carrierHull = hull with { HabmCapabilities = (short)(DataHullTypeIGC.HabmCarrier | DataStationTypeIGC.SabmRepair) };
            var carrier = new Ship(10, carrierHull, new(0, 0, -20), new(0, 0, -100), Orientation.Identity)
                { IsNpc = true, SideId = 0, PilotType = Ship.PilotTypeCarrier };
            mission.PlaceShip(carrier, mission.Sectors[0]);
            typeof(Ship).GetField("_retreatRepairShip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(ship, carrier);
        }
        typeof(Ship).GetProperty(nameof(Ship.IsRunningAway))!.SetValue(ship, running);
        typeof(Ship).GetField("_isGettingAmmo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(ship, ammo);
        ship.ApplyNativeNpcAutopilot(.05);
        Assert.Equal(1, ship.Controls.Throttle);
        Assert.Equal(expectedBoost, (ship.StateM & ShipControlState.AfterburnerButton) != 0);
        ship.Orientation.Yaw(MathF.PI);
        ship.ApplyNativeNpcAutopilot(.05);
        Assert.Equal(-1, ship.Controls.Throttle);
        Assert.Equal(0, ship.StateM & ShipControlState.AfterburnerButton);
    }

}

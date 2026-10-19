using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P745SiegeNavigationTests
{
    [SkippableFact]
    public void Native_heavy_strike_controls_make_progress_to_a_warp_without_obstacles()
    {
        Allegiance.TestInfra.Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var hull = core.HullTypes[124];
        var ship = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var destination = new Vector3(2500, 500, 300);
        for (int tick = 0; tick < 2400 && (destination - ship.Position).Length() > 100; tick++)
        {
            var turn = DockApproach.TurnState.FromShip(ship);
            var step = new DockApproach.Step(DockApproach.Mask.Fast, 0, destination, Vector3.Zero);
            var controls = SiegeNavigation.Steer(step, ship.Position, ship.Orientation, ship.Velocity, turn);
            if (ship.Orientation.GetForward() * (destination - ship.Position).Normalize() <= MathF.Cos(MathF.PI / 8))
                controls = controls with { Throttle = -1 };
            ship.Controls = controls;
            ship.StateM = DockApproach.MovementButtons(step, ship.Position, ship.Orientation, ship.Velocity, turn);
            ship.ExecuteMove(tick * .05, (tick + 1) * .05);
            ship.Position += ship.Velocity * .05f;
        }
        Assert.True((destination - ship.Position).Length() <= 100, $"position={ship.Position}, velocity={ship.Velocity}");
    }

    [Fact]
    public void Heavy_strike_turn_brakes_angular_drift_before_crossing_the_route_bearing()
    {
        var goal = new Vector3(.01f, 0, -1);
        var turn = new DockApproach.TurnState(50, 60, 1, 1, 10, 10, -.5f, 0, 750);
        var step = new DockApproach.Step(DockApproach.Mask.Fast, 0, goal, Vector3.Zero);
        var controls = SiegeNavigation.Steer(step, Vector3.Zero, Orientation.Identity, Vector3.Zero, turn);
        // The ship is already turning left faster than it can stop at the goal:
        // native must command right torque now. The old proportional face law
        // commands more left torque and sends the heavy hull past the bearing.
        Assert.True(controls.Yaw > 0);
        Assert.True(Allegiance.ClientCore.Flight.AutopilotSteering.Pursue(Vector3.Zero, Orientation.Identity, goal).Yaw < 0);
    }

    [Fact]
    public void Tangent_course_clears_the_nearest_static_blocker_without_removing_forward_progress()
    {
        var position = Vector3.Zero; var destination = new Vector3(0,0,-3000);
        var body = new SiegeNavigation.Obstacle(new(0,0,-1000), 200);
        var goal = SiegeNavigation.DivertGoal(position, 16, destination, [body]);
        Assert.True(goal.Z < 0);
        Assert.NotEqual(destination, goal);
        var direction = goal.Normalize();
        var closest = direction * (body.Position * direction);
        Assert.True((closest - body.Position).Length() > 200 + 16 + 2 + 10);
        Assert.Equal(destination, SiegeNavigation.DivertGoal(position, 16, destination,
            [body with { Position = new(0,0,1000) }]));
        Assert.Equal(destination, SiegeNavigation.DivertGoal(position, 16, destination,
            [body with { Position = new(2000,0,-1000) }]));
    }

    [Theory]
    [InlineData(100,0,0, ShipControlState.RightButton)]
    [InlineData(0,-100,0, ShipControlState.DownButton)]
    [InlineData(0,0,-100, ShipControlState.ForwardButton)]
    [InlineData(100,90,-10, ShipControlState.RightButton | ShipControlState.UpButton)]
    public void Dodge_applies_native_directional_thrusters_instead_of_turning_away(float x, float y, float z, int expected)
    {
        Assert.Equal(expected, SiegeNavigation.DirectionButtons(new(x,y,z), Orientation.Identity));
        Assert.Equal(0, SiegeNavigation.DirectionButtons(Vector3.Zero, Orientation.Identity));
    }
}

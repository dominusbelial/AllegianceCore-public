using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Native GotoPlan obstacle diversion and directional thrust, used only by
/// research finale duties. Igc/common.cpp:1575-1636,1785-1786,2120-2224.
/// The caller supplies only objects observed by the participating client.</summary>
internal static class SiegeNavigation
{
    /// <summary>Observed static or stationary pickable body (Igc/common.cpp:2140-2142).</summary>
    internal readonly record struct Obstacle(Vector3 Position, float Radius);

    /// <summary>Native angular stopping-drift control (Igc/common.cpp:152-246).
    /// The default interval is the MCP's 20 Hz decision cadence.</summary>
    internal static ControlData Steer(DockApproach.Step step, Vector3 position, Orientation orientation,
        Vector3 velocity, DockApproach.TurnState turn, float dt = .05f)
        => DockApproach.Steer(step, position, orientation, velocity, 0, dt: dt, turnState: turn);

    /// <summary>Native dominant-axis RCS selection (Igc/common.cpp:1575-1636).</summary>
    internal static int DirectionButtons(Vector3 direction, Orientation orientation)
    {
        if (direction.LengthSquared() < 1e-6f) return 0;
        var d = orientation.TimesInverse(direction);
        var axes = new (float Magnitude, int State)[]
        {
            (MathF.Abs(d.X), d.X >= 0 ? ShipControlState.RightButton : ShipControlState.LeftButton),
            (MathF.Abs(d.Y), d.Y >= 0 ? ShipControlState.UpButton : ShipControlState.DownButton),
            (MathF.Abs(d.Z), d.Z <= 0 ? ShipControlState.ForwardButton : ShipControlState.BackwardButton),
        };
        Array.Sort(axes, (a, b) => b.Magnitude.CompareTo(a.Magnitude));
        return axes[0].Magnitude > 2 * axes[1].Magnitude ? axes[0].State
            : axes[0].Magnitude > 2 * axes[2].Magnitude ? axes[0].State | axes[1].State
            : axes[0].State | axes[1].State | axes[2].State;
    }

    /// <summary>Nearest blocking body and tangent detour (Igc/common.cpp:2120-2224).
    /// The 2-unit ship padding and 10-unit diversion margin are native values.</summary>
    internal static Vector3 DivertGoal(Vector3 position, float shipRadius, Vector3 goal, IEnumerable<Obstacle> obstacles)
    {
        var path = goal - position;
        float distance2 = path.LengthSquared(), nearest = distance2;
        if (distance2 < .1f) return goal;
        Obstacle? blocking = null;
        foreach (var obstacle in obstacles)
        {
            var dp = obstacle.Position - position;
            float d2 = dp.LengthSquared(), dot = dp * path;
            if (d2 >= nearest || dot < 0) continue;
            float r = shipRadius + 2 + obstacle.Radius + 10; // myRadius + divertOffset
            var offset = position + path * (dot / distance2) - obstacle.Position;
            if (offset.LengthSquared() >= r * r) continue;
            blocking = obstacle; nearest = d2;
        }
        if (blocking is not { } body) return goal;
        var delta = body.Position - position;
        var off = position + path * ((delta * path) / distance2) - body.Position;
        Vector3 cross;
        if (off.LengthSquared() >= .5f)
            cross = Vector3.CrossProduct(delta, Vector3.CrossProduct(off, delta));
        else cross = delta.LengthSquared() > 1e-6f ? ShipDodgeLaw.OrthogonalVector(delta) : new Vector3(1,0,0);
        if (cross.LengthSquared() < 1e-6f) cross = ShipDodgeLaw.OrthogonalVector(delta);
        cross = cross.Normalize();
        double radius = shipRadius + 2 + body.Radius + 10, radius2 = radius * radius;
        double sin2 = radius2 / Math.Max(1e-6f, delta.LengthSquared());
        if (sin2 >= .99) return position + cross * (float)radius;
        var tangent = delta + cross * (float)Math.Sqrt(radius2 / (1 - sin2));
        var pca = tangent * ((delta * tangent) / tangent.LengthSquared());
        return position + pca + (pca - delta).Normalize() * (float)(radius * .02);
    }

}

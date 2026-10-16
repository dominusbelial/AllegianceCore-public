using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>Analytical treasure trajectory shared by simulation and clients.
/// Igc/treasureIGC.cpp:18,221-259: constant 10 units/s² deceleration.</summary>
public static class TreasureMotion
{
    public const float Deceleration = 10f;

    public static Vector3 PositionAt(Vector3 origin, Vector3 velocity, double elapsed)
    {
        float speed = velocity.Length();
        if (speed == 0) return origin;
        float t = (float)Math.Clamp(elapsed, 0, speed / Deceleration);
        return origin + velocity * (t - 0.5f * t * t * Deceleration / speed);
    }

    public static Vector3 VelocityAt(Vector3 velocity, double elapsed)
    {
        float speed = velocity.Length();
        return speed == 0 ? Vector3.Zero
            : velocity * Math.Max(0, 1 - Deceleration * (float)Math.Max(0, elapsed) / speed);
    }

    public static Vector3 Vector(float[]? value) => value is { Length: 3 }
        ? new(value[0], value[1], value[2]) : Vector3.Zero;
}

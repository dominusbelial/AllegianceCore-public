using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P982 visual substitution for effect/efimage.cpp:601-613,716-745.
/// Uses the same backdrop/occlusion order; no change to FlareOcclusion or bloom.</summary>
public static class SimGlare
{
    public const float HalfAngleDegrees = 0.35f;
    public const float Alpha = 0.25f;
    public const float DiscAlpha = 0.75f;
    public const float ConeDot = 0.7f;
    public static float GhostAlpha(float dot) => Alpha * Math.Clamp((dot - ConeDot) / (1f - ConeDot), 0, 1);
    public static float DiscHalfPixels(float projectionY, float height) =>
        MathF.Tan(MathHelper.ToRadians(HalfAngleDegrees)) * projectionY * height * 0.5f;
    public static Vector3? TowardLight(DataClusterIGC cluster)
    {
        if (cluster.LightDirection is not { Length: 3 } d) return null;
        var direction = new Vector3(d[0], d[1], d[2]);
        return direction.LengthSquared() < 1e-12f ? null : Vector3.Normalize(direction);
    }
}

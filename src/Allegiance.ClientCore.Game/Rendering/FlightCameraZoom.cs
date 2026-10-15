using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Native WinTrek.h:25-27; WinTrek.cpp:80-81,8393-8409,8618-8645.
/// Cockpit changes radians/sec; external distance changes by dt * distance.</summary>
public sealed class FlightCameraZoom
{
    public const float MinDistance = 100, MaxDistance = 6000;
    public float FovRadians { get; private set; } = MathHelper.ToRadians(50);
    public float ExternalDistance { get; private set; } = 500;
    public void Step(float dt, bool inward, bool outward, bool cockpit)
    {
        dt = MathF.Max(0, dt);
        if (cockpit)
            FovRadians = Math.Clamp(FovRadians + (inward ? -dt : outward ? dt : 0), MathHelper.ToRadians(5), MathHelper.ToRadians(50));
        else ExternalDistance = Math.Clamp(ExternalDistance * (1 + (outward ? dt : inward ? -dt : 0)), MinDistance, MaxDistance);
    }
    public static bool CanZoomExternal(CameraMode mode) => mode is CameraMode.Chase or CameraMode.External
        or CameraMode.ExternalTarget or CameraMode.ExternalBase or CameraMode.ExternalFlyByTarget
        or CameraMode.ExternalFlyBy or CameraMode.ExternalOrbit or CameraMode.ExternalStation or CameraMode.ExternalTurret;
}

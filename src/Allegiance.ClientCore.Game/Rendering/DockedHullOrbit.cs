namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P519 — the hull-preview orbit behind the docked HULLS/PARTS panes. Pure and
/// MonoGame-free so the orbit rates are unit-testable: the selected hull's real
/// in-game model is rotated at native's <c>cmExternalOrbit</c> camera rates
/// (WinTrek.cpp:7186-7191 — <c>Pitch(dt*0.35f)</c>, <c>Yaw(dt*0.2f)</c>,
/// <c>Roll(dt*0.5f)</c>, radians/sec). The renderer rotates the model in place
/// by these accumulated angles under a fixed viewing camera, the visual
/// equivalent of the native camera orbiting the static ship.
/// </summary>
public static class DockedHullOrbit
{
    /// <summary>Native <c>cmExternalOrbit</c> pitch rate (rad/s), WinTrek.cpp:7187.</summary>
    public const float PitchRate = 0.35f;
    /// <summary>Native <c>cmExternalOrbit</c> yaw rate (rad/s), WinTrek.cpp:7188.</summary>
    public const float YawRate = 0.20f;
    /// <summary>Native <c>cmExternalOrbit</c> roll rate (rad/s), WinTrek.cpp:7189.</summary>
    public const float RollRate = 0.50f;

    /// <summary>Advance the accumulated orbit angles (radians) by <paramref name="dt"/>
    /// seconds at native's rates. Feed the output back in each frame.</summary>
    public static (float Pitch, float Yaw, float Roll) Advance(
        float pitch, float yaw, float roll, double dt)
    {
        float d = (float)dt;
        return (
            pitch + PitchRate * d,
            yaw + YawRate * d,
            roll + RollRate * d);
    }
}

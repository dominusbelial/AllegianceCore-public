namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A colour as four 0..1 float components. Deliberately a plain data type with
/// no MonoGame / <c>Microsoft.Xna.Framework</c> dependency so the HUD's pure
/// logic (<see cref="HudFormat"/>) is unit-testable without a graphics device,
/// mirroring the <see cref="Flight"/> namespace's no-MonoGame-types discipline.
/// The renderer (<see cref="HudRenderer"/>) converts this to a MonoGame
/// <c>Color</c> at the last moment.
/// </summary>
public readonly record struct HudColor(float R, float G, float B, float A = 1f)
{
    /// <summary>The HUD's accent — a teal/cyan-green deliberately tied to the
    /// Bios Scout's own model-texture energy-conduit look, so the overlay reads
    /// as belonging to this ship rather than as an arbitrary palette choice.</summary>
    public static readonly HudColor TealAccent = new(0.07f, 0.95f, 0.84f);

    public static HudColor Lerp(in HudColor a, in HudColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t <= 0f) return a;   // exact endpoints — a saturated ramp must land
        if (t >= 1f) return b;   // precisely on the target, not ±1ulp of it
        return new HudColor(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);
    }
}

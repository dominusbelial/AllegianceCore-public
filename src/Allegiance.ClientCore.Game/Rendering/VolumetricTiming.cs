using System;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P582b — the shared pure timing model for the volumetric (raymarch A) and
/// displaced-sphere (B) fireballs. Both shaders grow/cool their volumes on a
/// FILM-scaled clock so the fireball matches the native film's peak cadence
/// (fighter death: ~0.3–0.5 s) instead of the too-slow whole-life ramp that
/// made the P573/P579 volumes swell and cool across the full ~3.9 s
/// fire+smoke-tail life ("the fireball plays too slowly and lands out of
/// sync"). Pure static math (no MonoGame/GPU types) so it is unit-testable
/// headless; the HLSL (<c>Volumetric.fx</c>, <c>DisplacedSphere.fx</c>) mirrors
/// the exact same constants — keep them in lock-step.
///
/// Native anchor (<c>effect/explosion.cpp:183,187-189</c>): the fireball film
/// plays at 20 fps and every frame stays fully visible until
/// <c>frame &gt;= frameCount</c> — i.e. the fireball is bright almost from
/// birth and holds through its whole film; the film's hot core is brightest in
/// its first few frames and the decal is removed wholesale once the film ends.
/// Our fireball therefore: (a) is visible within the first frame (birth radius
/// floor, not near-zero), (b) reaches full radius / peak hot by
/// <see cref="PeakFilmFrac"/> of the film, and (c) cools to smoke over the film
/// (<see cref="CoolStartFrac"/> → 1), then lingers as cold smoke through the
/// smoke tail (sampler-side survival). All curves are smoothsteps (C2, no
/// kinks) so there is no visual pop at the film/tail boundary.
/// </summary>
public static class VolumetricTiming
{
    /// <summary>Fraction of the film at which the volume reaches FULL radius /
    /// peak hot. 0.28 × fighter filmLife (1.5–1.6 s) = 0.42–0.45 s — inside the
    /// task's native film-peak window (fighter death ≈ 0.3–0.5 s). Bigger ships
    /// (longer films) peak proportionally later, matching their slower native
    /// film cadence.</summary>
    public const float PeakFilmFrac = 0.28f;

    /// <summary>Volume radius at birth (age 0), as a fraction of peak radius —
    /// non-zero so the very first frame already shows a hot core (native's film
    /// frame 0 is fully visible, not a dot).</summary>
    public const float BirthRadiusFrac = 0.18f;

    /// <summary>Fraction of the film at which cooling toward smoke begins
    /// easing (stays white-hot through the peak, then ramps to cold smoke by the
    /// film's end, mirroring native's bright-through-the-film decal).</summary>
    public const float CoolStartFrac = 0.25f;

    /// <summary>Denominator floor guarding division by zero for degenerate
    /// zero-length films.</summary>
    public const float MinLife = 0.045f;

    /// <summary>Smoothstep(x): 0 → 1, C2, symmetric.</summary>
    public static float SmoothStep(float x) => x * x * (3f - 2f * x);

    /// <summary>age/life normalized to [0,1] against the FILM life.</summary>
    public static float FilmNormalized(float age, float filmLife)
        => Math.Clamp(age / Math.Max(MinLife, filmLife), 0f, 1f);

    /// <summary>Growth envelope 0→1, reaching 1 at <see cref="PeakFilmFrac"/> of
    /// the film (then held at 1 for the rest of the film + smoke tail).</summary>
    public static float Grow(float age, float filmLife)
    {
        float t = Math.Clamp(FilmNormalized(age, filmLife) / PeakFilmFrac, 0f, 1f);
        return SmoothStep(t);
    }

    /// <summary>Instantaneous radius as a fraction of peak radius (birth floor →
    /// 1 at the film peak).</summary>
    public static float RadiusFrac(float age, float filmLife)
        => BirthRadiusFrac + (1f - BirthRadiusFrac) * Grow(age, filmLife);

    /// <summary>Cooling 0 (white-hot) → 1 (cold smoke) across the film; stays 0
    /// until <see cref="CoolStartFrac"/>, reaches 1 at the film's end and stays 1
    /// through the smoke tail.</summary>
    public static float Cool(float age, float filmLife)
    {
        float tn = FilmNormalized(age, filmLife);
        float t = Math.Clamp((tn - CoolStartFrac) / (1f - CoolStartFrac), 0f, 1f);
        return SmoothStep(t);
    }

    /// <summary>Absolute wall-seconds at which the fireball reaches its peak
    /// (full radius + hot). E.g. fighter filmLife 1.5 s → 0.42 s.</summary>
    public static float PeakTimeSec(float filmLife) => filmLife * PeakFilmFrac;
}

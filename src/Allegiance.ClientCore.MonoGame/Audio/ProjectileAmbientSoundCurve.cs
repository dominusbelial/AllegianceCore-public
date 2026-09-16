namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P232 — the pure distance → volume mapping for a projectile's in-flight
/// ambient sound, deliberately free of any MonoGame type (same discipline as
/// <see cref="EngineSoundCurve"/> / <see cref="Flight.FlightInput"/>), so the
/// falloff is unit-testable without a graphics/audio device.
/// </summary>
/// <remarks>
/// <para><b>What this models.</b> Native attaches a per-object
/// <c>AmbientSoundSite</c> to every projectile whose type has a real ambient
/// sound (<c>DataProjectileTypeIGC.AmbientSound</c>; the site starts the sound
/// at the projectile's position and the sound engine attenuates it by distance,
/// <c>WinTrek/trekigc.cpp:694-728,1454-1463</c>). That is the actual mechanism
/// behind "the sounds of projectiles passing nearby your cockpit": a normal
/// 3D-positional loop whose volume swells as the bolt flies close to the
/// listener and fades as it recedes — <b>not</b> a special near-miss detector
/// (none exists in the native source). This port has no 3D-positional audio
/// engine yet (P213's engine/cockpit/ambience cues are all non-positional), so
/// per the P232 brief's blessed scope-cut, the same audible behaviour is
/// approximated by computing the listener-to-projectile distance each frame and
/// scaling a looped sound instance's volume by that distance via this curve.</para>
/// <para><b>Falloff shape.</b> Linear over <see cref="AudibleRadius"/>: full
/// volume <see cref="MaxVolume"/> at zero distance, ramping to silence at
/// <see cref="AudibleRadius"/>. Deliberately simple and predictable; a linear
/// ramp reproduces the qualitative "swell on approach, fade on recession" the
/// positional engine provides, without pretending to match its exact
/// perceptual falloff.</para>
/// <para><b>Real per-type fidelity is out of reach client-side.</b> This port's
/// MonoGame client is a pure renderer with no loaded static core
/// (MissionInfo carries only the core <i>filename</i>; grep StaticCore/MissionMap
/// under the client = nothing), so <c>DataProjectileTypeIGC.AmbientSound</c>'s
/// per-type value — a SoundID into the core's sound table — cannot be resolved
/// to an asset. A single real default loop (<c>erbeam.ogg</c>, native
/// <c>er_beam</c>, sounddef.mdl) stands in for every type; documented, not
/// forced (see the P232 summary).</para>
/// </remarks>
public static class ProjectileAmbientSoundCurve
{
    /// <summary>Distance (world units) at which a projectile's ambient sound
    /// reaches silence. Chosen to match the renderer's projectile visibility
    /// radius (the P231 <c>5900²</c> "sector-filter" analog), so what you can
    /// hear is exactly what you can see — no audible projectile is invisible
    /// and no visible projectile is inaudible.</summary>
    public const float AudibleRadius = 5900f;

    /// <summary>Peak ambient volume (0..1) at zero distance from the listener —
    /// a projectile brushing the cockpit is audible but must not drown the
    /// engine/ambience bed, so a moderate cap.</summary>
    public const float MaxVolume = 0.55f;

    /// <summary>Distance (world units) from the listener within which a
    /// freshly-spawned projectile is attributed to the LOCAL ship's own fire —
    /// i.e. its spawn (muzzle) point lies at the listener, so this is a local
    /// shot. Drives the weapon-fire one-shot (see
    /// <see cref="ProjectileSoundPlanner"/>). Large enough to cover the muzzle
    /// offset the sim uses (P231 measured spawn ≈14 units from ship centre),
    /// small enough that a distant enemy bolt is never misread as our fire.
    /// The P88 wire carries no shooter id, so this position heuristic is the
    /// port's own local-vs-other discrimination (documented, not fabricated).</summary>
    public const float FireOriginRadius = 150f;

    /// <summary>Map a listener→projectile distance (world units) to an ambient
    /// sound volume in [0, <see cref="MaxVolume"/>]: linear falloff from full at
    /// distance 0 to silence at <see cref="AudibleRadius"/>. Negative/zero
    /// distances clamp to full; distances past the audible radius clamp to 0.</summary>
    public static float VolumeForDistance(float distance)
    {
        if (distance <= 0f) return MaxVolume;
        if (distance >= AudibleRadius) return 0f;
        float t = distance / AudibleRadius;
        return MaxVolume * (1f - t);
    }

    /// <summary>True when a projectile spawned at <paramref name="spawnDistance"/>
    /// from the listener is attributable to the local ship's own fire (within
    /// <see cref="FireOriginRadius"/>).</summary>
    public static bool IsLocalFire(float spawnDistance) => spawnDistance <= FireOriginRadius;
}

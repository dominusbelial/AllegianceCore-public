namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P574 — the pure distance → volume mapping for a WORLD explosion one-shot
/// (a ship/station/asteroid/mine blast heard as <c>EXPLOSION_EVENT(207)</c>),
/// deliberately free of any MonoGame type (same discipline as
/// <see cref="ProjectileAmbientSoundCurve"/> / <see cref="HitSoundCurve"/>) so
/// the falloff is unit-testable without an audio device. Native plays a
/// positional <c>GenericSoundSource(position, velocity)</c> for every
/// <c>AddExplosion</c> in the player's current cluster
/// (<c>trekigc.cpp:86-102</c>), attenuated by distance; this is that
/// attenuation for the port's wire-driven explosions. The TIER (which cue —
/// large/medium/small) is chosen separately by the blast scale via
/// <see cref="ExplosionSoundTierMapper.ForScale"/> (<c>trekigc.cpp:96-101</c>).
/// </summary>
public static class ExplosionSoundCurve
{
    /// <summary>Distance (world units) at which an explosion sound reaches
    /// silence — the same convention as every other positional layer
    /// (<see cref="ProjectileAmbientSoundCurve.AudibleRadius"/> et al.).</summary>
    public const float AudibleRadius = 6000f;

    /// <summary>Peak volume (0..1) of an explosion one-shot at zero distance — a
    /// point-blank blast is loud but capped so it never clips (matches the
    /// missile-explosion peak, <see cref="MissileAmbientSoundCurve.MaxExplosionVolume"/>).</summary>
    public const float MaxVolume = 0.9f;

    /// <summary>Linear falloff factor in [0,1]: full at distance 0 to silence at
    /// <see cref="AudibleRadius"/> (negative/zero → 1, past → 0) — the same
    /// linear shape every other positional layer uses.</summary>
    public static float Falloff(float distance)
    {
        if (distance <= 0f) return 1f;
        if (distance >= AudibleRadius) return 0f;
        return 1f - distance / AudibleRadius;
    }

    /// <summary>Explosion one-shot volume (0..<see cref="MaxVolume"/>) for a
    /// blast at <paramref name="distance"/> from the listener.</summary>
    public static float Volume(float distance) => Falloff(distance) * MaxVolume;
}

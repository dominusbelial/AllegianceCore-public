namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P236 — the pure distance → volume mappings for the three missile sound cues,
/// deliberately free of any MonoGame type (same discipline as
/// <see cref="ProjectileAmbientSoundCurve"/> / <see cref="EngineSoundCurve"/>),
/// so the falloffs and the launch/explosion attribution rules are unit-testable
/// without a graphics/audio device.
/// </summary>
/// <remarks>
/// <para><b>Real per-type fidelity is out of reach client-side.</b> This port's
/// MonoGame client is a pure renderer with no loaded static core (MissionInfo
/// carries only the core <i>filename</i>), so the real per-type sound ids native
/// reads from the loaded core — <c>DataMissileTypeIGC.LaunchSound</c> /
/// <c>AmbientSound</c> (both already decoded, <c>DataMissileTypeIGC.cs:69,135</c>),
/// which are SoundIDs into the core's sound table — and the explosion-scale tier
/// (<c>DataMissileTypeIGC.BlastRadius</c> &gt; 100/10 thresholds) cannot be
/// resolved to assets or a tier client-side. The same single-real-default
/// convention P232 established for weapon/projectile sounds applies here:
/// a single real missile-launch cue (<c>lrgslideout.ogg</c>,
/// <c>HornetMissileLaunch</c>, sounddef.mdl:1302), a single real in-flight loop
/// (<c>mmissile.ogg</c>, <c>HornetMissileFlight</c>, sounddef.mdl:1303), and a
/// single default explosion tier stand in for every type — documented, not
/// fabricated (see the P236 summary).</para>
/// <para><b>Explosion tier.</b> Native tiers the explosion sound by the blast
/// <c>scale</c> (the explosion's radius, <c>trekigc.cpp:80-101</c>: &gt;100 large,
/// &gt;10 medium, else small). The port DOES carry a real radius concept
/// server-side (the P96 <see cref="Allegiance.Core.Sim.Explosion"/> engine's
/// <c>Radius</c>, port of native <c>ExplosionData</c>), but it is never sent to
/// the client on the missile wire (FIRE_MISSILE / MISSILE_UPDATE /
/// MISSILE_DESTROYED carry only type/id/cluster/position/heading), so the tier is
/// unreachable client-side — a single default tier is the documented fallback
/// (<see cref="DefaultExplosionScale"/>, which selects the medium tier, the same
/// tier native uses for its generic ship explosion, <c>explodeShipSound =
/// mediumExplosionSound</c>, sounddef.mdl:276).</para>
/// </remarks>
public static class MissileAmbientSoundCurve
{
    /// <summary>Distance (world units) at which any missile sound cue reaches
    /// silence. Matches the projectile convention (<see cref="ProjectileAmbientSoundCurve.AudibleRadius"/>,
    /// scaled up a little for missiles since they matter at greater range);
    /// the launch, in-flight ambient, and explosion are all gated/scaled by it.</summary>
    public const float AudibleRadius = 6000f;

    /// <summary>Peak volume (0..1) of a missile's continuous in-flight ambient
    /// loop at zero distance from the listener — prominent but not drowning the
    /// engine/ambience bed.</summary>
    public const float MaxAmbientVolume = 0.6f;

    /// <summary>Peak volume (0..1) of the missile-explosion one-shot at zero
    /// distance — a nearby detonation is loud but capped so it never clips.</summary>
    public const float MaxExplosionVolume = 0.9f;

    /// <summary>Distance (world units) from the listener within which a
    /// freshly-spawned missile is attributed to the LOCAL ship's own launch —
    /// i.e. its spawn point lies at the listener's muzzle, so this is our shot,
    /// deserving the launch one-shot (native positions the launch sound at the
    /// firing ship, <c>clintlib.cpp:3103</c> <c>PlaySoundEffect(…, pShip)</c>;
    /// the wire carries no per-observer shooter-derived audio, so this position
    /// heuristic is the port's own local-vs-other discrimination, exactly like
    /// <see cref="ProjectileAmbientSoundCurve.FireOriginRadius"/> for weapon
    /// fire). Wide enough to cover a muzzle at our ship, narrow enough that a
    /// distant enemy's launch is never misread as ours.</summary>
    public const float LaunchOriginRadius = 200f;

    /// <summary>The default blast scale used to pick the explosion tier when no
    /// real blast radius reaches the client. Chosen to land in the <b>medium</b>
    /// band (&gt;10, ≤100 — native's threshold, trekigc.cpp:80-101): the same tier
    /// native uses for its generic ship explosion (<c>explodeShipSound =
    /// mediumExplosionSound</c>, sounddef.mdl:276), a sensible mid-size default
    /// for a standard missile detonation. Kept explicit and adjustable rather
    /// than hiding the fallback.</summary>
    public const float DefaultExplosionScale = 60f;

    /// <summary>Map a listener→source distance (world units) to a linear
    /// falloff factor in [0,1]: full at distance 0 to silence at
    /// <see cref="AudibleRadius"/>. Negative/zero clamps to 1; past the audible
    /// radius clamps to 0.</summary>
    public static float Falloff(float distance)
    {
        if (distance <= 0f) return 1f;
        if (distance >= AudibleRadius) return 0f;
        return 1f - distance / AudibleRadius;
    }

    /// <summary>Ambient in-flight volume (0..<see cref="MaxAmbientVolume"/>) for a
    /// missile at <paramref name="distance"/> from the listener.</summary>
    public static float VolumeForDistance(float distance) =>
        Falloff(distance) * MaxAmbientVolume;

    /// <summary>Explosion one-shot volume (0..<see cref="MaxExplosionVolume"/>) for a
    /// detonation at <paramref name="distance"/> from the listener.</summary>
    public static float ExplosionVolumeForDistance(float distance) =>
        Falloff(distance) * MaxExplosionVolume;

    /// <summary>True when a missile spawned at <paramref name="spawnDistance"/>
    /// from the listener is attributable to the LOCAL ship's own launch (within
    /// <see cref="LaunchOriginRadius"/>).</summary>
    public static bool IsLocalLaunch(float spawnDistance) => spawnDistance <= LaunchOriginRadius;
}

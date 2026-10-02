namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P237 — the pure distance → volume mapping for a warp's ambient sound,
/// deliberately free of any MonoGame type (same discipline as
/// <see cref="ProjectileAmbientSoundCurve"/> / <see cref="EngineSoundCurve"/>),
/// so the falloff is unit-testable without a graphics/audio device.
/// </summary>
/// <remarks>
/// <para><b>What this models.</b> Native attaches a per-object
/// <c>AmbientSoundSite</c> to every warp via the same generic site asteroids /
/// missiles / probes / stations all use (<c>case OT_warp:</c>,
/// <c>WinTrek/trekigc.cpp:1436-1437</c>):
/// <c>m_pSoundSite = new AmbientSoundSite(pmodel, alephSound)</c>. That is the
/// actual mechanism behind Domingo's requested "a distinct sound that increases
/// as you get close": a normal 3D-positional <b>looping</b> sound at the warp's
/// position whose volume the engine attenuates by distance — <b>not</b> a
/// special proximity trigger (none exists in the native source). The real cue is
/// <c>alephloop.ogg</c> (<c>alephSound = GainSound(SFXGain, ThreeDSound(200,
/// LoopingSound(ImportWave("alephloop"))))</c>, <c>Artwork/sounddef.mdl:385</c>).
/// </para>
/// <para><b>Falloff shape.</b> This port has no 3D-positional audio engine yet
/// (P213's engine/cockpit/ambience cues are all non-positional), so per the P232
/// brief's blessed scope-cut the same audible behaviour is approximated by
/// computing the listener→warp distance each frame and scaling a looped sound
/// instance's volume by that distance. Linear over <see cref="AudibleRadius"/>:
/// full volume <see cref="MaxVolume"/> at zero distance, ramping to silence at
/// <see cref="AudibleRadius"/>. Simple and predictable — the qualitative "swell
/// on approach, fade on recession" the positional engine provides, without
/// pretending to match its exact perceptual falloff.</para>
/// <para><b>Audible radius choice.</b> Native authors the warp's positioned
/// radius as <c>ThreeDSound(200, …)</c> — a 200-unit attenuation distance, a
/// leftover constant shared with small projectile one-shots that predates the
/// huge scale of a rendered warp gate. Against P237's visual resize the gate is
/// now ~1900 units across, so a 200-unit audible radius would mean the sound
/// only becomes audible essentially <i>inside</i> the funnel — the opposite of
/// hearing a landmark gate on approach. The port's approximation instead spans a
/// few gate-diameters (<see cref="AudibleRadius"/> = 4 × ~1900) so a player
/// entering a sector hears the distinctive warp as they close in and the volume
/// swells meaningfully — the behaviour Domingo asked for, documented rather than
/// treated as a faithful-but-useless literal 200.</para>
/// </remarks>
public static class WarpAmbientSoundCurve
{
    /// <summary>Distance (world units) at which a warp's ambient loop reaches
    /// silence. Chosen as a few diameters of the rendered gate (~1900 units after
    /// the P237 visual scale) so the warp reads as a distance landmark that
    /// swells on approach, rather than native's unrealistically-tiny
    /// <c>ThreeDSound(200)</c> (see remarks).</summary>
    public const float AudibleRadius = 7700f;

    /// <summary>Peak ambient volume (0..1) at zero distance from the listener.
    /// Warps are the sector's most prominent fixtures, so louder than a
    /// projectile's 0.55 — but capped so sitting at the lip does not drown the
    /// engine/ambience bed.</summary>
    public const float MaxVolume = 0.8f;

    /// <summary>Map a listener→warp distance (world units) to an ambient volume
    /// in [0, <see cref="MaxVolume"/>]: linear falloff from full at distance 0 to
    /// silence at <see cref="AudibleRadius"/>. Negative/zero distances clamp to
    /// full; distances past the audible radius clamp to 0.</summary>
    public static float VolumeForDistance(float distance)
    {
        if (distance <= 0f) return MaxVolume;
        if (distance >= AudibleRadius) return 0f;
        float t = distance / AudibleRadius;
        return MaxVolume * (1f - t);
    }
}

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P260 — the pure distance → volume mappings for the two probe sound cues
/// (the deploy one-shot and the ambient scanning loop), deliberately free of any
/// MonoGame type (same discipline as <see cref="MissileAmbientSoundCurve"/> /
/// <see cref="ProjectileAmbientSoundCurve"/> / <see cref="EngineSoundCurve"/>),
/// so the falloffs and attribution rules are unit-testable without a graphics /
/// audio device.
/// </summary>
/// <remarks>
/// <para><b>Real per-type fidelity is out of reach client-side.</b> This port's
/// MonoGame client is a pure renderer with no loaded static core, so the probe's
/// per-type decoded <c>DataProbeTypeIGC.AmbientSound</c> (byte offset 484,
/// <c>missionigc.cpp:968</c>, exposed via <c>CprobeIGC::GetAmbientSound()</c>,
/// <c>probeigc.h:261-264</c>) — a SoundID into the core's sound table — cannot
/// be resolved to an asset client-side. The same single-real-default convention
/// P236 established for missile sounds applies here: a single real scanning-loop
/// asset stands in for every probe type, documented, not fabricated.</para>
/// <para><b>The deploy sound is real and generic.</b> Native plays the probe's
/// deploy cue through the expendable-launcher dispatch
/// (<c>clintlib.cpp:3245-3257</c>: <c>case OT_probeType: PlaySoundEffect(deployProbeSound, pShip)</c>),
/// where <c>deployProbeSound = GainSound(SFXGain, ImportWave("dropobject"))</c>
/// (<c>sounddef.mdl:369</c>) — a generic expendable-drop sound <b>reused for
/// chaff, mine and probe alike</b> (lines 367-369), so native deliberately does
/// NOT give the probe its own launch sound. P755 bundles the real
/// <c>dropobject</c> wave (for the chaff-deploy cue), so the deploy cue now maps
/// to it directly (sounddef.mdl:369) — faithfully mirroring that native reuses
/// ONE generic deploy sound for all expendables.</para>
/// </remarks>
public static class ProbeAmbientSoundCurve
{
    /// <summary>Distance (world units) at which any probe sound cue reaches
    /// silence. Smaller than the missile radius because a probe is a stationary
    /// object you encounter at close range, not something you hear across the
    /// sector; both the deploy and ambient cues are gated/scaled by it.</summary>
    public const float AudibleRadius = 4000f;

    /// <summary>Peak volume (0..1) of a probe's continuous scanning ambient loop
    /// at zero distance — a subtle device hum, well under the engine/ambience
    /// bed so an idle probe does not dominate the mix.</summary>
    public const float MaxAmbientVolume = 0.35f;

    /// <summary>Map a listener→source distance (world units) to a linear falloff
    /// factor in [0,1]: full at distance 0 to silence at
    /// <see cref="AudibleRadius"/>. Negative/zero clamps to 1; past the audible
    /// radius clamps to 0.</summary>
    public static float Falloff(float distance)
    {
        if (distance <= 0f) return 1f;
        if (distance >= AudibleRadius) return 0f;
        return 1f - distance / AudibleRadius;
    }

    /// <summary>Ambient scanning-loop volume (0..<see cref="MaxAmbientVolume"/>) for
    /// a probe at <paramref name="distance"/> from the listener.</summary>
    public static float VolumeForDistance(float distance) =>
        Falloff(distance) * MaxAmbientVolume;
}

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P787b — the per-asteroid ambient loop's pure curve (SIM-0345). Native
/// attaches an <c>AmbientSoundSite</c> to every asteroid
/// (<c>WinTrek/trekigc.cpp:1432-1434</c>) whose sound is the shared
/// <c>asteroidSound</c> template — <c>GainSound(SFXGain, PitchSound(0.35,
/// ThreeDSound(200, LoopingSound(ImportWave("shipout")))))</c>
/// (sounddef.mdl, the <c>asteroid</c> DEFSOUND, sounds.h:140): a pitch-0.35
/// shipout loop. The port replays the bundled <c>shipout</c> wave at pitch
/// −0.35, distance-attenuated.
///
/// <para><b>Audible radius.</b> Native authors the positioned radius as
/// <c>ThreeDSound(200)</c> — 200 world units, far below inter-asteroid
/// spacing; the port honours the authored radius (rocks are dense enough that
/// mining runs sit inside it, and native's value IS the intended near-field
/// rumble).</para>
/// </summary>
public static class AsteroidAmbientSoundCurve
{
    /// <summary>Native <c>ThreeDSound(200, …)</c> — the authored attenuation
    /// distance (sounddef.mdl's asteroidSound template).</summary>
    public const float AudibleRadius = 200f;

    /// <summary>Peak ambient volume (0..1) at zero distance. Native applies no
    /// extra gain (SFXGain); the port's peak sits below the station bed's 0.55
    /// because native's 200-unit radius means a rock is only ever heard right
    /// next to it (mining distance).</summary>
    public const float MaxVolume = 0.5f;

    /// <summary>Map a listener→asteroid distance (world units) to an ambient
    /// volume in [0, <see cref="MaxVolume"/>]: linear falloff from full at
    /// distance 0 to silence at <see cref="AudibleRadius"/>.</summary>
    public static float VolumeForDistance(float distance)
    {
        if (distance <= 0f) return MaxVolume;
        if (distance >= AudibleRadius) return 0f;
        float t = distance / AudibleRadius;
        return MaxVolume * (1f - t);
    }
}

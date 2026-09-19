namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P267 — the pure distance → volume mapping for a station's exterior ambient
/// loop, deliberately free of any MonoGame type (same discipline as
/// <see cref="ProjectileAmbientSoundCurve"/> / <see cref="WarpAmbientSoundCurve"/>),
/// so the falloff is unit-testable without a graphics/audio device.
/// </summary>
/// <remarks>
/// <para><b>What this models.</b> Native attaches a per-object
/// <c>AmbientSoundSite</c> to every station via the same generic site ships /
/// missiles / warps / probes all use (<c>case OT_station:</c>,
/// <c>WinTrek/trekigc.cpp:1465-1468</c>):
/// <c>m_pSoundSite = new AmbientSoundSite(pmodel,
/// ((IstationIGC*)pmodel)-&gt;GetExteriorSound())</c> — a looping, 3D-positioned
/// sound at the station's location, attenuated by distance. The port's
/// flyable POC reads the real station data (<c>DataStationIGC.Position</c>),
/// so it mirrors the already-wired warp/projectile ambient pattern exactly:
/// each in-sector station within audible range gets a distance-scaled loop.</para>
/// <para><b>Default asset.</b> Every station type in the demo core
/// (PCore001.igc) carries <c>ExteriorSound = 380</c>, which the sounddef maps
/// to <c>ILStationExternalSound</c> (<c>sounddef.mdl:1998</c>) — a paired cue
/// whose continuous loop is <c>shipout.ogg</c> (pitched 0.5). The client has
/// no core, so per the P232 single-real-default convention it uses that
/// <c>shipout</c> loop (also the small-ship exterior, id 180).</para>
/// <para><b>Audible radius choice.</b> Native authors the station's positioned
/// radius as <c>ThreeDSound(200, LoopingSound(shipout))</c> — a 200-unit
/// attenuation distance, an unrealistically-tiny leftover shared with
/// one-shots (the same problem the P237 warp curve documented). Stations are
/// major sector fixtures you undock from and orbit; the port's approximation
/// spans a comfortable near-field (<see cref="AudibleRadius"/>) so launching
/// from a station is audible and receding fades it out.</para>
/// </remarks>
public static class StationAmbientSoundCurve
{
    /// <summary>Distance (world units) at which a station's ambient loop reaches
    /// silence. Chosen as a near-field radius around a base (see remarks) so a
    /// launching/approaching pilot hears the station and receding fades it,
    /// rather than native's unrealistically-tiny <c>ThreeDSound(200)</c>.</summary>
    public const float AudibleRadius = 5000f;

    /// <summary>Peak ambient volume (0..1) at zero distance from the listener.
    /// Quieter than a warp's landmark 0.8 — a station is a background fixture,
    /// not a transit landmark — but still clearly audible while docked.</summary>
    public const float MaxVolume = 0.55f;

    /// <summary>Map a listener→station distance (world units) to an ambient
    /// volume in [0, <see cref="MaxVolume"/>]: linear falloff from full at
    /// distance 0 to silence at <see cref="AudibleRadius"/>. Negative/zero
    /// distances clamp to full; distances past the audible radius clamp to 0.</summary>
    public static float VolumeForDistance(float distance)
    {
        if (distance <= 0f) return MaxVolume;
        if (distance >= AudibleRadius) return 0f;
        float t = distance / AudibleRadius;
        return MaxVolume * (1f - t);
    }
}

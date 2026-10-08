using System;
using System.Collections.Generic;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// One <b>sub-volume</b> of a P573 volumetric explosion — the raymarched
/// analogue of a native fireball decal. Mirrors the native per-decal setup in
/// <c>ExplosionGeo::AddExplosion</c> (<c>effect/explosion.cpp:104-117</c>):
/// sub-volume <c>i</c> is born at <c>i * 0.25</c> s, sits at
/// <c>position + RandomPosition(radiusExplosion·0.5)</c> (the same deterministic
/// <see cref="ExplosionSampler.OffsetFor"/> offsets the sprite path uses), and
/// drifts with the wreck's velocity (<c>t × m_dposition</c>,
/// <c>effect/explosion.cpp:161-163</c>). The renderer turns each unit into a
/// raymarched impostor whose radius/shape evolve over <see cref="Life"/> and cool
/// to smoke. Pure data — no MonoGame/wire types.
/// </summary>
public readonly record struct VolumetricSubUnit(
    Vector3 Center,   // world centre, already drifted by the wreck's velocity
    float PeakRadius, // the native radiusExplosion the volume expands toward
    float Elapsed,    // seconds since the EXPLOSION started (this sample's time)
    float Born,       // seconds after explosion start this unit begins
    float Life,       // total life seconds (fire + smoke tail)
    float Seed,       // deterministic per-unit seed (rotation, offset parity)
    Vector3 Tint);    // native type tint (rgb, 0..1)

/// <summary>
/// P573 Technique A (volumetric) — pure translation of a live explosion into
/// its raymarched sub-volumes. Every rule is cited from native
/// <c>effect/explosion.cpp</c> (the same timing family <see cref="ExplosionSampler"/>
/// already ports); the only addition is a smoke <see cref="SmokeTailSeconds"/>
/// that lets the volume's smoke keep expanding and thinning after the fireball
/// decal's film ends (<c>effect/explosion.cpp:180-202</c> runs the film at 20 fps).
/// </summary>
public static class VolumetricExplosionSampler
{
    /// <summary>Per-decal spawn stagger — decal i starts at <c>i * 0.25</c>s
    /// (<c>effect/explosion.cpp:113</c>). Identical to
    /// <see cref="ExplosionSampler.SpriteStagger"/>.</summary>
    public const float SpriteStagger = 0.25f;

    /// <summary>Extra seconds the volume lingers as cooling/expanding smoke after
    /// its fireball film ends (this port's own smoke tail — native's decal simply
    /// pops off after its last frame; the task asks the smoke to keep thinning).</summary>
    public const float SmokeTailSeconds = 2.4f;

    /// <summary>Native sprite playback rate — <c>frame = (int)(time * 20.0f)</c>
    /// (<c>effect/explosion.cpp:183</c>).</summary>
    public const float FramesPerSecond = 20f;

    /// <summary>Tint fallback when the native type has no tint (missile/asteroid —
    /// <c>trekigc.cpp:115-119,148-151</c> pass no colour): a generic white-hot.</summary>
    public static readonly Vector3 DefaultTint = new(1f, 0.925f, 0.78f);

    /// <summary>
    /// The sub-volumes a blast shows at <paramref name="elapsed"/> seconds.
    /// Units not yet born (stagger) or past their total life are omitted. Centres
    /// are drifted by <paramref name="velocity"/> × elapsed (native
    /// <c>t × m_dposition</c>).
    /// </summary>
    public static List<VolumetricSubUnit> SampleUnits(
        ExplosionPreset preset, float elapsed,
        Vector3 position, Vector3 velocity)
    {
        if (elapsed < 0f) elapsed = 0f;
        var units = new List<VolumetricSubUnit>(preset.CountDecals);
        Vector3 tint = TintFor(preset);

        for (int i = 0; i < preset.CountDecals; i++)
        {
            float born = i * SpriteStagger;
            if (elapsed < born) continue;                  // not begun yet

            var film = preset.Films[i % preset.Films.Length];
            float local = elapsed - born;
            int frame = (int)(local * FramesPerSecond);
            float filmLife = film.FrameCount / FramesPerSecond;
            if (frame >= film.FrameCount && local > filmLife + SmokeTailSeconds) continue;

            // Native: position + RandomPosition(radiusExplosion*0.5) + t*velocity.
            var centre = new Vector3(
                position.X + ExplosionSampler.OffsetFor(i, preset.RadiusExplosion * 0.5f).X + velocity.X * elapsed,
                position.Y + ExplosionSampler.OffsetFor(i, preset.RadiusExplosion * 0.5f).Y + velocity.Y * elapsed,
                position.Z + ExplosionSampler.OffsetFor(i, preset.RadiusExplosion * 0.5f).Z + velocity.Z * elapsed);

            units.Add(new VolumetricSubUnit(
                centre,
                preset.RadiusExplosion,
                elapsed,
                born,
                filmLife + SmokeTailSeconds,
                ExplosionSampler.AngleFor(i),
                tint));
        }
        return units;
    }

    /// <summary>The native type tint (the shock-wave colour the preset carries —
    /// <c>trekigc.cpp:107-114</c> mine violet, <c>:121-146</c> ship/station orange),
    /// or <see cref="DefaultTint"/> for tintless types.</summary>
    public static Vector3 TintFor(ExplosionPreset preset)
    {
        if (preset.ShockR > 0.02f || preset.ShockG > 0.02f || preset.ShockB > 0.02f)
            return new Vector3(preset.ShockR, preset.ShockG, preset.ShockB);
        return DefaultTint;
    }
}

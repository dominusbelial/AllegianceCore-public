using System;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P562 — the pure (MonoGame-free) native aleph star-core + transit-swell
/// rules, extracted from the original <c>AlephGeo::Render</c> so they are
/// unit-testable headless. Two counter-rotating <c>f02bmp</c> star decals sit
/// at the aleph pinch; on a ship transit they swell to ~6× with 8 resonant
/// pulses, then decay over 0.25 s — native's exact curve
/// (<c>effect/alephgeo.cpp:158-198</c>).
/// </summary>
public static class WarpVortex
{
    /// <summary>First star decal's rotation rate in rad/s (alephgeo.cpp:197:
    /// <c>0.25f * m_time</c>).</summary>
    public const float StarRateA = 0.25f;

    /// <summary>Second star decal's rotation rate in rad/s, counter-rotating
    /// (alephgeo.cpp:207: <c>-0.375f * m_time</c>).</summary>
    public const float StarRateB = -0.375f;

    /// <summary>The base star size as a FRACTION of the funnel's unit rim
    /// radius (alephgeo.cpp:160, <c>fScale = 0.1f</c> — "the size of the star
    /// on the aleph by default").</summary>
    public const float StarBaseScaleFraction = 0.1f;

    /// <summary>Post-explosion decay window (alephgeo.cpp:162,
    /// <c>fPostExplosionDuration = 0.25f</c>).</summary>
    public const float PostExplosionDuration = 0.25f;

    /// <summary>Number of resonant pulses during the swell (alephgeo.cpp:176,
    /// <c>fNumPulses = 8.0f</c>).</summary>
    public const float SwellPulses = 8f;

    /// <summary>Peak swell multiple over the base star (alephgeo.cpp:166,
    /// <c>fMaxSwellSize = 6.0f − fScale</c>).</summary>
    public const float MaxSwellSize = 6f;

    /// <summary>
    /// Native star-scale curve (alephgeo.cpp:158-188). <paramref name="now"/>
    /// is the current clock; <paramref name="triggerTime"/> the instant the
    /// swell began; <paramref name="explodeDuration"/> how long the
    /// 8-pulse swell phase runs before the 0.25 s decay. Returns the star's
    /// current scale as a fraction of the funnel rim radius (0.1 idle, up to
    /// ~6–10 during the swell), which the renderer multiplies by the aleph's
    /// world rim radius to size the two f02 decals.
    /// </summary>
    public static float StarScale(float now, float triggerTime, float explodeDuration,
        float baseScale = StarBaseScaleFraction)
    {
        float fScale = baseScale;
        float explodeTime = triggerTime + explodeDuration;
        float delta = explodeTime - now;                 // how long 'til the swell climax

        if (delta <= -PostExplosionDuration)             // idle: no imminent/just-finished swell
            return fScale;

        float maxSwell = MaxSwellSize - baseScale;
        if (delta >= 0f)
        {
            // Swell pending: progress 0 → 1 across the explode duration.
            float dur = explodeDuration > 0f ? explodeDuration : 1e-3f;
            float progress = 1f - delta / dur;
            float newScale = progress * progress * maxSwell;
            // Native's "resonant pulse": |sin(π·(numPulses+0.5)·progress)|.
            float cosine = MathF.Abs(MathF.Sin(MathF.PI * (SwellPulses + 0.5f) * progress));
            fScale += (newScale * cosine * cosine) + (newScale * 0.8f);
        }
        else
        {
            // Swell just peaked: decay from 1 → 0 over PostExplosionDuration.
            float progress = 1f + delta / PostExplosionDuration;
            fScale += progress * progress * maxSwell;
        }

        return fScale;
    }
}

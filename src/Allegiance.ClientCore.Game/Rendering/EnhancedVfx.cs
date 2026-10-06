using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P546 — near-camera smoke/fire fade parameters (adopted from the P543 spike).
/// Full opacity beyond <see cref="FadeEnd"/> metres from the eye, gone below
/// <see cref="FadeStart"/> metres, linearly faded in between. Keeps your own
/// missiles' smoke off the screen while leaving distant puffs visible.
/// <see cref="Factor"/> is pure and unit-testable headless.
/// </summary>
public readonly record struct ParticleNearFade(bool Enabled, float FadeStart, float FadeEnd)
{
    /// <summary>The near-fade alpha multiplier for a puff <paramref name="dist"/>
    /// metres from the eye: 1.0 outside (beyond <paramref name="fadeEnd"/>),
    /// 0.0 inside (below <paramref name="fadeStart"/>), linear between. Disabled
    /// → always 1.0.</summary>
    public static float Factor(float dist, bool enabled, float fadeStart, float fadeEnd)
    {
        if (!enabled) return 1f;
        float span = fadeEnd - fadeStart;
        if (span <= 0f) return dist < fadeEnd ? 0f : 1f;
        return MathHelper.Clamp((dist - fadeStart) / span, 0f, 1f);
    }

    /// <summary>The default disabled value (no fading).</summary>
    public static readonly ParticleNearFade Off = new(false, 0f, 0f);

    /// <summary>
    /// P546 — the re-picked near-fade for the REAL in-game flight/chase camera.
    /// Gone below 6&nbsp;u, full above 45&nbsp;u. The chase camera sits
    /// <c>distanceBehind=350</c>/<c>heightAbove=90</c> scaled by
    /// <c>shipRadius / ChaseCamera.ReferenceRadius(95)</c>, i.e. ≈30&nbsp;u
    /// behind a fighter and ≈76&nbsp;u behind a bomber. 45&nbsp;u full keeps
    /// every trail/ship puff at or beyond the camera's own ship fully visible,
    /// while 6&nbsp;u gone clears only the smoke literally at the eye (your own
    /// muzzle/clip-through), the only case that actually occludes the screen.
    /// Re-picked from the spike's offscreen 2&nbsp;m/18&nbsp;m against these real
    /// camera distances — see docs/reports/P546_ENHANCED_EXPLOSIONS_AND_SMOKE.md.
    /// </summary>
    public static readonly ParticleNearFade Flight = new(true, 6f, 45f);
}

/// <summary>
/// P546 — shared pure helpers for the enhanced smoke/fire path: the P530
/// sector-light tint that makes smoke read as a grey volume rather than a glow,
/// and a couple of deterministic hashing helpers used for random frame/rotation
/// selection. All pure so they are unit-testable headless.
/// </summary>
public static class EnhancedVfx
{
    /// <summary>
    /// The premultiplied smoke tint for a given sector light: luminance from the
    /// ambient + a fraction of the key colour, then biased toward the key hue so
    /// smoke picks up the sector's dominant light without ever glowing. Neutral
    /// warm-grey when no light is available. Returns r,g,b in [~0.12, 0.55].
    /// </summary>
    public static XnaVec LitSmokeTint(SectorLighting? sectorLight)
    {
        if (sectorLight is { } s)
        {
            float amb = (s.Ambient.X + s.Ambient.Y + s.Ambient.Z) / 3f;
            float key = (s.KeyColor.X + s.KeyColor.Y + s.KeyColor.Z) / 3f;
            float l = MathHelper.Clamp(amb * 0.8f + key * 0.30f, 0.12f, 0.55f);
            float kr = MathHelper.Clamp(s.KeyColor.X, 0f, 1f);
            float kg = MathHelper.Clamp(s.KeyColor.Y, 0f, 1f);
            float kb = MathHelper.Clamp(s.KeyColor.Z, 0f, 1f);
            float mx = Math.Max(Math.Max(kr, kg), kb) + 1e-4f;
            return new XnaVec(l * (0.7f + 0.3f * kr / mx),
                              l * (0.7f + 0.3f * kg / mx),
                              l * (0.7f + 0.3f * kb / mx));
        }
        return new XnaVec(0.42f, 0.42f, 0.44f);
    }

    /// <summary>A cheap integer hash (fmix-style) — seeds the per-puff random
    /// frame/rotation deterministically per explosion/ship position.</summary>
    public static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
        return x;
    }

    /// <summary>
    /// The premultiplied-alpha transform used for every smoke puff: the sector-lit
    /// tint scaled by the puff alpha, so the blended result is <c>rgb = tint·α</c>
    /// (and rgb ≤ α always — smoke accumulates translucency, never glowing to
    /// white). Pure and unit-testable headless.
    /// </summary>
    public static (float R, float G, float B) Premultiplied(float r, float g, float b, float alpha)
        => (r * alpha, g * alpha, b * alpha);

    /// <summary>Next uniform [0,1) from an xorshift <paramref name="state"/>
    /// (advanced in place).</summary>
    public static float Rand01(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return state / 4294967296f;
    }
}

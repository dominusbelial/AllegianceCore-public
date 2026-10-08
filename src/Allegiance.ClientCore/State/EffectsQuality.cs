namespace Allegiance.ClientCore.State;

/// <summary>
/// P573 — the three effects tiers. Every tier keeps the SAME native explosion
/// timing/size (the <see cref="ExplosionPreset"/> table, cited
/// <c>trekigc.cpp:95-177</c>); they differ only in how each blast is rendered.
/// <list type="bullet">
/// <item><b>Native</b> — the original sprite artwork path (classic look, parity checks).</item>
/// <item><b>Enhanced</b> — CC0 flipbook explosions + animated premultiplied smoke
/// (P546/P561). The Android / low-end default.</item>
/// <item><b>Volumetric</b> — real-time raymarched volumes (Technique A) or
/// displaced-sphere fireballs (Technique B), the DesktopGL default when the
/// shader loads and the GPU probe passes.</item>
/// </list>
/// </summary>
public enum EffectsQuality { Native = 0, Enhanced = 1, Volumetric = 2 }

/// <summary>Which volumetric renderer technique the Volumetric tier uses.</summary>
public enum VolumetricTechnique { RaymarchA = 0, DisplacedSphereB = 1 }

/// <summary>
/// P573 — pure resolution of the effects tier from the request, the platform,
/// shader availability and the GPU probe. Deterministic and unit-testable so the
/// platform-default / fallback contract is pinned.
/// </summary>
public static class EffectsQualityResolver
{
    /// <summary>Parse the client's <c>--fx</c> value. Returns null for an
    /// unrecognised value (caller falls back to the platform default).</summary>
    public static (EffectsQuality Quality, VolumetricTechnique Tech)? ParseFx(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "native":     return (EffectsQuality.Native, VolumetricTechnique.RaymarchA);
            case "enhanced":   return (EffectsQuality.Enhanced, VolumetricTechnique.RaymarchA);
            case "volumetric": return (EffectsQuality.Volumetric, VolumetricTechnique.RaymarchA);
            case "displaced":  return (EffectsQuality.Volumetric, VolumetricTechnique.DisplacedSphereB);
            default:           return null;
        }
    }

    /// <summary>Platform default: <b>DesktopGL → Volumetric</b> (better GPU),
    /// <b>Android / mobile → Enhanced</b> (the baked flipbooks ship there; A's
    /// fill-rate is too heavy for the GLES2 baseline).</summary>
    public static EffectsQuality PlatformDefault(bool isMobile)
        => isMobile ? EffectsQuality.Enhanced : EffectsQuality.Volumetric;

    /// <summary>
    /// Resolve the EFFECTIVE tier from the request:
    /// <list type="number">
    /// <item>A request of Volumetric only holds if <paramref name="volumetricAvailable"/>
    /// (shader loaded) AND <paramref name="gpuUnderBudget"/> (frame-time probe passes).</item>
    /// <item>Otherwise it falls back to Enhanced.</item>
    /// <item>Enhanced holds unless the caller forced a specific lower tier.</item>
    /// </list>
    /// <paramref name="forced"/> (an explicit <c>--fx</c>) wins over the platform
    /// default; when null the platform default governs. Returns the final tier and
    /// whether it fell back from a requested Volumetric.
    /// </summary>
    public static EffectsQuality Resolve(
        EffectsQuality requested,
        bool volumetricAvailable,
        bool gpuUnderBudget)
    {
        if (requested == EffectsQuality.Volumetric && volumetricAvailable && gpuUnderBudget)
            return EffectsQuality.Volumetric;
        if (requested == EffectsQuality.Volumetric)
            return EffectsQuality.Enhanced;          // shader fail or over budget -> drop a tier
        return requested;
    }

    /// <summary>Whether the Volumetric tier fell back to Enhanced under the given
    /// conditions (used to log the degradation exactly once).</summary>
    public static bool FellBack(EffectsQuality requested, EffectsQuality effective)
        => requested == EffectsQuality.Volumetric && effective != EffectsQuality.Volumetric;
}

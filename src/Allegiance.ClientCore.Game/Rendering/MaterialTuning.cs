using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HD-6 — per-material tuning for the experimental PBR path. The ESRGAN-derived
/// roughness/metallic maps are too rough and not metallic enough, which makes the
/// PBR hull pale, grey and matte. Rather than regenerate textures, these params
/// adjust the sampled maps and the scene lighting at draw time, driven by optional
/// per-atlas overrides in the pack manifest ("materialTuning" section) so hull
/// plating can differ from rubber/glass/glow. Pure math here mirrors the HLSL in
/// NativePbr.fx so it is unit-testable without a graphics device.
/// </summary>
/// <remarks>
/// Native lighting basis (see docs/reports/HD6_PBR_TUNING.md, §Scene lighting):
///  - bidirectional ambient 0.25 applied flat to EVERY surface regardless of metal —
///    device3d.cpp:792 bidirectional branch; clusterIGC.h:331 GetBidirectionalAmbientLevel.
///    So <see cref="AmbientMetalFloor"/> defaults to 1 (flat albedo*ambient, native),
///    restoring hull colour in shadow that HD-2's lerp(1,.25,metal) had crushed.
///  - coloured key + constant blue back light, no specular (device3d.cpp:758); the GGX
///    specular here is the intended modern addition for believable highlights.
/// </remarks>
public sealed record MaterialTuning
{
    // ── roughness curve:  rough' = clamp(rough*Scale + Bias, Min, Max) ─────────
    // ESRGAN rough median ~0.62 → ≈0.53: glossy enough for crisp believable
    // glints, but not mirror-flat so broad surfaces don't wash toward white.
    public float RoughnessScale { get; init; } = 0.75f;
    public float RoughnessBias { get; init; } = 0.05f;
    public float RoughnessMin { get; init; } = 0.15f;
    public float RoughnessMax { get; init; } = 0.92f;

    // ── metallic curve: lift neutral plating toward conductor, keep warm painted
    //    metal & matte dielectric reads warm so the NATIVE diffuse mood survives ──
    // metal >= Deadband: metal' = (m-Deadband)*Gain + Deadband (clamped to Max).
    // metal <  Deadband (rubber/glass/glow, warm paint): unchanged (dielectric —
    //   keeps the native warm diffuse; believable highlights still come from the
    //   white dielectric specular + low roughness).
    public float MetallicGain { get; init; } = 1.15f;
    public float MetallicDeadband { get; init; } = 0.30f;
    public float MetallicMin { get; init; } = 0.0f;
    public float MetallicMax { get; init; } = 0.90f;

    // ── scene lighting / tonemap ──────────────────────────────────────────────
    /// <summary>Minimum ambient factor reached at full metal (1 = native's flat
    /// 0.25 ambient on every surface; HD-2's 0.25 crushed metallic plating).</summary>
    public float AmbientMetalFloor { get; init; } = 1.0f;
    /// <summary>Global exposure before the tone map. The base lighting now runs in
    /// gamma space and matches the native classic path exactly, so unity exposure
    /// keeps the native brightness; the added highlights sit on top.</summary>
    public float Exposure { get; init; } = 1.0f;
    /// <summary>Hue-preserving shoulder onset (below this the curve is identity).</summary>
    public float TonemapThreshold { get; init; } = 0.8f;
    /// <summary>Shoulder roll-off slope (larger = harder knee into white).</summary>
    public float TonemapSlope { get; init; } = 0.5f;
    /// <summary>Conductor fresnel sheen strength (0 = off; native has no specular).
    /// Kept low so the cool blue back-light doesn't wash warm painted metal.</summary>
    public float EnvSheen { get; init; } = 0.10f;

    public static MaterialTuning Default { get; } = new();

    /// <summary>Merge an optional per-atlas manifest override (case-insensitive
    /// param name → value) onto the defaults. Unknown names are ignored. Keys are
    /// normalized to an ordinal-ignore-case lookup regardless of the source
    /// dictionary's comparer (manifests arrive OrdinalIgnoreCase; tests may not).</summary>
    public static MaterialTuning FromOverrides(IReadOnlyDictionary<string, float>? o)
    {
        if (o is null || o.Count == 0) return Default;
        var map = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in o) map[kv.Key] = kv.Value;
        var t = new MaterialTuning();
        return t with
        {
            RoughnessScale = Pick(map, "RoughnessScale", t.RoughnessScale),
            RoughnessBias = Pick(map, "RoughnessBias", t.RoughnessBias),
            RoughnessMin = Pick(map, "RoughnessMin", t.RoughnessMin),
            RoughnessMax = Pick(map, "RoughnessMax", t.RoughnessMax),
            MetallicGain = Pick(map, "MetallicGain", t.MetallicGain),
            MetallicDeadband = Pick(map, "MetallicDeadband", t.MetallicDeadband),
            MetallicMin = Pick(map, "MetallicMin", t.MetallicMin),
            MetallicMax = Pick(map, "MetallicMax", t.MetallicMax),
            AmbientMetalFloor = Pick(map, "AmbientMetalFloor", t.AmbientMetalFloor),
            Exposure = Pick(map, "Exposure", t.Exposure),
            TonemapThreshold = Pick(map, "TonemapThreshold", t.TonemapThreshold),
            TonemapSlope = Pick(map, "TonemapSlope", t.TonemapSlope),
            EnvSheen = Pick(map, "EnvSheen", t.EnvSheen),
        };
    }
    private static float Pick(IReadOnlyDictionary<string, float> o, string n, float dflt)
        => o.TryGetValue(n, out var v) ? v : dflt;

    // ── pure mirror of the shader math (unit-tested; HLSL in NativePbr.fx) ─────
    public static float AdjustRoughness(float rough, MaterialTuning t)
        => Math.Clamp(rough * t.RoughnessScale + t.RoughnessBias, t.RoughnessMin, t.RoughnessMax);

    public static float AdjustMetallic(float metal, MaterialTuning t)
    {
        float m = metal >= t.MetallicDeadband ? (metal - t.MetallicDeadband) * t.MetallicGain + t.MetallicDeadband : metal;
        return Math.Clamp(m, t.MetallicMin, t.MetallicMax);
    }

    public static float AmbientFactor(float metal, MaterialTuning t)
        => MathHelper.Lerp(1f, t.AmbientMetalFloor, metal);

    public static Vector3 ToneMap(Vector3 c, MaterialTuning t)
    {
        var lc = c * t.Exposure;
        float peak = Math.Max(lc.X, Math.Max(lc.Y, lc.Z));
        if (peak <= t.TonemapThreshold) return lc;
        float mapped = t.TonemapThreshold + (peak - t.TonemapThreshold) / (1f + Math.Max(peak - t.TonemapThreshold, 0f) / t.TonemapSlope);
        float s = mapped / Math.Max(peak, 1e-5f);
        return new Vector3(lc.X * s, lc.Y * s, lc.Z * s);
    }
}

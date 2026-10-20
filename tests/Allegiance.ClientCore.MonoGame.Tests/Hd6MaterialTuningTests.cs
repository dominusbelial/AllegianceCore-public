using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HD-6 — pure-math tests for the tuned experimental PBR (<see cref="MaterialTuning"/>).
/// These mirror the HLSL in NativePbr.fx so the material-map curves, the native-
/// faithful ambient, and the hue-preserving tone map are verified without a graphics
/// device. Several assertions target the FAILING behaviour of the pre-HD-6 code
/// (e.g. ambient factor was lerp(1,.25,metal), crushing metallic plating to 25%
/// ambient below native's flat 0.25).
/// </summary>
public sealed class Hd6MaterialTuningTests
{
    // ── native-faithful ambient (the core HD-5→HD-6 colour-mood fix) ────────
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.4f)]
    [InlineData(0.9f)]
    [InlineData(1.0f)]
    public void Ambient_factor_is_flat_one_regardless_of_metal(float metal)
    {
        // Native applies its flat 0.25 bidirectional ambient to EVERY surface
        // (clusterIGC.h:331; device3d.cpp:792). Pre-HD-6 did lerp(1,.25,metal),
        // so at metal=1 the ambient factor was 0.25 — which darkened metallic
        // plating below native and greyed the hull. Default floor must be 1.
        Assert.Equal(1f, MaterialTuning.AmbientFactor(metal, MaterialTuning.Default), 6);
    }

    [Fact]
    public void Ambient_factor_honors_an_explicit_sub_1_floor()
    {
        var t = MaterialTuning.Default with { AmbientMetalFloor = 0.5f };
        Assert.Equal(1f, MaterialTuning.AmbientFactor(0f, t), 6);
        Assert.Equal(0.755f, MaterialTuning.AmbientFactor(0.51f, t), 2);
        Assert.Equal(0.5f, MaterialTuning.AmbientFactor(1f, t), 6);
    }

    // ── material-map curves ─────────────────────────────────────────────────
    [Fact]
    public void Roughness_is_flattened_but_clamped_within_bounds()
    {
        var t = MaterialTuning.Default;
        // The ESRGAN estimate median ~0.62 for hull plating was too rough (matte).
        Assert.Equal(0.515f, MaterialTuning.AdjustRoughness(0.62f, t), 3);
        // Clamping: values can never escape [RoughnessMin, RoughnessMax].
        Assert.Equal(t.RoughnessMin, MaterialTuning.AdjustRoughness(0f, t), 6);
        Assert.Equal(t.RoughnessMax, MaterialTuning.AdjustRoughness(2f, t), 6);
    }

    [Fact]
    public void Metallic_lifts_plating_but_leaves_low_matte_reads_dielectric()
    {
        var t = MaterialTuning.Default;
        // Mid plating (0.53) is lifted toward conductor for crisp believable glints.
        Assert.Equal((0.53f - t.MetallicDeadband) * t.MetallicGain + t.MetallicDeadband,
            MaterialTuning.AdjustMetallic(0.53f, t), 4);
        // Low / matte reads (rubber/glass/glow) below the deadband stay dielectric —
        // they must NOT be dragged into metal (that washed warm hulls to silver).
        Assert.Equal(0.1f, MaterialTuning.AdjustMetallic(0.1f, t), 6);
        Assert.Equal(0f, MaterialTuning.AdjustMetallic(0f, t), 6);
        // Never exceeds MetallicMax.
        Assert.Equal(t.MetallicMax, MaterialTuning.AdjustMetallic(1f, t), 6);
    }

    // ── hue-preserving tone map + exposure ─────────────────────────────────
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.3999f)]
    [InlineData(0.7999f)]
    public void Tonemap_is_identity_below_threshold(float peak)
    {
        var c = new Vector3(peak, peak * 0.5f, peak * 0.25f);
        Assert.Equal(c, MaterialTuning.ToneMap(c, MaterialTuning.Default));
    }

    [Fact]
    public void Tonemap_compresses_peak_channel_while_preserving_hue()
    {
        // Above the threshold only the peak is pulled down; every channel is scaled
        // by the SAME factor so the rgb ratios (hue/saturation) survive — the legacy
        // raw clip desaturated highlights to white.
        var c = new Vector3(1.2f, 0.6f, 0.3f);
        var m = MaterialTuning.ToneMap(c, MaterialTuning.Default);
        float peak = m.X, scale = peak / 1.2f;
        Assert.True(peak < 1.2f, "peak must compress");
        Assert.True(peak >= 1.019f, "peak should land in the shoulder, not clip");
        Assert.Equal(0.5f, m.Y / peak, 4);   // ratio g/r preserved
        Assert.Equal(0.25f, m.Z / peak, 4);  // ratio b/r preserved
        Assert.Equal(1.2f * scale, m.X, 4);
    }

    [Fact]
    public void Exposure_scales_the_whole_input_before_the_curve()
    {
        var t = MaterialTuning.Default with { Exposure = 0.9f };
        var outLow = MaterialTuning.ToneMap(new Vector3(0.4f, 0.2f, 0.1f), t);
        Assert.Equal(0.36f, outLow.X, 5); Assert.Equal(0.18f, outLow.Y, 5); Assert.Equal(0.09f, outLow.Z, 5);
        // Above the threshold the shoulder applies after exposure (peak 1.0*0.9=0.9 > 0.8).
        var outHi = MaterialTuning.ToneMap(new Vector3(1.0f, 0.5f, 0.25f), t);
        Assert.InRange(outHi.X, 0.878f, 0.889f);   // peak 0.9 → shoulder ~0.883
        Assert.Equal(0.5f, outHi.Y / outHi.X, 4);
    }

    // ── manifest overrides ─────────────────────────────────────────────────
    [Fact]
    public void Manifest_overrides_apply_case_insensitively_and_ignore_unknown_keys()
    {
        var o = new Dictionary<string, float>
        {
            ["RoughnessScale"] = 0.5f,
            ["roughnessbias"] = 0.1f,       // lower-case key must still apply
            ["NotAParam"] = 123f,           // unknown → ignored
        };
        var t = MaterialTuning.FromOverrides(o);
        Assert.Equal(0.5f, t.RoughnessScale);
        Assert.Equal(0.1f, t.RoughnessBias);
        Assert.Equal(MaterialTuning.Default.RoughnessMin, t.RoughnessMin); // untouched
        Assert.Equal(MaterialTuning.Default.EnvSheen, t.EnvSheen);
    }

    [Fact]
    public void Null_or_empty_overrides_yield_the_defaults()
    {
        Assert.Same(MaterialTuning.Default, MaterialTuning.FromOverrides(null));
        Assert.Same(MaterialTuning.Default, MaterialTuning.FromOverrides(new Dictionary<string, float>()));
    }
}

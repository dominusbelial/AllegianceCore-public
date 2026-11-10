using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P989 — the resolved look of one frame: every knob the single output transform
/// (<see cref="HdrOutput"/>) and the existing bloom pass read. Producing a grade
/// value instead of scattering constants through the render path is what lets
/// <c>visuals.space.simPalette</c> switch the whole look on and off, and what
/// lets a test prove the default grade is exactly P984's.
/// </summary>
/// <param name="Curve">Curve the output transform applies.</param>
/// <param name="Exposure">Exposure, applied in the curve's own domain.</param>
/// <param name="Saturation">World saturation (1 = untouched), applied in linear
/// light after exposure and before the filmic fit — the "world" half of the
/// grade. The HUD never sees it (the HUD draws after the transform).</param>
/// <param name="BloomThreshold">Bright-pass threshold, in the SCENE TARGET's
/// stored domain (see <see cref="SimPalette.BloomThresholdStored"/>).</param>
/// <param name="BloomContribution">Glow contribution: the bright-pass scale the
/// existing bloom pass already applies (1 = the P547 look).</param>
public readonly record struct HdrGrade(
    HdrCurve Curve,
    float Exposure,
    float Saturation,
    float BloomThreshold,
    float BloomContribution)
{
    /// <summary>The sim grade is in force (anything other than P547/P984's look).</summary>
    public bool IsSim => Saturation != 1f || Exposure != HdrOutput.DefaultExposure;

    /// <summary>
    /// P984's picture: the neutral curve, unity exposure, no saturation change and
    /// the P547 bloom. Bit-exact to the frame P984 shipped, which is what makes
    /// "flag off is pixel-identical" a property of the grade value, not of hope.
    /// </summary>
    public static HdrGrade Native(HdrCurve curve)
        => new(curve, HdrOutput.DefaultExposure, 1f, BloomPostProcessor.DefaultThreshold,
            BloomPostProcessor.DefaultBloomScale);
}

/// <summary>
/// P989 — <c>visuals.space.simPalette</c>: the FS2-Open-flavoured "sim" colour
/// grade on top of P984's linear HDR pipeline (<c>docs/reports/FS2OPEN_STUDY_P978.md</c>
/// §5.3, candidate G2).
/// <para><b>What it changes: nothing structural.</b> There is no new fullscreen
/// pass and no new render target. The grade retunes the ONE output transform
/// P984 already runs (filmic curve, exposure, world saturation) and the ONE bloom
/// pass that already exists (threshold, contribution), so the cost of the look is
/// the cost of parameters on passes the frame already pays for.</para>
/// <para><b>Dependency.</b> The grade lives in the float scene path: exposure and
/// saturation act on >1.0 highlights and on linear light, and a bloom threshold of
/// 1.2 has nothing to cut in an 8-bit target. So the flag is a NO-OP unless
/// <c>visuals.space.hdrPipeline</c> really produced the RGBA16F scene target and
/// the output transform (<see cref="Applies"/>); the options page row says
/// "inactive" when it does not.</para>
/// <para><b>Starting values, and what tuning changed.</b> P978 proposed exposure
/// 0.9, world saturation 0.85, no tint/lift, bloom threshold 1.2 (linear) and
/// contribution 0.08 as a FIRST EXPERIMENT after linear-HDR plumbing. Exposure,
/// tint, threshold and contribution survived the captures unchanged; the
/// saturation came down to 0.75 because P978's 0.85 is linear-light and measured
/// on the RENDERED frame it raised display chroma instead of lowering it (see
/// <see cref="Saturation"/>). Nothing here is copied from SCP's lighting
/// profiles, whose numbers belong to a different content scale (P978 §5.3 says so
/// explicitly).</para>
/// </summary>
public static class SimPalette
{
    /// <summary>Exposure of the sim grade (P978 §5.3 starting value).</summary>
    public const float Exposure = 0.9f;

    /// <summary>
    /// World saturation (1 = native), applied in linear light.
    /// <para><b>Tuned down from P978's 0.85 after the captures.</b> P978's 0.85 is a
    /// LINEAR-light figure; measured on the rendered frame it RAISED display-space
    /// chroma (mean max-min over the frame: native sky 25.1 -> 25.9, HD sky
    /// 28.8 -> 31.6) because the filmic curve pulls the washed-out nebula down out of
    /// its shoulder while the exposure drops. The look P978 describes is a slightly
    /// DESATURATED, neutral world, so the linear saturation came down until the
    /// rendered frame is back at chroma parity with the ungraded picture (0.75:
    /// HD sky 28.68 vs 28.81 ungraded) while the filmic roll-off and the darker,
    /// restrained exposure stay. Numbers in docs/reports/SIM_PALETTE_P989.md §5-6.</para>
    /// </summary>
    public const float Saturation = 0.75f;

    /// <summary>Bloom bright-pass threshold in LINEAR light (P978 §5.3). The
    /// scene target stores display-encoded values with headroom, so the pass gets
    /// <see cref="BloomThresholdStored"/>.</summary>
    public const float BloomThresholdLinear = 1.2f;

    /// <summary>Glow contribution of the bloom composite in the sim grade.</summary>
    public const float BloomContribution = 0.08f;

    /// <summary>No tint, no lift (P978 §5.3): the grade is curve + exposure +
    /// saturation only, so a white stays white and black stays black.</summary>
    public static readonly Vector3 Tint = Vector3.One;

    /// <summary>The bright-pass threshold in the scene target's stored
    /// (display-encoded, may exceed 1) domain — 1.2 linear is ~1.083 stored.</summary>
    public static float BloomThresholdStored => HdrOutput.ToStored(BloomThresholdLinear);

    /// <summary>The grade actually applies: the flag is on AND the float HDR
    /// scene target + output transform really exist on this device.</summary>
    public static bool Applies(bool requested, bool hdrActive) => requested && hdrActive;

    /// <summary>
    /// Resolve this frame's grade. <paramref name="acesComparison"/> is P984's
    /// standalone ACES toggle; the sim grade already is the filmic curve, so the
    /// two agree when both are on. With the pipeline unavailable the P984 look is
    /// returned unchanged (the dependency rule: the flag does nothing).
    /// </summary>
    public static HdrGrade Grade(bool requested, bool hdrActive, bool acesComparison)
    {
        var baseline = HdrGrade.Native(acesComparison ? HdrCurve.Aces : HdrCurve.Neutral);
        if (!Applies(requested, hdrActive)) return baseline;
        return baseline with
        {
            Curve = HdrCurve.Aces,
            Exposure = Exposure,
            Saturation = Saturation,
            BloomThreshold = BloomThresholdStored,
            BloomContribution = BloomContribution,
        };
    }

    /// <summary>One log line describing the resolved grade (startup + tests).</summary>
    public static string Describe(HdrGrade grade, bool requested, bool hdrActive)
    {
        if (!Applies(requested, hdrActive))
            return requested
                ? "simPalette requested but INACTIVE (needs the float HDR scene path) - P984 look kept"
                : "simPalette off - P984 look kept";
        return $"simPalette ACTIVE curve=aces exposure={grade.Exposure:0.00} "
            + $"saturation={grade.Saturation:0.00} bloomThreshold={BloomThresholdLinear:0.0}(linear) "
            + $"bloomContribution={grade.BloomContribution:0.00}";
    }
}

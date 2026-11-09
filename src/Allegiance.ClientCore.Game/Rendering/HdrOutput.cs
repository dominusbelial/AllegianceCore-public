using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P984 — the output curve the single end-of-scene transform applies.</summary>
public enum HdrCurve
{
    /// <summary>The hue-preserving shoulder the PBR shader used to run itself
    /// (<see cref="MaterialTuning.ToneMap"/>), moved to the one transform.</summary>
    Neutral,

    /// <summary>An ACES filmic approximation, for comparison with the neutral
    /// curve (Domingo's FS2 Open "sim vibe" reference).</summary>
    Aces,
}

/// <summary>P984 — one step of the 3-D scene's ordered pass list. The list is
/// produced by <see cref="HdrOutput.Plan"/>; it exists so the order the brief
/// requires (bloom before the output transform, the HUD after it) is data a test
/// can pin instead of a comment.</summary>
public enum ScenePass
{
    /// <summary>Opaque sky/geometry/particles → the scene colour target.</summary>
    Scene,

    /// <summary>The linear-depth pass feeding soft particles (unchanged, RGBA8).</summary>
    Depth,

    /// <summary>Bloom's thresholded bright-pass into the half-res target.</summary>
    BloomBright,

    /// <summary>Bloom's separable Gaussian blur (horizontal + vertical).</summary>
    BloomBlur,

    /// <summary>Bloom's additive composite (scene + glow) into the post target.</summary>
    BloomComposite,

    /// <summary>FSR EASU + RCAS upscale to the output resolution.</summary>
    FsrUpscale,

    /// <summary>The SINGLE output transform: curve + display transfer.</summary>
    OutputTransform,

    /// <summary>HUD / radar / overlays — drawn after the transform, never graded.</summary>
    Hud,
}

/// <summary>
/// P984 — the linear HDR scene foundation (<c>visuals.space.hdrPipeline</c>).
/// <para><b>What the flag changes.</b> With the flag ON the 3-D scene renders
/// into a floating-point target (<see cref="SurfaceFormat.HalfVector4"/>, RGBA16F)
/// instead of an 8-bit one, the PBR/HD-material path writes LINEAR radiance
/// without its own tone curve, and exactly ONE output transform
/// (<c>Shaders/Tonemap.fx</c>) converts the scene to the display-referred image
/// the HUD is then drawn over. With the flag OFF every target, format and pass
/// is the native 8-bit one and the frame is byte-identical to before.</para>
/// <para><b>Storage domain.</b> The float target holds <i>display-encoded</i>
/// RGB with headroom: sRGB-encoded values that may exceed 1.0. That is deliberate
/// — every legacy writer that shares the target (BasicEffect sky/hull/asteroid
/// meshes, SpriteBatch particles, the volumetric pass) already emits
/// display-encoded values, and re-encoding the whole 3-D scene would relight
/// content this slice must leave alone ("same picture, HDR plumbing"). The PBR
/// path decodes its albedo/lighting into linear, lights and adds specular there,
/// and applies the sRGB transfer once on write, so its highlights reach the
/// target above 1.0 instead of being clamped inside the shader. The output
/// transform's neutral curve is then the identity (plus the same hue-preserving
/// shoulder the PBR shader used to run), and its ACES curve decodes the stored
/// value back to linear, applies the filmic fit and re-encodes — one transfer in,
/// one transfer out, in one place.</para>
/// <para><b>No auto-exposure, no HDR10 output</b> — out of scope for this slice,
/// and nothing here reads the GPU back synchronously.</para>
/// </summary>
public static class HdrOutput
{
    /// <summary>The exposure the single output transform applies. Unity: no
    /// auto-exposure in this slice (the palette task owns exposure tuning).</summary>
    public const float DefaultExposure = 1f;

    /// <summary>The neutral shoulder's onset/slope — the HD-6 PBR defaults
    /// (<see cref="MaterialTuning.TonemapThreshold"/> /
    /// <see cref="MaterialTuning.TonemapSlope"/>), so the central curve behaves
    /// exactly like the per-shader one it replaced.</summary>
    public const float NeutralThreshold = 0.8f;
    public const float NeutralSlope = 0.5f;

    /// <summary>The render-target format for the 3-D scene at the given setting.
    /// HalfVector4 is RGBA16F (MonoGame's DesktopGL/GLES float target); the
    /// native path keeps the 8-bit <see cref="SurfaceFormat.Color"/>.</summary>
    public static SurfaceFormat SceneFormat(bool hdr)
        => hdr ? SurfaceFormat.HalfVector4 : SurfaceFormat.Color;

    /// <summary>
    /// The ordered pass list for this frame. HDR adds the output transform and
    /// routes bloom/FSR through float targets; the HUD is always the last step and
    /// is never inside the transform.
    /// </summary>
    public static IReadOnlyList<ScenePass> Plan(bool hdr, bool bloom, bool fsr)
    {
        var passes = new List<ScenePass>(8) { ScenePass.Scene };
        if (bloom)
        {
            passes.Add(ScenePass.BloomBright);
            passes.Add(ScenePass.BloomBlur);
            passes.Add(ScenePass.BloomComposite);
        }
        if (fsr) passes.Add(ScenePass.FsrUpscale);
        if (hdr) passes.Add(ScenePass.OutputTransform);
        passes.Add(ScenePass.Hud);
        return passes;
    }

    /// <summary>Index of <paramref name="pass"/> in the plan, or -1.</summary>
    public static int IndexOf(IReadOnlyList<ScenePass> plan, ScenePass pass)
    {
        for (int i = 0; i < plan.Count; i++)
            if (plan[i] == pass) return i;
        return -1;
    }

    /// <summary>True when <paramref name="a"/> runs before <paramref name="b"/> in
    /// the plan (false when either is absent).</summary>
    public static bool RunsBefore(IReadOnlyList<ScenePass> plan, ScenePass a, ScenePass b)
    {
        int ia = IndexOf(plan, a), ib = IndexOf(plan, b);
        return ia >= 0 && ib >= 0 && ia < ib;
    }

    /// <summary>The exact sRGB transfer (decode) used by the ACES path.</summary>
    public static float Decode(float encoded)
    {
        if (!(encoded > 0f)) return 0f;                       // also catches NaN
        return encoded <= 0.04045f ? encoded / 12.92f
                                   : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>The exact sRGB transfer (encode).</summary>
    public static float Encode(float linear)
    {
        if (!(linear > 0f)) return 0f;
        if (linear >= 1f) return 1f;
        return linear <= 0.0031308f ? linear * 12.92f
                                    : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Component-wise sRGB decode (ACES input).</summary>
    public static Vector3 Decode(Vector3 encoded)
        => new(Decode(encoded.X), Decode(encoded.Y), Decode(encoded.Z));

    /// <summary>Component-wise sRGB encode (display output).</summary>
    public static Vector3 Encode(Vector3 linear)
        => new(Encode(linear.X), Encode(linear.Y), Encode(linear.Z));

    /// <summary>
    /// The neutral curve: the hue-preserving peak shoulder the PBR shader used to
    /// apply in gamma space, now applied ONCE to the whole scene, then clamped into
    /// the display range. Below the onset it is the identity, which is what keeps
    /// today's picture on the neutral path. Mirrors <c>PS_Neutral</c> in
    /// <c>Shaders/Tonemap.fx</c> (and <see cref="MaterialTuning.ToneMap"/>).
    /// </summary>
    public static Vector3 Neutral(Vector3 encoded, float exposure = DefaultExposure,
        float threshold = NeutralThreshold, float slope = NeutralSlope)
    {
        Vector3 c = encoded * exposure;
        float peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (peak > threshold)
        {
            float excess = MathF.Max(peak - threshold, 0f);
            float mapped = threshold + excess / (1f + excess / slope);
            c *= mapped / MathF.Max(peak, 1e-5f);
        }
        return new Vector3(Math.Clamp(c.X, 0f, 1f), Math.Clamp(c.Y, 0f, 1f), Math.Clamp(c.Z, 0f, 1f));
    }

    /// <summary>
    /// Narkowicz's ACES filmic approximation (a public, widely used fit — not
    /// FS2 Open code), applied in linear space. Mirrors <c>PS_Aces</c> in
    /// <c>Shaders/Tonemap.fx</c>.
    /// </summary>
    public static float Aces(float linear)
    {
        if (!(linear > 0f)) return 0f;
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        float mapped = (linear * (a * linear + b)) / (linear * (c * linear + d) + e);
        return Math.Clamp(mapped, 0f, 1f);
    }

    /// <summary>Component-wise ACES on linear RGB.</summary>
    public static Vector3 Aces(Vector3 linear)
        => new(Aces(linear.X), Aces(linear.Y), Aces(linear.Z));

    /// <summary>
    /// The display value the single output transform produces for a stored
    /// (display-encoded, may exceed 1) scene sample — the C# mirror the tests use
    /// to prove there is exactly one tone map on the HDR path.
    /// </summary>
    public static Vector3 Output(Vector3 stored, HdrCurve curve, float exposure = DefaultExposure)
        => curve == HdrCurve.Aces
            ? Encode(Aces(Decode(stored) * exposure))
            : Neutral(stored, exposure);

    /// <summary>One log line describing the resolved pipeline (startup + tests).</summary>
    public static string Describe(bool hdr, HdrCurve curve, bool bloom, bool fsr)
    {
        var plan = Plan(hdr, bloom, fsr);
        var names = new string[plan.Count];
        for (int i = 0; i < plan.Count; i++) names[i] = plan[i].ToString();
        return (hdr ? $"hdr=on curve={(curve == HdrCurve.Aces ? "aces" : "neutral")}" : "hdr=off")
            + $" passes={string.Join(">", names)}";
    }
}

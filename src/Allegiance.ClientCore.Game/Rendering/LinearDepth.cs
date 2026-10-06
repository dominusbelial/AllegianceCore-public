using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P547 — the linear-depth plumbing shared by the <c>LinearDepth.fx</c> depth
/// pass and the <c>SoftParticle.fx</c> consumer. Pure functions (unit-testable
/// headless) plus the named constants behind the soft-particle tuning.
/// </summary>
/// <remarks>
/// <para><b>Why RGBA8 / SurfaceFormat.Color.</b> The depth pass renders opaque
/// scene geometry's LINEAR view depth into its own RenderTarget2D (the
/// backbuffer depth buffer can't be sampled on OpenGL). Rather than a
/// <c>Single</c>/<c>HalfSingle</c> target — whose support is not guaranteed on
/// every GLES/Android driver — we use <c>SurfaceFormat.Color</c> (universally
/// supported) and pack a [0,1] normalized depth into 32 bits with the Valve
/// float-RGBA packing, which preserves float precision. The shaders'
/// <c>EncodeFloatRGBA</c>/<c>DecodeFloatRGBA</c> (LinearDepth.fx / SoftParticle.fx)
/// and this class are the same algorithm, so the depth round-trips precisely.</para>
/// <para><b>Depth normalization.</b> Both scene and particle depth are
/// normalized the same way, <c>(viewDist - Near) * (1/(Far-Near))</c>, so their
/// difference is meaningful and the softness constant is expressed in the same
/// normalized units on both the CPU and in the shaders.</para>
/// </summary>
public static class LinearDepth
{
    /// <summary>Near plane used for depth normalization. Matches the in-flight
    /// cockpit/missile cameras (near=1) and bounds every flight camera.</summary>
    public const float DefaultNear = 1f;

    /// <summary>Far plane used for depth normalization. Matches every camera's
    /// far plane (6000).</summary>
    public const float DefaultFar = 6000f;

    /// <summary>
    /// The soft-particle fade softness, in NORMALIZED linear-depth units.
    /// <c>1/Softness</c> is the slope of the saturate fade in SoftParticle.fx.
    /// Justification: with the default 1..6000 range, 0.024 normalized ≈ 144
    /// units of view depth — far too loose. 0.004 ≈ 24 units of view depth,
    /// which melts a smoke puff across a hull/rock surface (a fighter's
    /// half-extent is ~2-10 units, an asteroid several tens) with no visible
    /// hard cut, yet dissipates quickly enough that a puff no longer reads
    /// once the surface slides well past it. Tuned visually in the P547 proof.
    /// </summary>
    public const float SoftnessNormalized = 0.004f;

    /// <summary>
    /// The soft-particle fade factor (pure, unit-testable): how much a particle
    /// at <paramref name="particleNorm"/> survives given the scene depth at its
    /// pixel <paramref name="sceneNorm"/> (both normalized [0,1]). Saturate of
    /// <c>(scene − particle) / softness</c>: 0 when the scene is at/behind the
    /// particle's own depth, rising to 1 once the scene is softness units
    /// BEHIND the particle. The particle melts where geometry is nearer.
    /// </summary>
    public static float SoftFade(float sceneNorm, float particleNorm, float softnessNorm)
        => Math.Clamp((sceneNorm - particleNorm) / Math.Max(1e-6f, softnessNorm), 0f, 1f);

    /// <summary>Normalize a positive view distance to [0,1] with the given
    /// near/far range (clamped).</summary>
    public static float Normalize(float viewDist, float near, float far)
        => Math.Clamp((viewDist - near) * (1f / Math.Max(1e-6f, far - near)), 0f, 1f);

    /// <summary>Reciprocal of the depth range: <c>1/(Far-Near)</c> (the shader's
    /// <c>InvRange</c>).</summary>
    public static float InvRange(float near, float far) => 1f / Math.Max(1e-6f, far - near);

    /// <summary>The LinearDepth.fx parameters the depth pass sets from code (via
    /// the Asteroid/Station/Ship <c>DrawDepth</c> methods). Exposed for the
    /// P547 unit test (no reliance on GL parameter defaults).</summary>
    public static readonly string[] DepthPassParameters =
        { "WorldView", "WorldViewProj", "Near", "InvRange" };

    /// <summary>
    /// Encode a [0,1] float into RGBA8 using the Valve float-RGBA packing —
    /// exactly the <c>EncodeFloatRGBA</c> in LinearDepth.fx/SoftParticle.fx —
    /// so the CPU-side helper matches what the shaders write/read.
    /// </summary>
    public static void Encode(float v, out byte r, out byte g, out byte b, out byte a)
    {
        v = Math.Clamp(v, 0f, 1f);
        float c0 = v * 1f, c1 = v * 255f, c2 = v * 65025f, c3 = v * 16581375f;
        float f0 = c0 - (float)Math.Floor(c0);
        float f1 = c1 - (float)Math.Floor(c1);
        float f2 = c2 - (float)Math.Floor(c2);
        float f3 = c3 - (float)Math.Floor(c3);
        f0 -= f1 * (1f / 255f);
        f1 -= f2 * (1f / 255f);
        f2 -= f3 * (1f / 255f);
        r = (byte)Math.Clamp(Math.Round(f0 * 255f), 0, 255);
        g = (byte)Math.Clamp(Math.Round(f1 * 255f), 0, 255);
        b = (byte)Math.Clamp(Math.Round(f2 * 255f), 0, 255);
        a = (byte)Math.Clamp(Math.Round(f3 * 255f), 0, 255);
    }

    /// <summary>Encode a [0,1] float into an RGBA <see cref="Color"/>.</summary>
    public static Color Encode(float v)
    {
        Encode(v, out byte r, out byte g, out byte b, out byte a);
        return new Color(r, g, b, a);
    }

    /// <summary>Decode an RGBA8 (values 0..255) back to the packed [0,1] float —
    /// the inverse of <see cref="Encode(float, out byte, out byte, out byte, out byte)"/>.</summary>
    public static float Decode(byte r, byte g, byte b, byte a)
        => r / 255f + (g / 255f) * (1f / 255f) + (b / 255f) * (1f / 65025f) + (a / 255f) * (1f / 16581375f);

    /// <summary>Decode an RGBA <see cref="Color"/> back to the packed [0,1] float.</summary>
    public static float Decode(Color c) => Decode(c.R, c.G, c.B, c.A);
}

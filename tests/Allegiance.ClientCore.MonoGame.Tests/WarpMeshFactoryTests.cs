using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpMeshFactory"/> — no graphics device
/// involved (the same discipline as <see cref="RockMeshFactoryTests"/>). They
/// pin the invariants that make the procedural funnel safe to feed into a
/// VertexBuffer/IndexBuffer: a real vertex/triangle count, indices in range,
/// deterministic output, a true rim→tip taper along the forward axis, and the
/// cool dark-navy-rim → pale-cyan-white-hot-tip color gradient (Domingo's
/// correction: the aleph reads bluish, not the P227 tutorial's "brown").
/// </summary>
public class WarpMeshFactoryTests
{
    private const int RingCount = 8;
    private const int Segments = 12;

    [Fact]
    public void DefaultParams_ProduceARealMesh()
    {
        var mesh = WarpMeshFactory.Generate();
        Assert.True(mesh.VertexCount >= 40, $"expected a real mesh, got {mesh.VertexCount} vertices");
        Assert.True(mesh.TriangleCount >= 40, $"expected a real mesh, got {mesh.TriangleCount} triangles");
        Assert.Equal(mesh.VertexCount * 3, mesh.Positions.Length);
        Assert.Equal(mesh.VertexCount * 4, mesh.Colors.Length);
        Assert.Equal(0, mesh.Indices.Length % 3);
    }

    [Fact]
    public void AllIndices_AreWithinVertexRange()
    {
        var mesh = WarpMeshFactory.Generate();
        foreach (int idx in mesh.Indices)
            Assert.InRange(idx, 0, mesh.VertexCount - 1);
    }

    [Fact]
    public void SameParams_TwoCalls_ByteIdentical()
    {
        var a = WarpMeshFactory.Generate();
        var b = WarpMeshFactory.Generate();
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Colors, b.Colors);
        Assert.Equal(a.Indices, b.Indices);
    }

    [Fact]
    public void DifferentParams_DifferentShape()
    {
        var loose = WarpMeshFactory.Generate(spiralTightness: 0.1f);
        var tight = WarpMeshFactory.Generate(spiralTightness: 8.0f);
        Assert.NotEqual(loose.Positions, tight.Positions);
    }

    [Fact]
    public void Funnel_TapersFromRimToTipAlongForwardAxis()
    {
        var mesh = WarpMeshFactory.Generate(ringCount: RingCount, segments: Segments, rimRadius: 1f, depth: 2f);

        // First `Segments` vertices = rim ring (t=0), last `Segments` = tip (t≈1).
        float rimMax = MaxRadialDistance(mesh.Positions, 0, Segments);
        float tipMax = MaxRadialDistance(mesh.Positions, Segments * (RingCount - 1), Segments);
        Assert.True(tipMax < rimMax * 0.3f, $"tip ring must be much closer to the axis than the rim (rim {rimMax:F3}, tip {tipMax:F3})");
        Assert.InRange(rimMax, 0.99f, 1.0101f);   // rim radius ≈ mesh rimRadius

        // Tip extends further along +Z than the rim (the drain goes forward).
        float rimZ = mesh.Positions[2];
        float tipZ = mesh.Positions[(Segments * (RingCount - 1)) * 3 + 2];
        Assert.True(tipZ > rimZ, "tip must be ahead of the rim along the forward axis");
    }

    [Fact]
    public void ColorGradient_DarkRimToWhiteHotTip()
    {
        var mesh = WarpMeshFactory.Generate(ringCount: RingCount, segments: Segments);

        float rimLuma = AverageLuminance(mesh.Colors, 0, Segments);
        float tipLuma = AverageLuminance(mesh.Colors, Segments * (RingCount - 1), Segments);
        Assert.True(tipLuma > rimLuma, $"tip must be far brighter than the rim (rim {rimLuma:F3}, tip {tipLuma:F3})");

        // Absolute invariants of the look: the outer rim is genuinely DARK
        // (the "dark at the outer rim" pole of the gradient) and the tip is
        // genuinely BRIGHT ("bright spot in the center"). These absolute bars
        // discriminate a real gradient from any flat/one-sided approximation.
        Assert.True(rimLuma < 0.5f, $"outer rim must be dark, got luminance {rimLuma:F3}");
        Assert.True(tipLuma > 0.9f, $"center tip must be bright, got luminance {tipLuma:F3}");

        // Rim is a dark cool navy: distinctly blue-dominant, not the warm brown
        // the P227 tutorial lead suggested (Domingo corrected the hue live).
        float rimR = mesh.Colors[0];
        float rimB = mesh.Colors[2];
        Assert.True(rimB > rimR * 2f, $"rim must be blue-dominant navy (R {rimR:F3}, B {rimB:F3})");
        float tipB = mesh.Colors[(Segments * (RingCount - 1)) * 4 + 2];
        Assert.True(tipB > rimB, "tip must be whiter/bluer than the dark navy rim");

        // Opaque in the mesh — the translucency comes from WarpRenderer's
        // additive blend, not from a half-transparent mesh (additive needs the
        // full srcAlpha to add the vertex color at full strength).
        for (int i = 0; i < mesh.VertexCount; i++)
            Assert.Equal(1f, mesh.Colors[i * 4 + 3]);
    }

    /// <summary>
    /// P242 — pins the hue correction: the aleph is BLUISH, not the P227
    /// warm-brown. Per Domingo's direct correction (his memory is the source
    /// of truth; native's aleph texture is unrecoverable from this checkout),
    /// the rim is a dark navy, the mid band is a strong blue (blue dominates
    /// red — the exact opposite of the old amber, where red dominated blue),
    /// and the tip stays a bright white/pale-cyan.
    /// </summary>
    [Fact]
    public void ColorPalette_IsBluish_NotBrown()
    {
        var mesh = WarpMeshFactory.Generate(ringCount: RingCount, segments: Segments);

        // Mid band: blue must dominate red by a wide margin (would FAIL on the
        // old amber, where midR≈0.96 crushed midB≈0.07). Both are averaged over
        // the ring's segments so the helical spiral-arm brightness wedges (which
        // scale R,G,B together per vertex) don't distort the hue ratio.
        int mid = RingCount / 2;
        float midR = AvgChannel(mesh.Colors, mid * Segments, Segments, 0);
        float midB = AvgChannel(mesh.Colors, mid * Segments, Segments, 2);
        Assert.True(midB > 0.55f, $"mid must be a strong blue, got B={midB:F3}");
        Assert.True(midB > midR * 2f, $"mid must be blue-dominated not amber/brown (R {midR:F3}, B {midB:F3})");

        // Rim: dark (already asserted elsewhere) AND a real navy blue — blue well
        // above red, matching the dark-rim pole of the gradient.
        float rimR = AvgChannel(mesh.Colors, 0, Segments, 0);
        float rimB = AvgChannel(mesh.Colors, 0, Segments, 2);
        Assert.True(rimB > rimR * 2f, $"rim must be navy (R {rimR:F3}, B {rimB:F3})");

        // Center stays white-hot (bright spot in the center), and is the brightest
        // part of the funnel — the dark-rim/bright-center structure is retained.
        float tipR = AvgChannel(mesh.Colors, Segments * (RingCount - 1), Segments, 0);
        float tipG = AvgChannel(mesh.Colors, Segments * (RingCount - 1), Segments, 1);
        float tipB = AvgChannel(mesh.Colors, Segments * (RingCount - 1), Segments, 2);
        Assert.True(tipR > 0.8f && tipG > 0.8f && tipB > 0.8f, "tip must be near-white (bright center)");
    }

    [Fact]
    public void SpiralArms_ProduceAzimuthalColorVariation()
    {
        var mesh = WarpMeshFactory.Generate(ringCount: RingCount, segments: Segments);

        // The `SpiralArms` brighter wedges must make the mid ring's colour vary
        // noticeably around the circumference (so the mesh reads as a swirling
        // vortex, and the renderer's animated spin has visible structure to
        // turn) — not a uniform smooth cone.
        int mid = RingCount / 2;
        float min = float.MaxValue, max = float.MinValue;
        for (int j = 0; j < Segments; j++)
        {
            int o = (mid * Segments + j) * 4;
            float lum = 0.2126f * mesh.Colors[o] + 0.7152f * mesh.Colors[o + 1] + 0.0722f * mesh.Colors[o + 2];
            min = MathF.Min(min, lum);
            max = MathF.Max(max, lum);
        }
        Assert.True(max - min > 0.05f,
            $"mid ring must show spiral-arm brightness variation, got range {max - min:F3}");
    }

    private static float MaxRadialDistance(float[] pos, int startVertex, int count)
    {
        float max = 0f;
        for (int i = 0; i < count; i++)
        {
            int o = (startVertex + i) * 3;
            float r = MathF.Sqrt(pos[o] * pos[o] + pos[o + 1] * pos[o + 1]);
            max = MathF.Max(max, r);
        }
        return max;
    }

    private static float AverageLuminance(float[] col, int startVertex, int count)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            int o = (startVertex + i) * 4;
            sum += 0.2126f * col[o] + 0.7152f * col[o + 1] + 0.0722f * col[o + 2];
        }
        return sum / count;
    }

    private static float AvgChannel(float[] col, int startVertex, int count, int channel)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
            sum += col[(startVertex + i) * 4 + channel];
        return sum / count;
    }

    /// <summary>
    /// P237 — pins the VISUAL-ONLY scale calibration against the ACTUAL rendered
    /// ship model, not a data stat. Every warp in <c>m2xspiderw.igc</c> has real
    /// <c>Radius = 100</c> (measured live); the single rendered ship model
    /// (<c>bios_scout_upscaled.fbx</c>) has a world-unit bounding box of
    /// <c>~190 × 102 × 179</c> under the renderer's own transform (measured live),
    /// longest dimension ≈ 190.065. The real gameplay Radius is NOT scaled — only
    /// this rendered rim is.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>P247 correction (2026-09-17)</b>: P237's original figure here —
    /// "Domingo's accepted figure is that the warp should read at ~10x the size
    /// of a ship" — is superseded, not silently replaced. It was correct as a
    /// pure art-direction target in isolation, but P244 later decoupled the
    /// real transit <em>trigger</em> from this visual scale (tightened to the
    /// raw <c>warp.Radius</c>, matching native), leaving a ~9x gap between what
    /// looked crossable and what actually was. Live flight-testing confirmed
    /// this broke the feature in practice (Domingo flew to 436u, well inside
    /// the old 950u visual rim, without transiting — the real trigger only
    /// fires at ~108u). Asked directly, Domingo chose to shrink the funnel to
    /// roughly match the real crossable size over the alternative (keep the
    /// epic scale, add a distinct bright "core" marker at the true trigger
    /// point) — see <see cref="Sector.WarpVisualScale"/>'s own doc comment.
    /// 2.5x is the new, live-verified figure: a real flight-tested end-to-end
    /// transit (trigger fired, sector changed, P245 flash armed) at exactly
    /// this value.
    /// </remarks>
    [Fact]
    public void VisualScaleMultiplier_CalibratesWarpToAboutTwoAndHalfTimesRenderedShipSize()
    {
        const float warpRealRadius = 100f;            // m2xspiderw.igc every warp (measured live)
        const float renderedShipLongestDim = 190.065f; // bios_scout_upscaled.fbx bounds (measured live)

        // The funnel mesh's unit rim radius is scaled by Radius×multiplier, so the
        // rim DIAMETER (the warp's perceived "size") is 2 × Radius × multiplier.
        float warpVisualDiameter = 2f * warpRealRadius * WarpRenderer.VisualScaleMultiplier;
        float ratio = warpVisualDiameter / renderedShipLongestDim;

        Assert.InRange(ratio, 2.5f, 3.0f);
    }
}

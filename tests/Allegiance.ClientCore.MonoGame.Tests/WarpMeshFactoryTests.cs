using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpMeshFactory"/> — no graphics device
/// involved (the same discipline as <see cref="RockMeshFactoryTests"/>). They
/// pin the invariants that make the procedural hourglass safe to feed into a
/// VertexBuffer/IndexBuffer: a real vertex/triangle count, indices in range,
/// deterministic output, a true symmetric-rim→center shape along the forward
/// axis, and the cool dark-navy-rim → blue-band → white-hot-center color
/// gradient (P258 reshaped the old one-directional cone into a symmetric
/// bicone: wide rims at BOTH ends, bright spot at the center pinch; the P242
/// correction that the aleph reads bluish is kept).
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
    public void Hourglass_BothEndsWideCenterPinched_AlongForwardAxis()
    {
        var mesh = WarpMeshFactory.Generate(ringCount: RingCount, segments: Segments, rimRadius: 1f, depth: 2f);

        // First `Segments` vertices = ring 0 (t=0), last `Segments` = ring 7 (t=1).
        // P258 symmetric hourglass: BOTH end rings are the WIDE rims (radius ≈
        // rimRadius), and the middle (nearest the center pinch) is far narrower.
        float rim0Max = MaxRadialDistance(mesh.Positions, 0, Segments);
        float rim1Max = MaxRadialDistance(mesh.Positions, Segments * (RingCount - 1), Segments);
        Assert.InRange(rim0Max, 0.99f, 1.0101f);   // end 0 is a wide rim (radius ≈ rimRadius)
        Assert.InRange(rim1Max, 0.99f, 1.0101f);   // end 1 is an EQUALLY wide rim — this is what
        Assert.True(MathF.Abs(rim0Max - rim1Max) < 1e-3f, "both rims must be equally wide");   // the old cone failed

        // The middle ring (ring 4, nearest t=0.5) must be far closer to the axis — the pinch.
        int center = RingCount / 2;
        float centerMax = MaxRadialDistance(mesh.Positions, center * Segments, Segments);
        Assert.True(centerMax < rim0Max * 0.3f,
            $"center ring must be far narrower than either rim (rim {rim0Max:F3}, center {centerMax:F3})");

        // Symmetry about the center: the two rims sit at ±depth/2 flanking the origin,
        // and the middle ring hugs z=0.
        float z0 = mesh.Positions[2];
        float z1 = mesh.Positions[(Segments * (RingCount - 1)) * 3 + 2];
        Assert.True(z0 < 0f && z1 > 0f, $"rims must flank the origin (z0 {z0:F3}, z1 {z1:F3})");
        Assert.True(MathF.Abs(z0) - MathF.Abs(z1) is < 1e-3f and > -1e-3f, "rims must be equidistant from center");
        float zMid = mesh.Positions[(center * Segments) * 3 + 2];
        Assert.True(MathF.Abs(zMid) < MathF.Abs(z0) * 0.3f, $"middle must sit near z=0 (got {zMid:F3})");

        // No ring may exceed rimRadius — the wide rim IS the widest part. This is what
        // keeps the P247 visual-scale calibration intact (rim diameter = 2·Radius·scale).
        float globalMax = 0f;
        for (int v = 0; v < mesh.VertexCount; v++)
            globalMax = MathF.Max(globalMax, MathF.Sqrt(mesh.Positions[v * 3] * mesh.Positions[v * 3] + mesh.Positions[v * 3 + 1] * mesh.Positions[v * 3 + 1]));
        Assert.InRange(globalMax, 0.990f, 1.0101f);
    }

    [Fact]
    public void ColorGradient_DarkBothRimsBrightCenter()
    {
        var mesh = WarpMeshFactory.Generate(ringCount: RingCount, segments: Segments);

        float rim0Luma = AverageLuminance(mesh.Colors, 0, Segments);
        float rim1Luma = AverageLuminance(mesh.Colors, Segments * (RingCount - 1), Segments);
        int center = RingCount / 2;
        float centerLuma = AverageLuminance(mesh.Colors, center * Segments, Segments);

        // BOTH wide rims are genuinely DARK (the "dark at the rim" pole of the
        // gradient, now at both ends of the symmetric hourglass) — the old cone
        // had a bright tip here, so this is a discriminating new invariant.
        Assert.True(rim0Luma < 0.2f, $"rim 0 must be dark, got {rim0Luma:F3}");
        Assert.True(rim1Luma < 0.2f, $"rim 1 must be dark, got {rim1Luma:F3}");
        Assert.True(MathF.Abs(rim0Luma - rim1Luma) < 1e-3f, "both rims must be equally dark");

        // The center is the bright spot in the middle — far brighter than either rim.
        Assert.True(centerLuma > rim0Luma * 4f && centerLuma > 0.6f,
            $"center must be the bright spot (rim0 {rim0Luma:F3}, center {centerLuma:F3})");

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
    /// the rims are dark navy, the band just outside the center is a strong
    /// blue (blue dominates red — the exact opposite of the old amber, where
    /// red dominated blue), and the center pinch is a bright white/pale-cyan.
    /// P258 keeps the hue but mirrors the layout to the symmetric hourglass,
    /// so the strong-blue band is now a symmetric SHELL around the center
    /// (rings 2 and 5, where u≈MidT) rather than a one-sided mid band.
    /// </summary>
    [Fact]
    public void ColorPalette_IsBluish_NotBrown()
    {
        var mesh = WarpMeshFactory.Generate(ringCount: RingCount, segments: Segments);

        // Blue band: the symmetric shell where u≈MidT — rings 2 and 5 straddle the
        // center pinch on opposite sides (u≈0.57), the two "arms" of the hourglass's
        // blue band. Averaged over segments so the helical spiral-arm brightness
        // wedges (which scale R,G,B together per vertex) don't distort the hue ratio.
        // Blue must dominate red by a wide margin here (would FAIL on the old amber,
        // where midR≈0.96 crushed midB≈0.07).
        int lo = RingCount / 4;                                   // ring 2
        int hi = RingCount - 1 - RingCount / 4;                   // ring 5
        float bandR = (AvgChannel(mesh.Colors, lo * Segments, Segments, 0) + AvgChannel(mesh.Colors, hi * Segments, Segments, 0)) / 2f;
        float bandB = (AvgChannel(mesh.Colors, lo * Segments, Segments, 2) + AvgChannel(mesh.Colors, hi * Segments, Segments, 2)) / 2f;
        Assert.True(bandB > 0.55f, $"blue band must be a strong blue, got B={bandB:F3}");
        Assert.True(bandB > bandR * 2f, $"blue band must be blue-dominated not amber/brown (R {bandR:F3}, B {bandB:F3})");

        // Both rims: dark (already asserted in ColorGradient_DarkBothRimsBrightCenter)
        // AND a real navy blue — blue well above red, the dark-rim pole of the gradient
        // now sitting at BOTH ends of the symmetric hourglass.
        float rimR0 = AvgChannel(mesh.Colors, 0, Segments, 0);
        float rimB0 = AvgChannel(mesh.Colors, 0, Segments, 2);
        float rimR1 = AvgChannel(mesh.Colors, Segments * (RingCount - 1), Segments, 0);
        float rimB1 = AvgChannel(mesh.Colors, Segments * (RingCount - 1), Segments, 2);
        Assert.True(rimB0 > rimR0 * 2f && rimB1 > rimR1 * 2f,
            $"both rims must be navy (rim0 R {rimR0:F3}/B {rimB0:F3}, rim1 R {rimR1:F3}/B {rimB1:F3})");

        // Center stays white-hot (the bright spot in the center) — the brightest
        // part of the hourglass; the dark-rim/bright-center structure is retained.
        int center = RingCount / 2;
        float centerR = AvgChannel(mesh.Colors, center * Segments, Segments, 0);
        float centerG = AvgChannel(mesh.Colors, center * Segments, Segments, 1);
        float centerB = AvgChannel(mesh.Colors, center * Segments, Segments, 2);
        Assert.True(centerR > 0.6f && centerG > 0.7f && centerB > 0.85f,
            $"center must be bright near-white (R {centerR:F3}, G {centerG:F3}, B {centerB:F3})");
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

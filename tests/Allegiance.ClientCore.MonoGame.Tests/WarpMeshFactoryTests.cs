using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpMeshFactory"/> — no graphics device
/// involved (the same discipline as <see cref="RockMeshFactoryTests"/>). They
/// pin the invariants that make the procedural funnel safe to feed into a
/// VertexBuffer/IndexBuffer: a real vertex/triangle count, indices in range,
/// deterministic output, a true rim→tip taper along the forward axis, and the
/// warm dark-rim → white-hot-tip color gradient (the "swirling brown vortex
/// with a bright spot in the center" look, training/mission4.cpp:494).
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

        // Rim is a dark warm brown: little blue channel vs the (warmer) tip.
        float rimB = mesh.Colors[2];
        float tipB = mesh.Colors[(Segments * (RingCount - 1)) * 4 + 2];
        Assert.True(tipB > rimB, "tip must be bluer/whiter than the dark brown rim");

        // Opaque throughout.
        for (int i = 0; i < mesh.VertexCount; i++)
            Assert.Equal(1f, mesh.Colors[i * 4 + 3]);
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

    /// <summary>
    /// P237 — pins the VISUAL-ONLY scale calibration against the ACTUAL rendered
    /// ship model, not a data stat. Every warp in <c>m2xspiderw.igc</c> has real
    /// <c>Radius = 100</c> (measured live); the single rendered ship model
    /// (<c>bios_scout_upscaled.fbx</c>) has a world-unit bounding box of
    /// <c>~190 × 102 × 179</c> under the renderer's own transform (measured live),
    /// longest dimension ≈ 190.065. Domingo's accepted figure is that the warp
    /// should read at ~10x the size of a ship, so
    /// <see cref="WarpRenderer.VisualScaleMultiplier"/> (9.5) is chosen so the rim
    /// <b>diameter</b> — <c>2 × Radius × multiplier = 2×100×9.5 = 1900</c> — is
    /// ~10 × 190.065. The real gameplay Radius is NOT scaled — only this rendered
    /// rim is.
    /// </summary>
    [Fact]
    public void VisualScaleMultiplier_CalibratesWarpToAboutTenTimesRenderedShipSize()
    {
        const float warpRealRadius = 100f;            // m2xspiderw.igc every warp (measured live)
        const float renderedShipLongestDim = 190.065f; // bios_scout_upscaled.fbx bounds (measured live)

        // The funnel mesh's unit rim radius is scaled by Radius×multiplier, so the
        // rim DIAMETER (the warp's perceived "size") is 2 × Radius × multiplier.
        float warpVisualDiameter = 2f * warpRealRadius * WarpRenderer.VisualScaleMultiplier;
        float ratio = warpVisualDiameter / renderedShipLongestDim;

        Assert.InRange(ratio, 9.5f, 10.5f);
    }
}

using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpCoreMeshFactory"/> — the P341
/// camera-facing radial-gradient glow disc that anchors the aleph (warp gate).
/// No graphics device involved (same discipline as
/// <see cref="WarpMeshFactoryTests"/>). They pin the invariants that make the
/// disc safe to feed into a VertexBuffer/IndexBuffer AND that make it read as a
/// glow rather than a flat coin: a real vertex/triangle count, indices in range,
/// deterministic output, a FLAT XY-plane unit disc whose rim reaches exactly
/// <c>radius</c>, and a radial gradient that is bright+opaque at the center and
/// transparent-navy at the rim (so the renderer's additive blend makes the disc
/// fade into the background instead of painting a hard-edged circle).
/// </summary>
public class WarpCoreMeshFactoryTests
{
    private const int Rings = 10;
    private const int Segments = 16;

    [Fact]
    public void DefaultParams_ProduceARealMesh()
    {
        var mesh = WarpCoreMeshFactory.Generate();
        Assert.True(mesh.VertexCount >= 32, $"expected a real mesh, got {mesh.VertexCount} vertices");
        Assert.True(mesh.TriangleCount >= 32, $"expected a real mesh, got {mesh.TriangleCount} triangles");
        Assert.Equal(mesh.VertexCount * 3, mesh.Positions.Length);
        Assert.Equal(mesh.VertexCount * 4, mesh.Colors.Length);
        Assert.Equal(0, mesh.Indices.Length % 3);
    }

    [Fact]
    public void AllIndices_AreWithinVertexRange()
    {
        var mesh = WarpCoreMeshFactory.Generate();
        foreach (int idx in mesh.Indices)
            Assert.InRange(idx, 0, mesh.VertexCount - 1);
    }

    [Fact]
    public void SameParams_TwoCalls_ByteIdentical()
    {
        var a = WarpCoreMeshFactory.Generate();
        var b = WarpCoreMeshFactory.Generate();
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Colors, b.Colors);
        Assert.Equal(a.Indices, b.Indices);
    }

    [Fact]
    public void Disc_IsFlat_InLocalXY_WithRimAtRadius()
    {
        var mesh = WarpCoreMeshFactory.Generate(rings: Rings, segments: Segments, radius: 1f);

        // Every vertex lies in the local XY plane (normal = +Z) — a flat disc the
        // renderer billboards to face the camera.
        for (int v = 0; v < mesh.VertexCount; v++)
            Assert.Equal(0f, mesh.Positions[v * 3 + 2]);

        // Ring 0 is the degenerate center point (all vertices at the origin).
        float centerMax = MaxRadialDistance(mesh.Positions, 0, Segments);
        Assert.True(centerMax < 1e-4f, $"center ring must hug the axis, got r={centerMax:F5}");

        // The outermost ring reaches exactly the requested radius.
        float rimMax = MaxRadialDistance(mesh.Positions, Segments * (Rings - 1), Segments);
        Assert.InRange(rimMax, 0.999f, 1.001f);

        // No vertex may exceed the requested radius (the rim IS the widest part).
        float globalMax = 0f;
        for (int v = 0; v < mesh.VertexCount; v++)
            globalMax = MathF.Max(globalMax,
                MathF.Sqrt(mesh.Positions[v * 3] * mesh.Positions[v * 3] + mesh.Positions[v * 3 + 1] * mesh.Positions[v * 3 + 1]));
        Assert.InRange(globalMax, 0.998f, 1.002f);

        // An intermediate ring (halfway to the rim) is between the two extremes.
        int mid = Rings / 2;
        float midMax = MaxRadialDistance(mesh.Positions, mid * Segments, Segments);
        Assert.True(midMax > centerMax + 0.1f && midMax < rimMax - 0.1f,
            $"mid ring must sit between center and rim (got r={midMax:F3})");
    }

    [Fact]
    public void RadialGradient_WhiteOpaqueCenter_TransparentNavyRim()
    {
        var mesh = WarpCoreMeshFactory.Generate(rings: Rings, segments: Segments);

        // Center ring: white-cyan AND fully opaque (alpha ≈ 1) — the additive
        // blend needs full srcAlpha at the core so the warp's bright center burns.
        float centerAlpha = AverageChannel(mesh.Colors, 0, Segments, 3);
        float centerLuma = AverageLuminance(mesh.Colors, 0, Segments);
        Assert.InRange(centerAlpha, 0.995f, 1.0f);
        Assert.True(centerLuma > 0.9f, $"center must be near-white, got luma {centerLuma:F3}");

        // Rim ring: transparent (alpha ≈ 0) so the disc fades into the background
        // under additive blend — no hard-edged coin.
        float rimAlpha = AverageChannel(mesh.Colors, Segments * (Rings - 1), Segments, 3);
        Assert.True(rimAlpha < 0.01f, $"rim must be transparent, got alpha {rimAlpha:F4}");

        // The rim keeps the warp's blue-family palette: blue dominant over red.
        float rimR = AverageChannel(mesh.Colors, Segments * (Rings - 1), Segments, 0);
        float rimB = AverageChannel(mesh.Colors, Segments * (Rings - 1), Segments, 2);
        Assert.True(rimB > rimR * 1.5f, $"rim must be navy/blue (R {rimR:F3}, B {rimB:F3})");

        // Opacity must fall off monotonically from center (ring 0) to rim
        // (ring N-1) — a radial glow, not arbitrary flicker.
        float prev = float.MaxValue;
        for (int ring = 0; ring < Rings; ring++)
        {
            float a = AverageChannel(mesh.Colors, ring * Segments, Segments, 3);
            Assert.True(a <= prev + 1e-3f, $"alpha must be non-increasing outward (ring {ring}: {a:F4} > {prev:F4})");
            prev = a;
        }
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

    private static float AverageChannel(float[] col, int startVertex, int count, int channel)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
            sum += col[(startVertex + i) * 4 + channel];
        return sum / count;
    }
}

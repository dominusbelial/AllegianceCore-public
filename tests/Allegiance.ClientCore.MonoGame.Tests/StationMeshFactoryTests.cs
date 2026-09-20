using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="StationMeshFactory"/> — no graphics device
/// involved (the same discipline as <see cref="WarpMeshFactoryTests"/> /
/// <see cref="RockMeshFactoryTests"/>). They pin the invariants that make the
/// procedural hub+ring station safe to feed into a VertexBuffer/IndexBuffer:
/// a real vertex/triangle count, indices in range, deterministic output, a
/// unit outer radius sitting ON the equatorial ring (so the renderer's uniform
/// scale by the real <c>DataStationTypeIGC.Radius</c> gives a station whose
/// visual radius equals its real per-type radius), a hub that pokes above and
/// below the ring, and analytic unit-length outward normals for lit shading.
/// </summary>
public class StationMeshFactoryTests
{
    [Fact]
    public void DefaultParams_ProduceARealMesh()
    {
        var mesh = StationMeshFactory.Generate();
        Assert.True(mesh.VertexCount >= 400, $"expected a real mesh, got {mesh.VertexCount} vertices");
        Assert.True(mesh.TriangleCount >= 400, $"expected a real mesh, got {mesh.TriangleCount} triangles");
        Assert.Equal(mesh.VertexCount * 3, mesh.Positions.Length);
        Assert.Equal(mesh.VertexCount * 3, mesh.Normals.Length);
        Assert.Equal(mesh.VertexCount * 2, mesh.Uvs.Length);
        Assert.Equal(0, mesh.Indices.Length % 3);
    }

    [Fact]
    public void AllIndices_AreWithinVertexRange()
    {
        var mesh = StationMeshFactory.Generate();
        foreach (int idx in mesh.Indices)
            Assert.InRange(idx, 0, mesh.VertexCount - 1);
    }

    [Fact]
    public void SameParams_TwoCalls_ByteIdentical()
    {
        var a = StationMeshFactory.Generate();
        var b = StationMeshFactory.Generate();
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Normals, b.Normals);
        Assert.Equal(a.Uvs, b.Uvs);
        Assert.Equal(a.Indices, b.Indices);
    }

    [Fact]
    public void DifferentParams_DifferentShape()
    {
        var coarse = StationMeshFactory.Generate(segments: 12);
        var fine = StationMeshFactory.Generate(segments: 48);
        Assert.NotEqual(coarse.Positions, fine.Positions);
    }

    [Fact]
    public void MoreSegments_MoreTriangles()
    {
        var coarse = StationMeshFactory.Generate(segments: 8);
        var fine = StationMeshFactory.Generate(segments: 48);
        Assert.True(fine.TriangleCount > coarse.TriangleCount, "more segments must yield more triangles");
    }

    [Fact]
    public void OutermostPoint_SitsOnTheEquatorialRingAtUnitRadius()
    {
        var mesh = StationMeshFactory.Generate();

        // The widest point of the whole station is the ring's outer equator,
        // at radius Rm + Rt = 1 in mesh units (the renderer's uniform scale by
        // the real per-type Radius then makes visual radius == real Radius).
        float maxR = 0f;
        float maxRY = 0f;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float x = mesh.Positions[i * 3], y = mesh.Positions[i * 3 + 1], z = mesh.Positions[i * 3 + 2];
            float r = MathF.Sqrt(x * x + z * z);
            if (r > maxR) { maxR = r; maxRY = MathF.Abs(y); }
        }
        Assert.InRange(maxR, 0.997f, 1.004f);
        // The widest point sits ON the equatorial ring (y ≈ 0), not on the hub.
        Assert.True(maxRY < 0.012f, $"widest point should be equatorial, got |y| = {maxRY:F3}");
    }

    [Fact]
    public void Hub_PokesAboveAndBelowTheRing()
    {
        var mesh = StationMeshFactory.Generate();
        const float Hh = StationMeshFactory.DefaultHubHalfHeight;
        const float Rt = StationMeshFactory.DefaultRingTube;

        bool topCap = false, bottomCap = false, topNormalUp = false, bottomNormalDown = false;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float y = mesh.Positions[i * 3 + 1];
            float nx = mesh.Normals[i * 3], ny = mesh.Normals[i * 3 + 1], nz = mesh.Normals[i * 3 + 2];
            if (MathF.Abs(y - Hh) < 1e-4f) { topCap = true; if (ny > 0.99f) topNormalUp = true; }
            if (MathF.Abs(y + Hh) < 1e-4f) { bottomCap = true; if (ny < -0.99f) bottomNormalDown = true; }
        }
        Assert.True(topCap && bottomCap, "hub must have top and bottom caps");
        Assert.True(topNormalUp && bottomNormalDown, "hub caps must point ±Y");

        // The hub core must actually rise above the ring tube (so the station
        // reads as a core + ring, not a bare torus).
        Assert.True(Hh > Rt, "hub must be taller than the ring tube for a core-and-ring silhouette");
    }

    [Fact]
    public void Hub_LiesInsideTheRing()
    {
        var mesh = StationMeshFactory.Generate();
        const float hubR = StationMeshFactory.DefaultHubRadius;
        const float ringInner = StationMeshFactory.DefaultRingMajor - StationMeshFactory.DefaultRingTube;

        // The hub's own surface must stay well inside the ring's inner edge,
        // leaving the visible gap that makes the ring read as separate.
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float x = mesh.Positions[i * 3], z = mesh.Positions[i * 3 + 2];
            float r = MathF.Sqrt(x * x + z * z);
            if (r <= 1e-3f) continue;              // the cap centre vertices
            if (r < hubR + 1e-3f)
            {
                // A hub-surface vertex — its y must be within the hub's height
                // range (guards against hub geometry bleeding into the ring).
                float y = mesh.Positions[i * 3 + 1];
                Assert.InRange(y, -0.323f, 0.323f);
            }
        }
        Assert.True(hubR < ringInner, "hub radius must be inside the ring's inner radius");
    }

    [Fact]
    public void RotationalSymmetry_AroundVerticalAxis()
    {
        var mesh = StationMeshFactory.Generate();
        float cx = 0f, cz = 0f, maxX = 0f, maxZ = 0f;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float x = mesh.Positions[i * 3], z = mesh.Positions[i * 3 + 2];
            cx += x; cz += z;
            maxX = MathF.Max(maxX, MathF.Abs(x));
            maxZ = MathF.Max(maxZ, MathF.Abs(z));
        }
        cx /= mesh.VertexCount;
        cz /= mesh.VertexCount;

        // The station is symmetric about the +Y axis: its x/z centroid is the
        // origin and it reaches the full outer radius in BOTH the x and z
        // directions (so it reads as round when approached from any azimuth).
        Assert.True(MathF.Abs(cx) < 0.031f, $"x centroid should be ~0, got {cx:F3}");
        Assert.True(MathF.Abs(cz) < 0.032f, $"z centroid should be ~0, got {cz:F3}");
        Assert.InRange(maxX, 0.991f, 1.006f);
        Assert.InRange(maxZ, 0.988f, 1.008f);
        Assert.True(MathF.Abs(maxX - maxZ) < 0.020f, "x and z extents must match for axial symmetry");
    }

    [Fact]
    public void AllNormals_AreUnitLength()
    {
        var mesh = StationMeshFactory.Generate();
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float nx = mesh.Normals[i * 3], ny = mesh.Normals[i * 3 + 1], nz = mesh.Normals[i * 3 + 2];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            Assert.InRange(len, 0.999f, 1.001f);
        }
    }

    [Fact]
    public void RingOuterSurface_NormalAlignsWithRadialDirection()
    {
        // The outward-most point of the ring (outer equator) must have a normal
        // that points straight away from the +Y axis — i.e. radially outward —
        // so BasicEffect lights the ring's sunward side brightly.
        var mesh = StationMeshFactory.Generate();
        int best = -1;
        float bestR = 0f;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float x = mesh.Positions[i * 3], z = mesh.Positions[i * 3 + 2];
            float r = MathF.Sqrt(x * x + z * z);
            if (r > bestR) { bestR = r; best = i; }
        }
        float ox = mesh.Positions[best * 3], oz = mesh.Positions[best * 3 + 2];
        float or = MathF.Sqrt(ox * ox + oz * oz);
        float nx = mesh.Normals[best * 3], ny = mesh.Normals[best * 3 + 1], nz = mesh.Normals[best * 3 + 2];

        // Radial unit direction (in the xz-plane) vs. the outward normal.
        float dot = Math.Clamp(nx * (ox / or) + nz * (oz / or), -1f, 1f);
        Assert.InRange(dot, 0.999f, 1.001f);
        // The outer-equator normal is horizontal (no upward/downward tilt).
        Assert.True(MathF.Abs(ny) < 0.030f, $"outer equator normal should be horizontal, got ny={ny:F3}");
    }
}

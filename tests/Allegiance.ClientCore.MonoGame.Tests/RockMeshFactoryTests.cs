using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="RockMeshFactory"/> — no graphics device
/// involved. They pin the invariants that make the procedural rock mesh safe to
/// feed into a VertexBuffer/IndexBuffer: a real (non-degenerate) vertex/triangle
/// count, every index in range, deterministic output (same params → byte-identical
/// across calls), and a physically-sensible displaced-shell radius.
/// </summary>
public class RockMeshFactoryTests
{
    [Fact]
    public void DefaultParams_ProduceARealMesh()
    {
        var mesh = RockMeshFactory.Generate();
        Assert.True(mesh.VertexCount >= 100, $"expected a real mesh, got {mesh.VertexCount} vertices");
        Assert.True(mesh.TriangleCount >= 100, $"expected a real mesh, got {mesh.TriangleCount} triangles");
        Assert.Equal(mesh.VertexCount * 3, mesh.Positions.Length);
        Assert.Equal(mesh.VertexCount * 3, mesh.Normals.Length);
        Assert.Equal(0, mesh.Indices.Length % 3);
    }

    [Fact]
    public void AllIndices_AreWithinVertexRange()
    {
        var mesh = RockMeshFactory.Generate();
        foreach (int idx in mesh.Indices)
            Assert.InRange(idx, 0, mesh.VertexCount - 1);
    }

    [Fact]
    public void SameSeed_TwoCalls_ByteIdentical()
    {
        var a = RockMeshFactory.Generate();
        var b = RockMeshFactory.Generate();
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Normals, b.Normals);
        Assert.Equal(a.Indices, b.Indices);
    }

    [Fact]
    public void DifferentSeed_DifferentShape()
    {
        var a = RockMeshFactory.Generate(seed: 1);
        var b = RockMeshFactory.Generate(seed: 999983);
        Assert.NotEqual(a.Positions, b.Positions);
    }

    [Fact]
    public void MoreSubdivisions_MoreTriangles()
    {
        var coarse = RockMeshFactory.Generate(subdivisions: 1);
        var fine = RockMeshFactory.Generate(subdivisions: 3);
        Assert.True(fine.TriangleCount > coarse.TriangleCount, "more subdivisions must yield more triangles");
    }

    [Fact]
    public void ProtrudingVertexNormal_IsNotItsOwnRadialDirection()
    {
        // The outermost vertex is a prominent protrusion of the bumped surface.
        // Smooth face-normal averaging over the actual displaced triangles must
        // tilt its lighting normal away from the plain pre-displacement sphere
        // direction. The pre-P251 code assigned every normal to EXACTLY that
        // radial direction (0° here) — so this test reds when reverted.
        var mesh = RockMeshFactory.Generate();

        int maxIdx = 0;
        float maxR = 0f;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float x = mesh.Positions[i * 3], y = mesh.Positions[i * 3 + 1], z = mesh.Positions[i * 3 + 2];
            float r = MathF.Sqrt(x * x + y * y + z * z);
            if (r > maxR) { maxR = r; maxIdx = i; }
        }

        float nx = mesh.Normals[maxIdx * 3], ny = mesh.Normals[maxIdx * 3 + 1], nz = mesh.Normals[maxIdx * 3 + 2];
        float rx = mesh.Positions[maxIdx * 3] / maxR, ry = mesh.Positions[maxIdx * 3 + 1] / maxR, rz = mesh.Positions[maxIdx * 3 + 2] / maxR;
        float dot = Math.Clamp(nx * rx + ny * ry + nz * rz, -1f, 1f);
        float angleDeg = MathF.Acos(dot) * 180f / MathF.PI;

        // Measured ~17° at default params; a smooth-sphere normal would be 0°.
        // Threshold of 5° gives a wide margin on both sides.
        Assert.True(angleDeg > 5f,
            $"protrusion normal should deviate from its radial direction, got {angleDeg:F1}°");
    }

    [Fact]
    public void AllNormals_AreUnitLengthAndPointOutward()
    {
        // Sanity for the shaded surface: every normal is a unit vector and faces
        // away from the rock centre (never lighting the back of the rock).
        var mesh = RockMeshFactory.Generate();
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float nx = mesh.Normals[i * 3], ny = mesh.Normals[i * 3 + 1], nz = mesh.Normals[i * 3 + 2];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            Assert.InRange(len, 0.998f, 1.002f);

            float x = mesh.Positions[i * 3], y = mesh.Positions[i * 3 + 1], z = mesh.Positions[i * 3 + 2];
            float r = MathF.Sqrt(x * x + y * y + z * z);
            float outwardDot = nx * (x / r) + ny * (y / r) + nz * (z / r);
            Assert.True(outwardDot > 0f, $"vertex {i} normal points inward");
        }
    }

    [Fact]
    public void AllVertices_LieWithinDisplacedShell()
    {
        var mesh = RockMeshFactory.Generate(displacement: 0.22f);
        float maxR = 0f;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float x = mesh.Positions[i * 3], y = mesh.Positions[i * 3 + 1], z = mesh.Positions[i * 3 + 2];
            maxR = MathF.Max(maxR, MathF.Sqrt(x * x + y * y + z * z));
        }
        // displacement 0.22 → radii in [1 − 0.22, 1 + 0.22] = [0.78, 1.22].
        Assert.InRange(maxR, 1f, 1.22f);
    }

    [Fact]
    public void Uvs_ArePresent_WithinUnitSquare()
    {
        // P253 — one u,v per vertex, each in the [0, 1] texture square.
        var mesh = RockMeshFactory.Generate();
        Assert.Equal(mesh.VertexCount * 2, mesh.Uvs.Length);
        for (int i = 0; i < mesh.Uvs.Length; i++)
            Assert.InRange(mesh.Uvs[i], 0f, 1f);
    }

    [Fact]
    public void Uvs_AreDeterministic()
    {
        // Uvs must ride the same byte-identical determinism contract as the
        // rest of the mesh (same seed → identical Uvs; different seed → the
        // differently-shaped rock carries different surface directions, so its
        // UVs differ too).
        var a = RockMeshFactory.Generate();
        var b = RockMeshFactory.Generate();
        Assert.Equal(a.Uvs, b.Uvs);

        var c = RockMeshFactory.Generate(seed: 424242);
        Assert.NotEqual(a.Uvs, c.Uvs);
    }

    [Fact]
    public void Uvs_WindFullyAroundLongitudeAndLatitude()
    {
        // Discriminates the real equirectangular mapping from the pre-P253
        // all-zero UVs. Longitude must wrap the full rock (u stretches almost
        // the whole [0, 1] range), latitude must run pole-to-pole (v likewise),
        // and the UVs must not collapse to a single point (the old bug).
        var mesh = RockMeshFactory.Generate();
        float umin = float.MaxValue, umax = float.MinValue;
        float vmin = float.MaxValue, vmax = float.MinValue;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            umin = MathF.Min(umin, mesh.Uvs[i * 2]);
            umax = MathF.Max(umax, mesh.Uvs[i * 2]);
            vmin = MathF.Min(vmin, mesh.Uvs[i * 2 + 1]);
            vmax = MathF.Max(vmax, mesh.Uvs[i * 2 + 1]);
        }

        Assert.True(umax - umin > 0.9f, $"longitude should wind around the rock, u range {umax - umin:F3}");
        Assert.True(vmax - vmin > 0.9f, $"latitude should run pole to pole, v range {vmax - vmin:F3}");
        Assert.True(umax - umin > 0.0001f || vmax - vmin > 0.0001f,
            "UVs must not be collapsed to a single value (the old all-Zero bug)");
    }
}

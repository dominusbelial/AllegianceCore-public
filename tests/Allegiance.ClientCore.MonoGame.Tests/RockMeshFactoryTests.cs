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
}

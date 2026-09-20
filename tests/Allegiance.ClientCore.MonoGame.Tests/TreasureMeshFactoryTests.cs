using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P293 — pure-logic tests for <see cref="TreasureMeshFactory"/> — no graphics
/// device involved. They pin the gem placeholder's determinism (byte-identical
/// across calls — the contract it shares with <see cref="WarpMeshFactory"/> /
/// <see cref="RockMeshFactory"/>), its shape invariants (a closed 8-triangle
/// octahedron within the unit radius the renderer scales by the native pickup
/// radius 5.0), and its unit scale.
/// </summary>
public class TreasureMeshFactoryTests
{
    [Fact]
    public void Generate_IsDeterministic_ByteIdenticalAcrossCalls()
    {
        var a = TreasureMeshFactory.Generate();
        var b = TreasureMeshFactory.Generate();

        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Colors, b.Colors);
        Assert.Equal(a.Indices, b.Indices);
    }

    [Fact]
    public void Generate_ProducesAClosedOctahedron_6Verts8Triangles()
    {
        var mesh = TreasureMeshFactory.Generate();

        Assert.Equal(6, mesh.VertexCount);      // 2 apexes + 4 girdle
        Assert.Equal(8, mesh.TriangleCount);    // top cone 4 + bottom cone 4
        Assert.Equal(6 * 3, mesh.Positions.Length);
        Assert.Equal(6 * 4, mesh.Colors.Length);
        Assert.Equal(8 * 3, mesh.Indices.Length);

        // Every index references a real vertex.
        Assert.All(mesh.Indices, i => Assert.InRange(i, 0, 5));
    }

    [Fact]
    public void Generate_IsWithinUnitRadius_SoRendererScalesByNativeRadius()
    {
        var mesh = TreasureMeshFactory.Generate();

        // Max |component| of any vertex must be <= 1 (the unit sphere): the
        // renderer multiplies the whole mesh by TreasureRenderer.DefaultRadius
        // (the native pickup radius 5.0).
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float x = mesh.Positions[i * 3], y = mesh.Positions[i * 3 + 1], z = mesh.Positions[i * 3 + 2];
            Assert.True(Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) <= 1f,
                $"vertex {i} ({x},{y},{z}) exceeds the unit sphere");
        }
    }

    [Fact]
    public void Generate_IsElongatedAlongY_WithFlatGirdleAtEquator()
    {
        var mesh = TreasureMeshFactory.Generate();

        // Top apex at +1 Y, bottom apex at -1 Y (vertices 4 and 5), both on the
        // vertical axis (x = z = 0).
        Assert.Equal(0f, mesh.Positions[4 * 3]);        // top x = 0
        Assert.Equal(1f, mesh.Positions[4 * 3 + 1]);    // top y = +1
        Assert.Equal(0f, mesh.Positions[5 * 3]);        // bottom x = 0
        Assert.Equal(-1f, mesh.Positions[5 * 3 + 1]);   // bottom y = -1

        // The four girdle vertices sit on the y = 0 plane, each at radius
        // GirdleRadius (0.7) from the axis.
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0f, mesh.Positions[i * 3 + 1]);   // y = 0
            float r = MathF.Sqrt(mesh.Positions[i * 3] * mesh.Positions[i * 3]
                               + mesh.Positions[i * 3 + 2] * mesh.Positions[i * 3 + 2]);
            Assert.Equal(TreasureMeshFactory.GirdleRadius, r, 3);
        }
    }

    [Fact]
    public void Generate_ColorsAreGold_InUnitRange_WithBrightTop()
    {
        var mesh = TreasureMeshFactory.Generate();

        // All RGBA channels in [0,1].
        Assert.All(mesh.Colors, c => Assert.InRange(c, 0f, 1f));

        // Gold look: red dominates and stays high throughout; the TOP apex
        // (vertex 4) is the brightest (near-white gold).
        var top = (mesh.Colors[4 * 4], mesh.Colors[4 * 4 + 1], mesh.Colors[4 * 4 + 2]);
        Assert.True(top.Item1 >= 0.9f, $"top red {top.Item1} too dim");
        Assert.True(top.Item1 >= top.Item3, "gold should be red-dominant (not blue)");

        // Opaque alpha.
        for (int i = 0; i < mesh.VertexCount; i++)
            Assert.Equal(1f, mesh.Colors[i * 4 + 3]);
    }
}

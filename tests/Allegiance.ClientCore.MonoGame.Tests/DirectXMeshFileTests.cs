using System;
using System.IO;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the DirectX <c>.x</c> mesh loader (<see cref="DirectXMeshFile"/>),
/// which supplies the P569 shield-flare caps (<c>lens30/lens60/lens90.x</c> — the
/// DirectX exports of native's <c>GetNameSpace(AWF_SHIELD_FLARE)-&gt;FindMember</c>
/// caps, trekigc.cpp:2517-2523). Asserts the caps are <b>unit-radius spherical
/// caps</b> — apex at (0,0,1), concentric latitude rings at z = cos(θ) for
/// θ = 30°/60°/90°, per-vertex normals equal to the positions — the unit-radius
/// cap <c>m_radiusMesh = 1</c> the native <c>Matrix(orient, 0, m_radiusMesh)</c>
/// expects (thinggeo.cpp:1289).
/// </summary>
public class DirectXMeshFileTests
{
    private static readonly string ArtRoot = AssetSources.ResolveRoot();

    private static MdlMesh ParseReal(string stem)
    {
        string path = Path.Combine(ArtRoot, stem + ".x");
        Assert.True(File.Exists(path), $"native cap artwork missing: {path}");
        return DirectXMeshFile.Parse(File.ReadAllText(path));
    }

    private static (int nVerts, int nTris, float minZ, float maxLen, float[] apex) Stats(MdlMesh m)
    {
        int nVerts = m.VertexCount;
        float minZ = float.MaxValue, maxLen = 0f;
        var apex = new float[3];
        for (int v = 0; v < nVerts; v++)
        {
            float x = m.Positions[3 * v], y = m.Positions[3 * v + 1], z = m.Positions[3 * v + 2];
            minZ = MathF.Min(minZ, z);
            maxLen = MathF.Max(maxLen, MathF.Sqrt(x * x + y * y + z * z));
            if (z > apex[2]) { apex[0] = x; apex[1] = y; apex[2] = z; }
        }
        return (nVerts, m.TriangleCount, minZ, maxLen, apex);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("lens30", 30)]
    [InlineData("lens60", 60)]
    [InlineData("lens90", 90)]
    public void Cap_is_a_unit_radius_spherical_cap_at_the_native_angle(string stem, int degrees)
    {
        Require.Artwork();
        var m = ParseReal(stem);
        Assert.True(m.VertexCount > 0, "cap has vertices");
        Assert.True(m.TriangleCount > 0, "cap has triangles");

        var (nVerts, nTris, minZ, maxLen, apex) = Stats(m);

        // Unit-radius: every vertex sits on the unit sphere.
        Assert.True(maxLen <= 1.0001f, $"cap exceeds unit radius (max {maxLen})");
        // Apex at +Z (the cap's dome tip).
        Assert.Equal(0f, apex[0], 1e-3f);
        Assert.Equal(0f, apex[1], 1e-3f);
        Assert.Equal(1f, apex[2], 1e-3f);
        // The deepest latitude ring sits at z = cos(half-angle).
        Assert.True(minZ <= MathF.Cos(degrees * MathF.PI / 180f) + 1e-3f,
            $"cap minZ {minZ} should reach cos({degrees}°) = {MathF.Cos(degrees * MathF.PI / 180f)}");
        // Indices all in range.
        foreach (var ix in m.Indices)
            Assert.True(ix < nVerts, "index out of range");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Caps_differ_in_angular_width()
    {
        Require.Artwork();
        // lens30 reaches only to cos(30°); lens60 to cos(60°); lens90 to the
        // equator (z≈0) — distinct caps, not identical meshes.
        var z30 = Stats(ParseReal("lens30")).minZ;
        var z60 = Stats(ParseReal("lens60")).minZ;
        var z90 = Stats(ParseReal("lens90")).minZ;
        Assert.True(z30 > z60, "lens30 should be narrower (higher minZ) than lens60");
        Assert.True(z60 > z90, "lens60 should be narrower than lens90");
        Assert.True(z90 <= 0.051f, "lens90 should reach the equator (minZ ≈ 0)");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Normals_equal_positions_on_the_unit_sphere()
    {
        Require.Artwork();
        var m = ParseReal("lens30");
        for (int v = 0; v < m.VertexCount; v++)
        {
            Assert.Equal(m.Positions[3 * v], m.Normals[3 * v], 1e-4f);
            Assert.Equal(m.Positions[3 * v + 1], m.Normals[3 * v + 1], 1e-4f);
            Assert.Equal(m.Positions[3 * v + 2], m.Normals[3 * v + 2], 1e-4f);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Texture_coords_cover_the_full_cap_disc()
    {
        Require.Artwork();
        var m = ParseReal("lens30");
        Assert.Equal(m.VertexCount, m.TexCoords.Length / 2);
        // Apex samples the disc centre (0.5,0.5); the widest ring reaches the
        // disc rim (some texcoord touches 0 or 1).
        Assert.Equal(0.5f, m.TexCoords[0], 1e-3f);
        Assert.Equal(0.5f, m.TexCoords[1], 1e-3f);
        bool touchesRim = false;
        for (int v = 0; v < m.VertexCount; v++)
            if (MathF.Abs(m.TexCoords[2 * v] - 0f) < 1e-3f || MathF.Abs(m.TexCoords[2 * v] - 1f) < 1e-3f
                || MathF.Abs(m.TexCoords[2 * v + 1] - 0f) < 1e-3f || MathF.Abs(m.TexCoords[2 * v + 1] - 1f) < 1e-3f)
                touchesRim = true;
        Assert.True(touchesRim, "outer ring should reach the texture edge");
    }
}

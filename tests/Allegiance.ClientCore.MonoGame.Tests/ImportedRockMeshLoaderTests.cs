using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure tests for <see cref="ImportedRockMeshLoader.Finalize"/> (the 
/// device-free finalize stage that centres/normalizes the imported geometry,
/// recomputes outward normals, and fixes the Meshy/FBX reversed winding) and
/// for <see cref="AsteroidRenderer.MeshVariantFor"/> (the silhouette-choice
/// selector). No graphics device needed.
/// </summary>
public class ImportedRockMeshLoaderTests
{
    /// <summary>A regular tetrahedron (max radius 1), fed with its 4 faces in
    /// REVERSED (inside-out) winding to exercise the winding fix.</summary>
    private static (float[] Positions, int[] Indices, float[] Uvs) ReversedTetra()
    {
        float[] pos =
        [
            1f, 1f, 1f,     // v0
            1f, -1f, -1f,   // v1
            -1f, 1f, -1f,   // v2
            -1f, -1f, 1f,   // v3
        ];
        // Correct-outward faces, each deliberately reversed (swap last two) so
        // the input is inside-out and Finalize must flip the winding back.
        // Correct CCW-outward faces:
        //   F0=(v1,v2,v3) F1=(v0,v3,v2) F2=(v0,v1,v3) F3=(v0,v2,v1)
        int[] indices =
        [
            1, 3, 2,   // reversed F0
            0, 2, 3,   // reversed F1
            0, 3, 1,   // reversed F2
            0, 1, 2,   // reversed F3
        ];
        float[] uvs = [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f];
        return (pos, indices, uvs);
    }

    [Fact]
    public void Finalize_NormalizesToUnitExtentAndCentresOnOrigin()
    {
        var (pos, idx, uvs) = ReversedTetra();
        // Displace the whole tetra off-origin so centring is observable.
        for (int i = 0; i < pos.Length; i += 3)
        {
            pos[i] += 100f; pos[i + 1] -= 50f; pos[i + 2] += 25f;
        }
        var rock = ImportedRockMeshLoader.Finalize(pos, idx, uvs);

        float maxR = 0f, cx = 0f, cy = 0f, cz = 0f;
        for (int i = 0; i < rock.VertexCount; i++)
        {
            float x = rock.Positions[i * 3], y = rock.Positions[i * 3 + 1], z = rock.Positions[i * 3 + 2];
            maxR = MathF.Max(maxR, MathF.Sqrt(x * x + y * y + z * z));
            cx += x; cy += y; cz += z;
        }
        // Centred on the origin + unit-extent (max radius 1), regardless of the
        // off-origin, unscaled source — so the renderer's CreateScale(radius)
        // reproduces the real world size.
        Assert.InRange(maxR, 0.999f, 1.001f);
        Assert.True(cx * cx + cy * cy + cz * cz < 1e-6f, "centroid must land on the origin");
    }

    [Fact]
    public void Finalize_NormalsAreUnitLengthAndPointOutward()
    {
        var (pos, idx, uvs) = ReversedTetra();
        var rock = ImportedRockMeshLoader.Finalize(pos, idx, uvs);

        for (int i = 0; i < rock.VertexCount; i++)
        {
            float nx = rock.Normals[i * 3], ny = rock.Normals[i * 3 + 1], nz = rock.Normals[i * 3 + 2];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            Assert.InRange(len, 0.998f, 1.002f);

            float x = rock.Positions[i * 3], y = rock.Positions[i * 3 + 1], z = rock.Positions[i * 3 + 2];
            float r = MathF.Sqrt(x * x + y * y + z * z);
            if (r < 1e-6f) continue;
            float d = nx * (x / r) + ny * (y / r) + nz * (z / r);
            Assert.True(d > 0f, $"vertex {i} normal points inward");
        }
    }

    [Fact]
    public void Finalize_FixesReversedWindingToFaceOutward()
    {
        // MUST-FAIL discriminator for the winding fix: with the input tetra fed
        // inside-out, every triangle's final geometric normal must agree (point
        // outward) with the recomputed outward surface normal. Without the flip
        // (or with a wrong flip) this fails.
        var (pos, idx, uvs) = ReversedTetra();
        var rock = ImportedRockMeshLoader.Finalize(pos, idx, uvs);

        for (int t = 0; t < rock.Indices.Length; t += 3)
        {
            int ia = rock.Indices[t], ib = rock.Indices[t + 1], ic = rock.Indices[t + 2];
            float ax = rock.Positions[ia * 3], ay = rock.Positions[ia * 3 + 1], az = rock.Positions[ia * 3 + 2];
            float bx = rock.Positions[ib * 3], by = rock.Positions[ib * 3 + 1], bz = rock.Positions[ib * 3 + 2];
            float cx = rock.Positions[ic * 3], cy = rock.Positions[ic * 3 + 1], cz = rock.Positions[ic * 3 + 2];
            // Geometric normal from the final winding:
            float gx = (by - ay) * (cz - az) - (bz - az) * (cy - ay);
            float gy = (bz - az) * (cx - ax) - (bx - ax) * (cz - az);
            float gz = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            float gl = MathF.Sqrt(gx * gx + gy * gy + gz * gz);
            if (gl < 1e-9f) continue;
            // Reference outward normal = sum of the triangle's vertex normals.
            float rx = rock.Normals[ia * 3] + rock.Normals[ib * 3] + rock.Normals[ic * 3];
            float ry = rock.Normals[ia * 3 + 1] + rock.Normals[ib * 3 + 1] + rock.Normals[ic * 3 + 1];
            float rz = rock.Normals[ia * 3 + 2] + rock.Normals[ib * 3 + 2] + rock.Normals[ic * 3 + 2];
            float dot = gx * rx + gy * ry + gz * rz;
            Assert.True(dot > 0f, $"triangle {t / 3} winding faces inward after Finalize");
        }
    }

    [Fact]
    public void Finalize_PreservesUvs()
    {
        var (pos, idx, uvs) = ReversedTetra();
        var rock = ImportedRockMeshLoader.Finalize(pos, idx, uvs);
        // Vertex count unchanged; UVs pass through untouched (length = verts*2).
        Assert.Equal(4, rock.VertexCount);
        Assert.Equal(8, rock.Uvs.Length);
        Assert.Equal(uvs, rock.Uvs);
    }

    [Fact]
    public void MeshVariantFor_CoversAllThreeShapesDeterministically()
    {
        // Consecutive AsteroidIds sweep 0..2 (the three imported meshes), so a
        // sector's asteroids visibly vary in silhouette, and the mapping is
        // stable for a given id.
        var seen = new HashSet<int>();
        for (int id = 0; id < 100; id++) seen.Add(AsteroidRenderer.MeshVariantFor(id));
        Assert.True(seen.Contains(0) && seen.Contains(1) && seen.Contains(2), "all three variants must be reachable");
        Assert.Equal(AsteroidRenderer.MeshVariantFor(7), AsteroidRenderer.MeshVariantFor(7));
        Assert.InRange(AsteroidRenderer.MeshVariantFor(-5), 0, 2);   // negative-safe
    }
}

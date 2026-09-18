namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The output of <see cref="RockMeshFactory"/>: plain vertex positions +
/// per-vertex normals + a triangle-list index buffer, all as primitive float/
/// int arrays so the producer is pure logic with NO MonoGame types on its
/// surface (the same discipline as <c>Flight/FlightInput.cs</c> and the former
/// <c>Flight/RemoteShipPredictor.cs</c> — see commit a8b54af). This keeps the
/// mesh geometry unit-testable without a graphics device.
/// </summary>
public sealed record RockMesh(
    float[] Positions,   // x,y,z per vertex
    float[] Normals,     // x,y,z per vertex
    int[] Indices)       // triangle list (multiple of 3)
{
    /// <summary>Number of vertices (Positions.Length / 3).</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles (Indices.Length / 3).</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Generates a deterministic, procedurally-built irregular "rock" mesh — an
/// icosphere (small fixed subdivision count: rough rock, not a smooth ball)
/// whose vertices are displaced radially by a per-vertex hash seeded from a
/// fixed constant. There is deliberately no real asteroid 3D asset in the
/// project's artwork tree (only generic UI-primitive <c>.x</c> files), and
/// Domingo's scoped decision for P218 was a quick procedural rock — NOT a
/// Meshy-generation round-trip like the ship model needed. Every rock on the
/// map reuses this one shared mesh, scaled per instance by the real
/// <c>DataAsteroidIGC.Radius</c> in the renderer (same "one shared mesh"
/// scope cut already accepted for ships).
/// </summary>
/// <remarks>
/// Determinism is the contract: the shape must be byte-identical every run and
/// across calls, so it uses the same fixed-seed-for-run-to-run-stability
/// discipline as <see cref="Starfield"/>'s <c>seed</c> parameter — NOT
/// <c>System.Random</c> re-seeded per call, and NOT time-based. Only integer
/// arithmetic feeds the displacement hash, so identical inputs always produce
/// identical output (unit tests assert this).
/// </remarks>
public static class RockMeshFactory
{
    /// <summary>Fixed seed so the rock shape is stable across runs and across
    /// the two <see cref="Generate"/> calls the unit tests make.</summary>
    public const int DefaultSeed = 81737;

    /// <summary>
    /// Build the rock mesh.
    /// </summary>
    /// <param name="subdivisions">Icosphere subdivision count (default 2). 1-2
    /// is plenty for a rough rock; 0 is a bare icosahedron.</param>
    /// <param name="displacement">Fractional radial displacement amplitude —
    /// each vertex is pushed to a radius in [1−displacement, 1+displacement].</param>
    /// <param name="seed">Fixed seed for run-to-run stable shape (see class
    /// remarks). Defaults to <see cref="DefaultSeed"/>.</param>
    public static RockMesh Generate(int subdivisions = 2, float displacement = 0.22f, int seed = DefaultSeed)
    {
        if (subdivisions < 0)
            throw new ArgumentOutOfRangeException(nameof(subdivisions), "subdivisions must be >= 0");
        if (displacement < 0f || displacement >= 1f)
            throw new ArgumentOutOfRangeException(nameof(displacement), "displacement must be in [0, 1)");

        var positions = BuildIcosahedron(out var indices);
        for (int s = 0; s < subdivisions; s++)
            Subdivide(positions, ref indices);

        // Displace each vertex radially by a deterministic per-vertex hash. Only
        // POSITIONS change here — the pre-displacement direction of each vertex is
        // kept solely as the ray its displaced position lies on (dirX*scale), so
        // this stage is byte-identical to the previous implementation.
        for (int i = 0; i < positions.Count / 3; i++)
        {
            int x = i * 3, y = i * 3 + 1, z = i * 3 + 2;
            float px = positions[x], py = positions[y], pz = positions[z];
            float len = MathF.Sqrt(px * px + py * py + pz * pz);
            if (len < 1e-9f) { px = 1f; py = 0f; pz = 0f; len = 1f; }
            float dirX = px / len, dirY = py / len, dirZ = pz / len;

            float t = HashUnit(unchecked((uint)seed * 2654435761u + (uint)i));
            float scale = 1f + (t - 0.5f) * 2f * displacement;

            positions[x] = dirX * scale;
            positions[y] = dirY * scale;
            positions[z] = dirZ * scale;
        }

        // Compute smooth per-vertex normals from the ACTUAL displaced geometry.
        // For every triangle we take the cross product of two of its (now bumped)
        // edges to get that face's true normal, orient it outward (away from the
        // rock's centre) so a triangle's winding never decides the lit side, and
        // average it into each of the triangle's three vertices. Adjacent faces
        // already share vertices via the subdivision midpoint cache, so summing +
        // renormalizing gives the classic smooth-shaded surface normal at every
        // vertex — lighting that genuinely tracks the bumpy silhouette instead of
        // the original pre-displacement sphere direction (which made highlights/
        // shadows not follow the real shape — P251).
        var normals = new float[positions.Count];
        int[] tris = indices.ToArray();
        for (int t = 0; t < tris.Length; t += 3)
        {
            int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            float ax = positions[ia * 3], ay = positions[ia * 3 + 1], az = positions[ia * 3 + 2];
            float bx = positions[ib * 3], by = positions[ib * 3 + 1], bz = positions[ib * 3 + 2];
            float cx = positions[ic * 3], cy = positions[ic * 3 + 1], cz = positions[ic * 3 + 2];

            // Edge vectors b-a and c-a; their cross product is the face normal.
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;
            float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12f) continue;   // degenerate sliver; contributes nothing

            float mx = (ax + bx + cx) / 3f, my = (ay + by + cy) / 3f, mz = (az + bz + cz) / 3f;
            if (nx * mx + ny * my + nz * mz < 0f) { nx = -nx; ny = -ny; nz = -nz; }

            normals[ia * 3] += nx; normals[ia * 3 + 1] += ny; normals[ia * 3 + 2] += nz;
            normals[ib * 3] += nx; normals[ib * 3 + 1] += ny; normals[ib * 3 + 2] += nz;
            normals[ic * 3] += nx; normals[ic * 3 + 1] += ny; normals[ic * 3 + 2] += nz;
        }

        // Renormalize the summed face normals into true unit surface normals.
        for (int i = 0; i < positions.Count / 3; i++)
        {
            int x = i * 3, y = i * 3 + 1, z = i * 3 + 2;
            float nx = normals[x], ny = normals[y], nz = normals[z];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9f)
            {
                // Unreferenced/degenerate vertex; fall back to its radial ray.
                float rx = positions[x], ry = positions[y], rz = positions[z];
                len = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
                if (len < 1e-9f) len = 1f;
                normals[x] = rx / len; normals[y] = ry / len; normals[z] = rz / len;
            }
            else
            {
                normals[x] = nx / len; normals[y] = ny / len; normals[z] = nz / len;
            }
        }

        return new RockMesh(positions.ToArray(), normals, indices.ToArray());
    }

    /// <summary>A tiny integer-only hash mapped to [0, 1) — fully deterministic,
    /// no <c>System.Random</c>, no time. Knuth-style multiplicative mixing.</summary>
    private static float HashUnit(uint h)
    {
        h ^= h >> 16;
        h *= 0x7feb352d;
        h ^= h >> 15;
        h *= 0x846ca68b;
        h ^= h >> 16;
        return (h & 0x00FFFFFF) / 16777216f;   // 24 bits → [0, 1)
    }

    /// <summary>A unit icosahedron (12 vertices, 20 triangles), all vertices
    /// normalized to the unit sphere.</summary>
    private static List<float> BuildIcosahedron(out List<int> indices)
    {
        float p = (1f + MathF.Sqrt(5f)) / 2f;   // golden ratio φ ≈ 1.618034
        Span<(float X, float Y, float Z)> v =
        [
            (-1f, p, 0f), (1f, p, 0f), (-1f, -p, 0f), (1f, -p, 0f),
            (0f, -1f, p), (0f, 1f, p), (0f, -1f, -p), (0f, 1f, -p),
            (p, 0f, -1f), (p, 0f, 1f), (-p, 0f, -1f), (-p, 0f, 1f),
        ];

        var positions = new List<float>(36);
        for (int i = 0; i < 12; i++)
        {
            float x = v[i].X, y = v[i].Y, z = v[i].Z;
            float len = MathF.Sqrt(x * x + y * y + z * z);
            positions.Add(x / len);
            positions.Add(y / len);
            positions.Add(z / len);
        }

        int[] faces =
        [
            0, 11, 5,  0, 5, 1,  0, 1, 7,  0, 7, 10,  0, 10, 11,
            1, 5, 9,  5, 11, 4,  11, 10, 2,  10, 7, 6,  7, 1, 8,
            3, 9, 4,  3, 4, 2,  3, 2, 6,  3, 6, 8,  3, 8, 9,
            4, 9, 5,  2, 4, 11,  6, 2, 10,  8, 6, 7,  9, 8, 1,
        ];
        indices = new List<int>(faces);
        return positions;
    }

    /// <summary>One icosphere subdivision: split every triangle into four,
    /// inserting a unit-normalized midpoint per edge (deduplicated via an
    /// unordered-edge cache so adjacent triangles share the split vertex).</summary>
    private static void Subdivide(List<float> positions, ref List<int> indices)
    {
        var cache = new Dictionary<(int Lo, int Hi), int>();
        int next = positions.Count / 3;
        int[] tris = indices.ToArray();
        var outIdx = new List<int>(tris.Length * 4);

        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            int ab = MidPoint(positions, cache, ref next, a, b);
            int bc = MidPoint(positions, cache, ref next, b, c);
            int ca = MidPoint(positions, cache, ref next, c, a);

            outIdx.Add(a); outIdx.Add(ab); outIdx.Add(ca);
            outIdx.Add(ab); outIdx.Add(b); outIdx.Add(bc);
            outIdx.Add(ca); outIdx.Add(bc); outIdx.Add(c);
            outIdx.Add(ab); outIdx.Add(bc); outIdx.Add(ca);
        }
        indices = outIdx;
    }

    private static int MidPoint(List<float> positions, Dictionary<(int, int), int> cache, ref int next, int i0, int i1)
    {
        int lo = Math.Min(i0, i1), hi = Math.Max(i0, i1);
        if (cache.TryGetValue((lo, hi), out int found)) return found;

        int idx = next++;
        float ax = positions[lo * 3], ay = positions[lo * 3 + 1], az = positions[lo * 3 + 2];
        float bx = positions[hi * 3], by = positions[hi * 3 + 1], bz = positions[hi * 3 + 2];
        float mx = ax + bx, my = ay + by, mz = az + bz;
        float len = MathF.Sqrt(mx * mx + my * my + mz * mz);
        if (len < 1e-12f) len = 1f;

        positions.Add(mx / len);
        positions.Add(my / len);
        positions.Add(mz / len);
        cache[(lo, hi)] = idx;
        return idx;
    }
}

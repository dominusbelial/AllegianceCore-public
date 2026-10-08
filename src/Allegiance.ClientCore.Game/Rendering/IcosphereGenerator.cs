using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A unit icosphere: <see cref="Vertices"/> are unit-length positions
/// (so each vertex's normal == its position), <see cref="Indices"/> a triangle
/// list. Pure data, generated offline at load — no GPU work at runtime.</summary>
public sealed record IcosphereMesh(Vector3[] Vertices, int[] Indices)
{
    public int VertexCount => Vertices.Length;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// P573 Technique B — a procedural unit icosphere (midpoint subdivision of an
/// icosahedron, then radial normalisation). Subdivision level 4 ≈ 2.5k verts,
/// 3 ≈ 641, 2 ≈ 161 — the LOD ladder the displaced-sphere renderer uses by
/// on-screen size. Pure + unit-testable.
/// </summary>
public static class IcosphereGenerator
{
    /// <summary>Vertex counts per subdivision level (icosahedron 12, then ×~4
    /// minus shared-edge merging). Index 0 = 12, 2 = 162, 3 = 642, 4 = 2562.</summary>
    public static int ExpectedVertexCount(int subdivisions) => subdivisions switch
    {
        0 => 12,
        1 => 42,
        2 => 162,
        3 => 642,
        _ => 2562,
    };

    public static IcosphereMesh Generate(int subdivisions)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        // Icosahedron: 12 vertices, 20 triangles.
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        void Add(Vector3 v) => positions.Add(Vector3.Normalize(v));
        Add(new Vector3(-1, t, 0)); Add(new Vector3(1, t, 0));
        Add(new Vector3(-1, -t, 0)); Add(new Vector3(1, -t, 0));
        Add(new Vector3(0, -1, t)); Add(new Vector3(0, 1, t));
        Add(new Vector3(0, -1, -t)); Add(new Vector3(0, 1, -t));
        Add(new Vector3(t, 0, -1)); Add(new Vector3(t, 0, 1));
        Add(new Vector3(-t, 0, -1)); Add(new Vector3(-t, 0, 1));

        int[][] faces =
        {
            new[]{0,11,5}, new[]{0,5,1}, new[]{0,1,7}, new[]{0,7,10}, new[]{0,10,11},
            new[]{1,5,9}, new[]{5,11,4}, new[]{11,10,2}, new[]{10,7,6}, new[]{7,1,8},
            new[]{3,9,4}, new[]{3,4,2}, new[]{3,2,6}, new[]{3,6,8}, new[]{3,8,9},
            new[]{4,9,5}, new[]{2,4,11}, new[]{6,2,10}, new[]{8,6,7}, new[]{9,8,1},
        };
        foreach (var f in faces) { indices.Add(f[0]); indices.Add(f[1]); indices.Add(f[2]); }

        var midCache = new Dictionary<(int, int), int>();
        for (int s = 0; s < subdivisions; s++)
        {
            midCache.Clear();
            int n = indices.Count;
            var next = new List<int>(n * 4);
            for (int i = 0; i < n; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                int ab = MidPoint(positions, midCache, a, b);
                int bc = MidPoint(positions, midCache, b, c);
                int ca = MidPoint(positions, midCache, c, a);
                next.Add(a); next.Add(ab); next.Add(ca);
                next.Add(b); next.Add(bc); next.Add(ab);
                next.Add(c); next.Add(ca); next.Add(bc);
                next.Add(ab); next.Add(bc); next.Add(ca);
            }
            indices = next;
        }

        return new IcosphereMesh(positions.ToArray(), indices.ToArray());
    }

    private static int MidPoint(List<Vector3> positions, Dictionary<(int, int), int> cache, int i0, int i1)
    {
        var key = i0 < i1 ? (i0, i1) : (i1, i0);
        if (cache.TryGetValue(key, out var idx)) return idx;
        var v = Vector3.Normalize((positions[i0] + positions[i1]) * 0.5f);
        idx = positions.Count;
        positions.Add(v);
        cache[key] = idx;
        return idx;
    }
}

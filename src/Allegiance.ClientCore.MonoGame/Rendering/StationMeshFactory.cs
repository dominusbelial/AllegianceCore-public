namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The output of <see cref="StationMeshFactory"/>: plain vertex positions +
/// per-vertex normals + per-vertex texture coordinates (u,v) + a triangle-list
/// index buffer, all as primitive float/int arrays so the producer is pure
/// logic with NO MonoGame types on its surface (the same discipline as
/// <see cref="RockMesh"/> / <see cref="WarpMesh"/>). Keeps the mesh geometry
/// unit-testable without a graphics device.
/// </summary>
/// <remarks>
/// A station is a solid, lit structure (unlike a self-lit warp), so it carries
/// analytic outward normals for <see cref="BasicEffect"/> shading rather than
/// per-vertex colours — the <see cref="AsteroidRenderer"/> "normals for
/// lighting" model, not the <see cref="WarpRenderer"/> "colours for glow"
/// model. The per-instance friendly/enemy side tint is applied as the
/// renderer's flat material (<c>DiffuseColor</c>) at draw time, so the mesh
/// itself stays colour-free and deterministic.
/// </remarks>
public sealed record StationMesh(
    float[] Positions,   // x,y,z per vertex (local up = +Y, forward = +Z)
    float[] Normals,     // x,y,z per vertex, unit length, outward
    float[] Uvs,         // u,v per vertex, each in [0,1]
    int[] Indices)       // triangle list (multiple of 3)
{
    /// <summary>Number of vertices (Positions.Length / 3).</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles (Indices.Length / 3).</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Generates a deterministic, procedurally-built generic "space station"
/// silhouette — a squat central hub cylinder with an equatorial torus ring
/// around it. It is an explicitly TEMPORARY placeholder (P289): real
/// per-station-type art (Garrison vs. Supremacy Center vs. Shipyard distinct
/// models) is a named future Meshy-asset task, out of scope here. This is the
/// client-wide "no station renders at all today" unblock — one generic shape
/// that reads unambiguously as a habitation ring around a core when flown at.
/// </summary>
/// <remarks>
/// <para><b>Why procedural (no texture/asset):</b> the budget for generating
/// real Meshy station models is spent (330 credits held back), and there is no
/// station mesh in this checkout's artwork tree — the same two-tree situation
/// ships (P218) and asteroids (P218) were already in. So the station is
/// generated, matching the established <see cref="RockMeshFactory"/> /
/// <see cref="WarpMeshFactory"/> precedent exactly: a deterministic,
/// unit-testable pure-logic producer consumed by a thin renderer
/// (<see cref="StationRenderer"/>), one shared mesh reused for every station,
/// scaled per-instance by the real per-station-type
/// <c>DataStationTypeIGC.Radius</c>.</para>
/// <para><b>Determinism is the contract:</b> same params &rarr; byte-identical
/// mesh across calls and runs. Only <c>MathF</c> arithmetic, no
/// <c>System.Random</c>, no time (the same fixed-seed-for-stability discipline
/// as <see cref="RockMeshFactory"/> and <see cref="Starfield"/>).</para>
/// <para><b>Coordinate framing:</b> local up = <c>+Y</c> (vertical axis of the
/// hub), local forward = <c>+Z</c>. The station is rotationally symmetric about
/// <c>+Y</c>: a central closed cylinder (radius <see cref="DefaultHubRadius"/>,
/// half-height <see cref="DefaultHubHalfHeight"/>) surrounded at the equator by
/// a torus ring (major radius <see cref="DefaultRingMajor"/>, tube radius
/// <see cref="DefaultRingTube"/>). The outermost point sits at radius
/// <c>ringMajor + ringTube = 1</c> in mesh units, so the renderer's uniform
/// scale by the real <c>Radius</c> makes the visual station radius equal the
/// real per-type radius (the same "unit mesh, scale by the real data field"
/// contract <see cref="WarpMeshFactory.DefaultRimRadius"/> establishes for
/// warps). All normals are analytic and point outward, so the default
/// back-face culling is correct (the solid-rock argument
/// <see cref="AsteroidRenderer"/> makes, not the hollow-funnel CullNone a warp
/// needs).</para>
/// </remarks>
public static class StationMeshFactory
{
    /// <summary>Circumference resolution for the torus tube and the hub's
    /// rings. 32 keeps the placeholder smooth at flight distances while the
    /// generated mesh stays small (a few thousand triangles).</summary>
    public const int DefaultSegments = 32;

    /// <summary>Rows of lateral vertices up the hub's curved side (bottom /
    /// middle / top).</summary>
    public const int DefaultStackRings = 3;

    /// <summary>Central hub cylinder radius, in mesh units.</summary>
    public const float DefaultHubRadius = 0.308f;

    /// <summary>Central hub cylinder half-height (hub spans y = ±this), in mesh
    /// units. Taller than the ring tube so the core visibly pokes above/below
    /// the ring.</summary>
    public const float DefaultHubHalfHeight = 0.322f;

    /// <summary>Torus major radius (distance from the +Y axis to the ring
    /// tube's centre), in mesh units.</summary>
    public const float DefaultRingMajor = 0.943f;

    /// <summary>Torus tube radius, in mesh units. Outer radius =
    /// <see cref="DefaultRingMajor"/> + <see cref="DefaultRingTube"/> = 1, so
    /// the mesh's widest point is exactly at unit radius (scaled by the real
    /// <c>Radius</c> in the renderer).</summary>
    public const float DefaultRingTube = 0.057f;

    /// <summary>
    /// Build the station mesh.
    /// </summary>
    /// <param name="segments">Circumference resolution (≥ 4); used for both the
    /// torus tube and the hub rings.</param>
    /// <param name="stackRings">Lateral ring rows up the hub's curved side
    /// (≥ 2).</param>
    /// <param name="hubRadius">Central hub cylinder radius (&gt; 0).</param>
    /// <param name="hubHalfHeight">Central hub cylinder half-height (&gt; 0).</param>
    /// <param name="ringMajor">Torus major radius (&gt; 0).</param>
    /// <param name="ringTube">Torus tube radius (&gt; 0; <c>ringMajor +
    /// ringTube</c> is the mesh's outer radius).</param>
    public static StationMesh Generate(
        int segments = DefaultSegments,
        int stackRings = DefaultStackRings,
        float hubRadius = DefaultHubRadius,
        float hubHalfHeight = DefaultHubHalfHeight,
        float ringMajor = DefaultRingMajor,
        float ringTube = DefaultRingTube)
    {
        if (segments < 4)
            throw new ArgumentOutOfRangeException(nameof(segments), "segments must be >= 4");
        if (stackRings < 2)
            throw new ArgumentOutOfRangeException(nameof(stackRings), "stackRings must be >= 2");
        if (hubRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(hubRadius), "hubRadius must be > 0");
        if (hubHalfHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(hubHalfHeight), "hubHalfHeight must be > 0");
        if (ringMajor <= 0f)
            throw new ArgumentOutOfRangeException(nameof(ringMajor), "ringMajor must be > 0");
        if (ringTube <= 0f)
            throw new ArgumentOutOfRangeException(nameof(ringTube), "ringTube must be > 0");

        var pos = new List<float>(512);
        var nor = new List<float>(512);
        var uvs = new List<float>(512);
        var idx = new List<int>(2048);

        BuildRing(pos, nor, uvs, idx, segments, ringMajor, ringTube);
        BuildHub(pos, nor, uvs, idx, segments, stackRings, hubRadius, hubHalfHeight);

        return new StationMesh(pos.ToArray(), nor.ToArray(), uvs.ToArray(), idx.ToArray());
    }

    /// <summary>The equatorial torus ring: a fat circle of tube in the xz-plane
    /// centred on the +Y axis. All normals are the analytic outward unit
    /// vector <c>cos(v)·r̂ + sin(v)·ŷ</c>, where v is the tube angle.</summary>
    private static void BuildRing(
        List<float> pos, List<float> nor, List<float> uvs, List<int> idx,
        int segments, float ringMajor, float ringTube)
    {
        int base_ = pos.Count / 3;

        for (int i = 0; i < segments; i++)
        {
            float u = (MathF.Tau * i) / segments;
            float cu = MathF.Cos(u), su = MathF.Sin(u);
            for (int j = 0; j < segments; j++)
            {
                float v = (MathF.Tau * j) / segments;
                float cv = MathF.Cos(v), sv = MathF.Sin(v);

                // Outward unit normal: cos(v) along the radial direction,
                // sin(v) along +Y (the tube wraps around the major circle).
                float nx = cv * cu;
                float ny = sv;
                float nz = cv * su;

                float px = (ringMajor + ringTube * cv) * cu;
                float py = ringTube * sv;
                float pz = (ringMajor + ringTube * cv) * su;

                AddVertex(pos, nor, uvs, px, py, pz, nx, ny, nz, (float)i / segments, (float)j / segments);
            }
        }

        // Wrap around BOTH the major circle (i) and the tube (j). Each (i,j)
        // cell becomes a quad split into two triangles.
        for (int i = 0; i < segments; i++)
        {
            int i2 = (i + 1) % segments;
            for (int j = 0; j < segments; j++)
            {
                int j2 = (j + 1) % segments;
                int a = base_ + i * segments + j;
                int b = base_ + i2 * segments + j;
                int c = base_ + i2 * segments + j2;
                int d = base_ + i * segments + j2;
                idx.Add(a); idx.Add(b); idx.Add(c);
                idx.Add(a); idx.Add(c); idx.Add(d);
            }
        }
    }

    /// <summary>The central closed hub cylinder (lateral wall + top/bottom
    /// disks) so the core reads as a solid drum, not a hollow tube. Lateral
    /// normals are radial; cap normals are ±Y.</summary>
    private static void BuildHub(
        List<float> pos, List<float> nor, List<float> uvs, List<int> idx,
        int segments, int stackRings, float hubRadius, float hubHalfHeight)
    {
        // --- Lateral curved wall: `stackRings` rows of segments vertices. ---
        int latBase = pos.Count / 3;
        for (int r = 0; r < stackRings; r++)
        {
            float t = stackRings == 1 ? 0f : (float)r / (stackRings - 1);
            float y = (2f * t - 1f) * hubHalfHeight;   // -Hh .. +Hh
            for (int j = 0; j < segments; j++)
            {
                float a = (MathF.Tau * j) / segments;
                float cx = MathF.Cos(a), sx = MathF.Sin(a);
                AddVertex(pos, nor, uvs,
                    hubRadius * cx, y, hubRadius * sx,
                    cx, 0f, sx,
                    (float)j / segments, t);
            }
        }
        for (int r = 0; r < stackRings - 1; r++)
        {
            int row0 = latBase + r * segments;
            int row1 = latBase + (r + 1) * segments;
            for (int j = 0; j < segments; j++)
            {
                int j2 = (j + 1) % segments;
                int v00 = row0 + j, v01 = row0 + j2, v10 = row1 + j, v11 = row1 + j2;
                idx.Add(v00); idx.Add(v10); idx.Add(v11);
                idx.Add(v00); idx.Add(v11); idx.Add(v01);
            }
        }

        BuildDiskCap(pos, nor, uvs, idx, segments, hubRadius, +hubHalfHeight, +1f);
        BuildDiskCap(pos, nor, uvs, idx, segments, hubRadius, -hubHalfHeight, -1f);
    }

    /// <summary>A flat disk cap closing the hub end at <c>y = ±Hh</c>, normal
    /// ±Y, fan-triangulated from a centre vertex to the rim.</summary>
    private static void BuildDiskCap(
        List<float> pos, List<float> nor, List<float> uvs, List<int> idx,
        int segments, float hubRadius, float y, float dirY)
    {
        int centre = pos.Count / 3;
        AddVertex(pos, nor, uvs, 0f, y, 0f, 0f, dirY, 0f, 0.5f, 0.5f);

        int rimBase = pos.Count / 3;
        for (int j = 0; j < segments; j++)
        {
            float a = (MathF.Tau * j) / segments;
            float cx = MathF.Cos(a), sx = MathF.Sin(a);
            AddVertex(pos, nor, uvs,
                hubRadius * cx, y, hubRadius * sx,
                0f, dirY, 0f,
                0.5f + 0.495f * cx, 0.5f + 0.495f * sx);
        }
        for (int j = 0; j < segments; j++)
        {
            int j2 = (j + 1) % segments;
            idx.Add(centre);
            // P340 (U2) — the fan's geometric normal is determined by winding:
            // `(centre, j2, j)` yields +Y for BOTH caps, which opposes the true
            // outward (-Y) direction of the bottom cap and would cull it under
            // back-face culling (now live since P339 restored culling to the
            // station pass). Emit the opposite order for the downward cap so
            // each cap's winding agrees with its outward dirY normal.
            if (dirY > 0f)
            {
                idx.Add(rimBase + j2);
                idx.Add(rimBase + j);
            }
            else
            {
                idx.Add(rimBase + j);
                idx.Add(rimBase + j2);
            }
        }
    }

    private static void AddVertex(
        List<float> pos, List<float> nor, List<float> uvs,
        float x, float y, float z, float nx, float ny, float nz, float u, float v)
    {
        pos.Add(x); pos.Add(y); pos.Add(z);
        nor.Add(nx); nor.Add(ny); nor.Add(nz);
        uvs.Add(u); uvs.Add(v);
    }
}

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The output of <see cref="WarpMeshFactory"/>: plain vertex positions +
/// per-vertex RGBA colors + a triangle-list index buffer, all as primitive
/// float/int arrays so the producer is pure logic with NO MonoGame types on
/// its surface (the same discipline as <see cref="RockMesh"/> /
/// <c>Flight/FlightInput.cs</c>). Keeps the mesh geometry unit-testable
/// without a graphics device.
/// </summary>
/// <remarks>
/// A warp carries no per-vertex normal: the look is a self-lit energy glow
/// (see <see cref="WarpRenderer"/>), not lighting-shaded geometry, so colors
/// are carried per-vertex instead of normals + a flat tint.
/// </remarks>
public sealed record WarpMesh(
    float[] Positions,   // x,y,z per vertex (local forward = +Z)
    float[] Colors,      // r,g,b,a per vertex, each in [0,1]
    int[] Indices)       // triangle list (multiple of 3)
{
    /// <summary>Number of vertices (Positions.Length / 3).</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles (Indices.Length / 3).</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Generates a deterministic, procedurally-built warp-gate mesh — a tapering
/// funnel/vortex (a series of concentric rings shrinking toward a point along
/// the forward axis, each ring twisted further around the axis by an
/// increasing spiral phase) — per the original game's own tutorial
/// description (<c>training/mission4.cpp:494</c>): <i>"like a whirlpool in
/// space... a big swirling brown vortex, with a bright spot in the center."</i>
/// </summary>
/// <remarks>
/// <para><b>Why procedural (no texture asset):</b> the native client textured
/// its warp model from a per-warp texture name
/// (<c>warpIGC.cpp:33</c> <c>LoadWarp(dataWarp-&gt;warpDef.textureName)</c>),
/// but no such asset is present in this checkout's artwork tree (the same
/// two-tree situation asteroids and ships were already in for P218). So the
/// swirl is built into the <em>geometry</em> (static helix twist per ring)
/// plus a per-vertex warm-to-white-hot color gradient, rather than painted on
/// a texture. Every warp on the map reuses this ONE shared mesh, scaled per
/// instance by the real <c>DataWarpIGC.Radius</c> in the renderer — the same
/// "one shared mesh" scope cut already accepted for asteroids/ships.</para>
/// <para><b>Determinism is the contract:</b> same params &rarr; byte-identical
/// mesh across calls and runs. Only <c>MathF</c> arithmetic, no
/// <c>System.Random</c>, no time (the same fixed-seed discipline as
/// <see cref="RockMeshFactory"/> and <see cref="Starfield"/>).</para>
/// <para><b>Coordinate framing:</b> local forward = <c>+Z</c>. The rim ring
/// sits at <c>z = 0</c> (radius <c>rimRadius</c>) and the funnel converges to
/// its tip at <c>z = depth</c> (radius → 0). The renderer composes a
/// continuous rotation about <c>+Z</c> on top of the instance orientation, so
/// the whole vortex constantly swirls around the warp's own forward axis.</para>
/// </remarks>
public static class WarpMeshFactory
{
    /// <summary>Radians of helix twist applied across the full funnel depth
    /// (rim → tip). Higher = tighter swirl baked into the static mesh.</summary>
    public const float DefaultSpiralTightness = 3.2f;

    /// <summary>Number of concentric rings along the depth axis.</summary>
    public const int DefaultRingCount = 10;

    /// <summary>Number of vertices per ring around the circumference.</summary>
    public const int DefaultSegments = 24;

    /// <summary>Taper exponent: radius ∝ (1−t)^Taper where t∈[0,1] is depth
    /// fraction. &gt;1 makes the funnel narrow toward the tip faster.</summary>
    public const float DefaultTaper = 2.2f;

    /// <summary>Unit rim radius; the renderer scales the whole mesh by the real
    /// per-instance <c>DataWarpIGC.Radius</c>, so rim diameter = Radius in
    /// world units.</summary>
    public const float DefaultRimRadius = 1f;

    /// <summary>Number of brighter "arm" wedges that wind helically down the
    /// funnel (their phase rides the same per-ring twist as the geometry), so
    /// the static mesh reads as a swirling vortex and the animated spin in
    /// <see cref="WarpRenderer"/> has visible structure to turn.</summary>
    public const int SpiralArms = 3;

    /// <summary>Azimuthal brightness swing of the spiral arms (±this fraction).</summary>
    private const float ArmAmplitude = 0.30f;

    /// <summary>Axial funnel length in mesh units (scaled with the radius by the
    /// renderer's uniform scale).</summary>
    public const float DefaultDepth = 1.6f;

    // Warm-energy gradient stops (rim → mid → tip), all opaque.
    private const float RimR = 0.16f, RimG = 0.06f, RimB = 0.018f;      // dark brown
    private const float MidR = 0.96f, MidG = 0.44f, MidB = 0.075f;      // saturated amber
    private const float TipR = 1.00f, TipG = 0.988f, TipB = 0.92f;      // white-hot
    private const float MidT = 0.6f;                                     // amber peaks here

    /// <summary>
    /// Build the warp funnel mesh.
    /// </summary>
    /// <param name="ringCount">Concentric rings along the depth axis (≥ 2).</param>
    /// <param name="segments">Vertices per ring (≥ 3).</param>
    /// <param name="rimRadius">Outer rim radius in mesh units (default 1).</param>
    /// <param name="depth">Axial funnel length (rim at z=0, tip at z=depth).</param>
    /// <param name="spiralTightness">Total helix twist (radians) across the depth.</param>
    /// <param name="taper">Taper exponent (&gt;0).</param>
    public static WarpMesh Generate(
        int ringCount = DefaultRingCount,
        int segments = DefaultSegments,
        float rimRadius = DefaultRimRadius,
        float depth = DefaultDepth,
        float spiralTightness = DefaultSpiralTightness,
        float taper = DefaultTaper)
    {
        if (ringCount < 2)
            throw new ArgumentOutOfRangeException(nameof(ringCount), "ringCount must be >= 2");
        if (segments < 3)
            throw new ArgumentOutOfRangeException(nameof(segments), "segments must be >= 3");
        if (rimRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(rimRadius), "rimRadius must be > 0");
        if (depth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(depth), "depth must be > 0");

        int vertexCount = ringCount * segments;
        var pos = new float[vertexCount * 3];
        var col = new float[vertexCount * 4];

        for (int ring = 0; ring < ringCount; ring++)
        {
            float t = ringCount == 1 ? 0f : (float)ring / (ringCount - 1);   // 0 = rim, 1 = tip
            float radius = rimRadius * MathF.Pow(MathF.Max(0f, 1f - t), taper);
            float z = depth * t;
            float phase = spiralTightness * t;                                // accumulated helix twist

            for (int j = 0; j < segments; j++)
            {
                float angle = (MathF.Tau * j / segments) + phase;
                float x = radius * MathF.Cos(angle);
                float y = radius * MathF.Sin(angle);

                int vi = ring * segments + j;
                pos[vi * 3 + 0] = x;
                pos[vi * 3 + 1] = y;
                pos[vi * 3 + 2] = z;

                // Warm gradient: dark brown rim → saturated amber → white-hot tip,
                // overlaid with `SpiralArms` brighter wedges that wind helically
                // down the funnel (their phase rides the same per-ring twist as
                // the geometry), so the static mesh reads as a swirling vortex
                // and the animated spin has visible structure to turn.
                (float r, float g, float b) = ColorAt(t);
                float arm = 1f + ArmAmplitude * MathF.Cos(SpiralArms * angle);
                r = Math.Clamp(r * arm, 0f, 1f);
                g = Math.Clamp(g * arm, 0f, 1f);
                b = Math.Clamp(b * arm, 0f, 1f);
                col[vi * 4 + 0] = r;
                col[vi * 4 + 1] = g;
                col[vi * 4 + 2] = b;
                col[vi * 4 + 3] = 1f;
            }
        }

        // Grid triangulation between consecutive rings: each pair of adjacent
        // vertices on ring i and the same pair on ring i+1 forms a quad → 2
        // triangles. Winding is arbitrary here: WarpRenderer renders with
        // back-face culling disabled (a hollow funnel must show its inner wall).
        var indices = new List<int>((ringCount - 1) * segments * 6);
        for (int ring = 0; ring < ringCount - 1; ring++)
        {
            int row0 = ring * segments;
            int row1 = (ring + 1) * segments;
            for (int j = 0; j < segments; j++)
            {
                int j2 = (j + 1) % segments;
                int v00 = row0 + j;
                int v01 = row0 + j2;
                int v10 = row1 + j;
                int v11 = row1 + j2;
                indices.Add(v00); indices.Add(v10); indices.Add(v11);
                indices.Add(v00); indices.Add(v11); indices.Add(v01);
            }
        }

        return new WarpMesh(pos, col, indices.ToArray());
    }

    /// <summary>Piecewise-linear warm gradient in [0,1]: dark brown (t=0) →
    /// saturated amber (t=<see cref="MidT"/>) → white-hot (t=1).</summary>
    private static (float R, float G, float B) ColorAt(float t)
    {
        if (t < MidT)
        {
            float k = MidT <= 0f ? 0f : t / MidT;
            return (RimR + (MidR - RimR) * k, RimG + (MidG - RimG) * k, RimB + (MidB - RimB) * k);
        }

        float k2 = (1f - MidT) <= 0f ? 0f : (t - MidT) / (1f - MidT);
        return (MidR + (TipR - MidR) * k2, MidG + (TipG - MidG) * k2, MidB + (TipB - MidB) * k2);
    }
}

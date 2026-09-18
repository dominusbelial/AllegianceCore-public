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
/// plus a per-vertex cool-to-white-hot color gradient, rather than painted on
/// a texture. Every warp on the map reuses this ONE shared mesh, scaled per
/// instance by the real <c>DataWarpIGC.Radius</c> in the renderer — the same
/// "one shared mesh" scope cut already accepted for asteroids/ships.</para>
/// <para><b>Determinism is the contract:</b> same params &rarr; byte-identical
/// mesh across calls and runs. Only <c>MathF</c> arithmetic, no
/// <c>System.Random</c>, no time (the same fixed-seed discipline as
/// <see cref="RockMeshFactory"/> and <see cref="Starfield"/>).</para>
/// <para><b>Coordinate framing:</b> local forward = <c>+Z</c>. P258 reshaped
/// the single-direction taper into a <b>symmetric hourglass/bicone</b>: a
/// wide rim ring (radius <c>rimRadius</c>) sits at <em>both</em> ends
/// <c>z = ±depth/2</c>, tapering to the bright pinch point (radius → 0) at
/// <c>z = 0</c>. Because the renderer places the mesh's local origin at the
/// real warp <c>Position</c>, the real warp now sits at the narrow bright
/// center with a wide open mouth extending symmetrically in both directions —
/// so the same gate reads as a comparable "wide opening into a bright
/// center" approached from either side (the transit trigger
/// <see cref="Allegiance.Core.Model.Sector.ResolveWarpCollisions"/> was
/// already a plain symmetric distance-to-center sphere test; the asymmetry
/// was purely this mesh's visual shape). The renderer composes a continuous
/// rotation about <c>+Z</c> on top of the instance orientation, so the whole
/// vortex constantly swirls around the warp's own forward axis.</para>
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

    /// <summary>Taper exponent of the symmetric hourglass: radius ∝ s^Taper where
    /// s∈[0,1] is the axial fraction measured from the center outward (s=0 at the
    /// center pinch, s=1 at either wide rim). &gt;1 makes the funnel narrow toward
    /// the center pinch faster.</summary>
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

    /// <summary>Axial length of the symmetric hourglass in mesh units (scaled
    /// with the radius by the renderer's uniform scale): the two wide rims sit
    /// at z = ±depth/2 and the pinch point at z = 0.</summary>
    public const float DefaultDepth = 1.6f;

    // Cool energy-gradient stops, all opaque. P242: P227's warm brown/amber
    // (driven by training/mission4.cpp:494 "big swirling brown vortex") does
    // NOT match how the real aleph reads — Domingo's direct memory of the
    // original game is that the warp is bluish, and his memory is the source
    // of truth here (native's own aleph texture is unrecoverable from this
    // checkout, the same two-tree situation ship models were already in for
    // P218). P258 mirrors the gradient to match the symmetric hourglass: DARK
    // deep-navy at both wide rims (t=0 and t=1), rising through the mid-tone
    // blue band to the pale-cyan WHITE-HOT at the center pinch (t=0.5) — the
    // "dark rim → bright center" structure P227 established, now with a bright
    // CENTER (matching native's tutorial "a bright spot in the center")
    // instead of a bright trailing tip. (An additive blend in WarpRenderer
    // turns this into a glowing, genuinely see-through energy field.)
    private const float RimR = 0.040f, RimG = 0.070f, RimB = 0.320f;    // deep navy (both rims)
    private const float MidR = 0.096f, MidG = 0.368f, MidB = 1.000f;    // mid-tone blue band
    private const float TipR = 1.000f, TipG = 0.976f, TipB = 1.000f;    // white-hot (center pinch)
    private const float MidT = 0.6f;                                     // blue band peaks here (fraction to center)

    /// <summary>
    /// Build the warp funnel mesh.
    /// </summary>
    /// <param name="ringCount">Concentric rings along the depth axis (≥ 2).</param>
    /// <param name="segments">Vertices per ring (≥ 3).</param>
    /// <param name="rimRadius">Outer rim radius in mesh units, at BOTH ends
    /// (default 1).</param>
    /// <param name="depth">Axial hourglass length (rims at z=±depth/2, pinch at
    /// z=0).</param>
    /// <param name="spiralTightness">Total helix twist (radians) across the full
    /// depth (rim → pinch → rim).</param>
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
            // t ∈ [0,1] across the whole symmetric hourglass: 0 = one wide rim,
            // 0.5 = the center pinch, 1 = the other wide rim. The mesh spans
            // z = -depth/2 .. +depth/2 with the real warp position at z = 0.
            float t = ringCount == 1 ? 0f : (float)ring / (ringCount - 1);

            // Axial fraction measured from the CENTER outward: s = 0 at the
            // pinch (t = 0.5), s = 1 at either wide rim (t = 0 or t = 1).
            float s = MathF.Abs(2f * t - 1f);
            float radius = rimRadius * MathF.Pow(MathF.Max(0f, s), taper);
            float z = depth * (t - 0.5f);

            // The spiral twist's phase ACCUMULATES MONOTONICALLY end-to-end
            // (0 → spiralTightness across the whole span), it does NOT mirror
            // at the center. Reason: a swirling vortex rotates about its axis
            // with a single coherent handedness — a phase that peaked at the
            // center and unwound toward both rims would reverse the apparent
            // whirl direction halfway down the hourglass and fight the animated
            // spin (WarpRenderer's continuous rotation about +Z is the same sign
            // everywhere). Keeping the monotonic accumulation preserves the
            // established "helical spiral-arm twist" technique unchanged while
            // only the taper curve and colour mapping are reshaped.
            float phase = spiralTightness * t;

            for (int j = 0; j < segments; j++)
            {
                float angle = (MathF.Tau * j / segments) + phase;
                float x = radius * MathF.Cos(angle);
                float y = radius * MathF.Sin(angle);

                int vi = ring * segments + j;
                pos[vi * 3 + 0] = x;
                pos[vi * 3 + 1] = y;
                pos[vi * 3 + 2] = z;

                // Mirrored cool gradient (P258): deep-navy dark at BOTH rims
                // (t=0/t=1), rising through the mid-tone blue band to the
                // pale-cyan white-hot at the CENTER pinch (t=0.5), overlaid
                // with `SpiralArms` brighter wedges that wind helically along
                // the hourglass (their phase rides the same per-ring twist as
                // the geometry), so the static mesh reads as a swirling vortex
                // and the animated spin has visible structure to turn.
                (float r, float g, float b) = ColorAt(MirrorCenter(t));
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

    /// <summary>Maps the axial parameter t∈[0,1] (0 = one rim, 0.5 = center
    /// pinch, 1 = other rim) onto u∈[0,1] = 1−|2t−1|, the fraction of the way
    /// from a rim TOWARD the center: u=0 at BOTH rims, u=1 at the center pinch.
    /// This is what lets the gradient mirror symmetrically — a single monotone
    /// <see cref="ColorAt"/> in u produces identical dark-navy at both ends and
    /// white-hot at the middle.</summary>
    private static float MirrorCenter(float t) => 1f - MathF.Abs(2f * t - 1f);

    /// <summary>Piecewise-linear cool gradient in u∈[0,1] (u = fraction from a
    /// rim toward the center pinch): deep navy (u=0, the wide rim) →
    /// mid-tone blue (u=<see cref="MidT"/>) → pale-cyan white-hot (u=1, the
    /// bright center). Called through <see cref="MirrorCenter"/> so the two
    /// symmetric rims are both dark and the center is the bright spot.</summary>
    private static (float R, float G, float B) ColorAt(float u)
    {
        if (u < MidT)
        {
            float k = MidT <= 0f ? 0f : u / MidT;
            return (RimR + (MidR - RimR) * k, RimG + (MidG - RimG) * k, RimB + (MidB - RimB) * k);
        }

        float k2 = (1f - MidT) <= 0f ? 0f : (u - MidT) / (1f - MidT);
        return (MidR + (TipR - MidR) * k2, MidG + (TipG - MidG) * k2, MidB + (TipB - MidB) * k2);
    }
}

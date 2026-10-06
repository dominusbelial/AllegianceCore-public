namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The output of <see cref="WarpMeshFactory"/>: plain vertex positions +
/// per-vertex RGBA colors + per-vertex UVs + a triangle-list index buffer,
/// all as primitive float/int arrays so the producer is pure logic with NO
/// MonoGame types on its surface (the same discipline as <see cref="RockMesh"/>
/// / <c>Flight/FlightInput.cs</c>). Keeps the mesh geometry unit-testable
/// without a graphics device.
/// </summary>
/// <remarks>
/// <para>A warp carries no per-vertex normal: the look is a self-lit energy
/// glow (see <see cref="WarpRenderer"/>), not lighting-shaded geometry, so
/// colors are carried per-vertex instead of normals + a flat tint.</para>
/// <para>P562 — the hourly vortex is now TEXTURED (native <c>alephbmp</c>,
/// see <see cref="WarpMeshFactory"/>): each vertex carries a base UV whose
/// <c>v</c> coordinate the renderer animates every frame (native
/// <c>UpdateTextureCoordinates</c>), producing the inward-flowing
/// water-draining look. <see cref="Uvs"/> are the <b>time = 0</b> base; the
/// renderer recomputes <c>v = mod(time/10,1) + r</c> per frame.</para>
/// </remarks>
public sealed record WarpMesh(
    float[] Positions,   // x,y,z per vertex (local forward = +Z)
    float[] Colors,      // r,g,b,a per vertex, each in [0,1]
    float[] Uvs,         // u,v per vertex (base, time = 0)
    int[] Indices)       // triangle list (multiple of 3)
{
    /// <summary>Number of vertices (Positions.Length / 3).</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles (Indices.Length / 3).</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Generates the double-horn (hourglass) warp-gate mesh, now TEXTURED the
/// native way (P562). Two funnel horns meet at a central pinch (the wide rims
/// at both ends, the narrow bright point at the centre) exactly as P258 built
/// it; each horn is mapped with the original game's <c>AlephGeo</c> vortex —
/// native's per-vertex radial parameter <c>r</c> (0 at the pinch, 1 at the
/// rim), the <c>2r(1−r)</c> brightness band (<c>dark centre &amp; rim, bright
/// mid-band</c>), and native's animated UVs (inward <c>v</c>-flow + spiral
/// <c>u</c>-twist) that make the aleph read as water draining.
/// </summary>
/// <remarks>
/// <para><b>Native source.</b> <c>effect/alephgeo.cpp</c> (<c>InitializeVertices</c>
/// :80-102, <c>UpdateTextureCoordinates</c> :126-145). Native builds a single
/// funnel with <c>countDepth</c> rings × <c>countSection</c> sections; the
/// port keeps the DOUBLE-HORN silhouette (Domingo's explicit scope: keep the
/// double horn design, adopt the native draining look) by mirroring native's
/// funnel into two horns that share the central pinch ring. The per-vertex
/// formulas are native's verbatim, parameterised by <c>r</c> =
/// ring-fraction from the pinch to the rim.</para>
/// <para><b>Brightness.</b> Native sets every vertex colour to
/// <c>bright = 2·r·(1−r)</c> (<c>alephgeo.cpp:82</c>) and Gouraud-modulates
/// the <c>alephbmp</c> texture by it, so the texture dims to nothing at the
/// pinch and the rims and glows at the mid-band. <see cref="WarpRenderer"/>
/// feeds these colours into a textured BasicEffect (texture × vertex colour,
/// additive blend) — native's exact look.</para>
/// <para><b>No baked geometry swirl.</b> Native's funnel has no static helix
/// twist (its circle angle is plainly <c>2·pi·indexSection/countSection</c>,
/// alephgeo.cpp:85); ALL the swirl comes from the animated UVs. The P242/P258
/// static spiral-arm colour wedges and baked helix twist are therefore
/// REMOVED — the vortex motion now lives entirely in the per-frame UV flow
/// (<see cref="WarpRenderer"/>), so a rotating solid no longer reads as a
/// spinning cone. This is the crux of the "water draining" port.</para>
/// <para><b>Animated UVs.</b> Base <c>u</c> = <c>4·section/countSection +
/// 5·(1−(1−r)²)</c> (the spiral twist, strongest at the rim; alephgeo.cpp:134)
/// and base <c>v</c> = <c>r</c>. Each frame the renderer adds native's time
/// flow <c>v = mod(time/10,1) + r</c> (<c>alephgeo.cpp:128,133</c>), sliding
/// the texture window along the funnel — the inward-draining motion.</para>
/// <para><b>Determinism is the contract:</b> same params &rarr; byte-identical
/// mesh across calls and runs. Only <c>MathF</c> arithmetic, no
/// <c>System.Random</c>, no time (the same fixed-seed discipline as
/// <see cref="RockMeshFactory"/> and <see cref="Starfield"/>).</para>
/// <para><b>Coordinate framing:</b> local forward = <c>+Z</c>. The wide rim
/// rings (radius <c>rimRadius</c>) sit at <c>z = ±depth/2</c>, the bright
/// pinch at <c>z = 0</c>.</para>
/// </remarks>
public static class WarpMeshFactory
{
    /// <summary>
    /// Native LOD tiers (alephgeo.cpp:239-251, <c>CreateAlephGeo</c>): each is
    /// a <c>(countDepth, countSection)</c> pair. The finest (8,32) is used up
    /// close, falling to (2,8) far away, selected in
    /// <see cref="WarpRenderer"/> by the warp's projected on-screen size.
    /// </summary>
    public static readonly (int Depth, int Section)[] LodTiers =
        [(8, 32), (4, 16), (2, 16), (2, 8)];

    /// <summary>Finest-tier per-horn depth rings and circumferential sections
    /// — the default mesh (native 8 × 32).</summary>
    public const int DefaultCountDepth = 8;
    public const int DefaultCountSection = 32;

    /// <summary>Unit rim radius; the renderer scales the whole mesh by the
    /// real per-instance <c>DataWarpIGC.Radius</c> ×
    /// <see cref="WarpRenderer.VisualScaleMultiplier"/>, so rim diameter =
    /// Radius × scale in world units.</summary>
    public const float DefaultRimRadius = 1f;

    /// <summary>Axial length of the symmetric hourglass in mesh units (scaled
    /// with the radius by the renderer's uniform scale): the two wide rims sit
    /// at z = ±depth/2 and the pinch point at z = 0.</summary>
    public const float DefaultDepth = 1.6f;

    /// <summary>Taper exponent of the symmetric hourglass: radius ∝ s^Taper
    /// where s∈[0,1] is the axial fraction measured from the center outward
    /// (s=0 at the pinch, s=1 at either wide rim). &gt;1 makes the funnel
    /// narrow toward the center pinch faster. (Independent of the native UV
    /// parameter <c>r</c>, which is the ring fraction, not the geometric
    /// radius.)</summary>
    public const float DefaultTaper = 2.2f;

    /// <summary>
    /// Native brightness band (alephgeo.cpp:82): <c>bright = 2·r·(1−r)</c> —
    /// dark at the pinch (r=0) and the rim (r=1), peaking at 0.5 on the
    /// mid-band ring (r=0.5). This is the "dark centre / bright ring band /
    /// dark rim" the draining look needs.
    /// </summary>
    public static float Brightness(float r) => 2f * r * (1f - r);

    /// <summary>
    /// Native spiral-twist u-coordinate (alephgeo.cpp:134,137):
    /// <c>u = 4·section/countSection + 5·(1−(1−r)²)</c>. The texture tiles 4
    /// times around the circumference; the <c>5·(1−(1−r)²)</c> term advances
    /// the angular phase toward the rim, bending the tiled pattern into the
    /// funnel's spiral arms.
    /// </summary>
    public static float TwistU(int section, int countSection, float r) =>
        4f * section / countSection + 5f * (1f - MathF.Pow(1f - r, 2f));

    /// <summary>
    /// Native inward-flow v-coordinate (alephgeo.cpp:128,133):
    /// <c>v = mod(time/10,1) + r</c>. <c>time/10 mod 1</c> cycles every 10s;
    /// adding it to each ring's base <c>r</c> slides the texture window along
    /// the funnel every frame — the "water draining" motion.
    /// </summary>
    public static float FlowV(float time, float r) => Mod(time / 10f, 1f) + r;

    /// <summary>Floating-point modulo in [0, m) (native <c>mod</c>).</summary>
    private static float Mod(float a, float m)
    {
        float r = a % m;
        return r < 0f ? r + m : r;
    }

    /// <summary>
    /// Build the double-horn textured warp mesh.
    /// </summary>
    /// <param name="countDepth">Per-horn ring bands along the depth axis (≥ 1).
    /// The full hourglass has <c>2·countDepth+1</c> rings (two mirrored horns
    /// sharing the centre pinch ring).</param>
    /// <param name="countSection">Circumferential sections (≥ 3). Each ring
    /// carries <c>countSection+1</c> vertices, the extra one closing the loop
    /// (native's duplicated seam vertex, alephgeo.cpp:84).</param>
    /// <param name="rimRadius">Outer rim radius in mesh units, at BOTH ends
    /// (default 1).</param>
    /// <param name="depth">Axial hourglass length (rims at z=±depth/2, pinch at
    /// z=0).</param>
    /// <param name="taper">Taper exponent (&gt;0).</param>
    public static WarpMesh Generate(
        int countDepth = DefaultCountDepth,
        int countSection = DefaultCountSection,
        float rimRadius = DefaultRimRadius,
        float depth = DefaultDepth,
        float taper = DefaultTaper)
    {
        if (countDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(countDepth), "countDepth must be >= 1");
        if (countSection < 3)
            throw new ArgumentOutOfRangeException(nameof(countSection), "countSection must be >= 3");
        if (rimRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(rimRadius), "rimRadius must be > 0");
        if (depth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(depth), "depth must be > 0");

        int ringCount = 2 * countDepth + 1;   // symmetric hourglass (two horns + shared pinch)
        int sections = countSection + 1;      // native's duplicated seam vertex
        int vertexCount = ringCount * sections;
        var pos = new float[vertexCount * 3];
        var col = new float[vertexCount * 4];
        var uv = new float[vertexCount * 2];

        for (int ring = 0; ring < ringCount; ring++)
        {
            // t ∈ [0,1] across the whole symmetric hourglass: 0 = one wide rim,
            // 0.5 = the center pinch, 1 = the other wide rim.
            float t = ringCount == 1 ? 0f : (float)ring / (ringCount - 1);

            // s = axial fraction from the CENTER outward (0 pinch → 1 rim), and
            // r = per-horn native radial parameter. Native's r == its geometric
            // radius (alephgeo.cpp:80 radius = indexDepth/countDepth), so the
            // wide rim carries r=1 and the pinch carries r=0 (task scope:
            // "r from the pinch (0) to the rim (1)").
            float s = MathF.Abs(2f * t - 1f);
            float r = s;

            float radius = rimRadius * MathF.Pow(MathF.Max(0f, s), taper);
            float z = depth * (t - 0.5f);
            float bright = Brightness(r);

            for (int j = 0; j < sections; j++)
            {
                // No static helix twist: native's plain circle angle
                // (alephgeo.cpp:85). The swirl comes from the animated UVs.
                float angle = MathF.Tau * j / countSection;
                float x = radius * MathF.Cos(angle);
                float y = radius * MathF.Sin(angle);

                int vi = ring * sections + j;
                pos[vi * 3 + 0] = x;
                pos[vi * 3 + 1] = y;
                pos[vi * 3 + 2] = z;

                // Native brightness band, grayscale (r=g=b=bright, alpha 1) —
                // dark pinch & rim, bright mid-band. The texture is
                // Gouraud-modulated by this in the renderer.
                col[vi * 4 + 0] = bright;
                col[vi * 4 + 1] = bright;
                col[vi * 4 + 2] = bright;
                col[vi * 4 + 3] = 1f;

                // Base UVs (time = 0); the renderer adds the inward v-flow per
                // frame (FlowV) so the texture drains continuously.
                uv[vi * 2 + 0] = TwistU(j, countSection, r);
                uv[vi * 2 + 1] = r;
            }
        }

        // Grid triangulation between consecutive rings: each pair of adjacent
        // vertices on ring i and the same pair on ring i+1 forms a quad → 2
        // triangles. Winding is arbitrary here: WarpRenderer renders with
        // back-face culling disabled (a hollow funnel must show its inner wall).
        var indices = new List<int>(2 * countDepth * countSection * 6);
        for (int ring = 0; ring < ringCount - 1; ring++)
        {
            int row0 = ring * sections;
            int row1 = (ring + 1) * sections;
            for (int j = 0; j < countSection; j++)
            {
                int j2 = j + 1;               // the duplicated seam vertex
                int v00 = row0 + j;
                int v01 = row0 + j2;
                int v10 = row1 + j;
                int v11 = row1 + j2;
                indices.Add(v00); indices.Add(v10); indices.Add(v11);
                indices.Add(v00); indices.Add(v11); indices.Add(v01);
            }
        }

        return new WarpMesh(pos, col, uv, indices.ToArray());
    }
}

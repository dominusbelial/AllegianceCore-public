namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The output of <see cref="TreasureMeshFactory"/>: plain vertex positions +
/// per-vertex RGBA colors + a triangle-list index buffer, all as primitive
/// float/int arrays so the producer is pure logic with NO MonoGame types on
/// its surface (the same discipline as <see cref="WarpMesh"/> /
/// <see cref="RockMesh"/> / <c>Flight/FlightInput.cs</c>). Keeps the gem
/// geometry unit-testable without a graphics device.
/// </summary>
/// <remarks>
/// A treasure is a self-lit glowing pickup, so no per-vertex normal is needed
/// (colors are carried per-vertex, exactly like <see cref="WarpMesh"/>), and
/// the renderer draws it with back-face culling disabled so winding never
/// matters.
/// </remarks>
public sealed record TreasureMesh(
    float[] Positions,   // x,y,z per vertex (local Y is the gem's long axis)
    float[] Colors,      // r,g,b,a per vertex, each in [0,1]
    int[] Indices)       // triangle list (multiple of 3)
{
    /// <summary>Number of vertices (Positions.Length / 3).</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles (Indices.Length / 3).</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Generates a deterministic, procedurally-built treasure placeholder mesh — a
/// gold faceted gem (an octahedron: two pointed cones base-to-base), the
/// classic "lootable pickup" shape. Built as pure fixed-seed logic with no
/// <c>System.Random</c>, no time, and only <c>MathF</c> arithmetic, so the
/// same input yields a byte-identical mesh across calls and runs — the same
/// determinism contract as <see cref="WarpMeshFactory"/> /
/// <see cref="RockMeshFactory"/>.
/// </summary>
/// <remarks>
/// <para><b>Why a placeholder at all.</b> Native represents a treasure with a
/// <em>decided at runtime</em> visual, not a fixed <c>ModelName</c>: a
/// buyable (part/development) treasure uses that buyable's own model
/// (<c>treasureIGC.cpp:125-126</c> <c>name = m_buyable-&gt;GetModelName()</c>),
/// a flag treasure uses the <c>"biosflag"</c> decal or <c>"artifact"</c>
/// model (<c>treasureIGC.cpp:128-149</c>), and a cash/powerup treasure uses
/// the fixed <c>"acs14"</c> model (<c>treasureIGC.cpp:153-155</c>). The
/// <c>DataTreasureIGC</c> wire record carries <b>no ModelName</b> of its own
/// (igc.h:2517-2529) — the visual is derived from <c>treasureCode</c> +
/// <c>treasureID</c> — and none of those native art assets is present in this
/// checkout's artwork tree (the same two-tree situation ships/warps/asteroids
/// were already in). So, exactly as P289's station placeholder framed it, this
/// is a procedural stand-in for a model we cannot load — deliberately not
/// over-designed (a single shared gold gem), not a faithful reproduction of
/// native's per-code art.</para>
/// <para><b>Scale.</b> The gem has a <b>unit</b> outer radius (the girdle
/// half-width and half-height are both ≤ 1), and the renderer scales it by the
/// real native treasure pickup radius <c>SetRadius(5.0f)</c>
/// (<c>treasureIGC.cpp:193</c>, the only size the decoded data/native carries —
/// its comment <c>//NYI -- needs to be gotten from the DB</c> confirms 5.0 is
/// the fixed collision radius, not a per-record field). So one shared unit
/// mesh serves every treasure, scaled per instance — the same "one shared
/// mesh" scope cut accepted for warps/asteroids.</para>
/// <para><b>Shape &amp; colour.</b> Elongated octahedron (pointed top apex,
/// flat girdle square at mid-height, pointed bottom tip), per-vertex gold
/// gradient — bright near-white gold at the top apex fading through mid gold at
/// the girdle to a darker bronze at the bottom tip — so the faceted gem reads
/// as a solid metallic pickup even under vertex colours alone (no lighting).
/// The renderer adds a slow spin about Y (<c>native</c> imparts a random
/// rotation to a part/powerup treasure, <c>treasureIGC.cpp:183-190</c>), giving
/// the single mesh a little life.</para>
/// </remarks>
public static class TreasureMeshFactory
{
    /// <summary>Nominal unit outer radius of the gem (girdle radius =
    /// <see cref="GirdleRadius"/>, half-height = 1). The renderer scales by the
    /// native pickup radius.</summary>
    public const float DefaultRadius = 1f;

    /// <summary>Radius of the equatorial (girdle) square, in mesh units.</summary>
    public const float GirdleRadius = 0.7f;

    /// <summary>Half-height of the gem (top apex at +1, bottom apex at −1).</summary>
    public const float HalfHeight = 1f;

    // Gold gradient stops (self-lit, no normals). Bright near-white gold at the
    // top apex, mid gold at the girdle, darker bronze at the bottom tip.
    private const float TopR = 1.00f, TopG = 0.955f, TopB = 0.680f;
    private const float GirdleR = 0.880f, GirdleG = 0.640f, GirdleB = 0.200f;
    private const float BottomR = 0.470f, BottomG = 0.330f, BottomB = 0.095f;

    /// <summary>
    /// Build the gem mesh. Constant parameter set (no arguments needed) — kept
    /// parameterless so the shared mesh is trivially reusable; determinism and
    /// the unit scale are the contract.
    /// </summary>
    public static TreasureMesh Generate()
    {
        // 6 vertices: top apex (4), girdle square p0..p3 (0..3), bottom apex (5).
        float h = HalfHeight, r = GirdleRadius;
        var pos = new float[6 * 3]
        {
            // girdle square (y = 0), CCW around +Y as seen from above
             r, 0f,  0f,     // p0
             0f, 0f,  r,     // p1
            -r, 0f,  0f,     // p2
             0f, 0f, -r,     // p3
             0f,  h,  0f,    // top apex (4)
             0f, -h,  0f,    // bottom apex (5)
        };

        var col = new float[6 * 4]
        {
            GirdleR, GirdleG, GirdleB, 1f,   // p0 girdle
            GirdleR, GirdleG, GirdleB, 1f,   // p1 girdle
            GirdleR, GirdleG, GirdleB, 1f,   // p2 girdle
            GirdleR, GirdleG, GirdleB, 1f,   // p3 girdle
            TopR,    TopG,    TopB,    1f,   // top apex
            BottomR, BottomG, BottomB, 1f,   // bottom apex
        };

        // 8 triangles: top cone (apex 4 -> each girdle edge) + bottom cone
        // (apex 5 -> each girdle edge, wound opposite). The renderer disables
        // culling so winding here is informational only.
        int[] indices =
        {
            4, 0, 1,    4, 1, 2,    4, 2, 3,    4, 3, 0,   // top cone
            5, 0, 3,    5, 3, 2,    5, 2, 1,    5, 1, 0,   // bottom cone
        };

        return new TreasureMesh(pos, col, indices);
    }
}

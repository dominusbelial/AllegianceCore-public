namespace Allegiance.Core.Collision;

/// <summary>
/// A 3-vector, mirroring the game's <c>Vector</c>. Defined locally so the
/// collider model does not depend on the fixed-byte payload structs.
/// </summary>
public readonly record struct Vector3(float X, float Y, float Z);

/// <summary>
/// One convex hull. Mirrors <c>SingleHull</c> (<c>src/_Utility/HitTest.cpp:287</c>)
/// but keeps the on-disk adjacency as <b>vertex indices</b> in file order — the
/// C++ rewrites them into pointers at load time and appends a NULL terminator
/// per vertex that is <b>not</b> on disk; neither transformation alters the
/// grammar, so neither is reproduced here.
/// </summary>
public sealed class CvhHull
{
    /// <summary>Hull-local origin, file value (the C++ negates <c>x</c> at load as a render-time transform).</summary>
    public required Vector3 Center { get; init; }

    /// <summary>Declared vertex count <c>nVertices</c>; also the adjacency index upper bound.</summary>
    public required int VertexCount { get; init; }

    /// <summary>Declared adjacency count <c>nAdjacencies</c>. Must equal the sum of every vertex's adjacency count.</summary>
    public required int AdjacencyCount { get; init; }

    /// <summary>Vertices in file order; <see cref="VertexCount"/> of them.</summary>
    public required IReadOnlyList<CvhVertex> Vertices { get; init; }
}

/// <summary>
/// A single vertex plus its adjacency indices. Mirrors <c>ConvexVertex</c>
/// (<c>src/_Utility/HitTest.cpp:226</c>) but stores indices rather than the
/// C++ runtime pointer list.
/// </summary>
public readonly record struct CvhVertex(Vector3 Position, int[] AdjacentIndices);

/// <summary>
/// A named frame (mount point): <c>FrameDataUTL</c>. Three physical lines per
/// frame after the hulls: a name line, then a 3-float position line, then a
/// 3-float forward line. File values kept as-is; the C++ flips signs as a
/// render transform (<c>src/_Utility/HitTest.cpp:796-801</c>).
/// </summary>
public readonly record struct CvhFrame(string Name, Vector3 Position, Vector3 Forward);

/// <summary>
/// A parsed <c>.cvh</c> collision-hull file. See the reader and the README for
/// the grammar.
/// </summary>
public sealed class CvhFile
{
    /// <summary>Original bounding-sphere radius; always &gt; 0 and encloses every vertex.</summary>
    public required float Radius { get; init; }

    /// <summary>Ellipse equation terms; the hull lives inside <c>(x/ee.X)²+(y/ee.Y)²+(z/ee.Z)² ≤ 1</c>.</summary>
    public required Vector3 EllipseEquation { get; init; }

    /// <summary>Ellipse radius multiplier (≥ radius).</summary>
    public required float EllipseRadiusMultiplier { get; init; }

    /// <summary>Hulls in file order.</summary>
    public required IReadOnlyList<CvhHull> Hulls { get; init; }

    /// <summary>Named frames (mount points), possibly empty.</summary>
    public required IReadOnlyList<CvhFrame> Frames { get; init; }
}

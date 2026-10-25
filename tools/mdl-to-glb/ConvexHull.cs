using System.Numerics;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P823 — a self-contained 3-D convex hull (quickhull) used by
/// <see cref="CvhWriter.Generate"/> in place of native's external
/// <c>qhull</c> call (<c>cvh/main.cpp:46-53</c> shells out to
/// <c>qhull -o -Tv</c>). Everything downstream of the hull — used-vertex
/// marking, adjacency construction, radius/ellipse fitting — is the faithful
/// port of <c>qh2cvh</c> in <see cref="CvhWriter"/>; only the hull triangles
/// themselves come from here.
/// </summary>
/// <remarks>
/// Output: triangle soup (3 indices per face) over the input points, oriented
/// outward, no shared-vertex deduplication beyond what the input has. Points
/// on a face plane (within the tolerance) are <b>not</b> hull vertices — the
/// same merge behaviour <c>qhull</c> applies, and the same premise native's
/// used-vertex rule (<c>cUsed &gt; 2</c>, <c>cvh/main.cpp:302-329</c>) relies on.
/// </remarks>
public static class ConvexHull
{
    /// <summary>Plane-distance tolerance, relative to the point-cloud extent.</summary>
    private const double RelativeEpsilon = 1e-7;

    public static int[] Compute(Vector3[] points)
    {
        if (points.Length < 4)
            throw new InvalidDataException($"convex hull needs at least 4 points (got {points.Length})");

        double extent = Extent(points);
        if (extent <= 0)
            throw new InvalidDataException("convex hull: all points coincide");
        double eps = RelativeEpsilon * extent;

        // -- initial tetrahedron from extreme points --------------------------
        int i0 = Extreme(points, p => p.X, -1);
        int i1 = Extreme(points, p => p.X, +1);
        if (i0 == i1) { i0 = Extreme(points, p => p.Y, -1); i1 = Extreme(points, p => p.Y, +1); }
        if (i0 == i1) { i0 = Extreme(points, p => p.Z, -1); i1 = Extreme(points, p => p.Z, +1); }
        if (i0 == i1)
            throw new InvalidDataException("convex hull: points are collinear/degenerate");

        int i2 = FarthestFromLine(points, i0, i1, eps);
        if (i2 < 0)
            throw new InvalidDataException("convex hull: points are collinear");
        int i3 = FarthestFromPlane(points, i0, i1, i2, eps);
        if (i3 < 0)
            throw new InvalidDataException("convex hull: points are coplanar");

        var faces = new List<Face>
        {
            MakeFace(points, i0, i1, i2, points[i3]),
            MakeFace(points, i0, i1, i3, points[i2]),
            MakeFace(points, i0, i2, i3, points[i1]),
            MakeFace(points, i1, i2, i3, points[i0]),
        };

        // -- assign points to faces (conflict lists) --------------------------
        var unassigned = new List<int>();
        for (int i = 0; i < points.Length; i++)
            if (i != i0 && i != i1 && i != i2 && i != i3)
                unassigned.Add(i);
        AssignOutside(points, faces, unassigned);

        // -- quickhull main loop ----------------------------------------------
        while (true)
        {
            int fi = -1;
            for (int f = 0; f < faces.Count; f++)
                if (faces[f].Alive && faces[f].Outside.Count > 0) { fi = f; break; }
            if (fi < 0) break;

            var face = faces[fi];
            int far = face.Outside[0];
            double farDist = -1;
            foreach (var pi in face.Outside)
            {
                double d = SignedPlaneDistance(points, face, points[pi]);
                if (d > farDist) { farDist = d; far = pi; }
            }

            // Visible faces = those whose plane puts `far` strictly outside
            // (faces are outward-oriented → signed distance > eps).
            var visible = faces.Where(f => f.Alive &&
                SignedPlaneDistance(points, f, points[far]) > eps).ToList();

            // Horizon edges: directed edges of the visible set whose reverse
            // is not also an edge of the visible set.
            var seenEdges = new HashSet<(int, int)>();
            foreach (var f in visible)
                for (int e = 0; e < 3; e++)
                {
                    int a = e == 0 ? f.A : e == 1 ? f.B : f.C;
                    int b = e == 0 ? f.B : e == 1 ? f.C : f.A;
                    seenEdges.Add((a, b));
                }

            var orphans = new List<int>();
            foreach (var f in visible)
            {
                f.Alive = false;
                orphans.AddRange(f.Outside);
                f.Outside.Clear();
            }

            var newFaces = new List<Face>();
            foreach (var f in visible)
                for (int e = 0; e < 3; e++)
                {
                    int a = e == 0 ? f.A : e == 1 ? f.B : f.C;
                    int b = e == 0 ? f.B : e == 1 ? f.C : f.A;
                    if (seenEdges.Contains((b, a))) continue;   // interior edge of the visible region
                    var nf = MakeFace(points, a, b, far, InteriorPoint(points, faces));
                    newFaces.Add(nf);
                }
            faces.AddRange(newFaces);
            AssignOutside(points, faces, orphans);
        }

        // -- collect + orientation fix -----------------------------------------
        var triangles = new List<int>();
        var centroid = Centroid(points);
        foreach (var f in faces)
        {
            if (!f.Alive) continue;
            if (!Outward(points, f.A, f.B, f.C, centroid))
                (f.B, f.C) = (f.C, f.B);
            triangles.Add(f.A); triangles.Add(f.B); triangles.Add(f.C);
        }
        if (triangles.Count == 0)
            throw new InvalidDataException("convex hull: produced no faces (degenerate input)");
        return [.. triangles];
    }

    private sealed class Face
    {
        public int A, B, C;
        public bool Alive = true;
        public List<int> Outside = [];
    }

    private static Vector3 InteriorPoint(Vector3[] points, List<Face> faces)
    {
        // Any strictly-inside reference works; use the centroid of alive face
        // vertices (the tetrahedron's interior early on, the hull's later).
        Vector3 sum = default;
        int n = 0;
        foreach (var f in faces)
        {
            if (!f.Alive) continue;
            sum += points[f.A] + points[f.B] + points[f.C];
            n += 3;
        }
        return n > 0 ? sum / n : default;
    }

    private static Vector3 Centroid(Vector3[] points)
    {
        Vector3 sum = default;
        foreach (var p in points) sum += p;
        return sum / points.Length;
    }

    private static Face MakeFace(Vector3[] points, int a, int b, int c, Vector3 against)
    {
        var f = new Face { A = a, B = b, C = c };
        if (!Outward(points, a, b, c, against))
            (f.B, f.C) = (f.C, f.B);
        return f;
    }

    private static bool Outward(Vector3[] points, int a, int b, int c, Vector3 against)
    {
        var n = Normal(points, a, b, c);
        return Vector3.Dot(n, against - points[a]) < 0;
    }

    private static Vector3 Normal(Vector3[] points, int a, int b, int c) =>
        Vector3.Cross(points[b] - points[a], points[c] - points[a]);

    private static double PlaneDistance(Vector3[] points, int a, int b, int c, Vector3 p)
    {
        var n = Normal(points, a, b, c);
        double len = n.Length();
        if (len < 1e-12) return 0;
        return Math.Abs(Vector3.Dot(n, p - points[a])) / len;
    }

    private static void AssignOutside(Vector3[] points, List<Face> faces, List<int> candidates)
    {
        double eps = RelativeEpsilon * Extent(points);
        foreach (var pi in candidates)
        {
            Face? best = null;
            double bestDist = 0;
            foreach (var f in faces)
            {
                if (!f.Alive) continue;
                double d = SignedPlaneDistance(points, f, points[pi]);
                if (d > eps && d > bestDist) { bestDist = d; best = f; }
            }
            best?.Outside.Add(pi);      // on-plane points belong to no face → dropped
        }
    }

    /// <summary>SIGNED distance: positive when the point is on the face's
    /// outside (faces are kept outward-oriented).</summary>
    private static double SignedPlaneDistance(Vector3[] points, Face f, Vector3 p)
    {
        var n = Normal(points, f.A, f.B, f.C);
        double len = n.Length();
        if (len < 1e-12) return 0;
        return Vector3.Dot(n, p - points[f.A]) / len;
    }

    private static int Extreme(Vector3[] points, Func<Vector3, float> coord, int sign)
    {
        int best = 0;
        float bestVal = coord(points[0]);
        for (int i = 1; i < points.Length; i++)
        {
            float v = coord(points[i]);
            if (sign > 0 ? v > bestVal : v < bestVal) { bestVal = v; best = i; }
        }
        return best;
    }

    private static int FarthestFromLine(Vector3[] points, int a, int b, double eps)
    {
        var dir = Vector3.Normalize(points[b] - points[a]);
        int best = -1;
        double bestDist = eps;
        for (int i = 0; i < points.Length; i++)
        {
            var rel = points[i] - points[a];
            double d = (rel - dir * Vector3.Dot(rel, dir)).Length();
            if (d > bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static int FarthestFromPlane(Vector3[] points, int a, int b, int c, double eps)
    {
        int best = -1;
        double bestDist = eps;
        for (int i = 0; i < points.Length; i++)
        {
            if (i == a || i == b || i == c) continue;
            double d = PlaneDistance(points, a, b, c, points[i]);
            if (d > bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static double Extent(Vector3[] points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return (max - min).Length();
    }
}

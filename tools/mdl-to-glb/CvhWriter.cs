using System.Globalization;
using System.Text;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P823 — writes a text <c>.cvh</c> collision file the port's
/// <see cref="Allegiance.Core.Collision.CvhReader"/> (and native
/// <c>HitTest::Load</c>, <c>_Utility/HitTest.cpp:639-807</c>) parses.
///
/// <para>Two paths:</para>
/// <list type="number">
/// <item><b>Sidecar round trip</b> — <see cref="WriteExact"/>: the sidecar's
/// parsed <c>.cvh</c> records are written back verbatim in the native
/// generator's printf formats (cvh/main.cpp: header <c>%16.8f</c> ×5 (:533-536),
/// hull line <c>%5d%5d</c> + <c>%18.8f</c> ×3 (:544), vertex line
/// <c>%18.8f</c> ×3 + <c>%5d</c> (:549-570), adjacency <c>%5d</c> per id,
/// frames copied verbatim from the authoring <c>.dat</c> (dat2cvh, :756-781 —
/// three physical lines: name, position, forward, <c>%20.10f</c> ×3)). The
/// sidecar stores float32 (what <c>CvhReader</c> itself parses), so decimals
/// beyond the float precision can shift by one digit in the last place — every
/// such byte is enumerated in the P823 report; the <b>parsed</b> values are
/// bit-identical.</item>
/// <item><b>Generated (new art)</b> — <see cref="Generate"/>: convex hull(s)
/// from the GLB meshes the way native <c>cvh/main.cpp</c> builds them, plus
/// frames from the hardpoint nodes, all mapped into the <c>.cvh</c> file space
/// (the <c>.mdl</c> space mirrored in z, the P812 finding).</item>
/// </list>
/// </summary>
public static class CvhWriter
{
    /// <summary>Whether generated hulls use the native minimum-volume ellipse
    /// search over all vertex triples (cvh/main.cpp:452-531). The search is
    /// O(n³) gaussian solves; above this vertex count only the sphere and
    /// bounding-box ellipse attempts run (documented simplification).</summary>
    public const int EllipseTripleSearchCap = 150;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // -- exact sidecar round trip -------------------------------------------

    /// <summary>Format the sidecar's collision record back to <c>.cvh</c>
    /// text. Validates the record first — the writer never emits a file the
    /// port's own reader would reject.</summary>
    public static string WriteExact(CollisionSidecar c) => Write(c, generated: false);

    private static string Write(CollisionSidecar c, bool generated)
    {
        Validate(c);

        var sb = new StringBuilder(4096);
        Header(sb, c.Radius, c.EllipseEquation[0], c.EllipseEquation[1], c.EllipseEquation[2],
            c.EllipseRadiusMultiplier);
        sb.Append(c.Hulls.Count.ToString(Inv)).Append('\n');
        foreach (var hull in c.Hulls)
        {
            sb.Append(hull.Vertices.Count.ToString(Inv).PadLeft(5));
            sb.Append(hull.Vertices.Sum(v => v.Adj.Length).ToString(Inv).PadLeft(5));
            sb.Append(F(hull.Center[0], 18, 8)).Append(F(hull.Center[1], 18, 8)).Append(F(hull.Center[2], 18, 8));
            sb.Append('\n');
            foreach (var v in hull.Vertices)
            {
                sb.Append(F(v.P[0], 18, 8)).Append(F(v.P[1], 18, 8)).Append(F(v.P[2], 18, 8));
                sb.Append(v.Adj.Length.ToString(Inv).PadLeft(5)).Append('\n');
                foreach (var a in v.Adj)
                    sb.Append(a.ToString(Inv).PadLeft(5));
                sb.Append('\n');
            }
        }
        foreach (var f in c.Frames)
        {
            sb.Append(f.Name).Append('\n');
            AppendVec(sb, f.Position);
            AppendVec(sb, f.Forward);
        }
        return sb.ToString();
    }

    private static void Header(StringBuilder sb, float radius, float ex, float ey, float ez, float erm) =>
        sb.Append(F(radius, 16, 8)).Append(F(ex, 16, 8)).Append(F(ey, 16, 8))
          .Append(F(ez, 16, 8)).Append(F(erm, 16, 8)).Append('\n');

    private static void AppendVec(StringBuilder sb, float[] v) =>
        sb.Append(F(v[0], 20, 10)).Append(F(v[1], 20, 10)).Append(F(v[2], 20, 10)).Append('\n');

    /// <summary>printf-style fixed formatting: decimals, then right-align to
    /// the native width (cvh/main.cpp's %16.8f / %18.8f / %20.10f).</summary>
    internal static string F(double v, int width, int decimals) =>
        v.ToString($"F{decimals}", Inv).PadLeft(width);

    /// <summary>The same structural checks <see cref="Allegiance.Core.Collision.CvhReader"/>
    /// enforces on read ("four oracles"), applied before write: declared counts
    /// match, every adjacency index in range, adjacency sums consistent,
    /// finite floats, radius &gt; 0.</summary>
    public static void Validate(CollisionSidecar c)
    {
        if (!IsFinite(c.Radius) || c.Radius <= 0f)
            throw new InvalidDataException($"collision radius must be finite and > 0 (got {c.Radius})");
        if (c.EllipseEquation.Any(e => !IsFinite(e)) || !IsFinite(c.EllipseRadiusMultiplier))
            throw new InvalidDataException("collision ellipse equation/multiplier must be finite");

        foreach (var (hull, hi) in c.Hulls.Select((h, i) => (h, i)))
        {
            if (hull.Vertices.Count == 0)
                throw new InvalidDataException($"hull {hi}: no vertices");
            if (hull.Center.Any(e => !IsFinite(e)))
                throw new InvalidDataException($"hull {hi}: non-finite centre");
            foreach (var (v, vi) in hull.Vertices.Select((v, i) => (v, i)))
            {
                if (v.P.Any(e => !IsFinite(e)))
                    throw new InvalidDataException($"hull {hi} vertex {vi}: non-finite position");
                if (v.Adj.Length == 0)
                    throw new InvalidDataException($"hull {hi} vertex {vi}: adjacency count must be positive");
                foreach (var a in v.Adj)
                    if (a < 0 || a >= hull.Vertices.Count)
                        throw new InvalidDataException(
                            $"hull {hi} vertex {vi}: adjacency index {a} out of range [0,{hull.Vertices.Count})");
            }
        }
        foreach (var f in c.Frames)
            if (f.Position.Any(e => !IsFinite(e)) || f.Forward.Any(e => !IsFinite(e)))
                throw new InvalidDataException($"frame '{f.Name}': non-finite position/forward");
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    // -- generated (new art) --------------------------------------------------

    /// <summary>
    /// Generate the <c>.cvh</c> for new art: one convex hull around the whole
    /// model (native's <c>-extract</c> shape) built the way native
    /// <c>cvh/main.cpp</c> builds it, plus frames from the hardpoint nodes.
    /// All coordinates are mapped into the <c>.cvh</c> file space — the
    /// <c>.mdl</c> space mirrored in z, <c>(x,y,z) → (x,y,−z)</c> on positions
    /// AND forwards (the P812 finding, verified on fig30/bom04/ss27).
    /// </summary>
    public static string Generate(GlbExportModel model)
    {
        // 1. Collect + mirror the mesh vertices (native flattens the whole
        // model mesh: x2pl, cvh/main.cpp:96-140) and drop exact duplicates
        // (qhull also merges them).
        var seen = new HashSet<(float, float, float)>();
        var pts = new List<System.Numerics.Vector3>();
        foreach (var mesh in model.Meshes)
            for (int i = 0; i + 2 < mesh.Positions.Length; i += 3)
            {
                var p = new System.Numerics.Vector3(
                    mesh.Positions[i], mesh.Positions[i + 1], -mesh.Positions[i + 2]);   // mirror-z
                if (seen.Add((p.X, p.Y, p.Z)))
                    pts.Add(p);
            }

        // 2. Convex hull (qhull substitution — see ConvexHull.cs; native shells
        // out to the qhull binary, cvh/main.cpp:46-53).
        var triangles = ConvexHull.Compute([.. pts]);

        // 3. Used vertices, adjacency, radius, ellipse — the qh2cvh walk
        // (cvh/main.cpp:227-620), documented line by line below.
        int nFaces = triangles.Length / 3;
        var cUsed = new int[pts.Count];
        foreach (var idx in triangles)
            cUsed[idx]++;

        // Used = referenced by MORE THAN TWO faces; the rest are singular and
        // dropped (cvh/main.cpp:302-329 — cUsed>2).
        var usedIds = new List<int>();
        var idOf = new int[pts.Count];
        Array.Fill(idOf, -1);
        int singularUseCount = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            if (cUsed[i] > 2) { idOf[i] = usedIds.Count; usedIds.Add(i); }
            else singularUseCount += cUsed[i];
        }
        if (usedIds.Count < 4)
            throw new InvalidDataException(
                $"mesh has only {usedIds.Count} hull vertices — cannot generate a convex hull (degenerate geometry)");

        // Radius = the most distant used vertex from the ORIGIN
        // (cvh/main.cpp:348-360) — not from the hull centre.
        float radius = 0f;
        var xyzMin = new float[3] { 0, 0, 0 };
        var xyzMax = new float[3] { 0, 0, 0 };
        bool first = true;
        foreach (var vi in usedIds)
        {
            var p = pts[vi];
            radius = MathF.Max(radius, p.LengthSquared());
            for (int j = 0; j < 3; j++)
            {
                float c = j == 0 ? p.X : j == 1 ? p.Y : p.Z;
                if (first) { xyzMin[j] = c; xyzMax[j] = c; }
                else { xyzMin[j] = MathF.Min(xyzMin[j], c); xyzMax[j] = MathF.Max(xyzMax[j], c); }
            }
            first = false;
        }
        radius = MathF.Sqrt(radius);

        // Ellipse: attempt 1 = sphere; attempt 2 = bounding-box ellipse scaled
        // by the containment fudge rMax (cvh/main.cpp:411-451); attempt 3 =
        // minimum-volume ellipse over all vertex triples with the reduce()
        // gaussian (cvh/main.cpp:88-155, 452-531) — capped (see
        // EllipseTripleSearchCap) and with native's 1.01 containment fudge
        // (cvh/main.cpp:520).
        double aBest = radius, bBest = radius, cBest = radius;
        double vBest = (double)aBest * bBest * cBest;
        {
            double a = Math.Max(Math.Abs(xyzMin[0]), Math.Abs(xyzMax[0]));
            double b = Math.Max(Math.Abs(xyzMin[1]), Math.Abs(xyzMax[1]));
            double c = Math.Max(Math.Abs(xyzMin[2]), Math.Abs(xyzMax[2]));
            if (a > 0 && b > 0 && c > 0)
            {
                double rMax = 0.0;
                foreach (var vi in usedIds)
                {
                    var p = pts[vi];
                    double r = (double)p.X * p.X / (a * a) + (double)p.Y * p.Y / (b * b) + (double)p.Z * p.Z / (c * c);
                    rMax = Math.Max(rMax, r);
                }
                rMax = Math.Sqrt(rMax);
                a *= rMax; b *= rMax; c *= rMax;
                double v = a * b * c;
                if (v < vBest) { vBest = v; aBest = a; bBest = b; cBest = c; }
            }
        }
        if (usedIds.Count <= EllipseTripleSearchCap)
        {
            for (int i = 0; i < usedIds.Count - 2; i++)
                for (int j = i + 1; j < usedIds.Count - 1; j++)
                    for (int k = j + 1; k < usedIds.Count; k++)
                    {
                        var p0 = pts[usedIds[i]]; var p1 = pts[usedIds[j]]; var p2 = pts[usedIds[k]];
                        // reduce() over rows [x² y² z² | 1] (cvh/main.cpp:494-507).
                        double[,] g =
                        {
                            { (double)p0.X * p0.X, (double)p0.Y * p0.Y, (double)p0.Z * p0.Z, 1.0 },
                            { (double)p1.X * p1.X, (double)p1.Y * p1.Y, (double)p1.Z * p1.Z, 1.0 },
                            { (double)p2.X * p2.X, (double)p2.Y * p2.Y, (double)p2.Z * p2.Z, 1.0 },
                        };
                        if (!Reduce(g) || g[0, 3] <= 1e-4 || g[1, 3] <= 1e-4 || g[2, 3] <= 1e-4)
                            continue;
                        double a = Math.Sqrt(1.0 / g[0, 3]);
                        double b = Math.Sqrt(1.0 / g[1, 3]);
                        double c = Math.Sqrt(1.0 / g[2, 3]);
                        double v = a * b * c;
                        if (v >= vBest) continue;
                        bool contains = true;
                        foreach (var vi in usedIds)
                        {
                            var p = pts[vi];
                            double x = p.X / a, y = p.Y / b, z = p.Z / c;
                            if (x * x + y * y + z * z > 1.01) { contains = false; break; }   // "a bit of fudging"
                        }
                        if (contains) { vBest = v; aBest = a; bBest = b; cBest = c; }
                    }
        }

        // Declared adjacency count: Euler's formula 2·(V + F − 2) with native's
        // singular-vertex correction (cvh/main.cpp:541).
        int nAdjacencies = 2 * (usedIds.Count + nFaces - singularUseCount - 2);

        var sb = new StringBuilder(4096);
        double erm = Math.Max(Math.Max(Math.Max(aBest, bBest), cBest), 1e-6) / radius;
        Header(sb, radius, (float)aBest, (float)bBest, (float)cBest, (float)erm);

        sb.Append('1').Append('\n');                                  // one hull (c_oneHull, cvh/main.cpp:538-539)
        sb.Append(usedIds.Count.ToString(Inv).PadLeft(5));
        sb.Append(nAdjacencies.ToString(Inv).PadLeft(5));
        sb.Append(F(0.5 * (xyzMin[0] + xyzMax[0]), 18, 8))
          .Append(F(0.5 * (xyzMin[1] + xyzMax[1]), 18, 8))
          .Append(F(0.5 * (xyzMin[2] + xyzMax[2]), 18, 8));           // centre = bbox centre (cvh/main.cpp:542-544)
        sb.Append('\n');

        // Adjacency per used vertex: scan the faces in order and collect the
        // other two corners, deduped, only corners that are themselves used
        // (cvh/main.cpp:549-570 + addAdjacent :180-193).
        int writtenAdjacency = 0;
        foreach (var vi in usedIds)
        {
            var p = pts[vi];
            var adj = new List<int>();
            for (int t = 0; t < nFaces; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                int other1, other2;
                if (a == vi) { other1 = b; other2 = c; }
                else if (b == vi) { other1 = a; other2 = c; }
                else if (c == vi) { other1 = a; other2 = b; }
                else continue;
                TryAddAdjacent(adj, other1, cUsed, idOf);
                TryAddAdjacent(adj, other2, cUsed, idOf);
            }
            if (adj.Count == 0)
                throw new InvalidDataException($"hull vertex {vi}: no adjacencies (degenerate hull)");
            sb.Append(F(p.X, 18, 8)).Append(F(p.Y, 18, 8)).Append(F(p.Z, 18, 8));
            sb.Append(adj.Count.ToString(Inv).PadLeft(5)).Append('\n');
            foreach (var a in adj)
                sb.Append(a.ToString(Inv).PadLeft(5));
            sb.Append('\n');
            writtenAdjacency += adj.Count;
        }
        // The written file must satisfy the port reader's own oracle.
        if (writtenAdjacency != nAdjacencies)
            throw new InvalidDataException(
                $"generated hull adjacency sum {writtenAdjacency} != declared {nAdjacencies} (degenerate hull geometry)");

        // Frames: one per hardpoint mount, names synthesized from the
        // HP_<Kind>_<Index> nodes (sidecar-named mounts keep their native
        // names), positions/forwards mirrored into .cvh file space.
        foreach (var m in model.Mounts)
        {
            var name = NativeNameMapper.IsNativeName(m.NativeName, m.NodeName)
                ? m.NativeName
                : NativeNameMapper.NativeNameFor(m.Kind, m.Index);
            sb.Append(name).Append('\n');
            AppendVec(sb, [m.Position[0], m.Position[1], -m.Position[2]]);
            AppendVec(sb, [m.Forward[0], m.Forward[1], -m.Forward[2]]);
        }
        return sb.ToString();
    }

    /// <summary>addAdjacent (cvh/main.cpp:180-193): only used vertices
    /// (cUsed &gt; 2) are recorded, deduped, as compacted used-IDs.</summary>
    private static void TryAddAdjacent(List<int> adj, int vertex, int[] cUsed, int[] idOf)
    {
        if (cUsed[vertex] <= 2) return;
        int id = idOf[vertex];
        if (!adj.Contains(id))
            adj.Add(id);
    }

    /// <summary>reduce() — the 3×4 gaussian elimination of cvh/main.cpp:88-155,
    /// solved with partial pivoting exactly as written there.</summary>
    private static bool Reduce(double[,] g)
    {
        if (Math.Abs(g[1, 0]) > Math.Abs(g[0, 0])) SwapRows(g, 0, 1);
        if (Math.Abs(g[2, 0]) > Math.Abs(g[0, 0])) SwapRows(g, 0, 2);
        if (Math.Abs(g[0, 0]) < 1.0e-4) return false;

        Subtract(g, g[1, 0] / g[0, 0], 1, 0);
        g[1, 0] = 0.0;
        Subtract(g, g[2, 0] / g[0, 0], 2, 0);
        g[2, 0] = 0.0;

        if (Math.Abs(g[2, 1]) > Math.Abs(g[1, 1])) SwapRows(g, 1, 2);
        if (Math.Abs(g[1, 1]) < 1.0e-4) return false;

        Subtract(g, g[2, 1] / g[1, 1], 2, 1);
        g[2, 1] = 0.0;

        if (Math.Abs(g[2, 2]) < 1.0e-4) return false;
        g[2, 3] /= g[2, 2];
        g[2, 2] = 1.0;

        Subtract(g, g[1, 2], 1, 2);
        g[1, 2] = 0.0;
        Subtract(g, g[0, 2], 0, 2);
        g[0, 2] = 0.0;

        g[1, 3] /= g[1, 1];
        g[1, 1] = 1.0;
        Subtract(g, g[0, 1], 0, 1);

        g[0, 3] /= g[0, 0];
        g[0, 0] = 1.0;
        return true;
    }

    private static void SwapRows(double[,] g, int i, int j)
    {
        for (int k = 0; k < 4; k++)
            (g[i, k], g[j, k]) = (g[j, k], g[i, k]);
    }

    private static void Subtract(double[,] g, double f, int i, int j)
    {
        for (int k = 0; k < 4; k++)
            g[i, k] -= g[j, k] * f;
    }
}

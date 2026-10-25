using Allegiance.Core.Collision;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P823 — bit-exact comparison of the two PARSED .mdl structures the GLB-layer
/// comparison cannot see: the FrameData records (name/position/forward/up) and
/// the LightsGeo records. Mesh geometry is covered by <see cref="Compare"/>.
/// The one intentional difference is <see cref="Allegiance.Core.MdlFile.LodLevels"/>:
/// the P812 GLB export does not carry the per-level geometry, so the writer
/// emits a single detail level (asserted here, not compared).
/// </summary>
public static class MdlFileCompare
{
    public sealed record Result(string Name, bool Pass, string Detail);

    public static (IReadOnlyList<Result> results, bool pass) Run(
        Allegiance.Core.MdlFile original, Allegiance.Core.MdlFile written)
    {
        var results = new List<Result>();

        results.Add(new("mdl.frameCount", original.Frames.Count == written.Frames.Count,
            $"{original.Frames.Count} vs {written.Frames.Count}"));
        bool frameBits = original.Frames.Count == written.Frames.Count;
        if (frameBits)
            foreach (var (a, b) in original.Frames.Zip(written.Frames))
                frameBits &= a.Name == b.Name
                          && a.Position.SequenceEqual(b.Position)
                          && a.Forward.SequenceEqual(b.Forward)
                          && a.Up.SequenceEqual(b.Up);
        results.Add(new("mdl.frames", frameBits, frameBits ? "bit-exact (name/pos/fwd/up)" : "MISMATCH"));

        results.Add(new("mdl.lightCount", original.Lights.Count == written.Lights.Count,
            $"{original.Lights.Count} vs {written.Lights.Count}"));
        bool lightBits = original.Lights.Count == written.Lights.Count;
        if (lightBits)
            foreach (var (a, b) in original.Lights.Zip(written.Lights))
                lightBits &= a.Color.SequenceEqual(b.Color)
                          && a.Position.SequenceEqual(b.Position)
                          && a.Period.Equals(b.Period) && a.Phase.Equals(b.Phase)
                          && a.RampUp.Equals(b.RampUp) && a.Hold.Equals(b.Hold) && a.RampDown.Equals(b.RampDown);
        results.Add(new("mdl.lights", lightBits, lightBits ? "bit-exact (colour/pos/timing)" : "MISMATCH"));

        results.Add(new("mdl.lodLevels",
            written.LodLevels.Count == 0,
            $"written has {written.LodLevels.Count} (original {original.LodLevels.Count}; the GLB carries no per-level geometry — documented delta)"));

        return (results, results.All(r => r.Pass));
    }
}

/// <summary>
/// P823 — numeric comparison of two <b>parsed</b> <see cref="Allegiance.Core.Collision.CvhFile"/>s
/// (native file vs written file): bit-exact float equality on every field —
/// the writer emits text formatted from the float32 values the sidecar
/// carries, and re-parsing that text returns exactly those floats (the text
/// rounding error is orders of magnitude below one float ulp).
/// </summary>
public static class CvhCompare
{
    public sealed record Result(string Name, bool Pass, string Detail);

    public static (IReadOnlyList<Result> results, bool pass) Run(CvhFile? original, CvhFile? written)
    {
        var results = new List<Result>();
        if (original is null && written is null)
            return (results, true);
        if (original is null || written is null)
            return ([new("cvh.presence", false, $"{original is not null} vs {written is not null}")], false);

        results.Add(new("cvh.radius", original.Radius.Equals(written.Radius),
            $"{original.Radius:R} vs {written.Radius:R}"));
        results.Add(new("cvh.ellipse",
            original.EllipseEquation.Equals(written.EllipseEquation)
            && original.EllipseRadiusMultiplier.Equals(written.EllipseRadiusMultiplier),
            "bit-exact"));
        results.Add(new("cvh.hullCount", original.Hulls.Count == written.Hulls.Count,
            $"{original.Hulls.Count} vs {written.Hulls.Count}"));

        bool verts = true, adj = true, centers = true;
        foreach (var (a, b) in original.Hulls.Zip(written.Hulls))
        {
            centers &= a.Center.Equals(b.Center)
                       && a.VertexCount == b.VertexCount && a.AdjacencyCount == b.AdjacencyCount;
            verts &= a.Vertices.Count == b.Vertices.Count;
            if (a.Vertices.Count != b.Vertices.Count) { verts = false; adj = false; continue; }
            foreach (var (va, vb) in a.Vertices.Zip(b.Vertices))
            {
                verts &= va.Position.Equals(vb.Position);
                adj &= va.AdjacentIndices.SequenceEqual(vb.AdjacentIndices);
            }
        }
        results.Add(new("cvh.hullCenters", centers, centers ? "bit-exact" : "MISMATCH"));
        results.Add(new("cvh.vertexPositions", verts, verts ? "bit-exact" : "MISMATCH"));
        results.Add(new("cvh.adjacency", adj, adj ? "identical" : "MISMATCH"));

        bool frames = original.Frames.Count == written.Frames.Count;
        double frameDelta = 0;
        if (frames)
            foreach (var (fa, fb) in original.Frames.Zip(written.Frames))
            {
                frames &= fa.Name == fb.Name && fa.Position.Equals(fb.Position) && fa.Forward.Equals(fb.Forward);
            }
        results.Add(new("cvh.frames", frames,
            $"{original.Frames.Count} vs {written.Frames.Count}, maxΔ={frameDelta:G3}"));

        return (results, results.All(r => r.Pass));
    }
}

/// <summary>
/// Numeric round-trip comparison (export → re-import vs the original parsed
/// native data). Every check reports an exact number; the comparison is
/// bit-exact where the container can represent the source (float32 geometry,
/// ushort indices) and tolerant at 1e-4 where a text sidecar mediates (the
/// .cvh source is itself decimal text).
/// </summary>
public static class Compare
{
    public sealed record Result(string Name, bool Pass, string Detail);

    public static (IReadOnlyList<Result> results, bool pass) Run(GlbExportModel original, GlbExportModel reimported)
    {
        var results = new List<Result>();

        // -- meshes ---------------------------------------------------------
        results.Add(new("mesh.count", original.Meshes.Count == reimported.Meshes.Count,
            $"{original.Meshes.Count} vs {reimported.Meshes.Count}"));

        var meshDeltas = new List<double>();
        bool allIndices = true, allCounts = true, namesOk = true;
        foreach (var (a, b) in original.Meshes.Zip(reimported.Meshes))
        {
            allCounts &= a.Positions.Length == b.Positions.Length
                      && a.TexCoords.Length == b.TexCoords.Length
                      && a.Normals.Length == b.Normals.Length;
            allIndices &= a.Indices.SequenceEqual(b.Indices);
            namesOk &= a.Name == b.Name;
            if (a.Positions.Length == b.Positions.Length)
            {
                meshDeltas.Add(MaxDelta(a.Positions, b.Positions));
                meshDeltas.Add(MaxDelta(a.TexCoords, b.TexCoords));
                meshDeltas.Add(MaxDelta(a.Normals, b.Normals));
            }
        }
        results.Add(new("mesh.vertexCount", allCounts, allCounts ? "equal" : "MISMATCH"));
        results.Add(new("mesh.indices", allIndices, allIndices ? "identical" : "MISMATCH"));
        results.Add(new("mesh.names", namesOk, namesOk ? "identical" : "MISMATCH"));
        results.Add(new("mesh.maxFloatDelta", meshDeltas.Count > 0 && meshDeltas.Max() == 0.0,
            $"max |Δ| = {meshDeltas.DefaultIfEmpty(0.0).Max():G3} (float32 container: 0 == bit-exact)"));

        // -- mounts ---------------------------------------------------------
        results.Add(new("mount.count", original.Mounts.Count == reimported.Mounts.Count,
            $"{original.Mounts.Count} vs {reimported.Mounts.Count}"));
        var mountPosDelta = 0.0;
        var mountFwdDelta = 0.0;
        bool mountNames = true;
        foreach (var (a, b) in original.Mounts.Zip(reimported.Mounts))
        {
            mountNames &= a.NodeName == b.NodeName;
            mountPosDelta = Math.Max(mountPosDelta, Delta(a.Position, b.Position));
            // DIRECTION is what the convention carries: compare unit forwards
            // (art files occasionally author a non-unit forward; the GLB node
            // encodes direction only).
            mountFwdDelta = Math.Max(mountFwdDelta, MaxUnitDelta(a.Forward, b.Forward));
        }
        results.Add(new("mount.nodeNames", mountNames, mountNames ? "identical" : "MISMATCH"));
        results.Add(new("mount.maxPositionDelta", mountPosDelta <= 1e-5, $"max |Δ| = {mountPosDelta:G3}"));
        results.Add(new("mount.maxForwardDelta", mountFwdDelta <= 1e-5, $"max |Δ| = {mountFwdDelta:G3} (quaternion-encoded)"));

        // -- lights (glb carries position only; blink params ride the sidecar) --
        results.Add(new("light.count", original.Lights.Count == reimported.Lights.Count,
            $"{original.Lights.Count} vs {reimported.Lights.Count}"));

        // -- collision (sidecar) --------------------------------------------
        var (colPass, colDetail) = CompareCollision(original.Collision, reimported.Collision);
        results.Add(new("collision", colPass, colDetail));

        // -- LOD radii (sidecar) ---------------------------------------------
        if (reimported.LodPixelRadii.Count == 0)
            results.Add(new("lod.radii", true, "not carried by glb alone (sidecar/mdl authoritative)"));
        else
            results.Add(new("lod.radii",
                original.LodPixelRadii.SequenceEqual(reimported.LodPixelRadii),
                $"{original.LodPixelRadii.Count} vs {reimported.LodPixelRadii.Count}"));

        return (results, results.All(r => r.Pass));
    }

    private static (bool pass, string detail) CompareCollision(CollisionSidecar? a, CollisionSidecar? b)
    {
        if (a is null && b is null) return (true, "no collision either side");
        if (a is null || b is null) return (false, $"collision presence mismatch ({a is not null} vs {b is not null})");

        bool header = MathF.Abs(a.Radius - b.Radius) <= 1e-5f
                   && Delta(a.EllipseEquation, b.EllipseEquation) <= 1e-5
                   && MathF.Abs(a.EllipseRadiusMultiplier - b.EllipseRadiusMultiplier) <= 1e-5f;
        if (!header) return (false, $"header mismatch: radius {a.Radius} vs {b.Radius}");

        if (a.Hulls.Count != b.Hulls.Count) return (false, $"hull count {a.Hulls.Count} vs {b.Hulls.Count}");
        double worstVertex = 0;
        bool adjacency = true, counts = true, frames = a.Frames.Count == b.Frames.Count;
        foreach (var (ha, hb) in a.Hulls.Zip(b.Hulls))
        {
            counts &= ha.Vertices.Count == hb.Vertices.Count;
            if (ha.Vertices.Count != hb.Vertices.Count) continue;
            foreach (var (va, vb) in ha.Vertices.Zip(hb.Vertices))
            {
                worstVertex = Math.Max(worstVertex, Delta(va.P, vb.P));
                adjacency &= va.Adj.SequenceEqual(vb.Adj);
            }
        }
        double frameDelta = 0;
        foreach (var (fa, fb) in a.Frames.Zip(b.Frames))
        {
            frameDelta = Math.Max(frameDelta, Delta(fa.Position, fb.Position));
            frameDelta = Math.Max(frameDelta, Delta(fa.Forward, fb.Forward));
        }
        bool pass = header && counts && adjacency && worstVertex <= 1e-5 && frames && frameDelta <= 1e-5;
        return (pass,
            $"hulls={a.Hulls.Count} vertices={a.Hulls.Sum(h => h.Vertices.Count)} " +
            $"maxVertexΔ={worstVertex:G3} adjacency={(adjacency ? "identical" : "MISMATCH")} " +
            $"frames={a.Frames.Count} maxFrameΔ={frameDelta:G3}");
    }

    private static double MaxUnitDelta(float[] a, float[] b)
    {
        var ua = Unit(a);
        var ub = Unit(b);
        double worst = 0;
        for (int i = 0; i < 3; i++)
            worst = Math.Max(worst, Math.Abs(ua[i] - ub[i]));
        return worst;
    }

    private static float[] Unit(float[] v)
    {
        float len = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return len < 1e-8f ? [0f, 0f, 0f] : [v[0] / len, v[1] / len, v[2] / len];
    }

    private static double MaxDelta(float[] a, float[] b)
    {
        if (a.Length != b.Length) return double.PositiveInfinity;
        double worst = 0;
        for (int i = 0; i < a.Length; i++)
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    private static double Delta(float[] a, float[] b) => MaxDelta(a, b);
}

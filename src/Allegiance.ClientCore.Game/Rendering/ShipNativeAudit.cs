using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P607 — the pure decision behind the startup NATIVE-ship-resolution audit:
/// given every hull's (id, name, ModelName-stem) and a per-stem resolver
/// predicate (the caller's native <c>.mdl</c> lookup), return the hulls whose
/// native art does NOT resolve. Extracted pure so the audit's report (N hulls,
/// 0 unresolved, or a list) is unit-testable headless — the caller wires its
/// real native renderer (<c>NativeShipRenderer.MeshFor</c>) as the predicate.
/// <para>P800 — extends the audit beyond "the mesh resolves" (the only thing
/// the P607 audit checked) with the DEGENERACY checks that can still leave a
/// hull invisible while the startup line says all is well: an empty or
/// zero-triangle mesh, a non-finite/non-positive mesh or vertex radius (the
/// native scale <c>(0.5·Length)/meshRadius</c> and the previews divide by
/// both), a non-positive hull Length, and an unresolvable texture stem.
/// <see cref="CollectDefects"/> is pure and drives both the startup audit and
/// the real-core regression test.</para>
/// </summary>
public static class ShipNativeAudit
{
    /// <summary>A hull that failed native art resolution, by name.</summary>
    public readonly record struct Unresolved(short HullId, string Name, string? Stem);

    /// <summary>P800 — one hull's measured native-art health, gathered by the
    /// caller from the real core + parsed meshes (GPU-free fields only).</summary>
    public readonly record struct HullHealth(
        short HullId, string Name, string? Stem,
        bool MeshResolved, int TriangleCount,
        float MeshRadius, float MaxVertexRadius,
        float HullLength, bool TextureStemResolved);

    /// <summary>
    /// Return the hulls (among <paramref name="hulls"/>) whose native mesh
    /// cannot be resolved by <paramref name="canResolve"/>. A null/blank stem
    /// counts as unresolved (no art to draw).
    /// </summary>
    public static IReadOnlyList<Unresolved> CollectUnresolved(
        IEnumerable<(short HullId, string Name, string? Stem)> hulls,
        System.Func<string?, bool> canResolve)
    {
        var list = new List<Unresolved>();
        foreach (var h in hulls)
        {
            var stem = h.Stem?.Trim();
            if (string.IsNullOrEmpty(stem) || !canResolve(stem))
                list.Add(new Unresolved(h.HullId, h.Name, h.Stem));
        }
        return list;
    }

    /// <summary>
    /// P800 — the per-hull defect strings for the extended audit: every way a
    /// hull can fail to RENDER even though its <c>.mdl</c> resolves (the blind
    /// spot the P607 line covered only as "mesh non-null"). Empty → the hull is
    /// healthy. Categories: no-stem / mesh-unresolved / empty-mesh /
    /// bad-mesh-radius / bad-vertex-radius / bad-length / no-texture-stem.
    /// </summary>
    public static IReadOnlyList<string> CollectDefects(IEnumerable<HullHealth> hulls)
    {
        var list = new List<string>();
        foreach (var h in hulls)
        {
            var stem = h.Stem?.Trim();
            if (string.IsNullOrEmpty(stem)) { list.Add($"{h.HullId} '{h.Name}' no-stem"); continue; }
            if (!h.MeshResolved) { list.Add($"{h.HullId} '{h.Name}' mesh-unresolved ({stem})"); continue; }
            if (h.TriangleCount <= 0) list.Add($"{h.HullId} '{h.Name}' empty-mesh ({stem})");
            if (!float.IsFinite(h.MeshRadius) || h.MeshRadius <= 0f)
                list.Add($"{h.HullId} '{h.Name}' bad-mesh-radius {h.MeshRadius} ({stem})");
            if (!float.IsFinite(h.MaxVertexRadius) || h.MaxVertexRadius <= 0f)
                list.Add($"{h.HullId} '{h.Name}' bad-vertex-radius {h.MaxVertexRadius} ({stem})");
            if (!float.IsFinite(h.HullLength) || h.HullLength <= 0f)
                list.Add($"{h.HullId} '{h.Name}' bad-length {h.HullLength}");
            if (!h.TextureStemResolved)
                list.Add($"{h.HullId} '{h.Name}' no-texture-stem ({stem})");
        }
        return list;
    }
}

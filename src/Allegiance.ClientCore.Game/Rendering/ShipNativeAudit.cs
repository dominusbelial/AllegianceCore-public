using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P607 — the pure decision behind the startup NATIVE-ship-resolution audit:
/// given every hull's (id, name, ModelName-stem) and a per-stem resolver
/// predicate (the caller's native <c>.mdl</c> lookup), return the hulls whose
/// native art does NOT resolve. Extracted pure so the audit's report (N hulls,
/// 0 unresolved, or a list) is unit-testable headless — the caller wires its
/// real native renderer (<c>NativeShipRenderer.MeshFor</c>) as the predicate.
/// </summary>
public static class ShipNativeAudit
{
    /// <summary>A hull that failed native art resolution, by name.</summary>
    public readonly record struct Unresolved(short HullId, string Name, string? Stem);

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
}

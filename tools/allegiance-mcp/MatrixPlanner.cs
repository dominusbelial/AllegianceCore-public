namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P432 — the pure planning half of <c>run_hull_matrix</c>: expand a caller's
/// <c>hulls × weapons × sizes</c> into the cartesian product of real
/// (hull, weapon, size) combinations, resolving every name through the same
/// real <see cref="Catalog"/> the spawn tools use (exact-over-substring,
/// ambiguity/no-match errors) and determining each combination's REAL
/// weapon/hull mount compatibility up front via
/// <c>DataHullTypeIGC.CanMountPart</c>.
///
/// This is deliberately free of any server: given a set of names it yields the
/// exact ordered list of combinations a run will execute, each either
/// <c>Error == null</c> (mount-compatible, runnable) or carrying a specific
/// compatibility error (the weapon's real part mask is covered by no hardpoint
/// of that real hull). The executor consumes this plan verbatim — so the
/// cartesian-product expansion and the resolution are provable with no wire.
/// </summary>
public static class MatrixPlanner
{
    /// <summary>One real combination the matrix will run — or, when
    /// <see cref="Error"/> is non-null, one that is rejected up front with a
    /// real, specific reason (weapon does not mount on hull).</summary>
    public readonly record struct MatrixCombo(
        string HullName,
        short HullId,
        string WeaponName,
        short WeaponPartId,
        int Size,
        string? Error);

    /// <summary>
    /// Expand <paramref name="hullQueries"/> × <paramref name="weaponQueries"/> ×
    /// <paramref name="sizes"/> into the ordered cartesian product of real
    /// combinations. Resolution is strict and reuse-only: hulls via
    /// <see cref="Catalog.ResolveHull"/>, weapons via
    /// <see cref="Catalog.ResolveWeapon"/> (which also enforces the part is a
    /// real weapon), and mount compatibility via the decoded core's real
    /// capability masks (<see cref="Catalog.RepresentativeCore"/> +
    /// <c>CanMountPart</c>). Combos iterate hull-outer, then weapon, then size —
    /// a stable, caller-visible order so a fast light fighter and a slow heavy
    /// hull can be compared at the same size.
    /// </summary>
    /// <exception cref="ArgumentException">Any hull or weapon name fails the real
    /// resolution (no match / ambiguous / not-a-weapon), or any size &lt; 1. A
    /// whole-matrix error — thrown before anything touches the wire.</exception>
    public static List<MatrixCombo> Plan(
        IReadOnlyList<string> hullQueries,
        IReadOnlyList<string> weaponQueries,
        IReadOnlyList<int> sizes)
    {
        if (hullQueries is null || hullQueries.Count == 0)
            throw new ArgumentException("run_hull_matrix: `hulls` must name at least one real hull (name or substring)");
        if (sizes is null || sizes.Count == 0)
            throw new ArgumentException("run_hull_matrix: `sizes` must contain at least one positive per-side count");
        foreach (var s in sizes)
            if (s < 1)
                throw new ArgumentException($"run_hull_matrix: `sizes` entries must be ≥ 1 (got {s})");

        // Resolve every hull/weapon ONCE up front (strict: any failure aborts the
        // whole plan before the executor does any live work).
        var hulls = hullQueries.Select(q => Catalog.ResolveHull(q)).ToList();
        var weapons = weaponQueries is { Count: > 0 }
            ? weaponQueries.Select(q => Catalog.ResolveWeapon(q)).ToList()
            : new List<Catalog.Part> { Catalog.DefaultWeaponPart() };

        var core = Catalog.RepresentativeCore();

        var combos = new List<MatrixCombo>();
        foreach (var hull in hulls)
        {
            var hullData = core.HullTypes[hull.Id];
            foreach (var weapon in weapons)
            {
                var weaponData = core.PartTypes.TryGetValue(weapon.Id, out var wd) ? wd : default;
                // The real mount-compatibility test (faithful
                // IhullTypeIGC::CanMount): a weapon fits iff at least one of the
                // hull's hardpoints covers the weapon's part mask.
                bool fits = weaponData.PartId != 0 && hullData.CanMountPart(in weaponData);
                string? error = fits
                    ? null
                    : $"weapon '{weapon.Name}' (part id {weapon.Id}) does not mount on hull '{hull.Name}' "
                      + $"(hull id {hull.Id}): no hardpoint part mask accepts that weapon's part mask";
                foreach (var size in sizes)
                    combos.Add(new MatrixCombo(hull.Name, hull.Id, weapon.Name, weapon.Id, size, error));
            }
        }
        return combos;
    }
}

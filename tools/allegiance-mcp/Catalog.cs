using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P426 — real static-core catalog + name resolution. This is the same real
/// <c>PCore001.igc</c> static core <c>tools/vizfight</c> already decodes
/// (<see cref="IgcReader"/>/<see cref="StaticCore"/>), resolved by the same
/// substring rules the caller discovers against. It is deliberately standalone
/// (no reference back into vizfight) so the MCP server owns its own copy —
/// mirroring how vizfight itself copies these lookups rather than sharing.
/// </summary>
internal static class Catalog
{
    /// <summary>The real artwork corpus root (same env knob + default as vizfight
    /// and the live tests). Loads the SAME <c>PCore001.igc</c> the running server
    /// uses, so a name that resolves here is a hull/part id the server's own
    /// static core should also carry.</summary>
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly Lazy<StaticCore> Core = new(() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"))));

    public static StaticCore RepresentativeCore() => Core.Value;

    // ── Hull resolution (mirror of vizfight's HullNameMap + substring match) ──

    /// <summary>A resolved hull: its real id + display name.</summary>
    public readonly record struct Hull(short Id, string Name);

    /// <summary>
    /// Resolve a hull by exact name or case-insensitive substring across the
    /// real corpus. Policies that match how a human names a hull:
    ///  • exact-name hits win over substring-only hits (so "Adv Miner" selects
    ///    the Adv Miner family even though "Adv Miner [SR]" also contains it);
    ///  • multiple records that share one NAME string (same-named variants —
    ///    the corpus has many, e.g. ten "Adv Miner" hull ids) are one hull: the
    ///    lowest-HullId representative is chosen;
    ///  • zero hits or several DISTINCT names throw a real, identifying error.
    /// Never a silent no-op.
    /// </summary>
    public static Hull ResolveHull(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("spawn_bot: `hull` must be a non-empty hull name or substring");

        var core = RepresentativeCore();
        var all = core.HullTypes.Values
            .Where(h => h.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                     || h.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0)
            throw new ArgumentException($"spawn_bot: no hull matches '{query}' (known hulls: {HullSummary(core)})");
        var exact = all.Where(h => h.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : all;
        var distinct = pool.Select(h => h.Name).Distinct().ToList();
        if (distinct.Count > 1)
            throw new ArgumentException(
                $"spawn_bot: hull '{query}' is ambiguous; it matches several distinct hulls: "
                + string.Join(", ", distinct.Select(n => $"'{n}'"))
                + ". Pass a more specific name.");
        var chosen = pool.OrderBy(h => h.HullId).First();
        return new Hull(chosen.HullId, chosen.Name);
    }

    private static string HullSummary(StaticCore core)
    {
        var names = core.HullTypes.Values.OrderBy(h => h.HullId).Select(h => h.Name).Distinct().ToList();
        if (names.Count > 40) names = names.Take(40).ToList();
        return string.Join(", ", names) + (names.Count >= 40 ? ", …" : "");
    }

    // ── Sector (cluster) resolution ──

    /// <summary>A resolved sector (cluster): its real id + display name.</summary>
    public readonly record struct Sector(short Id, string Name);

    /// <summary>
    /// Resolve a sector by its real numeric id (a decimal-string) or by exact /
    /// case-insensitive substring of its NAME, across the LIVE server map's
    /// clusters. Unlike hulls/parts the map is NOT part of the static
    /// PCore001.igc core — it is server-generated per mission and delivered to
    /// every peer as EXPORT OT_cluster / CLUSTERINFO — so the corpus is the
    /// connected session's real <paramref name="clusters"/> dictionary (the
    /// probe's, which is populated by the join-burst exports). The resolution
    /// policy is otherwise identical to <see cref="ResolveHull"/>: a numeric id
    /// hits its cluster directly, exact-name hits win over substring-only,
    /// same-named variants collapse to the lowest-id representative, and zero
    /// matches / several distinct names throw a real, identifying error. Never
    /// a silent no-op.
    /// </summary>
    public static Sector ResolveSector(string query, IReadOnlyDictionary<short, DataClusterIGC> clusters)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("spawn_bot: `target_sector` must be a non-empty sector id or name");
        if (clusters is null || clusters.Count == 0)
            throw new ArgumentException("spawn_bot: no sector map available yet (the server hasn't sent its clusters) — cannot resolve `target_sector`");

        // Numeric id form: a direct ClusterId hit wins outright.
        if (short.TryParse(query, out var id) && clusters.TryGetValue(id, out var byId))
            return new Sector(id, byId.Name);

        var all = clusters.Values
            .Where(c => c.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                     || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0)
            throw new ArgumentException($"spawn_bot: no sector matches '{query}' (known sectors: {SectorSummary(clusters)})");
        var exact = all.Where(c => c.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : all;
        var distinct = pool.Select(c => c.Name).Distinct().ToList();
        if (distinct.Count > 1)
            throw new ArgumentException(
                $"spawn_bot: sector '{query}' is ambiguous; it matches several distinct sectors: "
                + string.Join(", ", distinct.Select(n => $"'{n}'"))
                + ". Pass a more specific name or the numeric sector id.");
        var chosen = pool.OrderBy(c => c.ClusterId).First();
        return new Sector(chosen.ClusterId, chosen.Name);
    }

    private static string SectorSummary(IReadOnlyDictionary<short, DataClusterIGC> clusters)
    {
        var names = clusters.Values.OrderBy(c => c.ClusterId).Select(c => c.Name).Distinct().ToList();
        if (names.Count > 40) names = names.Take(40).ToList();
        return string.Join(", ", names) + (names.Count >= 40 ? ", …" : "");
    }

    // ── Part resolution ──

    /// <summary>A resolved part: real id + display name + equipment category.</summary>
    public readonly record struct Part(short Id, string Name, string Category);

    public static IReadOnlyList<Part> AllParts()
    {
        var core = RepresentativeCore();
        return core.PartTypes.Values
            .OrderBy(p => p.PartId)
            .Select(p => new Part(p.PartId, p.Name, p.EquipmentType.ToString()))
            .ToList();
    }

    /// <summary>Resolve one part name (exact or case-insensitive substring) with
    /// the same policies as <see cref="ResolveHull"/>: exact wins, same-named
    /// variants collapse to the lowest-id representative, zero matches and
    /// multiple distinct names both throw real, identifying errors.</summary>
    private static Part ResolvePart(string query, StaticCore core)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("spawn_bot: a part name must be non-empty");
        var all = core.PartTypes.Values
            .Where(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0)
            throw new ArgumentException($"spawn_bot: no part matches '{query}'");
        var exact = all.Where(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : all;
        var distinct = pool.Select(p => p.Name).Distinct().ToList();
        if (distinct.Count > 1)
            throw new ArgumentException(
                $"spawn_bot: part '{query}' is ambiguous; it matches several distinct parts: "
                + string.Join(", ", distinct.Select(n => $"'{n}'"))
                + ". Pass a more specific name.");
        var chosen = pool.OrderBy(p => p.PartId).First();
        return new Part(chosen.PartId, chosen.Name, chosen.EquipmentType.ToString());
    }

    /// <summary>Resolve a list of part names to the real <see cref="ExpandedPartData"/>
    /// wire loadout. Each name is validated; any failure throws a real identifying
    /// error before anything touches the wire.</summary>
    public static List<ExpandedPartData> ResolveParts(IEnumerable<string> names)
    {
        var core = RepresentativeCore();
        var seen = new HashSet<short>();
        var parts = new List<ExpandedPartData>();
        foreach (var n in names)
        {
            var p = ResolvePart(n, core);
            if (seen.Add(p.Id))
                parts.Add(new ExpandedPartData(p.Id, 1, 0, 255)); // amount 1, mount 0, fully mounted
        }
        return parts;
    }

    // ── Default loadout (mirror of vizfight's Fight + FullInventoryParts) ──

    /// <summary>The same fightable fighter + weapon pair vizfight selects: the
    /// first hull with energy/ammo + a fixed hardpoint, and the cheapest
    /// dispersion-0 weapon with real projectile power that hull can feed.</summary>
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static short? OuterWeaponPartId()
    {
        var core = RepresentativeCore();
        var (hull, weapon) = Fight.Value;
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon))
        {
            if (p.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId == weapon.ProjectileTypeId
                && w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                return p.PartId;
        }
        return null;
    }

    /// <summary>The real playable loadout vizfight equips (P420 full inventory:
    /// weapon + afterburner + cloak + shield). Used when the caller omits
    /// <c>parts</c> — never a fabricated loadout.</summary>
    public static List<ExpandedPartData> DefaultParts()
    {
        var core = RepresentativeCore();
        var parts = new List<ExpandedPartData>();
        if (OuterWeaponPartId() is { } w) parts.Add(new ExpandedPartData(w, 1, 0, 255));
        if (FirstPart(core, EquipmentType.Afterburner) is { } ab) parts.Add(new ExpandedPartData(ab, 1, 0, 255));
        if (FirstPart(core, EquipmentType.Cloak) is { } ck) parts.Add(new ExpandedPartData(ck, 1, 0, 255));
        if (FirstPart(core, EquipmentType.Shield) is { } sh) parts.Add(new ExpandedPartData(sh, 1, 0, 255));
        return parts;
    }

    private static short? FirstPart(StaticCore core, EquipmentType et) =>
        core.PartTypes.Values.Where(p => p.EquipmentType == et)
            .OrderBy(p => p.PartId).Select(p => p.PartId).Cast<short?>().FirstOrDefault();
}

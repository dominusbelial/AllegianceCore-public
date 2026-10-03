using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.Core.Content;

/// <summary>
/// P305 — turn <see cref="HullModelResolver.ReachableAssets"/> into a concrete,
/// spawn-ready roster for vizfight's <c>roster</c> mode: one entry per distinct
/// P287 ship asset (the real distinct ship models), each paired with a real
/// witness hull that resolves to that asset under its faction, plus a legible
/// faction-grouped grid placement.
///
/// <para><b>Why one entry per asset, not one per hull-stat.</b> The core has 447
/// hull-stat variants but only 72 real P287 ship models; most variants share one
/// of the 70 hull models under different names. <see cref="ReachableAssets"/>
/// already folds a hull's <c>(civ,class)</c> role down to the distinct asset, so
/// planning against it is exactly the "real distinct ship models only" the task
/// scopes to. <see cref="Plan"/> orders factions deterministically and picks, for
/// each asset, the real hull in the core whose <see cref="HullModelResolver.ResolveAsset(string,DataHullTypeIGC)"/>
/// returns that asset — preferring, since P350, the hull from the civ's own
/// dedicated <see cref="CivModelPrefixes"/> family when one exists for that role
/// (so a faction's roster never renders a cross-civ neighbour's ship), else
/// falling back to the first matching hull. The chosen hull is the concrete one
/// the spawned bot requests via the real <c>SendBuyLoadout</c> wire path.</para>
/// </summary>
public sealed record RosterEntry
{
    /// <summary>The P287 content-model asset name ("<c>&lt;civ&gt;-&lt;class&gt;</c>").</summary>
    public required string Asset { get; init; }
    /// <summary>Manifest civ key (e.g. "bios").</summary>
    public required string Civ { get; init; }
    /// <summary>The civ's class token within the asset name (e.g. "fighter").</summary>
    public required string CivClass { get; init; }
    /// <summary>A real PCore001 HullId whose hull resolves to <see cref="Asset"/> under <see cref="Civ"/>.</summary>
    public required short HullId { get; init; }
    /// <summary>The witness hull's native ModelName (mesh-backed role source).</summary>
    public required string ModelName { get; init; }
    /// <summary>The witness hull's display name.</summary>
    public required string DisplayName { get; init; }
    /// <summary>0-based index of this faction's column in the grid.</summary>
    public required int Column { get; init; }
    /// <summary>0-based row of this ship within its faction's column.</summary>
    public required int Row { get; init; }
    /// <summary>World-space grid X (column axis).</summary>
    public required float WorldX { get; init; }
    /// <summary>World-space grid Z (row axis; Y stays 0 — ships fly level).</summary>
    public required float WorldZ { get; init; }
}

/// <summary>Pure P305 roster-planning logic (headless-testable; no GraphicsDevice).</summary>
public static class RosterPlanner
{
    /// <summary>Deterministic faction display order, mapping each manifest civ
    /// key to its real PCore001 <c>CivilizationId</c> (the factions that have
    /// P287 roster art; Phoenix(4) is in <see cref="HullModelResolver.CivKeyById"/>
    /// since P510 but still has no phoenix-* slots, so its column lands in P515
    /// when that art exists). Larger rosters first so the
    /// widest faction columns sit at the outside of the grid.</summary>
    public static readonly (string Civ, short CivId)[] FactionOrder =
    [
        ("bios",  35),
        ("gt",     3),
        ("ic",    25),
        ("rix",  201),
        ("belters",101),
        ("dreg",   1),
        ("giga",  18),
        ("oh",     5),
        ("tf",     2),
    ];

    /// <summary>
    /// P350 — each playable faction's <em>dedicated</em> native ship-model
    /// <c>ModelName</c> prefix families (from the real PCore001 hull census).
    /// PCore001 hull records carry no <c>CivilizationId</c>, so a hull's civ is
    /// inferred from its model-name family: a faction's own ships share one
    /// consistent prefix (Bios's <c>quiz</c>, GT's <c>wc_gt</c>, …). The prefix
    /// families below were confirmed hull-by-hull from PCore001 (a hull-ID
    /// cluster where several classes share the prefix) and cross-checked
    /// against each civ's <c>TtbmBaseTechs</c> fingerprint in OT_civilization
    /// (the distinguishing tech byte of a family's <c>TtbmRequired</c> matches
    /// the owning civ's base-tech byte — e.g. <c>rain</c>/<c>tf</c>/<c>dn_fatesh</c>
    /// all carry the Technoflux 0x10 marker). Rixian and Belters are left with
    /// no entry: their ships reuse the shared generic <c>fig</c>/<c>bom</c>/<c>cap</c>
    /// meshes rather than a dedicated prefix, so no clean per-civ pick exists —
    /// their witnesses fall back to the historical first-match behaviour.
    /// <see cref="Plan"/> prefers a hull whose <c>ModelName</c> starts with the
    /// target civ's prefix when one resolves to the requested role, so a civ's
    /// roster never renders a cross-civ neighbour's ship as its own.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> CivModelPrefixes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["bios"]  = ["quiz"],
            ["gt"]    = ["wc_gt"],
            ["ic"]    = ["wc_ic"],
            ["giga"]  = ["wc_gc"],
            ["oh"]    = ["faohsh"],
            ["tf"]    = ["tf", "rain", "dn_fatesh"],   // Technoflux (rain/dn_fatesh share its tech marker)
            ["dreg"]  = ["dreg", "drg", "dn_fadrsh"],
            ["phoenix"] = ["dn_faphsh"],               // P510 — Phoenix's dedicated native family
            // rix, belters: no dedicated prefix family (shared generic meshes) → no entry.
        };

    /// <summary>Column spacing (world units) between faction columns — wide
    /// enough that the largest cap-ships don't visually collide.</summary>
    public const float ColumnSpacing = 260f;
    /// <summary>Row spacing (world units) between ships within a faction column.</summary>
    public const float RowSpacing = 110f;

    /// <summary>Plan the full roster: one entry per <see cref="HullModelResolver.ReachableAssets"/>
    /// asset, ordered by <see cref="FactionOrder"/> then class, with a grid slot per faction column.</summary>
    public static IReadOnlyList<RosterEntry> Plan(IEnumerable<DataHullTypeIGC> hulls)
    {
        var hullList = hulls.ToList();
        var reachable = HullModelResolver.ReachableAssets(hullList);
        var resolver = new HullModelResolver();

        var entries = new List<RosterEntry>();
        int cols = FactionOrder.Length;
        foreach (var (civ, _) in FactionOrder)
        {
            // Column assigned per faction: the index of this faction in FactionOrder.
            int column = Array.FindIndex(FactionOrder, f => f.Civ == civ);
            var assets = reachable
                .Where(kv => kv.Value.Civ == civ)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            int row = 0;
            foreach (var (asset, (_, cls)) in assets)
            {
                // P350 — prefer the civ's own dedicated hull family. The
                // historical "first hull that resolves" is order-dependent and
                // can land on a cross-civ neighbour's ship (e.g. Bios's fighter
                // used to resolve to GigaCorp's wc_gcfigx). When the civ has a
                // confirmed ModelName prefix block and any hull in it resolves
                // to this asset, use the lowest-HullId member of that block
                // (deterministic); otherwise fall back to the historical
                // first-match behaviour.
                var civPrefixes = CivModelPrefixes.TryGetValue(civ, out var pref) ? pref : Array.Empty<string>();
                DataHullTypeIGC witness = default;
                bool found = false;
                if (civPrefixes.Length > 0)
                {
                    foreach (var h in hullList.OrderBy(hh => hh.HullId))
                    {
                        if (resolver.ResolveAsset(civ, h) != asset) continue;
                        if (civPrefixes.Any(p => h.ModelName.Trim().StartsWith(p, StringComparison.Ordinal)))
                        { witness = h; found = true; break; }
                    }
                }
                if (!found)
                {
                    foreach (var h in hullList)
                    {
                        if (resolver.ResolveAsset(civ, h) == asset) { witness = h; found = true; break; }
                    }
                }
                if (!found)
                    throw new InvalidOperationException($"no real hull resolves to asset '{asset}' under civ '{civ}'");
                entries.Add(new RosterEntry
                {
                    Asset = asset,
                    Civ = civ,
                    CivClass = cls,
                    HullId = witness.HullId,
                    ModelName = witness.ModelName.Trim(),
                    DisplayName = witness.Name,
                    Column = column,
                    Row = row,
                    WorldX = (column - (cols - 1) / 2f) * ColumnSpacing,
                    WorldZ = row * RowSpacing,
                });
                row++;
            }
        }
        return entries;
    }
}

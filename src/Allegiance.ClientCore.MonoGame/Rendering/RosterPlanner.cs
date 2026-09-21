using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

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
/// each asset, the first real hull in the core whose <see cref="HullModelResolver.ResolveAsset(string,DataHullTypeIGC)"/>
/// returns that asset — the concrete hull the spawned bot requests via the real
/// <c>SendBuyLoadout</c> wire path.</para>
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
    /// key to its real PCore001 <c>CivilizationId</c> (the same 9 playable
    /// factions <see cref="HullModelResolver.CivKeyById"/> recognises; Phoenix
    /// has no P287 art so it is not a faction here). Larger rosters first so the
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
                DataHullTypeIGC witness = default;
                bool found = false;
                foreach (var h in hullList)
                {
                    if (resolver.ResolveAsset(civ, h) == asset) { witness = h; found = true; break; }
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

using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P305 — the roster planner that turns <see cref="HullModelResolver.ReachableAssets"/>
/// into a spawn-ready, one-bot-per-distinct-model plan: exactly one entry per
/// imported ship asset, every entry backed by a real hull that resolves to its
/// asset under its faction, and a non-overlapping faction-grouped grid.
/// </summary>
public class RosterPlannerTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    /// <summary>⭐ The roster is exactly one entry per imported ship asset —
    /// 72 real distinct models, not the 447 hull-stat variants.</summary>
    [Fact]
    public void Plan_yields_one_entry_per_distinct_ship_asset()
    {
        var plan = RosterPlanner.Plan(Catalog.Hulls);

        Assert.Equal(HullModelResolver.ShipAssets.Count, plan.Count);          // 72
        Assert.Equal(plan.Count, plan.Select(e => e.Asset).Distinct().Count()); // no dupes
        Assert.Equal(
            HullModelResolver.ShipAssets.OrderBy(x => x, StringComparer.Ordinal),
            plan.Select(e => e.Asset).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>Every entry is backed by a real hull that actually resolves to
    /// its asset under its civ — so the launched bot really renders that model.</summary>
    [Fact]
    public void Every_entry_hull_resolves_to_its_asset()
    {
        var resolver = new HullModelResolver();
        var byId = Catalog.Hulls.ToDictionary(h => h.HullId);
        foreach (var e in RosterPlanner.Plan(Catalog.Hulls))
        {
            Assert.True(byId.ContainsKey(e.HullId), $"entry {e.Asset} hull #{e.HullId} not in core");
            Assert.Equal(e.Asset, resolver.ResolveAsset(e.Civ, byId[e.HullId]));
        }
    }

    /// <summary>All 9 factions contribute; the civilisations cover the real ids.</summary>
    [Fact]
    public void Plan_covers_every_faction()
    {
        var plan = RosterPlanner.Plan(Catalog.Hulls);
        Assert.Equal(RosterPlanner.FactionOrder.Length, plan.Select(e => e.Civ).Distinct().Count());
        foreach (var (civ, civId) in RosterPlanner.FactionOrder)
        {
            Assert.Contains(plan, e => e.Civ == civ);
            Assert.Equal(civ, HullModelResolver.CivKeyByIdShort(civId));
        }
    }

    /// <summary>The grid slots are all distinct — a legible non-overlapping grid.</summary>
    [Fact]
    public void Grid_positions_are_unique_per_slot()
    {
        var plan = RosterPlanner.Plan(Catalog.Hulls);
        var slots = plan.Select(e => (e.WorldX, e.WorldZ)).ToArray();
        Assert.Equal(slots.Length, slots.Distinct().Count());
        // Each faction occupies exactly one column band.
        foreach (var g in plan.GroupBy(e => e.Civ))
        {
            Assert.Single(g.Select(e => e.Column).Distinct());
            Assert.Equal(g.First().Column, Array.FindIndex(RosterPlanner.FactionOrder, f => f.Civ == g.Key));
        }
    }

    /// <summary>Per-column counts are stable and sum to 72 (the P287 witness
    /// census). Guards against a silent regression in the role classifier.</summary>
    [Fact]
    public void Per_faction_counts_are_stable()
    {
        var counts = RosterPlanner.Plan(Catalog.Hulls)
            .GroupBy(e => e.Civ)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(72, counts.Values.Sum());

        // Exact figures computed from PCore001 + MODELS_MANIFEST_P287.json.
        Assert.Equal(3, counts["belters"]);
        Assert.Equal(13, counts["bios"]);
        Assert.Equal(3, counts["dreg"]);
        Assert.Equal(3, counts["giga"]);
        Assert.Equal(16, counts["gt"]);
        Assert.Equal(16, counts["ic"]);
        Assert.Equal(3, counts["oh"]);
        Assert.Equal(12, counts["rix"]);
        Assert.Equal(3, counts["tf"]);
    }
}

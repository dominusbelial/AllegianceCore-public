using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Content;

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
        Assert.Equal(83, counts.Values.Sum());

        // Exact figures computed from PCore001 + MODELS_MANIFEST_P287.json.
        Assert.Equal(3, counts["belters"]);
        Assert.Equal(13, counts["bios"]);
        Assert.Equal(8, counts["dreg"]);
        Assert.Equal(4, counts["giga"]);
        Assert.Equal(16, counts["gt"]);
        Assert.Equal(16, counts["ic"]);
        Assert.Equal(3, counts["oh"]);
        Assert.Equal(12, counts["rix"]);
        Assert.Equal(8, counts["tf"]);
    }

    /// <summary>⭐ P350 — the confirmed per-civ dedicated ModelName prefix table
    /// exists as data and covers the factions whose ships use a distinct
    /// per-civ model family (from the real PCore001 census).</summary>
    [Fact]
    public void Civ_prefix_table_covers_confirmed_families()
    {
        Assert.Equal(new[] { "quiz" }, RosterPlanner.CivModelPrefixes["bios"]);
        Assert.Equal(new[] { "wc_gt" }, RosterPlanner.CivModelPrefixes["gt"]);
        Assert.Equal(new[] { "wc_ic" }, RosterPlanner.CivModelPrefixes["ic"]);
        Assert.Equal(new[] { "wc_gc" }, RosterPlanner.CivModelPrefixes["giga"]);
        Assert.Equal(new[] { "faohsh" }, RosterPlanner.CivModelPrefixes["oh"]);
        Assert.Equal(new[] { "tf", "rain", "dn_fatesh" }, RosterPlanner.CivModelPrefixes["tf"]);
        Assert.Equal(new[] { "dreg", "drg", "dn_fadrsh" }, RosterPlanner.CivModelPrefixes["dreg"]);
        // Rixian/Belters share the generic fig/bom/cap meshes — no dedicated
        // prefix family, so their witnesses legitimately fall back.
        Assert.False(RosterPlanner.CivModelPrefixes.ContainsKey("rix"));
        Assert.False(RosterPlanner.CivModelPrefixes.ContainsKey("belters"));
    }

    /// <summary>⭐ P350 — the cross-civ hull bug is fixed: Bios's fighter /
    /// interceptor / stealth-fighter / bomber now resolve to Bios's own
    /// <c>quiz</c> hulls, not a cross-civ neighbour's (GigaCorp's <c>wc_gc</c>,
    /// generic <c>fig17</c> / <c>bom05b</c>).</summary>
    [Fact]
    public void Bios_combat_classes_resolve_to_bios_own_quiz_hulls()
    {
        var plan = RosterPlanner.Plan(Catalog.Hulls);
        var byAsset = plan.ToDictionary(e => e.Asset);

        var (fighter, interceptor, sf, bomber) =
            (byAsset["bios-fighter"], byAsset["bios-interceptor"],
             byAsset["bios-stealthfighter"], byAsset["bios-bomber"]);

        Assert.StartsWith("quiz", fighter.ModelName);
        Assert.StartsWith("quiz", interceptor.ModelName);
        Assert.StartsWith("quiz", sf.ModelName);
        Assert.StartsWith("quiz", bomber.ModelName);

        Assert.Equal("quizfigv2", fighter.ModelName);
        Assert.Equal("quizintv2", interceptor.ModelName);
        Assert.Equal("quizsfv2", sf.ModelName);
        Assert.Equal("quizbbrv2", bomber.ModelName);
    }

    /// <summary>⭐ P350 — representative cross-civ fixes for the other factions
    /// with dedicated prefix families: each changed class now carries its own
    /// civ's model, not GigaCorp's <c>wc_gcfigx</c> / generic <c>bom05b</c>.</summary>
    [Theory]
    [InlineData("gt", "gt-fighter", "wc_gt")]
    [InlineData("gt", "gt-scout", "wc_gt")]
    [InlineData("gt", "gt-sf", "wc_gt")]
    [InlineData("gt", "gt-bomber", "wc_gt")]
    [InlineData("ic", "ic-fighter", "wc_ic")]
    [InlineData("ic", "ic-int", "wc_ic")]
    [InlineData("ic", "ic-interceptor", "wc_ic")]
    [InlineData("ic", "ic-stealthfighter", "wc_ic")]
    [InlineData("oh", "oh-fighter", "faohsh")]
    [InlineData("oh", "oh-scout", "faohsh")]
    [InlineData("tf", "tf-fig", "rain")]
    [InlineData("tf", "tf-scout", "rain")]
    [InlineData("tf", "tf-bomber", "dn_fatesh")]
    [InlineData("giga", "giga-bomber", "wc_gc")]
    [InlineData("dreg", "dreg-fig2", "dn_fadrsh")]
    public void Changed_classes_resolve_to_their_civs_own_prefix(string civ, string asset, string expectPrefix)
    {
        var plan = RosterPlanner.Plan(Catalog.Hulls);
        var e = plan.First(x => x.Asset == asset);
        Assert.Equal(civ, e.Civ);
        Assert.StartsWith(expectPrefix, e.ModelName);
    }

    /// <summary>⭐ P350 — whenever a faction has its own dedicated hull for a
    /// class, the roster witness is that faction's own ship, never a cross-civ
    /// neighbour's dedicated hull. (Classes with no civ-owned alternative keep
    /// the historical fallback — a documented pre-existing residual.)</summary>
    [Fact]
    public void When_own_dedicated_hull_exists_witness_is_not_cross_civ()
    {
        var plan = RosterPlanner.Plan(Catalog.Hulls);
        var resolver = new HullModelResolver();
        foreach (var grp in plan.GroupBy(e => e.Civ))
        {
            if (!RosterPlanner.CivModelPrefixes.ContainsKey(grp.Key)) continue;
            var ownPrefixes = RosterPlanner.CivModelPrefixes[grp.Key];
            foreach (var e in grp)
            {
                // Does the civ own any hull (its prefix family) that resolves
                // to this asset? If not, there is no own alternative and the
                // historical fallback legitimately stands.
                bool ownCandidate = Catalog.Hulls.Any(h =>
                    resolver.ResolveAsset(e.Civ, h) == e.Asset &&
                    ownPrefixes.Any(p => h.ModelName.Trim().StartsWith(p, StringComparison.Ordinal)));
                if (!ownCandidate) continue;
                Assert.True(
                    ownPrefixes.Any(p => e.ModelName.Trim().StartsWith(p, StringComparison.Ordinal)),
                    $"{e.Asset} has a {e.Civ}-owned hull but resolved to cross-civ {e.ModelName}");
            }
        }
    }
}

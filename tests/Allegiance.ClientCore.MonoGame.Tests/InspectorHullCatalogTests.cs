using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Content;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P565 — pure tests for the hull inspector's enumeration (flyable filter,
/// civ grouping, fallback flag) against the real PCore001 core, exactly like
/// <see cref="HullModelResolverTests"/>.</summary>
public class InspectorHullCatalogTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly ShopCatalog Catalog =
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly HullModelResolver Resolver = new();

    private static bool NativeAvailable(DataHullTypeIGC h)
        => !string.IsNullOrEmpty(h.ModelName) && File.Exists(Path.Combine(ArtRoot, h.ModelName.Trim() + ".mdl"));

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flyable_only_excludes_drone_hulls_by_default()
    {
        Require.Artwork();
        var flyable = InspectorHullCatalog.Enumerate(Catalog.Hulls, Resolver, includeDrones: false, NativeAvailable);
        var all = InspectorHullCatalog.Enumerate(Catalog.Hulls, Resolver, includeDrones: true, NativeAvailable);
        Assert.True(all.Count > flyable.Count, "drones should add hulls");
        // every hull's GroupId matches the filter
        foreach (var h in all)
        {
            var hull = Catalog.Hulls.First(x => x.HullId == h.HullId);
            Assert.Equal(hull.GroupId < 0, h.IsDrone);
        }
        // flyable-only list has no drone entries
        Assert.All(flyable, h => Assert.False(h.IsDrone));
        Assert.Contains(all, h => h.IsDrone);   // and the drone list DOES include them
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Walk_order_is_stable_by_hull_id_and_deduplicated()
    {
        Require.Artwork();
        var all = InspectorHullCatalog.Enumerate(Catalog.Hulls, Resolver, includeDrones: true, NativeAvailable);
        var ids = all.Select(h => h.HullId).ToArray();
        Assert.Equal(ids.OrderBy(x => x).ToArray(), ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.True(ids.Length > 20, $"expected a substantial hull census, got {ids.Length}");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Civ_grouping_partitions_the_fleet()
    {
        Require.Artwork();
        var all = InspectorHullCatalog.Enumerate(Catalog.Hulls, Resolver, includeDrones: false, NativeAvailable);
        var civs = InspectorHullCatalog.Civs(all);
        Assert.NotEmpty(civs);
        // union of per-civ hulls covers every hull that HAS a primary civ (a hull with no
        // recognized art vocabulary belongs to no fleet, so it is legitimately uncovered).
        int nullCiv = all.Count(h => h.CivKey is null);
        int covered = civs.Sum(c => InspectorHullCatalog.ForCiv(all, c).Count);
        Assert.True(covered >= all.Count - nullCiv, $"covered {covered} of {all.Count - nullCiv} civ-owned hulls");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Known_bios_scout_hull_is_native_and_meshy_available()
    {
        Require.Artwork();
        var all = InspectorHullCatalog.Enumerate(Catalog.Hulls, Resolver, includeDrones: false, NativeAvailable);
        // fig05 -> scout role; civ "bios" resolves bios-scout
        var fig05 = all.First(h => h.ModelName.Equals("fig05", StringComparison.OrdinalIgnoreCase));
        Assert.True(fig05.NativeAvailable);
        Assert.False(fig05.FallbackUsed);
        Assert.NotNull(fig05.MeshyAsset);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fallback_flag_is_true_only_when_neither_native_nor_meshy_resolves()
    {
        Require.Artwork();
        var all = InspectorHullCatalog.Enumerate(Catalog.Hulls, Resolver, includeDrones: true, NativeAvailable);
        foreach (var h in all)
        {
            if (h.FallbackUsed)
            {
                Assert.False(h.NativeAvailable);
                Assert.Null(h.MeshyAsset);
            }
            else
            {
                Assert.True(h.NativeAvailable || h.MeshyAsset is not null);
            }
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Primary_civ_resolves_a_real_civ_key()
    {
        Require.Artwork();
        var all = InspectorHullCatalog.Enumerate(Catalog.Hulls, Resolver, includeDrones: false, NativeAvailable);
        foreach (var h in all.Where(h => h.CivKey is not null))
            Assert.Contains(h.CivKey, HullModelResolver.CivKeyById.Values);
    }
}

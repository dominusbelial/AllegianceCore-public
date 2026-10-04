using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Content;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P288 — the civ+class → P287-model resolver, verified against the real
/// PCore001.igc. The ⭐ coverage test is the programmatic proof that every one
/// of the 72 imported ship assets (MODELS_MANIFEST_P287.json) is reachable from
/// at least one real hull via the resolver — nothing P287 imported goes unused.
/// </summary>
public class HullModelResolverTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly HullModelResolver Resolver = new();

    private static string RepoManifestPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("cannot locate AllegianceCore repo root");
        return Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame",
            "Content", "Models", "MODELS_MANIFEST_P287.json");
    }

    /// <summary>⭐ P288 — the embedded ShipAssets set matches P287's manifest
    /// exactly (72 ships; asset == "&lt;civ&gt;-&lt;class&gt;"), so the resolver
    /// only ever emits real imported assets.</summary>
    [Fact]
    public void Embedded_ship_assets_match_P287_manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoManifestPath()));
        var ships = doc.RootElement.GetProperty("models").EnumerateArray()
            .Where(m => m.GetProperty("kind").GetString() == "ship")
            .Select(m => m.GetProperty("asset").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(ships.Count, HullModelResolver.ShipAssets.Count);
        Assert.Equal(83, HullModelResolver.ShipAssets.Count);
        Assert.True(ships.SetEquals(HullModelResolver.ShipAssets));
    }

    /// <summary>⭐ COVERAGE PROOF — every one of the 72 imported ship assets is
    /// reachable from at least one real hull in PCore001 via the civ+class
    /// resolver (a hull of role R covers each civ whose class-token for R is in
    /// the manifest, because the underlying native mesh is genuinely shared
    /// across those factions). Prints the per-asset witness civ+hull.</summary>
    [Fact]
    public void Every_imported_ship_model_is_reachable_from_a_real_hull()
    {
        var reachable = HullModelResolver.ReachableAssets(Catalog.Hulls);

        var missing = HullModelResolver.ShipAssets.Except(reachable.Keys).OrderBy(x => x).ToList();
        Assert.Empty(missing);

        // Also every reachable asset is genuinely one of the embedded set and
        // every (civ,class) pairs back to a real imported asset.
        Assert.All(reachable.Keys, k => Assert.Contains(k, HullModelResolver.ShipAssets));
        Assert.Equal(HullModelResolver.ShipAssets.Count, reachable.Count);
    }

    /// <summary>⭐ A per-asset witness table: each imported model is backed by a
    /// real hull (HullId + native ModelName + display name) for a real civ —
    /// the concrete "hull-to-model mapping entry" the task requires.</summary>
    [Fact]
    public void Each_asset_has_a_real_witness_hull()
    {
        var witnesses = Witnesses();
        Assert.Equal(HullModelResolver.ShipAssets.Count, witnesses.Count);
        // Witness every asset with a real hull that resolves to it.
        foreach (var asset in HullModelResolver.ShipAssets)
        {
            Assert.True(witnesses.TryGetValue(asset, out var w), $"no witness for {asset}");
            var (civ, hullId) = w;
            var hull = Catalog.Hulls.First(h => h.HullId == hullId);
            var civKey = HullModelResolver.CivKeyByIdShort(CivIdOf(civ));
            Assert.Equal(asset, Resolver.ResolveAsset(civKey!, hull));
        }
    }

    private static Dictionary<string, (string CivKey, short HullId)> Witnesses()
    {
        var result = new Dictionary<string, (string, short)>();
        foreach (var asset in HullModelResolver.ShipAssets.OrderBy(x => x))
        {
            var idx = asset.IndexOf('-');
            var civKey = asset[..idx];
            (DataHullTypeIGC Hull, string? Asset)? witness = null;
            foreach (var h in Catalog.Hulls)
            {
                var a = Resolver.ResolveAsset(civKey, h);
                if (a == asset) { witness = (h, a); break; }
            }
            Assert.True(witness.HasValue, $"no witness hull resolves to {asset}");
            result[asset] = (civKey, witness.Value.Hull.HullId);
        }
        return result;
    }

    private static short CivIdOf(string civKey) =>
        HullModelResolver.CivKeyById.First(kv => kv.Value == civKey).Key;

    /// <summary>Sanity: a known scout-shaped hull (fig05 → "Scout") resolves for
    /// a faction that has scout art, and a Lifepod (no art class) resolves to
    /// null → fallback.</summary>
    [Fact]
    public void Known_hull_classes_resolve_and_unmapped_falls_back()
    {
        var scout = Catalog.Hulls.First(h => h.ModelName.Trim() == "fig05");
        // GT has scout art → gt-scout; Dreghklar names it scout2. (Bios never
        // had a scout model imported by P287 — only art — so it has no asset.)
        Assert.Equal("gt-scout", Resolver.ResolveAsset("gt", scout));
        Assert.Equal("dreg-scout2", Resolver.ResolveAsset("dreg", scout));

        var lifepod = Catalog.Hulls.First(h => h.Name.Contains("Lifepod"));
        // Civ-key-resolution: a Lifepod's ModelName (e.g. utl100) is a pod, but
        // none of the playable civs imported a "pod"-classed Bios/IC asset map;
        // Belters/TF/OH/Giga only imported fighter/scout/bomber → Lifepod nulls
        // for them, exercising the fallback.
        Assert.Null(Resolver.ResolveAsset("belters", lifepod));
        Assert.Null(Resolver.ResolveAsset("giga", lifepod));
    }

    /// <summary>P512 — Giga coverage: every player-flyable GigaCorp hull
    /// (native <c>wc_gc</c> prefix, GroupId ≥ 0) now resolves to a dedicated
    /// giga asset — no <c>bios_scout</c> fallback (before P512, the three
    /// interceptor-class hulls wc_gchvy/gchvyx/gcint fell back).</summary>
    [Fact]
    public void All_giga_player_hulls_resolve_dedicated_no_fallback()
    {
        var resolver = new HullModelResolver();
        var giga = Catalog.Hulls
            .Where(h => h.GroupId >= 0)
            .Where(h => (h.ModelName ?? "").Trim().StartsWith("wc_gc", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(8, giga.Count); // real measured count over PCore001
        foreach (var h in giga)
            Assert.NotNull(resolver.ResolveAsset("giga", h));
    }

    /// <summary>P513 — Technoflux coverage: every player-flyable Technoflux
    /// hull (native <c>tf</c>/<c>rain</c>/<c>dn_fatesh</c> prefix family,
    /// GroupId ≥ 0) now resolves to a dedicated tf asset — no
    /// <c>bios_scout</c> fallback (before P513, int/sf/carrier/cruiser/tt
    /// classes fell back to the shared fallback).</summary>
    [Fact]
    public void All_tf_player_hulls_resolve_dedicated_no_fallback()
    {
        var resolver = new HullModelResolver();
        var tf = Catalog.Hulls
            .Where(h => h.GroupId >= 0)
            .Where(h =>
            {
                var mn = (h.ModelName ?? "").Trim();
                return mn.StartsWith("tf", StringComparison.Ordinal)
                    || mn.StartsWith("rain", StringComparison.Ordinal)
                    || mn.StartsWith("dn_fatesh", StringComparison.Ordinal);
            })
            .ToList();
        Assert.Equal(26, tf.Count); // real measured count over PCore001
        foreach (var h in tf)
            Assert.NotNull(resolver.ResolveAsset("tf", h));
    }

    [Fact]
    public void Civ_key_by_id_matches_real_core_ids()
    {
        Assert.Equal("bios", HullModelResolver.CivKeyByIdShort(35));
        Assert.Equal("gt", HullModelResolver.CivKeyByIdShort(3));
        Assert.Equal("rix", HullModelResolver.CivKeyByIdShort(201));
        Assert.Equal("dreg", HullModelResolver.CivKeyByIdShort(1));
        Assert.Equal("tf", HullModelResolver.CivKeyByIdShort(2));
        Assert.Equal("oh", HullModelResolver.CivKeyByIdShort(5));
        Assert.Equal("giga", HullModelResolver.CivKeyByIdShort(18));
        Assert.Equal("ic", HullModelResolver.CivKeyByIdShort(25));
        Assert.Equal("belters", HullModelResolver.CivKeyByIdShort(101));
        Assert.Equal("phoenix", HullModelResolver.CivKeyByIdShort(4)); // P510
        Assert.Null(HullModelResolver.CivKeyByIdShort(999));
    }

    [Fact]
    public void Missile_types_map_to_imported_assets_or_null()
    {
        var types = Catalog.MissileTypes;
        Assert.NotNull(types);
        Assert.Equal("mis05", MissileModelSource.ResolveImportedAsset(34, types));   // demo type
        Assert.Equal("mis05", MissileModelSource.ResolveImportedAsset(32, types));
        Assert.Equal("mis400", MissileModelSource.ResolveImportedAsset(83, types));
        Assert.Equal("artifact", MissileModelSource.ResolveImportedAsset(14, types));
        Assert.Equal("artifact", MissileModelSource.ResolveImportedAsset(70, types));
        // mis06/mis01 (types 7/8) were NOT imported by P287 → fallback.
        Assert.Null(MissileModelSource.ResolveImportedAsset(7, types));
        Assert.Null(MissileModelSource.ResolveImportedAsset(8, types));
        Assert.Null(MissileModelSource.ResolveImportedAsset(999, types));
        Assert.Null(MissileModelSource.ResolveImportedAsset(34, null));
    }
}

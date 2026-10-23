using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P800 — every hull renders: the real-core AUDIT test behind the startup
/// health audit (<see cref="ShipNativeAudit.CollectDefects"/>) and the
/// offline proof tool (<c>tools/p800-hull-proof</c>).
///
/// <para><b>What can fail while the P607 line says all is well.</b> The P607
/// startup audit only checked <c>MeshFor(stem) != null</c>. A hull could still
/// render EMPTY: a zero-triangle mesh, a non-finite/non-positive mesh or
/// max-vertex radius (the native uniform scale <c>(0.5·Length)/meshRadius</c>
/// and every preview divide by both), a non-positive hull Length (the hangar
/// preview's eye = 2.4 × 0.5·Length degenerates), or an unresolvable texture
/// stem. These tests walk the REAL PCore001.igc (447 hulls, 295 player-buyable,
/// 198 offered under testUnlock all-tech) CPU-only and assert none of that
/// happens — a hull that would render empty in the hangar preview or in flight
/// fails here, on master and forever after.</para>
///
/// <para><b>Known artwork gaps (justified, native-parity).</b> The lifepod
/// (<c>utl22</c>, hulls 444/844) imports texture <c>ilpodbmp</c> and the
/// artwork tree ships no <c>ilpodbmp.mdl</c>; native's modeler
/// (<c>xfile.cpp:577-586</c> LoadImage) hits the same missing file and draws
/// the hull untextured. The port's lit-solid fallback is the same outcome, so
/// the two lifepod hulls are the ONLY tolerated no-texture-stem entries.</para>
/// </summary>
public class P800EveryHullRendersTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static ShopCatalog LoadCore()
    {
        var catalog = ShopCatalog.TryLoad(ArtRoot);
        Assert.NotNull(catalog);
        Assert.NotEmpty(catalog!.Hulls);
        return catalog;
    }

    private static IAssetSource Assets() => new DirectoryAssetSource(ArtRoot);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealCore_hull_population_matches_the_P800_baseline()
    {
        Require.Artwork();
        var catalog = LoadCore();
        // The corpus facts the P800 brief measures the pass against. A change
        // here means the core (or the buyable/offered predicate) moved — update
        // the proof tool's baseline in the same commit.
        Assert.Equal(447, catalog.Hulls.Count);
        Assert.Equal(295, catalog.Hulls.Count(h => h.GroupId >= 0));
        var allTech = new byte[TechTreeBitMask.ByteCount];
        Array.Fill<byte>(allTech, 0xFF);
        Assert.Equal(198, catalog.FilteredFor(allTech, new TechTreeBitMask(allTech)).Hulls.Count);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealCore_every_hull_mesh_is_structurally_renderable()
    {
        Require.Artwork();
        var catalog = LoadCore();
        var assets = Assets();
        var defects = new List<string>();
        foreach (var hull in catalog.Hulls.OrderBy(h => h.HullId))
        {
            var stem = hull.ModelName?.Trim();
            if (string.IsNullOrEmpty(stem)) { defects.Add($"{hull.HullId} '{hull.Name}' no-stem"); continue; }
            MdlFile mdl;
            try { mdl = MdlFile.Parse(AssetSources.ReadAllBytes(assets, stem + ".mdl")); }
            catch (Exception e) { defects.Add($"{hull.HullId} '{hull.Name}' mesh-unresolved ({stem}: {e.Message})"); continue; }

            int tris = mdl.Meshes.Sum(m => m.Indices.Length) / 3;
            if (tris <= 0) defects.Add($"{hull.HullId} '{hull.Name}' empty-mesh ({stem})");

            float radius = NativeShipResolver.MergedRadius(mdl.Meshes);
            if (!float.IsFinite(radius) || radius <= 0f)
                defects.Add($"{hull.HullId} '{hull.Name}' bad-mesh-radius {radius} ({stem})");

            float vertexRadius = NativeShipResolver.MaxVertexRadius(mdl.Meshes, mdl.LodLevels);
            if (!float.IsFinite(vertexRadius) || vertexRadius <= 0f)
                defects.Add($"{hull.HullId} '{hull.Name}' bad-vertex-radius {vertexRadius} ({stem})");

            if (!float.IsFinite(hull.Length) || hull.Length <= 0f)
                defects.Add($"{hull.HullId} '{hull.Name}' bad-length {hull.Length}");

            // Native texture rule (trekigc.cpp:1837-1839 override + the model's
            // own import namespace, xfile.cpp:571-586): exactly the stem the
            // client's TextureStemFor resolves. Existence-only check here — the
            // two lifepod hulls' missing ilpodbmp.mdl is a documented artwork
            // gap (native fails the same load) asserted separately below.
            if (NativeShipResolver.ResolveTextureStem(mdl, hull.TextureName, stem,
                    s => assets.Exists(s + ".mdl")) is null
                && stem != "utl22")      // lifepod: known artwork gap, lit-solid (native parity)
                defects.Add($"{hull.HullId} '{hull.Name}' no-texture-stem ({stem})");
        }

        Assert.True(defects.Count == 0,
            "P800 hulls that would render empty/degenerate:\n  " + string.Join("\n  ", defects));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealCore_the_only_texture_gaps_are_the_two_lifepod_hulls_native_also_cannot_texture()
    {
        Require.Artwork();
        var catalog = LoadCore();
        var assets = Assets();
        var gaps = new List<string>();
        foreach (var hull in catalog.Hulls.OrderBy(h => h.HullId))
        {
            var stem = hull.ModelName?.Trim();
            if (string.IsNullOrEmpty(stem)) continue;
            var mdl = MdlFile.Parse(AssetSources.ReadAllBytes(assets, stem + ".mdl"));
            if (NativeShipResolver.ResolveTextureStem(mdl, hull.TextureName, stem,
                    s => assets.Exists(s + ".mdl")) is null)
                gaps.Add($"{hull.HullId} '{hull.Name}' ({stem})");
        }
        // The artwork tree ships no ilpodbmp.mdl (the lifepod's only import);
        // native's modeler fails the same load (xfile.cpp:586) and renders the
        // hull untextured. Exactly the two utl22 lifepod hulls may gap.
        var expected = new[] { "444 'Lifepod' (utl22)", "844 'Lifepod' (utl22)" };
        Assert.True(gaps.OrderBy(g => g, StringComparer.Ordinal).SequenceEqual(expected),
            "unexpected texture-stem gaps: " + string.Join(", ", gaps));
    }

    // ── the pure audit decision, no artwork needed ──────────────────────────

    [Fact]
    public void CollectDefects_flags_every_degeneracy_category()
    {
        var hulls = new[]
        {
            new ShipNativeAudit.HullHealth(1, "Ok", "ok1", true, 100, 4f, 3.5f, 20f, true),
            new ShipNativeAudit.HullHealth(2, "NoStem", "", true, 10, 1f, 1f, 10f, true),
            new ShipNativeAudit.HullHealth(3, "Unresolved", "gone", false, 0, 0f, 0f, 10f, false),
            new ShipNativeAudit.HullHealth(4, "Empty", "empt", true, 0, 2f, 2f, 10f, true),
            new ShipNativeAudit.HullHealth(5, "ZeroRadius", "zr", true, 10, 0f, 2f, 10f, true),
            new ShipNativeAudit.HullHealth(6, "NaNRadius", "nr", true, 10, float.NaN, 2f, 10f, true),
            new ShipNativeAudit.HullHealth(7, "ZeroLength", "zl", true, 10, 2f, 2f, 0f, true),
            new ShipNativeAudit.HullHealth(8, "NoTexture", "nt", true, 10, 2f, 2f, 10f, false),
        };
        var defects = ShipNativeAudit.CollectDefects(hulls);
        Assert.Contains("2 'NoStem' no-stem", defects);
        Assert.Contains("3 'Unresolved' mesh-unresolved (gone)", defects);
        Assert.Contains("4 'Empty' empty-mesh (empt)", defects);
        Assert.Contains("5 'ZeroRadius' bad-mesh-radius 0 (zr)", defects);
        Assert.Contains("6 'NaNRadius' bad-mesh-radius NaN (nr)", defects);
        Assert.Contains("7 'ZeroLength' bad-length 0", defects);
        Assert.Contains("8 'NoTexture' no-texture-stem (nt)", defects);
        // the healthy hull never appears
        Assert.DoesNotContain(defects, d => d.StartsWith("1 'Ok'"));
        Assert.Equal(7, defects.Count);
    }

    [Fact]
    public void CollectUnresolved_still_reports_blank_and_unresolvable_stems()
    {
        var unresolved = ShipNativeAudit.CollectUnresolved(
            new[] { ((short)1, "A", (string?)"a"), ((short)2, "B", (string?)"  "), ((short)3, "C", (string?)null) },
            stem => stem == "a");
        Assert.Equal(2, unresolved.Count);
        Assert.Equal(2, unresolved[0].HullId);
        Assert.Equal(3, unresolved[1].HullId);
    }
}

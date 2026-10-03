using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Content;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P510 — Phoenix (civ 4) civ-mapping + the verified per-civ player-flyable
/// native-model census for the remaining Meshy-regeneration civs (dreg, tf,
/// oh, giga, phoenix). All counts are re-derived from the real PCore001.igc by
/// the port's own <see cref="HullModelResolver"/>/<see cref="RosterPlanner"/>
/// — never from memory.
/// </summary>
public class PhoenixMappingTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly HullModelResolver Resolver = new();

    /// <summary>P510 — Phoenix (civilization id 4) is now a recognised civ key,
    /// so its hulls stop being invisible to the resolver (they still fall back
    /// for art, but the civ itself is mapped).</summary>
    [Fact]
    public void Phoenix_civ_key_maps_to_id_4()
    {
        Assert.Equal("phoenix", HullModelResolver.CivKeyByIdShort(4));
        Assert.Contains(new KeyValuePair<short, string>(4, "phoenix"), HullModelResolver.CivKeyById);
    }

    /// <summary>P510 — Phoenix's dedicated native ModelName prefix family is
    /// <c>dn_faphsh</c>, verified from the real PCore001 hull data below.</summary>
    [Fact]
    public void Phoenix_prefix_family_is_registered()
    {
        Assert.Equal(new[] { "dn_faphsh" }, RosterPlanner.CivModelPrefixes["phoenix"]);
    }

    /// <summary>P510 — the phoenix prefix family genuinely owns player-flyable
    /// hulls in PCore001 (verified, not assumed): 26 flyable hulls across 11
    /// distinct native model names.</summary>
    [Fact]
    public void Phoenix_prefix_owns_real_player_flyable_hulls()
    {
        var pf = Catalog.Hulls.Where(h => h.GroupId >= 0);
        var owned = pf.Where(h => h.ModelName.Trim().StartsWith("dn_faphsh", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(owned.Count >= 20, $"expected a sizable phoenix family, got {owned.Count}");
        var models = owned.Select(h => h.ModelName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(11, models.Count);
    }

    /// <summary>P510 — interim art behaviour, documented: Phoenix has no P287
    /// asset slots yet (added by P515), so its hulls still resolve to null →
    /// shared <c>bios_scout</c> fallback. The civ mapping enables the upcoming
    /// slots without pretending art exists today.</summary>
    [Fact]
    public void Phoenix_resolves_to_fallback_until_art_slots_exist()
    {
        var fig = Catalog.Hulls.First(h => h.ModelName.Trim().Equals("dn_faphshfig", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("fighter", HullModelResolver.RoleOf(fig.ModelName));
        // No "phoenix-*" asset in the P287 manifest → null → fallback.
        Assert.Null(Resolver.ResolveAsset("phoenix", fig));
        Assert.DoesNotContain(HullModelResolver.ShipAssets, a => a.StartsWith("phoenix-", StringComparison.Ordinal));

        // Every player-flyable Phoenix hull currently falls back.
        var pf = Catalog.Hulls.Where(h => h.GroupId >= 0);
        var owned = pf.Where(h => h.ModelName.Trim().StartsWith("dn_faphsh", StringComparison.OrdinalIgnoreCase));
        Assert.All(owned, h => Assert.Null(Resolver.ResolveAsset("phoenix", h)));
    }

    /// <summary>P510 — the verified per-civ distinct player-flyable native
    /// ModelName census for the remaining civs (dreg 6, tf 10, oh 12, giga 7,
    /// phoenix 11 = 46 total). Guards against a silent drift in the role /
    /// prefix classifiers. Order-independent set equality.</summary>
    [Theory]
    [InlineData("dreg", 6)]
    [InlineData("tf", 10)]
    [InlineData("oh", 12)]
    [InlineData("giga", 7)]
    [InlineData("phoenix", 11)]
    public void Remaining_civ_distinct_model_counts(string civ, int expected)
    {
        var prefixes = RosterPlanner.CivModelPrefixes[civ];
        var pf = Catalog.Hulls.Where(h => h.GroupId >= 0);
        var models = pf
            .Where(h => prefixes.Any(p => h.ModelName.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(h => h.ModelName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(expected, models.Count);
    }

    /// <summary>P510 — the exact 46-model list (used to price the Meshy
    /// submission plan). Ensures the plan and the code can never disagree.</summary>
    [Fact]
    public void Remaining_civ_model_list_is_exactly_the_priced_set()
    {
        var expected = new[]
        {
            // dreg
            "dn_fadrshfig","dn_fadrshstl","dregcar","dregtt","drgas","drgmf",
            // tf
            "dn_fateshbmb","raincarr","rainfigv2","rainintv2","rainscoutv2","rainsfv2",
            "tffig","tfflagship","tfsb2","tftransport",
            // oh
            "faohshafig","faohshass","faohshbmb","faohshcar","faohshcrs","faohshcrv",
            "faohshdev","faohshfig","faohshhint","faohshint","faohshsctb","faohshstl",
            // giga
            "wc_gcbmbx","wc_gcfig","wc_gcfigx","wc_gchttx","wc_gchvy","wc_gchvyx","wc_gcint",
            // phoenix
            "dn_faphshbmb","dn_faphshbmbs","dn_faphshcar","dn_faphshcrs","dn_faphshdst",
            "dn_faphshfig","dn_faphshfrg","dn_faphshint","dn_faphshsct","dn_faphshstl","dn_faphshvng",
        };
        var civs = new[] { "dreg", "tf", "oh", "giga", "phoenix" };
        var pf = Catalog.Hulls.Where(h => h.GroupId >= 0);
        var got = new System.Collections.Generic.List<string>();
        foreach (var civ in civs)
        {
            var prefixes = RosterPlanner.CivModelPrefixes[civ];
            got.AddRange(pf
                .Where(h => prefixes.Any(p => h.ModelName.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Select(h => h.ModelName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal),
                     got.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(46, got.Count);
    }
}

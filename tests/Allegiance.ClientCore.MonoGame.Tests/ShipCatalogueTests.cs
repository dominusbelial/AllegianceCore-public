using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P581b — pure tests for the replacement-model catalogue resolution and the
/// "catalogue:&lt;id&gt;" ship-art override extension.</summary>
public class ShipCatalogueTests
{
    private const string Cat =
        "{ \"models\": [" +
        "  { \"id\":\"dreg-scout2\",\"asset\":\"dreg-scout2\",\"source\":\"meshy-repaired\",\"grade\":\"repairable\",\"civ\":\"dreg\",\"class\":\"scout\",\"size_class\":\"small\",\"thumbnail\":\"assets/ships/thumbs/dreg-scout2.png\" }," +
        "  { \"id\":\"bios-carrier\",\"asset\":\"bios-carrier\",\"source\":\"meshy\",\"grade\":\"clean\",\"civ\":\"bios\",\"class\":\"carrier\",\"size_class\":\"large\",\"thumbnail\":\"assets/ships/thumbs/bios-carrier.png\" }" +
        "] }";

    [Fact]
    public void Parse_reads_ids_and_metadata()
    {
        var c = ShipCatalogue.Parse(Cat);
        Assert.Equal(2, c.Count);
        Assert.Equal(2, c.Ids.Count);
        Assert.Equal("dreg-scout2", c.ById("dreg-scout2")!.Id);
        Assert.Equal("meshy-repaired", c.ById("dreg-scout2")!.Source);
        Assert.Equal("large", c.ById("bios-carrier")!.SizeClass);
    }

    [Fact]
    public void Malformed_json_yields_empty_catalogue()
    {
        var c = ShipCatalogue.Parse("{ not json ");
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void ResolveKey_prefers_repaired_copy_when_loaded()
    {
        var c = ShipCatalogue.Parse(Cat);
        var loaded = new HashSet<string> { "dreg-scout2", "repaired/dreg-scout2" };
        Assert.Equal("repaired/dreg-scout2", c.ResolveKey("dreg-scout2", loaded));
    }

    [Fact]
    public void ResolveKey_falls_back_to_raw_asset_when_repaired_not_loaded()
    {
        var c = ShipCatalogue.Parse(Cat);
        var loaded = new HashSet<string> { "bios-carrier" };
        Assert.Equal("bios-carrier", c.ResolveKey("bios-carrier", loaded));
    }

    [Fact]
    public void ResolveKey_returns_null_when_asset_missing()
    {
        var c = ShipCatalogue.Parse(Cat);
        var loaded = new HashSet<string> { "something-else" };
        Assert.Null(c.ResolveKey("bios-carrier", loaded));
    }

    [Fact]
    public void ResolveKey_unknown_id_returns_null()
    {
        var c = ShipCatalogue.Parse(Cat);
        Assert.Null(c.ResolveKey("no-such-model", new HashSet<string>()));
    }

    // ---- ShipArtOverride catalogue:<id> support ----

    [Fact]
    public void Catalogue_value_resolves_as_meshy_with_id_accessible()
    {
        var ov = ShipArtOverride.Parse("{ \"fig05\": \"catalogue:dreg-scout2\" }");
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("fig05", 1));
        Assert.Equal("dreg-scout2", ov.TryGetCatalogue("fig05", 1));
        Assert.Null(ov.TryGetCatalogue("other", 2));
    }

    [Fact]
    public void Catalogue_by_hull_id_works()
    {
        var ov = ShipArtOverride.Parse("{ \"12\": \"catalogue:bios-carrier\" }");
        Assert.Equal("bios-carrier", ov.TryGetCatalogue("anything", 12));
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("anything", 12));
    }

    [Fact]
    public void Catalogue_value_round_trips_through_ToJson()
    {
        var ov = new ShipArtOverride();
        ov.SetCatalogueByModel("fig05", "dreg-scout2");
        var re = ShipArtOverride.Parse(ov.ToJson());
        Assert.Equal("dreg-scout2", re.TryGetCatalogue("fig05", 0));
    }

    [Fact]
    public void SetCatalogueByModel_removes_prior_native_entry()
    {
        var ov = new ShipArtOverride();
        ov.SetByModel("fig05", ShipArtOverride.ArtChoice.Native);
        ov.SetCatalogueByModel("fig05", "dreg-scout2");
        // no coexisting native + catalogue entries for the same hull
        Assert.Equal("dreg-scout2", ov.TryGetCatalogue("fig05", 0));
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("fig05", 0));
        Assert.Equal(1, ov.Count);
    }

    [Fact]
    public void Clearing_catalogue_returns_to_native()
    {
        var ov = ShipArtOverride.Parse("{ \"fig05\": \"catalogue:dreg-scout2\" }");
        ov.SetCatalogueByModel("fig05", null);
        Assert.Null(ov.TryGetCatalogue("fig05", 0));
        Assert.Equal(ShipArtOverride.ArtChoice.Native, ov.Resolve("fig05", 0));
        Assert.Equal(0, ov.Count);
    }

    // ---- P601 — public catalogue schema stays; Meshy entries come from the pack ----

    [Fact]
    public void Merge_overlays_pack_entries_on_public_native_only()
    {
        // The public file keeps the schema + native entries (empty models here,
        // exactly as P601 ships it); the pack supplies the Meshy entries.
        var publicCat = ShipCatalogue.Parse("{ \"models\": [] }");
        var packCat = ShipCatalogue.Parse(
            "{ \"models\": [" +
            " { \"id\":\"dreg-scout2\",\"asset\":\"dreg-scout2\",\"source\":\"meshy-repaired\",\"civ\":\"dreg\",\"class\":\"scout\",\"size_class\":\"small\",\"thumbnail\":\"assets/ships/thumbs/dreg-scout2.png\" }," +
            " { \"id\":\"bios-carrier\",\"asset\":\"bios-carrier\",\"source\":\"meshy\",\"civ\":\"bios\",\"class\":\"carrier\",\"size_class\":\"large\",\"thumbnail\":\"assets/ships/thumbs/bios-carrier.png\" }" +
            "] }");
        var merged = ShipCatalogue.Merge(publicCat, packCat);
        Assert.Equal(2, merged.Count);
        Assert.Equal("meshy-repaired", merged.ById("dreg-scout2")!.Source);
    }

    [Fact]
    public void Merge_pack_wins_on_duplicate_id()
    {
        var publicCat = ShipCatalogue.Parse("{ \"models\": [ { \"id\":\"a\",\"asset\":\"nat-a\",\"source\":\"native\" } ] }");
        var packCat = ShipCatalogue.Parse("{ \"models\": [ { \"id\":\"a\",\"asset\":\"meshy-a\",\"source\":\"meshy\" } ] }");
        var merged = ShipCatalogue.Merge(publicCat, packCat);
        Assert.Equal(1, merged.Count);
        Assert.Equal("meshy-a", merged.ById("a")!.Asset);
    }

    [Fact]
    public void NoPack_empty_catalogue_resolves_nothing_and_defaults_native()
    {
        // The public (native-only) catalogue when the pack is absent has no Meshy
        // models, so any "catalogue:<id>" override must fall back to native.
        var publicCat = ShipCatalogue.Parse("{ \"models\": [] }");
        Assert.Equal(0, publicCat.Count);
        Assert.Null(publicCat.ResolveKey("bios-carrier", new HashSet<string>()));
        // and a ShipArtOverride with no entries defaults every hull to native
        var ov = ShipArtOverride.Parse("{ }");
        Assert.Equal(ShipArtOverride.ArtChoice.Native, ov.Resolve("fig05", 1));
    }
}

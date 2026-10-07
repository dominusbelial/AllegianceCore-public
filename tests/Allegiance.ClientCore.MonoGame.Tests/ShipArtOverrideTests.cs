using System.IO;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P565 — pure tests for the per-hull ship-art override resolution order
/// (global flag &gt; per-hull override &gt; native default) and its JSON persistence.</summary>
public class ShipArtOverrideTests
{
    [Fact]
    public void Default_is_native_when_no_flag_and_no_override()
    {
        var ov = new ShipArtOverride();
        Assert.Equal(ShipArtOverride.ArtChoice.Native, ov.Resolve("fig05", 1));
    }

    [Fact]
    public void Global_force_beats_a_per_hull_override()
    {
        var ov = new ShipArtOverride { GlobalForce = ShipArtOverride.ArtChoice.Meshy };
        ov.SetByModel("fig05", ShipArtOverride.ArtChoice.Native);
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("fig05", 1));
    }

    [Fact]
    public void Override_by_model_name_wins_over_default()
    {
        var ov = new ShipArtOverride();
        ov.SetByModel("fig05", ShipArtOverride.ArtChoice.Meshy);
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("fig05", 1));
    }

    [Fact]
    public void Override_by_hull_id_wins_over_default()
    {
        var ov = ShipArtOverride.Parse("{ \"12\": \"meshy\" }");
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("whatever", 12));
    }

    [Fact]
    public void Model_name_match_is_case_insensitive()
    {
        var ov = ShipArtOverride.Parse("{ \"Fig05\": \"meshy\" }");
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("fig05", 1));
    }

    [Fact]
    public void Unknown_values_are_ignored()
    {
        var ov = ShipArtOverride.Parse("{ \"fig05\": \"banana\", \"fig06\": \"native\" }");
        // "banana" is not a valid choice -> no entry for fig05
        Assert.Equal(ShipArtOverride.ArtChoice.Native, ov.Resolve("fig05", 1));
        Assert.Equal(ShipArtOverride.ArtChoice.Native, ov.Resolve("fig06", 2));
    }

    [Fact]
    public void Malformed_json_degrades_to_empty_override()
    {
        var ov = ShipArtOverride.Parse("{ not json at all");
        Assert.Equal(0, ov.Count);
        Assert.Equal(ShipArtOverride.ArtChoice.Native, ov.Resolve("fig05", 1));
    }

    [Fact]
    public void Json_round_trip_preserves_entries()
    {
        var ov = new ShipArtOverride();
        ov.SetByModel("fig05", ShipArtOverride.ArtChoice.Meshy);
        ov.SetByModel("bom04", ShipArtOverride.ArtChoice.Native);
        var re = ShipArtOverride.Parse(ov.ToJson());
        Assert.Equal(2, re.Count);
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, re.Resolve("fig05", 0));
        Assert.Equal(ShipArtOverride.ArtChoice.Native, re.Resolve("bom04", 0));
    }

    [Fact]
    public void Set_by_model_clears_when_none()
    {
        var ov = new ShipArtOverride();
        ov.SetByModel("fig05", ShipArtOverride.ArtChoice.Meshy);
        Assert.Equal(ShipArtOverride.ArtChoice.Meshy, ov.Resolve("fig05", 0));
        ov.SetByModel("fig05", ShipArtOverride.ArtChoice.None);
        Assert.Equal(ShipArtOverride.ArtChoice.Native, ov.Resolve("fig05", 0));
    }

    [Fact]
    public void Save_writes_a_loadable_file()
    {
        var ov = new ShipArtOverride();
        ov.SetByModel("fig05", ShipArtOverride.ArtChoice.Meshy);
        string path = Path.Combine(Path.GetTempPath(), "ship-art-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ov.Save(path);
            Assert.True(File.Exists(path));
            var loaded = ShipArtOverride.LoadFile(path);
            Assert.Equal(ShipArtOverride.ArtChoice.Meshy, loaded.Resolve("fig05", 0));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

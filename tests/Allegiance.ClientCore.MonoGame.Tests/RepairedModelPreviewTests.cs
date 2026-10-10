using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P581 — pure tests for the repaired-Meshy preview resolution:
/// the repaired variant is used only when requested AND loaded; otherwise the
/// raw Meshy asset (and the missing-asset fallback) are unchanged. The inspected
/// hull keeps its native/ship-art.json choice regardless (this is view-only).</summary>
public class RepairedModelPreviewTests
{
    private static IReadOnlySet<string> Keys(params string[] k)
        => new HashSet<string>(k);

    [Fact]
    public void Not_preferring_repaired_returns_the_raw_asset()
    {
        var keys = Keys("dreg-scout2", "repaired/dreg-scout2");
        Assert.Equal("dreg-scout2", RepairedModelPreview.Resolve(keys, "dreg-scout2", false));
    }

    [Fact]
    public void Prefer_repaired_with_loaded_variant_returns_repaired_key()
    {
        var keys = Keys("dreg-scout2", "repaired/dreg-scout2");
        Assert.Equal("repaired/dreg-scout2", RepairedModelPreview.Resolve(keys, "dreg-scout2", true));
    }

    [Fact]
    public void Prefer_repaired_but_variant_not_loaded_falls_back_to_raw()
    {
        var keys = Keys("dreg-scout2");            // no repaired asset loaded
        Assert.Equal("dreg-scout2", RepairedModelPreview.Resolve(keys, "dreg-scout2", true));
    }

    [Fact]
    public void Empty_or_null_asset_resolves_safely()
    {
        var keys = Keys("dreg-scout2");
        Assert.Equal("", RepairedModelPreview.Resolve(keys, "", true));
        Assert.Null(RepairedModelPreview.Resolve(keys, null!, true));
    }

    [Fact]
    public void Repaired_key_is_prefixed()
    {
        Assert.Equal("repaired/dreg-scout2", RepairedModelPreview.RepairedKey("dreg-scout2"));
    }

    [Fact]
    public void Other_assets_repaired_variants_do_not_leak()
    {
        // only dreg-scout2 is repaired in P581; an unrepaired asset requested as
        // repaired falls back to raw even when another repaired asset exists.
        var keys = Keys("gt-fighter", "repaired/dreg-scout2");
        Assert.Equal("gt-fighter", RepairedModelPreview.Resolve(keys, "gt-fighter", true));
    }
}

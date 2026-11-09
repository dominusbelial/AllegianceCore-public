using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

public sealed class HudSkinCatalogTests
{
    [Fact]
    public void Built_in_skins_load_classic_and_modern()
    {
        var catalog = new HudSkinCatalog();
        Assert.Equal(2, catalog.Installed.Count);
        Assert.NotNull(catalog.TryGet(HudSkinIds.Classic));
        Assert.NotNull(catalog.TryGet(HudSkinIds.Modern));
    }

    [Fact]
    public void Classic_defaults_match_legacy_style_defaults()
    {
        var classic = new HudSkinCatalog().TryGet(HudSkinIds.Classic)!;
        Assert.False(classic.Defaults.SelectionBracket);
        Assert.False(classic.Defaults.RadarDeclutter);
        Assert.Equal(HudLevel.Tactical, classic.Defaults.Level);
    }

    [Fact]
    public void Modern_defaults_match_legacy_style_defaults()
    {
        var modern = new HudSkinCatalog().TryGet(HudSkinIds.Modern)!;
        Assert.True(modern.Defaults.SelectionBracket);
        Assert.True(modern.Defaults.RadarDeclutter);
        Assert.Equal(HudLevel.Tactical, modern.Defaults.Level);
    }

    [Fact]
    public void Resolve_unknown_id_returns_modern_with_one_diagnostic()
    {
        var catalog = new HudSkinCatalog();
        Assert.Null(catalog.TryGet("no-such-skin"));
        var modern = catalog.Resolve("no-such-skin");
        Assert.Equal(HudSkinIds.Modern, modern.Id);
    }

    [Fact]
    public void Reject_invalid_definition_and_keep_prior()
    {
        var catalog = new HudSkinCatalog();
        Assert.False(catalog.TryAdd("{ \"Schema\": \"hud-skin-1\" }"));
        Assert.Equal(2, catalog.Installed.Count);
    }

    [Fact]
    public void Reject_definition_with_non_normalized_id()
    {
        var catalog = new HudSkinCatalog();
        Assert.False(catalog.TryAdd(
            "{ \"Schema\": \"hud-skin-1\", \"Id\": \"Modern\", \"Name\": \"x\", \"Version\": \"1\", \"Defaults\": {\"SelectionBracket\":true,\"RadarDeclutter\":true,\"Level\":\"Tactical\"} }"));
    }

    [Fact]
    public void Reject_definition_with_unknown_level()
    {
        var catalog = new HudSkinCatalog();
        Assert.False(catalog.TryAdd(
            "{ \"Schema\": \"hud-skin-1\", \"Id\": \"bad\", \"Name\": \"x\", \"Version\": \"1\", \"Defaults\": {\"SelectionBracket\":true,\"RadarDeclutter\":true,\"Level\":99} }"));
    }
}

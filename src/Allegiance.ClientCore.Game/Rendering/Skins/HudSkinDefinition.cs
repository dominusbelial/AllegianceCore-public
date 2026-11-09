namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>A validated, versioned skin definition. Phase-1B definitions carry
/// only identity, display metadata and presentation defaults; palette, font,
/// stroke and widget bindings are added in later slices.</summary>
/// <param name="Schema">Schema version, e.g. <c>hud-skin-1</c>.</param>
/// <param name="Id">Canonical id matching <see cref="HudSkinIds"/> constants.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Version">Semver-style definition version.</param>
/// <param name="Defaults">Presentation defaults overridden by player options.</param>
public sealed record HudSkinDefinition(
    string Schema,
    string Id,
    string Name,
    string Version,
    HudSkinPresentationDefaults Defaults);

/// <summary>Presentation defaults that a skin recommends and a player option may
/// override. These values are intentionally independent of the legacy
/// <see cref="HudStyle"/> enum so new skins can supply their own defaults.</summary>
public sealed record HudSkinPresentationDefaults(
    bool SelectionBracket,
    bool RadarDeclutter,
    HudLevel Level);

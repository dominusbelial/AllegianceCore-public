namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Explicit services for the production passes that still live in
/// Game1. Bind once at LoadContent, never construct closures during Draw.
/// Callbacks consume the same semantic snapshot as the renderer.</summary>
public sealed record HudSkinResources(
    Action<HudModel> Radar,
    Action<HudModel, bool> ShipTags,
    Action<HudModel> PodDeath,
    Action<HudModel, bool> Command,
    Action<HudModel> Chat,
    Action<HudModel> Cargo,
    Action<HudModel> FlightParts);

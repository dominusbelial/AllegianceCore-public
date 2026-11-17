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
    Action<HudModel> FlightParts)
{
    /// <summary>
    /// P1040 — the command-camera chrome only: the native NORTH marker and the drag
    /// rectangle the command view draws (no command-bar art, no port summary lines). A skin
    /// that draws its own command console (Jet) calls this so the camera affordances stay
    /// native while its vector panel replaces the Classic art. Null keeps the pre-P1040
    /// behaviour for callers that do not bind it.
    /// </summary>
    public Action<HudModel>? CommandChrome { get; init; }
}

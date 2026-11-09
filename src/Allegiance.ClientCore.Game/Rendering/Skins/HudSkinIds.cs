namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Canonical identifiers for the installed HUD skins.
/// Ids are lowercase, whitespace-trimmed ASCII strings; all persistence and
/// comparison uses <see cref="Normalize(string?)"/>.</summary>
public static class HudSkinIds
{
    public const string Classic = "classic";
    public const string Modern = "modern";

    /// <summary>Returns the canonical form of <paramref name="id"/>,
    /// or <see cref="Modern"/> for null/blank input.</summary>
    public static string Normalize(string? id) =>
        string.IsNullOrWhiteSpace(id) ? Modern : id.Trim().ToLowerInvariant();
}

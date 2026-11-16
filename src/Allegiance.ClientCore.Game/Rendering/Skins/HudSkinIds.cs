namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Canonical identifiers for the installed HUD skins.
/// Ids are lowercase, whitespace-trimmed ASCII strings; all persistence and
/// comparison uses <see cref="Normalize(string?)"/>.</summary>
public static class HudSkinIds
{
    public const string Classic = "classic";
    public const string Modern = "modern";
    /// <summary>P1035 — the Jet military vector skin (P972 §4). Installed but
    /// gated behind the port's <c>hud.jetSkin</c> visual flag until it is
    /// feature-complete; see <see cref="VisualFlags.JetSkinKey"/>.</summary>
    public const string Jet = "jet";

    /// <summary>Returns the canonical form of <paramref name="id"/>,
    /// or <see cref="Modern"/> for null/blank input.</summary>
    public static string Normalize(string? id) =>
        string.IsNullOrWhiteSpace(id) ? Modern : id.Trim().ToLowerInvariant();
}

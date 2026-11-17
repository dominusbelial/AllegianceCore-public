namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1041 — the Jet skin's colour variants (P972 §4.1, §2.4). A variant is
/// SEPARATE persisted state (<c>hud.json</c>'s <c>JetPalette</c>), not a skin id:
/// the F-key skin cycle walks installed skins only, and the variant only swaps
/// the palette of the Jet skin that is already active. Unknown, blank or
/// missing values fall back to <see cref="Green"/> (the shipped variant), exactly
/// like an unknown skin id falls back to Modern.
/// </summary>
public static class JetPaletteIds
{
    /// <summary>The green variant, <c>#79FFC0</c> primary (P1035's shader
    /// variant, the Jet baseline every story has been built against).</summary>
    public const string Green = "green";

    /// <summary>The amber variant (P1041): the ENTIRE palette swaps — primary,
    /// dim, hot, plate and keyline — not only alerts (P972 §4.1).</summary>
    public const string Amber = "amber";

    /// <summary>Every variant, in the selector's order.</summary>
    public static readonly string[] All = [Green, Amber];

    /// <summary>True when <paramref name="id"/> is a variant this build ships.</summary>
    public static bool IsKnown(string? id) =>
        string.Equals(id, Green, StringComparison.Ordinal) || string.Equals(id, Amber, StringComparison.Ordinal);

    /// <summary>The canonical form of <paramref name="id"/>: trimmed, lower-case,
    /// and <see cref="Green"/> for anything the build does not ship.</summary>
    public static string Normalize(string? id)
    {
        var normalized = string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();
        return IsKnown(normalized) ? normalized! : Green;
    }

    /// <summary>The other variant (the selector's Left/Right step).</summary>
    public static string Next(string? id) => Normalize(id) == Green ? Amber : Green;

    /// <summary>One diagnostic for an unreadable persisted value, like the skin
    /// loader's unknown-id line; the caller keeps its stored fallback.</summary>
    public static void LogFallback(string? id)
    {
        if (_fallbackLogged || IsKnown(id)) return;
        _fallbackLogged = true;
        Console.WriteLine($"[hud.palette] Unknown Jet palette '{id}'; falling back to '{Green}'.");
    }

    private static bool _fallbackLogged;
}

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>A validated, versioned skin definition. Phase-1B definitions carry
/// identity, display metadata and presentation defaults; P1035 adds the optional
/// palette block the Jet skin needs (colour is presentation data, never
/// behaviour).</summary>
/// <param name="Schema">Schema version, e.g. <c>hud-skin-1</c>.</param>
/// <param name="Id">Canonical id matching <see cref="HudSkinIds"/> constants.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Version">Semver-style definition version.</param>
/// <param name="Defaults">Presentation defaults overridden by player options.</param>
/// <param name="Palette">P1035 — optional semantic palette; null for skins that
/// keep their colours in code (Classic/Modern), so their definitions stay
/// byte-identical.</param>
public sealed record HudSkinDefinition(
    string Schema,
    string Id,
    string Name,
    string Version,
    HudSkinPresentationDefaults Defaults,
    HudSkinPalette? Palette = null);

/// <summary>Presentation defaults that a skin recommends and a player option may
/// override. These values are intentionally independent of the legacy
/// <see cref="HudStyle"/> enum so new skins can supply their own defaults.</summary>
public sealed record HudSkinPresentationDefaults(
    bool SelectionBracket,
    bool RadarDeclutter,
    HudLevel Level);

/// <summary>P1035 — the optional palette block of a skin definition (P972 §4.1).
/// Every value is a <c>#RRGGBB</c> string; the block is all-or-nothing: a
/// definition that carries a palette must carry all five roles and every role
/// must parse, or the whole definition is rejected (see <see cref="HudSkinLoader"/>).</summary>
/// <param name="Primary">Main symbology ink (Jet green: <c>#79FFC0</c>).</param>
/// <param name="Dim">Secondary/out-of-range ink (<c>#3DA87B</c>).</param>
/// <param name="Hot">Alert/flash ink (<c>#C9FFE3</c>).</param>
/// <param name="Plate">Near-black backing behind readouts.</param>
/// <param name="Keyline">Near-black outer under-stroke every glyph carries.</param>
public sealed record HudSkinPalette(
    string Primary,
    string Dim,
    string Hot,
    string Plate,
    string Keyline);

/// <summary>P1035 — an 8-bit RGB colour parsed from a palette hex string. Kept
/// MonoGame-free so <see cref="HudSkinLoader"/> validates palettes headlessly; a
/// renderer converts it to its own colour type once, at construction.</summary>
public readonly record struct HudSkinColor(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RRGGBB</c>. The leading <c>#</c> is required (the
    /// diagrams and the P972 palette are written that way); anything else —
    /// short/long forms, named colours, null — returns false.</summary>
    public static bool TryParseHex(string? text, out HudSkinColor color)
    {
        color = default;
        if (text is null || text.Length != 7 || text[0] != '#') return false;
        int r = HexPair(text, 1), g = HexPair(text, 3), b = HexPair(text, 5);
        if (r < 0 || g < 0 || b < 0) return false;
        color = new((byte)r, (byte)g, (byte)b);
        return true;
    }

    private static int HexPair(string text, int index)
    {
        int hi = HexDigit(text[index]), lo = HexDigit(text[index + 1]);
        return hi < 0 || lo < 0 ? -1 : (hi << 4) | lo;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

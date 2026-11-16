using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Validates and loads a <see cref="HudSkinDefinition"/> from JSON.
/// Validation is all-or-nothing: any problem throws <see cref="HudSkinLoadException"/>.</summary>
public static class HudSkinLoader
{
    public static HudSkinDefinition Load(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    public static HudSkinDefinition Load(string json)
    {
        HudSkinDefinition? def;
        try
        {
            def = JsonSerializer.Deserialize(json, HudSkinJson.Default.HudSkinDefinition);
        }
        catch (JsonException ex)
        {
            throw new HudSkinLoadException($"Invalid skin JSON: {ex.Message}", ex);
        }

        if (def is null) throw new HudSkinLoadException("Skin definition is null.");
        Validate(def);
        return def;
    }

    private static void Validate(HudSkinDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Schema))
            throw new HudSkinLoadException("Missing schema.");
        if (string.IsNullOrWhiteSpace(def.Id))
            throw new HudSkinLoadException("Missing id.");
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new HudSkinLoadException("Missing name.");
        if (string.IsNullOrWhiteSpace(def.Version))
            throw new HudSkinLoadException("Missing version.");
        if (def.Id != HudSkinIds.Normalize(def.Id))
            throw new HudSkinLoadException($"Id '{def.Id}' is not normalized (must be lowercase, no surrounding whitespace).");
        if (def.Defaults is null)
            throw new HudSkinLoadException("Missing defaults.");
        if (!Enum.IsDefined(def.Defaults.Level))
            throw new HudSkinLoadException($"Unknown level '{(int)def.Defaults.Level}'.");
        ValidatePalette(def.Palette);
    }

    /// <summary>P1035 — the optional palette block is all-or-nothing: a definition
    /// that carries one must carry every role (an empty string is "missing"), and
    /// every role must be a <c>#RRGGBB</c> colour. A bad colour rejects the whole
    /// definition so the prior skin stays active (P972 §2.3).</summary>
    private static void ValidatePalette(HudSkinPalette? palette)
    {
        if (palette is null) return;   // Classic/Modern keep colours in code.
        ValidateRole("Primary", palette.Primary);
        ValidateRole("Dim", palette.Dim);
        ValidateRole("Hot", palette.Hot);
        ValidateRole("Plate", palette.Plate);
        ValidateRole("Keyline", palette.Keyline);

        static void ValidateRole(string role, string? value)
        {
            if (!HudSkinColor.TryParseHex(value, out _))
                throw new HudSkinLoadException($"Palette.{role} '{value}' is not a #RRGGBB colour.");
        }
    }
}

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
    }
}

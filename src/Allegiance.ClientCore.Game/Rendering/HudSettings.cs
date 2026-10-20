using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

public enum HudStyle { Classic, Modern }

/// <summary>Presentation only. A missing HUD preference on an existing profile
/// migrates to Classic; a fresh profile starts Modern. Persist the decision at boot,
/// before the intro screen can create player.json.</summary>
public sealed class HudSettings
{
    public HudStyle Style { get; set; } = HudStyle.Modern;
    public float Size { get; set; } = 1;
    public bool ColorBlindSafe { get; set; }
    /// <summary>HUD-4 — native optional round radar border (WinTrek.cpp:2715
    /// defaults false, i.e. square radar).</summary>
    public bool RoundRadar { get; set; }
    /// <summary>HUD-10 — the full-viewport translucent-red damage wash is a port
    /// extension (native flashes only the hull/shield gauge art, dialog.mdl:2403-2506).
    /// Default on to preserve existing behaviour; set false for native-only flash.</summary>
    public bool DamageWash { get; set; } = true;
    /// <summary>HUD-11 — native <c>ChatLines</c> preference
    /// (<c>LoadPreference("ChatLines", 7)</c>, WinTrek.cpp:3317, range 1-10
    /// SetChatLines WinTrek.cpp:5262-5280). This is the CONFIGURED chat-pane
    /// height (native <c>NumChatLines</c>), not the number of lines currently
    /// shown: the vote/critical-tip banner offsets read it.</summary>
    public int ChatLines { get; set; } = 7;
    public static string DefaultPath => Environment.GetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS")
        ?? Path.Combine(IntroPreferences.DirectoryPath, "hud.json");
    public void Normalize()
    {
        if (!Enum.IsDefined(Style)) Style = HudStyle.Classic;
        Size = float.IsFinite(Size) ? Math.Clamp(Size, .75f, 1.5f) : 1;
        ChatLines = Math.Clamp(ChatLines, 1, 10);
    }
    public static HudSettings Load(string path, bool existingPlayer)
    {
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(path), HudSettingsJson.Default.HudSettings) ?? new();
            settings.Normalize();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new() { Style = existingPlayer ? HudStyle.Classic : HudStyle.Modern }; }
    }
    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, HudSettingsJson.Default.HudSettings));
        File.Move(path + ".tmp", path, true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HudSettings))]
internal partial class HudSettingsJson : JsonSerializerContext;

using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

public sealed class HudSkinMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p981-migration-" + Guid.NewGuid());
    public HudSkinMigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string HudPath() => System.IO.Path.Combine(_dir, "hud.json");

    [Fact]
    public void Fresh_profile_with_no_file_gets_modern()
    {
        var loaded = HudSettings.Load(HudPath(), existingPlayer: false);
        Assert.Equal(HudSkinIds.Modern, loaded.Skin);
        Assert.Equal(HudStyle.Modern, loaded.Style);
    }

    [Fact]
    public void Existing_player_with_no_file_gets_classic()
    {
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(HudSkinIds.Classic, loaded.Skin);
        Assert.Equal(HudStyle.Classic, loaded.Style);
    }

    [Theory]
    [InlineData("\"Classic\"", HudSkinIds.Classic)]
    [InlineData("\"Modern\"", HudSkinIds.Modern)]
    [InlineData("\"classic\"", HudSkinIds.Classic)]
    [InlineData("\"modern\"", HudSkinIds.Modern)]
    [InlineData("0", HudSkinIds.Classic)]
    [InlineData("1", HudSkinIds.Modern)]
    public void Legacy_style_field_migrates_to_canonical_id(string legacy, string expectedId)
    {
        File.WriteAllText(HudPath(), $"{{ \"Style\": {legacy} }}");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(expectedId, loaded.Skin);
    }

    [Theory]
    [InlineData("\"classic\"", HudSkinIds.Classic)]
    [InlineData("\"modern\"", HudSkinIds.Modern)]
    [InlineData("\"CLASSIC\"", HudSkinIds.Classic)]
    [InlineData("\"  Modern  \"", HudSkinIds.Modern)]
    public void Skin_field_is_normalized_to_canonical_id(string raw, string expectedId)
    {
        File.WriteAllText(HudPath(), $"{{ \"Skin\": {raw} }}");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(expectedId, loaded.Skin);
    }

    [Fact]
    public void Unknown_skin_id_falls_back_to_modern()
    {
        File.WriteAllText(HudPath(), "{ \"Skin\": \"jet\" }");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(HudSkinIds.Modern, loaded.Skin);
        Assert.Equal(HudStyle.Modern, loaded.Style);
    }

    [Fact]
    public void Unknown_legacy_string_falls_back_to_modern()
    {
        File.WriteAllText(HudPath(), "{ \"Style\": \"Futuristic\" }");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(HudSkinIds.Modern, loaded.Skin);
    }

    [Fact]
    public void Unknown_legacy_number_falls_back_to_modern()
    {
        File.WriteAllText(HudPath(), "{ \"Style\": 7 }");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(HudSkinIds.Modern, loaded.Skin);
    }

    [Fact]
    public void Existing_player_no_style_field_gets_classic()
    {
        File.WriteAllText(HudPath(), "{ \"Size\": 1.25 }");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(HudSkinIds.Classic, loaded.Skin);
    }

    [Fact]
    public void Fresh_profile_no_style_field_gets_modern()
    {
        File.WriteAllText(HudPath(), "{ \"Size\": 1.25 }");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: false);
        Assert.Equal(HudSkinIds.Modern, loaded.Skin);
    }

    [Fact]
    public void Malformed_json_keeps_fallback_for_existing_player()
    {
        File.WriteAllText(HudPath(), "{ not json");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(HudSkinIds.Classic, loaded.Skin);
    }

    [Fact]
    public void Malformed_json_keeps_fallback_for_fresh_profile()
    {
        File.WriteAllText(HudPath(), "{ not json");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: false);
        Assert.Equal(HudSkinIds.Modern, loaded.Skin);
    }

    [Fact]
    public void Save_writes_skin_and_drops_legacy_style()
    {
        var settings = new HudSettings { Style = HudStyle.Classic, Size = 1.25f, ChatLines = 5 };
        settings.Save(HudPath());
        var text = File.ReadAllText(HudPath());
        Assert.Contains("\"Skin\": \"classic\"", text);
        Assert.DoesNotContain("\"Style\"", text);
        Assert.Contains("\"Size\": 1.25", text);
        Assert.Contains("\"ChatLines\": 5", text);
    }

    [Fact]
    public void Round_trip_preserves_skin_and_unrelated_options()
    {
        var original = new HudSettings
        {
            Style = HudStyle.Modern,
            Size = 1.5f,
            ChatLines = 4,
            RadarLabelMax = 8,
            ControlHintSeen = true,
            ColorBlindSafe = true,
        };
        original.Save(HudPath());
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        Assert.Equal(HudSkinIds.Modern, loaded.Skin);
        Assert.Equal(HudStyle.Modern, loaded.Style);
        Assert.Equal(1.5f, loaded.Size);
        Assert.Equal(4, loaded.ChatLines);
        Assert.Equal(8, loaded.RadarLabelMax);
        Assert.True(loaded.ControlHintSeen);
        Assert.True(loaded.ColorBlindSafe);
    }

    [Fact]
    public void Legacy_classic_file_round_trips_to_skin_and_keeps_options()
    {
        File.WriteAllText(HudPath(),
            "{ \"Style\": \"Classic\", \"Size\": 0.75, \"ControlHintSeen\": true, \"ChatLines\": 3 }");
        var loaded = HudSettings.Load(HudPath(), existingPlayer: true);
        loaded.Save(HudPath());
        var text = File.ReadAllText(HudPath());
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(HudSkinIds.Classic, doc.RootElement.GetProperty("Skin").GetString());
        Assert.False(doc.RootElement.TryGetProperty("Style", out _));
        Assert.Equal(0.75, doc.RootElement.GetProperty("Size").GetDouble());
        Assert.True(doc.RootElement.GetProperty("ControlHintSeen").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("ChatLines").GetInt32());
    }

    [Fact]
    public void SelectionBracket_and_RadarDeclutter_defaults_follow_resolved_skin()
    {
        var classic = new HudSettings { Skin = HudSkinIds.Classic };
        var modern = new HudSettings { Skin = HudSkinIds.Modern };
        Assert.False(classic.SelectionBracketOn);
        Assert.False(classic.RadarDeclutterOn);
        Assert.True(modern.SelectionBracketOn);
        Assert.True(modern.RadarDeclutterOn);
    }

    [Fact]
    public void Player_override_beats_skin_default()
    {
        var classic = new HudSettings { Skin = HudSkinIds.Classic, SelectionBracket = true, RadarDeclutter = true };
        var modern = new HudSettings { Skin = HudSkinIds.Modern, SelectionBracket = false, RadarDeclutter = false };
        Assert.True(classic.SelectionBracketOn);
        Assert.True(classic.RadarDeclutterOn);
        Assert.False(modern.SelectionBracketOn);
        Assert.False(modern.RadarDeclutterOn);
    }

    [Fact]
    public void Style_adapter_sets_skin_id()
    {
        var settings = new HudSettings { Style = HudStyle.Classic };
        Assert.Equal(HudSkinIds.Classic, settings.Skin);
        settings.Style = HudStyle.Modern;
        Assert.Equal(HudSkinIds.Modern, settings.Skin);
    }

    [Fact]
    public void Unknown_skin_resolves_to_modern_style_adapter()
    {
        var settings = new HudSettings { Skin = "no-such-skin" };
        Assert.Equal(HudStyle.Modern, settings.Style);
    }
}

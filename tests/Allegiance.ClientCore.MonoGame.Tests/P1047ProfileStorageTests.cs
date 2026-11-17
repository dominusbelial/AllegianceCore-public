using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Platform;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1047ProfileStorageTests
{
    [Fact]
    public void Existing_profile_entry_point_signatures_remain_available_to_desktop_and_Android_heads()
    {
        Assert.NotNull(typeof(PlayerConfig).GetMethod(nameof(PlayerConfig.Load), [typeof(string)]));
        Assert.NotNull(typeof(IntroPreferences).GetMethod(nameof(IntroPreferences.Load), [typeof(string)]));
        Assert.NotNull(typeof(IntroPreferences).GetMethod(nameof(IntroPreferences.Save), [typeof(string)]));
        Assert.NotNull(typeof(HudSettings).GetMethod(nameof(HudSettings.Load), [typeof(string), typeof(bool)]));
        Assert.NotNull(typeof(HudSettings).GetMethod(nameof(HudSettings.Save), [typeof(string)]));
        Assert.NotNull(typeof(KeyBindingStore).GetConstructor(Type.EmptyTypes));
    }

    [Fact]
    public void File_adapter_keeps_default_UTF8_bytes_and_atomic_rename_behavior()
    {
        string root = Path.Combine(Path.GetTempPath(), "p1047-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileProfileStorage();
            storage.CreateDirectory(root);
            string path = Path.Combine(root, "player.json");
            const string text = "{\n  \"Pilot\": \"éclair\"\n}";
            storage.WriteAllText(path + ".tmp", text);
            storage.Move(path + ".tmp", path, true);
            Assert.True(storage.Exists(path));
            Assert.False(storage.Exists(path + ".tmp"));
            Assert.Equal(Encoding.UTF8.GetBytes(text), File.ReadAllBytes(path));
            Assert.Equal(text, storage.ReadAllText(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Preferences_preserve_desktop_serializer_bytes_and_browser_round_trip()
    {
        string root = Path.Combine(Path.GetTempPath(), "p1047-profile-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "player.json");
        try
        {
            var prefs = new IntroPreferences { Pilot = "pilot", InvertMouseY = true, Enable3DSound = false, MouseSensitivity = 1.25f };
            prefs.Save(path);
            var expected = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true });
            Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
            Assert.Equal("pilot", PlayerConfig.Load(path).Pilot);
            var memory = new ResidentProfileStorage();
            prefs.Save("player.json", memory);
            Assert.Equal(expected, memory.ReadAllText("player.json"));
            Assert.True(IntroPreferences.Load("player.json", memory).InvertMouseY);
            Assert.Equal(1.25f, IntroPreferences.Load("player.json", memory).MouseSensitivity);
            Assert.Equal("pilot", PlayerConfig.Load("player.json", memory).Pilot);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Hud_atomic_save_has_identical_bytes_in_file_and_resident_stores(HudStyle style)
    {
        string root = Path.Combine(Path.GetTempPath(), "p1047-hud-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "hud.json");
        try
        {
            var memory = new ResidentProfileStorage();
            var hud = new HudSettings { Style = style };
            hud.Save(path); hud.Save("hud.json", memory);
            Assert.Equal(File.ReadAllText(path), memory.ReadAllText("hud.json"));
            Assert.False(File.Exists(path + ".tmp")); Assert.False(memory.Exists("hud.json.tmp"));
            Assert.Equal(style, HudSettings.Load("hud.json", true, memory).Style);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Keymap_file_and_resident_stores_produce_identical_documents_and_reload_presets()
    {
        string root = Path.Combine(Path.GetTempPath(), "p1047-keys-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "keymap.json");
        try
        {
            var memory = new ResidentProfileStorage();
            var desktop = new KeyBindingStore(new FileProfileStorage(), path);
            var browser = new KeyBindingStore(memory, "keymap.json");
            desktop.CycleLayout(); browser.CycleLayout();
            Assert.Equal("{\n  \"layout\": \"native\",\n  \"bindings\": {}\n}", desktop.Map.ToJson());
            Assert.Equal(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(memory.ReadAllText("keymap.json")));
            var restored = new KeyBindingStore(memory, "keymap.json");
            Assert.True(restored.HasKeymap);
            Assert.Equal(browser.Map.Layout, restored.Map.Layout);
            Assert.Equal(browser.Map.ToJson(), restored.Map.ToJson());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}

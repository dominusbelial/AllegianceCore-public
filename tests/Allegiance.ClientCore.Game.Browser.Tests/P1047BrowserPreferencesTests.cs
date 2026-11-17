using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Platform;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1047BrowserPreferencesTests
{
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Source_compiled_preferences_and_keymap_round_trip_without_files_or_secrets(HudStyle style)
    {
        var storage = new ResidentProfileStorage();
        new IntroPreferences { Pilot = "pilot", InvertMouseY = true, Enable3DSound = false }.Save("player.json", storage);
        new HudSettings { Style = style }.Save("hud.json", storage);
        var bindings = new KeyBindingStore(storage, "keymap.json");
        bindings.CycleLayout();
        Assert.Equal("{\n  \"layout\": \"native\",\n  \"bindings\": {}\n}", bindings.Map.ToJson());
        var restored = new ResidentProfileStorage();
        restored.Import(storage.Snapshot());
        Assert.Equal("pilot", PlayerConfig.Load("player.json", restored).Pilot);
        Assert.True(IntroPreferences.Load("player.json", restored).InvertMouseY);
        Assert.False(IntroPreferences.Load("player.json", restored).Enable3DSound);
        Assert.Equal(style, HudSettings.Load("hud.json", true, restored).Style);
        Assert.Equal(bindings.Map.ToJson(), new KeyBindingStore(restored, "keymap.json").Map.ToJson());
        foreach (var document in storage.Snapshot().Values)
        {
            Assert.DoesNotContain("password", document, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", document, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(typeof(ResidentProfileStorage).Assembly.GetType("Allegiance.ClientCore.MonoGame.Platform.FileProfileStorage"));
    }
}

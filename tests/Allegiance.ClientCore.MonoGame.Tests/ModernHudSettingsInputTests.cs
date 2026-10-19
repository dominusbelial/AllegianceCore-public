using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class ModernHudSettingsInputTests
{
    [Fact]
    public void Flight_settings_menu_opens_HUD_row_and_arrow_hot_switches_existing_renderers()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        void Set(string name, object value) => typeof(Game1).GetField(name, fields)!.SetValue(game, value);
        T Get<T>(string name) => (T)typeof(Game1).GetField(name, fields)!.GetValue(game)!;
        object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, fields)!.Invoke(game, args);
        var classic = (HudRenderer)RuntimeHelpers.GetUninitializedObject(typeof(HudRenderer));
        var modern = (ModernHudRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ModernHudRenderer)); GC.SuppressFinalize(modern);
        var settings = new HudSettings { Style = HudStyle.Classic };
        Set("_hudSettings", settings); Set("_hudRenderer", classic); Set("_modernHudRenderer", modern);
        Set("_consolePressEdges", new ActionPressEdges()); Set("_mainMenuVisible", true); Set("_mainMenuCursor", 6);
        Set("_flightGc", new FlightGcLatency(false)); Set("_upscaling", new UpscalingSettings());
        Set("_resolutionController", new DynamicResolutionController());
        var map = KeyBindingStore.Instance.Map; string saved = map.ToJson();
        string directory = Path.Combine(Path.GetTempPath(), "hud2-input-" + Guid.NewGuid());
        string? oldHud = Environment.GetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS");
        string? oldGraphics = Environment.GetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS");
        try
        {
            map.ResetToDefaults();
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", Path.Combine(directory, "hud.json"));
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", Path.Combine(directory, "graphics.json"));
            Call("UpdateMainMenu", new KeyboardState(Keys.Enter));
            Assert.True(Get<bool>("_graphicsSettingsVisible")); Assert.Equal(6, Get<int>("_graphicsRow"));
            Assert.Same(classic, typeof(Game1).GetProperty("ActiveHud", fields)!.GetValue(game));
            Call("UpdateGraphicsSettings", new KeyboardState(Keys.Right));
            Assert.Equal(HudStyle.Modern, settings.Style);
            Assert.Same(modern, typeof(Game1).GetProperty("ActiveHud", fields)!.GetValue(game));
            Call("UpdateGraphicsSettings", new KeyboardState(Keys.Right));
            Assert.Equal(HudStyle.Modern, settings.Style); // held key cannot toggle repeatedly
            Call("UpdateGraphicsSettings", new KeyboardState());
            Call("UpdateGraphicsSettings", new KeyboardState(Keys.Left));
            Assert.Equal(HudStyle.Classic, settings.Style);
            Assert.Equal(HudStyle.Classic, HudSettings.Load(HudSettings.DefaultPath, true).Style);
        }
        finally
        {
            map.ResetToDefaults(); map.ApplyJson(saved);
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", oldHud);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", oldGraphics);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

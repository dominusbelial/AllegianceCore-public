using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>WinTrek.cpp:10433-10437: toggle and persist ShowGrid.
/// JSON beside the keymap replaces the native registry preference.</summary>
public static class CommandGridPreference
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(KeyBindingStore.ResolvePath()) ?? ".", "console-settings.json");
    public static bool Load(string path)
    {
        try { return !File.Exists(path) || JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))?.ShowGrid != false; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return true; }
    }
    public static void Save(string path, bool visible)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Settings(visible)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"[console] could not save ShowGrid: {ex.Message}"); }
    }
    public sealed record Settings(bool ShowGrid);
}

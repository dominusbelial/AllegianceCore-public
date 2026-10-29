using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>WinTrek.cpp:10433-10437: toggle and persist ShowGrid.
/// JSON beside the keymap replaces the native registry preference. Native loads it
/// with default FALSE (<c>LoadPreference("ShowGrid", FALSE)</c>, WinTrek.cpp:3308-3309):
/// a missing or unreadable file means the grid is off outside command view.</summary>
public static class CommandGridPreference
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(KeyBindingStore.ResolvePath()) ?? ".", "console-settings.json");
    public static bool Load(string path)
    {
        try { return File.Exists(path) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))?.ShowGrid == true; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
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

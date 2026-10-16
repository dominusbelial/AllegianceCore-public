using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Portable player preferences, kept outside the installation.</summary>
public sealed record PlayerConfig(string Server = "", ushort Port = 6073, string Pilot = "pilot",
    string Lobby = "http://127.0.0.1:5000")
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllegianceCore");
    public static string FilePath => Path.Combine(DirectoryPath, "player.json");

    public static PlayerConfig Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<PlayerConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Empty player configuration: {path}");
    }
}

using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P731 — the desktop player preferences file, <c>player.json</c>. Lives OUTSIDE
/// the install (portable-bundle friendly) at
/// <c>%AppData%/AllegianceCore/player.json</c>. Remembers the last server, the
/// pilot name, the lobby URL, and a recent-server list so the intro screen
/// starts with the player's last values (native remembered the last character
/// name — <c>trekClient.SaveCharacterName</c>, introscreen.cpp:1607 — and this
/// port extends that to the server and a recent list per the task brief).
/// <para>
/// Field names deliberately match the P726 player-bundle shape
/// (<c>server</c>/<c>port</c>/<c>pilot</c>/<c>lobby</c>) so a bundled client and
/// this intro screen read the same file; <c>recent</c> is this port's addition.
/// Parsing is tolerant on load (corrupt/missing → defaults, never a crash).</para>
/// </summary>
public sealed class IntroPreferences
{
    public string Server { get; set; } = "";
    public int Port { get; set; } = 6073;
    public string Pilot { get; set; } = "pilot";
    public string Lobby { get; set; } = "http://127.0.0.1:5000";
    /// <summary>Recent server identities (<c>host</c> or <c>host:port</c>), newest
    /// first, capped to a small size.</summary>
    public List<string> Recent { get; set; } = new();

    public const int MaxRecent = 8;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllegianceCore");
    public static string FilePath => Path.Combine(DirectoryPath, "player.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static IntroPreferences Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return new IntroPreferences();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new IntroPreferences();
            return JsonSerializer.Deserialize<IntroPreferences>(json, JsonOptions) ?? new IntroPreferences();
        }
        catch (Exception)
        {
            // Corrupt file degrades to defaults; never crash the intro screen.
            return new IntroPreferences();
        }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // Preferences are best-effort; a read-only home must not crash boot.
        }
    }

    /// <summary>Record a just-joined server in <see cref="Recent"/> (newest first,
    /// de-duplicated, capped), and remember it as the last <see cref="Server"/>/<see cref="Port"/>.</summary>
    public void Remember(string host, int port)
    {
        string id = $"{host}:{port}";
        Server = host;
        Port = port;
        Recent.RemoveAll(r => r == id);
        Recent.Insert(0, id);
        if (Recent.Count > MaxRecent)
            Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }
}

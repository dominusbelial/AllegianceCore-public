using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P500 — one locally-saved connect target (label + host + port). A pure
/// value record: unit-testable without a graphics device. Host:port is the
/// identity key used for the "last used" memory and dedupe.
/// </summary>
public sealed record SavedServer(string Label, string Host, ushort Port)
{
    public string HostPort => $"{Host}:{Port}";
}

/// <summary>
/// P500 — the locally-persisted connect profile: the saved-server list, the
/// player name, the optional lobby URL, and the last-used server key. Owned by
/// the shared library and (de)serialized case-insensitively, so the Android
/// head persists one JSON file and desktop/test code can read/write the same
/// shape without an Android device.
/// <para>Serialization is deliberately tolerant on load (bad file → empty
/// profile, never a crash) so a corrupt file can't brick the connect screen.</para>
/// </summary>
public sealed class ConnectProfile
{
    public List<SavedServer> Servers { get; set; } = new();
    public string PlayerName { get; set; } = "monogame";
    public string LobbyUrl { get; set; } = "";

    /// <summary>"host:port" of the server used most recently; remembered across
    /// launches so the connect screen preselects it.</summary>
    public string? LastUsedKey { get; set; }

    /// <summary>P500 — true once the connect.json first-run seed has been
    /// folded into this profile. Guards against re-seeding connect.json after
    /// the player has deliberately emptied the saved-server list.</summary>
    public bool Initialized { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Parse a persisted profile JSON string. Unparsable / empty input
    /// degrades to an empty profile (never throws).</summary>
    public static ConnectProfile Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ConnectProfile();
        try
        {
            return JsonSerializer.Deserialize<ConnectProfile>(json, JsonOptions) ?? new ConnectProfile();
        }
        catch (Exception)
        {
            return new ConnectProfile();
        }
    }

    /// <summary>Serialize this profile to the JSON text used on disk.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// P500 — the durability seam for the connect profile. The shared game calls
/// <see cref="Load"/> on boot and <see cref="Save"/> after any edit; each head
/// supplies the backing store (Android: a JSON file in the app's private data
/// dir). Absent a store (the desktop head), the connect-screen editing
/// features are simply not offered and the existing server-browser behaves
/// exactly as before.
/// </summary>
public interface IConnectStore
{
    ConnectProfile Load();
    void Save(ConnectProfile profile);
}

/// <summary>
/// P500 — the soft-keyboard text-entry seam. The connect screen prompts for
/// labels/hosts/ports/names through this, so tests can supply a scripted fake
/// and the platform head can swap in its own keyboard UI. The shared default
/// is MonoGame's platform <c>KeyboardInput.Show(...)</c> (async; a real soft
/// keyboard on Android).
/// </summary>
public interface ITextPrompt
{
    /// <summary>Show a text prompt; returns the entered text, or null if the
    /// user cancelled.</summary>
    Task<string?> PromptAsync(string title, string description, string defaultValue);
}

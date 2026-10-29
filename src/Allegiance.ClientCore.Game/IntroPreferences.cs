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
    // Share the nested collector setting with PlayerConfig; ordinary preference saves must retain it.
    public BenchmarkCollectorConfig? Benchmark { get; set; }
    /// <summary>Recent server identities (<c>host</c> or <c>host:port</c>), newest
    /// first, capped to a small size.</summary>
    public List<string> Recent { get; set; } = new();

    /// <summary>SP-6 — remembered local skirmish choices (offline), restored when the
    /// Skirmish setup screen opens. Absent <c>null</c> leaves the defaults.</summary>
    public Allegiance.SinglePlayer.SkirmishPrefs? Skirmish { get; set; }

    /// <summary>TR-2 — the native <c>Ui.ShowStartupTrainingSuggestion</c> config flag
    /// (WinTrek/introscreen.cpp:1305, 1055): the first-run dialog suggesting the player
    /// train before going online is shown once, until they launch a mission or tick
    /// "don't ask me again", which clears the flag. Default <c>true</c> on a fresh
    /// install (native's <c>false == HasTrained</c>).</summary>
    public bool ShowStartupTrainingSuggestion { get; set; } = true;

    /// <summary>P789 — the user's game-over performance-panel toggle (the O key).
    /// Null = never toggled → the default applies: shown for Showcase matches,
    /// hidden for multiplayer/normal skirmish.</summary>
    public bool? PerfPanelShown { get; set; }

    /// <summary>P757 — the in-flight chat pane's default channel filter (native
    /// Game Options "Chat/Mute/Filter" family, WinTrek.cpp:4568-4582): true =
    /// start the pane in the native player-chats-only mode (m_bPlayerChatsOnly,
    /// chatpane.cpp:146), false = show every line (the port's historical
    /// default, since its chat log is also the console surface).</summary>
    public bool ChatFilterPlayersOnly { get; set; }

    /// <summary>P806 — native <c>FilterChatsToAll</c> (WinTrek.cpp:3271-3272,
    /// default <b>true</b>: a fresh install filters them). Native applies it in
    /// <c>ReceiveChat</c> — a message to <c>CHAT_EVERYONE</c> while in game is
    /// dropped (<c>trekigc.cpp:4493</c>) — and exposes the toggle on the Game
    /// Options → Chat/Mute/Filter submenu (<c>idmFilterChatsToAll</c>,
    /// WinTrek.cpp:4572/4706-4722).</summary>
    public bool FilterChatsToAll { get; set; } = true;

    /// <summary>P871 / SIM-0179 — native <c>m_bEnableFeedback</c>, loaded from
    /// <c>EnableFeedback</c> with a default of <b>1</b> (on):
    /// <c>m_bEnableFeedback = (LoadPreference("EnableFeedback", 1) != 0)</c>
    /// (<c>WinTrek/WinTrek.cpp:3049</c>), toggled by the Options → Game menu's
    /// <c>idmToggleEnableFeedback</c> (<c>:5308-5313,4599</c>) and gating every
    /// device call (<c>:8162-8166</c>).</summary>
    public bool EnableForceFeedback { get; set; } = true;

    /// <summary>P761 — the pilot's profile description, edited on the character
    /// sheet (native CharInfoScreen OnButtonApply sends
    /// CHARACTER_INFO_EDIT_DESCRIPTION to the club server; this port has no
    /// club channel, so the applied text is stored LOCALLY here — the parity
    /// row stays partial for exactly that reason).</summary>
    public string PilotDescription { get; set; } = "";

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

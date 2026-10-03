using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P500 — the Android head's <c>connect.json</c> values. Split out of the
/// Android project into the shared library so the case-handling + fallback
/// logic is unit-testable without an Android device or asset manager.
/// </summary>
public sealed class ConnectConfigValues
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2326;
    public string PlayerName { get; set; } = "android";

    /// <summary>P500 — optional lobby-directory URL. When set, the connect
    /// screen merges that lobby's <c>/api/servers</c> listings into the list
    /// via the existing server-browser poll. Blank = no lobby (221 runs only
    /// the game server, so manual entry is the norm).</summary>
    public string LobbyUrl { get; set; } = "";
}

/// <summary>
/// P500 — pure parser for the Android head's first-run seed file
/// <c>connect.json</c> (Assets). Host/port/name + optional lobby URL, all
/// camelCase on disk, matched case-insensitively.
/// <para><b>Bug fixed here:</b> P499's <c>AndroidConnectConfig.Load</c> used
/// <c>JsonSerializer.Deserialize&lt;AndroidConnectConfig&gt;(json)</c> with
/// default <b>case-sensitive</b> options while the committed connect.json uses
/// camelCase keys — so nothing ever matched, no exception was thrown, and the
/// app silently ran the hard-coded defaults. It was only noticed when a
/// 221-pointed APK still dialed <c>127.0.0.1:2326</c>. Reading with
/// <c>PropertyNameCaseInsensitive = true</c> makes a camelCase file with
/// non-default values actually honored.</para>
/// </summary>
public static class ConnectConfigJson
{
    /// <summary>Case-insensitive web-JSON binding (the FIX; see class doc).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public sealed record ParseResult(ConnectConfigValues? Values, bool AnyFieldRecognized, string? Warning)
    {
        public bool HasConfig => Values is not null && AnyFieldRecognized;
    }

    /// <summary>
    /// Parse connect.json text. <c>Values</c> is non-null whenever the JSON
    /// parses (fields bound case-insensitively, defaults for anything absent).
    /// <c>AnyFieldRecognized=false</c> + a warning is returned when the file is
    /// present and parsable but not one known field appeared — the "file
    /// present but no field recognised" case the caller must warn about (it
    /// means schema drift / an entirely foreign file).
    /// </summary>
    public static ParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ParseResult(null, false, "connect.json is empty");

        try
        {
            using var doc = JsonDocument.Parse(json);
            bool hasHost = false, hasPort = false, hasName = false, hasLobby = false;
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    switch (prop.Name.ToLowerInvariant())
                    {
                        case "host": hasHost = true; break;
                        case "port": hasPort = true; break;
                        case "playername": hasName = true; break;
                        case "lobbyurl": hasLobby = true; break;
                    }
                }
            }

            var values = JsonSerializer.Deserialize<ConnectConfigValues>(json, Options);
            bool any = hasHost || hasPort || hasName || hasLobby;
            if (values is null || !any)
            {
                return new ParseResult(values, false,
                    "connect.json parsed but no known field recognised (host/port/playerName/lobbyUrl)");
            }
            return new ParseResult(values, true, null);
        }
        catch (Exception ex)
        {
            return new ParseResult(null, false, $"connect.json unparsable: {ex.Message}");
        }
    }
}

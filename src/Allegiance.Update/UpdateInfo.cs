using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Update;

/// <summary>
/// P810 — the lobby's version advertisement, the HTTP analogue of native's
/// <c>AUTO_UPDATE_INFO(253)</c> (<c>lobby/messageslc.h:45-52</c>: FTP
/// site/dir/account/password + the official file-list CRC). The port carries no
/// credentials at all — just which client version is current, which one is the
/// hard minimum, and where the signed feed lives. All three fields are optional:
/// a lobby that advertises nothing leaves every client untouched, exactly like
/// a native lobby with AutoUpdate unconfigured.
/// </summary>
public sealed record LobbyUpdateInfo
{
    /// <summary>The lobby's current client release (e.g. "2.0.0").</summary>
    public string? CurrentVersion { get; init; }
    /// <summary>Clients older than this must update before joining.</summary>
    public string? MinimumVersion { get; init; }
    /// <summary>The signed feed root (latest.json + manifests + files).</summary>
    public string? FeedUrl { get; init; }
}

/// <summary>Fetches <c>GET /api/update/info</c> from a lobby directory.
/// Returns null when the lobby does not answer (older lobby, offline) — callers
/// then skip the update check entirely.</summary>
public static class LobbyUpdateInfoClient
{
    public static async Task<LobbyUpdateInfo?> FetchAsync(string lobbyBaseUrl, HttpClient http, CancellationToken ct = default)
    {
        try
        {
            var url = lobbyBaseUrl.TrimEnd('/') + "/api/update/info";
            return await http.GetFromJsonAsync(url, UpdateInfoJsonContext.Default.LobbyUpdateInfo, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LobbyUpdateInfo))]
internal sealed partial class UpdateInfoJsonContext : JsonSerializerContext;

/// <summary>
/// The version gate shared by the client and the server: is <paramref name="candidate"/>
/// strictly newer than <paramref name="installed"/>? Unparsable versions never
/// count as newer (fail-safe: an old build never "updates" to garbage).
/// </summary>
public static class UpdateVersionGate
{
    public static bool IsNewer(string? candidate, string? installed)
        => UpdateClient.TryParseVersion(candidate, out var c)
           && UpdateClient.TryParseVersion(installed, out var i)
           && c > i;

    /// <summary>Is <paramref name="installed"/> older than the required minimum
    /// (the "lobby says the build is old" trigger, native
    /// <c>lobby/client.cpp:272-281</c>)?</summary>
    public static bool IsTooOld(string? installed, string? minimum)
        => IsNewer(minimum, installed);
}

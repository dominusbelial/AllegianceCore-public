using System.Text.Json.Serialization;
using Allegiance.Core.Transport;

namespace Allegiance.ClientCore.Browser.Pages;

// Deliberately only the public discovery fields required by this slice.
public sealed record BrowserServer(string Id, string Name, int Port, string MissionName, string CoreFile,
    bool Verified, string? IdentityServerId, string? WebSocketUrl, bool PasswordRequired = false,
    string? MinClientVersion = null, BrowserMission[]? Missions = null)
{
    // P1052/P1051: the raw optional field is kept; every use goes through Route(),
    // which applies the directory's rule (strict public WSS, or loopback routes
    // only when the lobby itself is on loopback). Invalid routes yield null.
    public string? Route(bool loopbackDirectory) => WebSocketEndpoint.NormalizeServerListing(WebSocketUrl, loopbackDirectory);
}
public sealed record BrowserMission(uint Cookie, string Name, bool PasswordRequired = false);

/// <summary>P1061 — the published generation's build identity. The release pipeline writes
/// this beside the bundle; it is what LOGONREQ <c>ClientVersion</c> reports so a server's
/// advertised <c>minClientVersion</c> is enforced, never bypassed. Absent file = unknown.</summary>
public sealed record BrowserClientVersion(string Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrowserServer[]))]
[JsonSerializable(typeof(BrowserClientVersion))]
internal partial class BrowserJoinJson : JsonSerializerContext;

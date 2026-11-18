using System.Text.Json.Serialization;
using Allegiance.Core.Transport;

namespace Allegiance.ClientCore.Browser.Pages;

// Deliberately only the public discovery fields required by this slice.
public sealed record BrowserServer(string Id, string Name, int Port, string MissionName, string CoreFile,
    bool Verified, string? IdentityServerId, string? WebSocketUrl, bool PasswordRequired = false,
    string? MinClientVersion = null, BrowserMission[]? Missions = null)
{
    private string? _webSocketUrl = WebSocketEndpoint.NormalizeServerListing(WebSocketUrl);
    public string? WebSocketUrl
    {
        get => _webSocketUrl;
        init => _webSocketUrl = WebSocketEndpoint.NormalizeServerListing(value);
    }
}
public sealed record BrowserMission(uint Cookie, string Name, bool PasswordRequired = false);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrowserServer[]))]
internal partial class BrowserJoinJson : JsonSerializerContext;

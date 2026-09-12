namespace Allegiance.Lobby;

/// <summary>
/// The stored server-directory entity. Includes the ownership <see cref="Token"/>
/// that proves a caller may update or delete this listing. This type is internal
/// to the app: it is never returned from a read endpoint.
/// </summary>
internal sealed record ServerListing(
    string Id,
    string Token,
    string Name,
    string Address,
    int Port,
    int PlayerCount,
    int MaxPlayers,
    string MissionName,
    string CoreFile,
    string Version,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>
/// The public, read-only projection of a <see cref="ServerListing"/> — the exact
/// same fields <b>minus</b> <c>Token</c>. Kept as a genuinely separate type
/// (rather than <c>[JsonIgnore]</c> on the internal record) so that a future field
/// addition to <see cref="ServerListing"/> can never accidentally leak the token
/// into a public response: there is simply nowhere for it to land here.
/// </summary>
public sealed record ServerListingDto(
    string Id,
    string Name,
    string Address,
    int Port,
    int PlayerCount,
    int MaxPlayers,
    string MissionName,
    string CoreFile,
    string Version,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>
/// The payload a caller submits to <c>POST /api/servers/register</c>. Every
/// field except <see cref="Token"/> describes the server itself. When
/// <see cref="Token"/> is null/blank the call creates a brand-new listing; when
/// it holds the token returned by a previous registration it is an idempotent
/// heartbeat that refreshes that same listing in place.
/// </summary>
public sealed class RegisterRequest
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public int Port { get; set; }
    public int? PlayerCount { get; set; }
    public int? MaxPlayers { get; set; }
    public string? MissionName { get; set; }
    public string? CoreFile { get; set; }
    public string? Version { get; set; }

    /// <summary>The ownership token; null/blank means "new registration".</summary>
    public string? Token { get; set; }
}

/// <summary>
/// The result of a successful register/heartbeat call — the listing id and the
/// ownership token the caller must present to subsequently update or delete it.
/// </summary>
public sealed record RegisterResponse(string Id, string Token);

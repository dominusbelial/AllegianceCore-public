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
    DateTimeOffset LastSeenUtc,
    /// <summary>P761b — the server's pilot names, kept ONLY when the server
    /// opted in (<see cref="ServerListing.PublishPlayers"/>). They are never part
    /// of the public listing; they answer the per-name find-player query only.</summary>
    IReadOnlyList<string> Players,
    /// <summary>P761b — the per-server opt-in that lets this directory answer
    /// find-player queries from its pilot list. Default OFF.</summary>
    bool PublishPlayers);

/// <summary>
/// The public, read-only projection of a <see cref="ServerListing"/> — the exact
/// same fields <b>minus</b> <c>Token</c>. Kept as a genuinely separate type
/// (rather than <c>[JsonIgnore]</c> on the internal record) so that a future field
/// addition to <see cref="ServerListing"/> can never accidentally leak the token
/// into a public response: there is simply nowhere for it to land here.
/// </summary>
/// <para><b>P761b — no pilot names.</b> The public listing carries a player COUNT
/// only. Publishing every server's roster on an open endpoint would leak the whole
/// player base to anyone; native's FIND_PLAYER asked the lobby about exactly ONE
/// name (<c>WinTrek/gamescreen.cpp:1348-1352</c> → <c>FM_C_FIND_PLAYER</c>). That
/// query is the dedicated <c>GET /api/servers/findplayer?name=</c> endpoint, which
/// answers only which server(s) that one pilot sits on, and only for servers that
/// opted in (<see cref="FindPlayerResultDto"/>).</para>
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
/// P761b — one hit of the per-name find-player query (<c>GET /api/servers/findplayer?name=</c>):
/// the server the named pilot is on, with the same public fields as
/// <see cref="ServerListingDto"/> and NOTHING about any other pilot.
/// </summary>
public sealed record FindPlayerResultDto(
    string Id,
    string Name,
    string Address,
    int Port,
    int MaxPlayers,
    string MissionName,
    string CoreFile,
    string Version);

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
    /// <summary>P761 — the server's connected pilot names, the data native's
    /// FIND_PLAYER round trip searches (<c>WinTrek/gamescreen.cpp:1348-1352</c> →
    /// <c>FM_C_FIND_PLAYER</c>). Sanitized like every other caller-supplied
    /// field; capped per listing. <b>Ignored unless
    /// <see cref="PublishPlayers"/> is set</b> (P761b privacy fix).</summary>
    public List<string>? Players { get; set; }

    /// <summary>P761b — per-server opt-in for the directory's find-player data:
    /// a server that does not set this never has its pilot names stored or
    /// answered. Default <c>false</c> — the lobby cannot authenticate a
    /// self-hosted server, so publishing names is the server owner's explicit
    /// choice.</summary>
    public bool PublishPlayers { get; set; }

    /// <summary>The ownership token; null/blank means "new registration".</summary>
    public string? Token { get; set; }
}

/// <summary>
/// The result of a successful register/heartbeat call — the listing id and the
/// ownership token the caller must present to subsequently update or delete it.
/// </summary>
public sealed record RegisterResponse(string Id, string Token);

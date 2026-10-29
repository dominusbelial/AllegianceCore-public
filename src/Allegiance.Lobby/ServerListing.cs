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
    bool PublishPlayers,
    /// <summary>P806 — the game settings the SERVER-LIST FILTERS and the
    /// status/skill/settings SORT columns read. Native carries the same data in
    /// the <c>MissionParams</c> the lobby hands the client inside
    /// <c>MissionInfo</c> (<c>clintlib/clintlib.h:540-557</c>: AllowDevelopments /
    /// LimitedLives / ScoresCount / WasObjectModelCreated / GoalConquest /
    /// GoalDeathMatch / GoalProsperity / GoalCountdown / GoalArtifacts /
    /// GoalFlags / GoalTerritory / MinRank / MaxRank / InProgress). Null when a
    /// (pre-P806) host did not publish it — the client then cannot prove either
    /// side of a Yes/No filter and hides the listing while that flag is set.
    /// </summary>
    GameParamsDto? Game = null,
    bool Verified = false,
    string? IdentityServerId = null,
    /// <summary>P810 — the minimum client version this server accepts, when it
    /// declares one. Null = no requirement advertised.</summary>
    string? MinClientVersion = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<MissionListingDto>? Missions = null,
    // Native server-list capacity, lobby/client.cpp:361-362; omitted max uses
    // the lobby constructor's default (lobby/server.cpp:463).
    int CurrentGames = 0, int MaxGames = 20);

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
    DateTimeOffset LastSeenUtc,
    /// <summary>P806 — the registered game settings (same shape as
    /// <see cref="GameParamsDto"/>). Null for a host that publishes none.</summary>
    GameParamsDto? Game = null,
    bool Verified = false,
    string? IdentityServerId = null,
    /// <summary>P810 — the minimum client version this server requires, when it
    /// declares one (see the internal <see cref="ServerListing.MinClientVersion"/>).
    /// Null = no requirement advertised.</summary>
    string? MinClientVersion = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<MissionListingDto>? Missions = null,
    // Native server-list capacity, lobby/client.cpp:361-362; omitted max uses
    // the lobby constructor's default (lobby/server.cpp:463).
    int CurrentGames = 0, int MaxGames = 20);

/// <summary>
/// P806 — the native mission settings the server-list screen filters and sorts
/// on, as published by a registering server. Every field mirrors one native
/// <c>MissionInfo</c> accessor (<c>clintlib/clintlib.h:540-557</c>):
/// <list type="bullet">
/// <item><see cref="AllowDevelopments"/> — <c>bAllowDevelopments</c> (igc.h:1128);</item>
/// <item><see cref="Lives"/> — <c>iLives</c> (igc.h:1185); <c>0x7fff</c> =
/// unlimited, so <c>LimitedLives()</c> is <c>Lives != 0x7fff</c>;</item>
/// <item><see cref="ScoresCount"/> — <c>bScoresCount</c> (igc.h:1141);</item>
/// <item><see cref="ObjectModelCreated"/> — <c>bObjectModelCreated</c>
/// (igc.h:1150), the native "Zone" filter column;</item>
/// <item><see cref="InProgress"/> — <c>fInProgress</c> (the status/running-time
/// sort input);</item>
/// <item><see cref="GoalConquest"/>/<see cref="GoalDeathmatch"/>/<see cref="GoalProsperity"/>/<see cref="GoalCountdown"/>/<see cref="GoalArtifacts"/>/<see cref="GoalFlags"/>/<see cref="GoalTerritory"/> —
/// the seven winning-condition bits (<c>igc.h:1511-1540</c>);</item>
/// <item><see cref="MinRank"/>/<see cref="MaxRank"/> — <c>iMinRank</c>/<c>iMaxRank</c>
/// (igc.h:1196-1197), the skill sort column;</item>
/// <item><see cref="AllowFriendlyFire"/>/<see cref="AllowAlliedViz"/> — P856,
/// <c>bAllowFriendlyFire</c>/<c>bAllowAlliedViz</c> (igc.h:1149,1127), the two
/// create-game options the native Game Details summary prints.</item>
/// </list>
/// These are <b>native wire-carried</b> values: the native client gets them in
/// the mission definition the lobby relays (<c>MissionInfo::GetMissionParams()</c>),
/// not by inspecting a running game. In this port the HTTP directory is the
/// lobby, so the same fields ride the register/heartbeat payload.
/// </summary>
public sealed record GameParamsDto(
    bool AllowDevelopments,
    int Lives,
    bool ScoresCount,
    bool ObjectModelCreated,
    bool InProgress,
    bool GoalConquest,
    bool GoalDeathmatch,
    bool GoalProsperity,
    bool GoalCountdown,
    bool GoalArtifacts,
    bool GoalFlags,
    bool GoalTerritory,
    short MinRank,
    short MaxRank,
    /// <summary>P856 — <c>MissionParams.bAllowFriendlyFire</c>
    /// (<c>Igc/igc.h:1149</c>). Optional trailing member: a pre-P856 host omits
    /// it and the directory publishes native <c>Reset</c>'s off.</summary>
    bool AllowFriendlyFire = false,
    /// <summary>P856 — <c>MissionParams.bAllowAlliedViz</c>
    /// (<c>Igc/igc.h:1127</c>). Optional trailing member, native default off.</summary>
    bool AllowAlliedViz = false,
    // P866 / SIM-0537 — the rest of native pfmLobbyMissionInfo
    // (FedSrv/fsmission.cpp:3983-4096; wire shape lobby/messagesall.h:25-63).
    // Optional trailing members: a pre-P866 host omits them and the directory
    // publishes native MissionParams::Reset's defaults, exactly like the
    // existing optional members above.
    int NumPlayers = 0,
    int NumNoatPlayers = 0,
    short Teams = 0,
    int MaxPlayersPerGame = 0,
    short MinPlayersPerTeam = 0,
    short MaxPlayersPerTeam = 0,
    uint StartTimeOffsetMs = 0,
    bool CountdownStarted = false,
    bool SquadGame = false,
    bool EjectPods = true,
    bool InvulnerableStations = false,
    bool AnySlotsAvailable = false,
    bool GuaranteedSlotsAvailable = false,
    IReadOnlyList<string>? SquadIds = null);

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
    public string? IdentityServerId { get; set; }
    public string? IdentityServerSecret { get; set; }
    // Never bound from JSON: only the endpoint can authenticate enrollment.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Verified { get; set; }
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

    /// <summary>P806 — the game settings the server-list filters/sort read (see
    /// <see cref="GameParamsDto"/>). Optional: a host that omits it registers
    /// exactly as before, and the client treats the metadata as unknown.</summary>
    public GameParamsRequest? Game { get; set; }
    public List<MissionListingRequest>? Missions { get; set; }
    // Native logon MaxGames is stored by lobby/server.cpp:114.
    public int? CurrentGames { get; set; }
    public int? MaxGames { get; set; }

    /// <summary>P810 — the minimum client version this server requires (see
    /// <see cref="ServerListingDto.MinClientVersion"/>). Optional; omitted =
    /// no requirement, byte-identical to a pre-P810 payload.</summary>
    public string? MinClientVersion { get; set; }

    /// <summary>The ownership token; null/blank means "new registration".</summary>
    public string? Token { get; set; }
}

/// <summary>
/// P806 — the mutable JSON shape of <see cref="GameParamsDto"/> on the register
/// payload. Nullable members let an older host omit individual settings; the
/// registry fills each missing one with native <c>MissionParams::Reset</c>'s
/// value (<c>Igc/igc.h:1190-1310</c>), so a partial payload still describes a
/// plausible mission rather than flipping every filter.
/// </summary>
public sealed class GameParamsRequest
{
    public bool? AllowDevelopments { get; set; }
    /// <summary><c>MissionParams::iLives</c>; omitted → <c>c_cUnlimitedLives</c> (0x7fff).</summary>
    public int? Lives { get; set; }
    public bool? ScoresCount { get; set; }
    public bool? ObjectModelCreated { get; set; }
    public bool? InProgress { get; set; }
    public bool? GoalConquest { get; set; }
    public bool? GoalDeathmatch { get; set; }
    public bool? GoalProsperity { get; set; }
    public bool? GoalCountdown { get; set; }
    public bool? GoalArtifacts { get; set; }
    public bool? GoalFlags { get; set; }
    public bool? GoalTerritory { get; set; }
    /// <summary><c>iMinRank</c>; omitted → native default -1 (no lower bound).</summary>
    public int? MinRank { get; set; }
    /// <summary><c>iMaxRank</c>; omitted → native default 1000.</summary>
    public int? MaxRank { get; set; }
    /// <summary>P856 — <c>bAllowFriendlyFire</c> (igc.h:1149); omitted → native
    /// <c>Reset</c> default, off.</summary>
    public bool? AllowFriendlyFire { get; set; }
    /// <summary>P856 — <c>bAllowAlliedViz</c> (igc.h:1127); omitted → off.</summary>
    public bool? AllowAlliedViz { get; set; }
    // P866 / SIM-0537 — the rest of pfmLobbyMissionInfo. Omitted → the native
    // MissionParams::Reset default (fsmission.cpp:3983-4096 reads the same
    // fields off m_misdef.misparms), so an older host still advertises a
    // plausible mission rather than zeroes.
    public int? NumPlayers { get; set; }
    public int? NumNoatPlayers { get; set; }
    public int? Teams { get; set; }
    public int? MaxPlayersPerGame { get; set; }
    public int? MinPlayersPerTeam { get; set; }
    public int? MaxPlayersPerTeam { get; set; }
    /// <summary>Native <c>dwStartTime</c> — the countdown deadline offset in ms.</summary>
    public uint? StartTimeOffsetMs { get; set; }
    public bool? CountdownStarted { get; set; }
    public bool? SquadGame { get; set; }
    public bool? EjectPods { get; set; }
    public bool? InvulnerableStations { get; set; }
    public bool? AnySlotsAvailable { get; set; }
    public bool? GuaranteedSlotsAvailable { get; set; }
    /// <summary>Native <c>rgSquadIDs</c> — the playing sides' squad ids.</summary>
    public List<string>? SquadIds { get; set; }
}

/// <summary>
/// The result of a successful register/heartbeat call — the listing id and the
/// ownership token the caller must present to subsequently update or delete it.
/// </summary>
public sealed record MissionListingDto(uint Cookie, string Name, int PlayerCount, GameParamsDto? Game);
public sealed class MissionListingRequest
{
    public uint Cookie { get; set; }
    public string? Name { get; set; }
    public int PlayerCount { get; set; }
    public GameParamsRequest? Game { get; set; }
}

public sealed record RegisterResponse(string Id, string Token);

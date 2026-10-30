namespace Allegiance.Lobby;

/// <summary>
/// Configuration knobs for the lobby, bound from the <c>Lobby</c> config section with
/// sane defaults baked in. Tests override the sweep cadence/staleness via
/// <c>WebApplicationFactory</c> configuration so expiry can be observed quickly.
/// </summary>
public sealed class LobbySettings
{
    /// <summary>Hard cap on distinct listings ("a few hundred"). Heartbeats to existing ids are never evicted by it.</summary>
    public int MaxEntries { get; set; } = 384;
    public int MaxEntriesPerAddress { get; set; } = 4;
    /// <summary>Only these immediate proxy IPs may supply X-Forwarded-For.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>How often the background sweep prunes stale listings.</summary>
    public double SweepIntervalSeconds { get; set; } = 30;

    /// <summary>How old an entry must be (since its last heartbeat) before the sweep prunes it.</summary>
    public double MaxAgeSeconds { get; set; } = 90;

    /// <summary>P810 — the lobby's version advertisement (<c>GET /api/update/info</c>),
    /// the HTTP analogue of native's <c>AUTO_UPDATE_INFO(253)</c> payload
    /// (<c>lobby/messageslc.h:45-52</c>, which carried the FTP location plus the
    /// official file-list CRC — no credentials ride the port's shape at all).
    /// Null/empty means "not advertised": the client then skips the update check
    /// entirely, like a native lobby with AutoUpdate unconfigured.</summary>
    public string? CurrentClientVersion { get; set; }

    /// <summary>P810 — clients older than this must update before joining
    /// (native's trigger: the lobby compared the LOGON <c>crcFileList</c> against
    /// its official one, <c>lobby/client.cpp:272-281</c>).</summary>
    public string? MinimumClientVersion { get; set; }

    /// <summary>P810 — the signed release feed root the client downloads from.
    /// Advertised only when a feed is actually deployed.</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>P871 — the authoritative core catalog the new-game wizard may
    /// select (IGC static file names, e.g. <c>PCore001.igc</c>). Empty = every
    /// registered server's advertised core is selectable (the historical
    /// behaviour, so an unconfigured lobby still works).</summary>
    public List<string> GameCores { get; set; } = [];

    /// <summary>P871 — the authoritative map catalog the wizard may select. Empty
    /// = any name is accepted (matching a lobby that does not police maps).</summary>
    public List<string> GameMaps { get; set; } = [];

    /// <summary>P820b — the configurable Zone web link shown on the client's
    /// online hub (the events screen footer). Exposed read-only via
    /// <c>GET /api/config/zone-web</c>. Empty means "not configured": the client
    /// omits the link.</summary>
    public string ZoneWebUrl { get; set; } = "";

    /// <summary>P894 / LOB-0004 — the lobby ⇄ server protocol version this lobby
    /// speaks (native <c>LOBBYVER_LS</c>, <c>lobby/server.cpp:93,141-151</c>). A
    /// registering server that advertises a different
    /// <see cref="ServerListingDto.LobbyProtocolVersion"/> is refused, native's
    /// <c>LOGON_SERVER_NACK</c> reason; a pre-P894 host that omits the field is
    /// accepted. Default 1 = <see cref="Allegiance.Core.LobbyProtocol.Version"/>.</summary>
    public int LobbyProtocolVersion { get; set; } = Allegiance.Core.LobbyProtocol.Version;

    /// <summary>P894 / LOB-0005 — the approved-server list, native's
    /// <c>Allegiance.cfg [Lobby] NumberOfServers/Server1..N</c> read by
    /// <c>IsServerAllowed</c> (<c>lobby/server.cpp:24-84</c>). Entries are matched
    /// with native's prefix rule (the config entry starts with the caller's IP,
    /// e.g. <c>10.0.0.</c> or a full address) and loopback is always allowed.
    /// <b>Empty by default</b>: an unconfigured lobby accepts every server, so
    /// community-hosted servers keep listing.</summary>
    public List<string> AllowedServers { get; set; } = [];

    /// <summary>P894 / LOB-0005 — native's <c>[Lobby] FilterType</c>
    /// (<c>lobby/server.cpp:39-51</c>): <c>allow</c> (default) treats
    /// <see cref="AllowedServers"/> as an allowlist, <c>block</c> as a blocklist.
    /// Ignored while the list is empty (native: "no entry so allow all servers").</summary>
    public string ServerFilterMode { get; set; } = "allow";

    /// <summary>P894 / LOB-0029 — native's <c>c_GameFactor = 5</c>
    /// (<c>lobby/server.h:17</c>): a listed game weighs as many players when the
    /// per-server percent load <c>(games*factor + players)/maxLoad</c> is computed
    /// (<c>lobby/server.h:70-73</c>).</summary>
    public int GameFactor { get; set; } = 5;

    /// <summary>P894 / LOB-0029 — native's per-server load denominator
    /// (<c>m_maxLoad</c>, <c>lobby/server.cpp:457</c>'s 300 default).</summary>
    public int MaxLoad { get; set; } = 300;
}

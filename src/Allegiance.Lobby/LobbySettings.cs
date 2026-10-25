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
}

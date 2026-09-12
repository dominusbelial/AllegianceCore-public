using System.Collections.Concurrent;

namespace Allegiance.Lobby;

/// <summary>
/// The in-memory server directory. Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed on the listing <c>Id</c> because ASP.NET Core dispatches requests on a
/// thread pool — reads, writes and the background expiry sweep can all touch the
/// dictionary concurrently and must not corrupt each other.
///
/// Time is injected as a <see cref="TimeProvider"/> (instead of reading the clock
/// directly inside the class) so expiry is deterministically unit-testable with a
/// fake clock. This mirrors the codebase's existing explicit-time-parameter
/// design — e.g. <c>Mission.Update(double now)</c> in <c>Allegiance.Core</c> takes
/// <c>now</c> rather than sampling <c>DateTime.UtcNow</c> itself.
/// </summary>
public sealed class ServerRegistry
{
    /// <summary>Practical cap for <c>Name</c> / <c>MissionName</c>.</summary>
    private const int NameMax = 100;
    /// <summary><c>Address</c> must hold an IPv4/IPv6 literal or a DNS hostname.</summary>
    private const int AddressMax = 255;
    /// <summary>A core filename like <c>PCore001.igc</c>.</summary>
    private const int CoreFileMax = 260;
    /// <summary>A version tag.</summary>
    private const int VersionMax = 64;

    private readonly ConcurrentDictionary<string, ServerListing> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;

    /// <param name="time">Clock used for <c>RegisteredUtc</c>/<c>LastSeenUtc</c> and expiry checks.</param>
    /// <param name="maxEntries">Hard cap on distinct listings; new registrations are refused once full (heartbeats to existing ids never are).</param>
    /// <param name="maxAge">An entry whose <c>LastSeenUtc</c> is older than this is considered expired.</param>
    public ServerRegistry(TimeProvider time, int maxEntries = 512, TimeSpan? maxAge = null)
    {
        _time = time;
        _maxEntries = maxEntries;
        _maxAge = maxAge ?? TimeSpan.FromSeconds(90);
    }

    /// <summary>The configured expiry threshold (what <see cref="Get"/> treats as "expired").</summary>
    public TimeSpan MaxAge => _maxAge;

    /// <summary>Number of distinct listings currently held.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Registers a new server, or (when <paramref name="req"/>.<c>Token</c> matches an
    /// existing listing) refreshes that listing in place — the idempotent heartbeat path.
    ///
    /// Rules:
    ///  <list type="bullet">
    ///   <item>Null/blank token → create a NEW listing with a fresh random token.</item>
    ///   <item>Token matches an existing listing → update that listing in place, return the same token.</item>
    ///   <item>Token present but matching nothing → return <c>null</c> (reject). Not silently
    ///         creating a new listing here would mask a client bug where a stale token
    ///         silently becomes a brand-new phantom server.</item>
    ///   <item>Dictionary at <see cref="_maxEntries"/> and this is a NEW registration → return
    ///         <c>null</c>. A heartbeat to an already-listed id is never evicted by the cap.</item>
    ///  </list>
    /// </summary>
    /// <returns>The response, or <c>null</c> if the call was rejected (cap reached, or unrecognized token).</returns>
    public RegisterResponse? Register(RegisterRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var now = _time.GetUtcNow();

        bool isNew = string.IsNullOrWhiteSpace(req.Token);
        if (isNew)
        {
            if (_entries.Count >= _maxEntries)
                return null;

            var id = Guid.NewGuid().ToString("N");
            var token = Guid.NewGuid().ToString("N");
            var listing = new ServerListing(
                id, token,
                Sanitize(req.Name, NameMax),
                Sanitize(req.Address, AddressMax),
                NormalizePort(req.Port),
                Math.Max(0, req.PlayerCount ?? 0),
                Math.Max(0, req.MaxPlayers ?? 0),
                Sanitize(req.MissionName, NameMax),
                Sanitize(req.CoreFile, CoreFileMax),
                Sanitize(req.Version, VersionMax),
                now, now);

            if (!_entries.TryAdd(id, listing))
                return null; // id collision is effectively impossible; treat as a transient refusal

            return new RegisterResponse(id, token);
        }

        // Heartbeat: locate the listing owning this token and refresh it in place.
        var target = _entries.Values.FirstOrDefault(e => e.Token == req.Token);
        if (target is null)
            return null; // token present but no match → reject, do not create

        var refreshed = target with
        {
            Name = Sanitize(req.Name, NameMax),
            Address = Sanitize(req.Address, AddressMax),
            Port = NormalizePort(req.Port),
            PlayerCount = Math.Max(0, req.PlayerCount ?? 0),
            MaxPlayers = Math.Max(0, req.MaxPlayers ?? 0),
            MissionName = Sanitize(req.MissionName, NameMax),
            CoreFile = Sanitize(req.CoreFile, CoreFileMax),
            Version = Sanitize(req.Version, VersionMax),
            LastSeenUtc = now,
        };

        _entries[target.Id] = refreshed;
        return new RegisterResponse(target.Id, req.Token!);
    }

    /// <summary>
    /// Deletes a listing. <paramref name="token"/> is mandatory: a caller without the
    /// matching ownership token cannot delete someone else's server.
    /// </summary>
    /// <returns><c>true</c> if an entry was actually removed, <c>false</c> otherwise (missing id or wrong token).</returns>
    public bool Deregister(string id, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        if (!_entries.TryGetValue(id, out var current))
            return false;
        if (current.Token != token)
            return false;
        return _entries.TryRemove(new KeyValuePair<string, ServerListing>(id, current));
    }

    /// <summary>
    /// Returns every listing currently in the dictionary as a public DTO.
    ///
    /// <para>Pruning is owned by the background <see cref="ExpirySweepService"/> (which calls
    /// <see cref="PruneExpired"/> on a fixed interval), so this method is intentionally
    /// lock-free and cheap: an entry that has gone stale lingers at most until the next
    /// sweep removes it (~one interval). That is chosen over pruning inline here so that
    /// hot read path stays trivially concurrent and the single source of truth for "who
    /// is live" remains the sweep.</para>
    /// </summary>
    public IReadOnlyList<ServerListingDto> List()
    {
        return _entries.Values.Select(ToDto).ToList();
    }

    /// <summary>
    /// Returns a single listing as a public DTO, or <c>null</c> if it does not exist
    /// (including when it is already older than <see cref="MaxAge"/> — i.e. expired).
    /// </summary>
    public ServerListingDto? Get(string id)
    {
        if (!_entries.TryGetValue(id, out var listing))
            return null;
        if (_time.GetUtcNow() - listing.LastSeenUtc > _maxAge)
            return null;
        return ToDto(listing);
    }

    /// <summary>
    /// Removes every entry whose <c>LastSeenUtc</c> is older than <paramref name="maxAge"/>
    /// relative to the injected <see cref="TimeProvider"/>'s current time.
    /// </summary>
    public void PruneExpired(TimeSpan maxAge)
    {
        var cutoff = _time.GetUtcNow() - maxAge;
        foreach (var kv in _entries)
        {
            if (kv.Value.LastSeenUtc < cutoff)
                _entries.TryRemove(new KeyValuePair<string, ServerListing>(kv.Key, kv.Value));
        }
    }

    /// <summary>
    /// The one ingest-point hygiene guard against a malicious/broken self-hoster polluting
    /// the public listing: cap the length and strip control characters from every
    /// caller-supplied string field. Also collapses null to empty and trims surrounding
    /// whitespace.
    /// </summary>
    private static string Sanitize(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Strip ASCII + Unicode control characters (invisible junk, newlines, NULs…).
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int n = 0;
        foreach (var ch in value)
        {
            if (!char.IsControl(ch))
                buffer[n++] = ch;
        }

        string cleaned = new string(buffer[..n]).Trim();
        return cleaned.Length > maxLen ? cleaned[..maxLen] : cleaned;
    }

    private static int NormalizePort(int port) => port switch
    {
        <= 0 => 0,
        > 65535 => 65535,
        _ => port,
    };

    private static ServerListingDto ToDto(ServerListing l) => new(
        l.Id, l.Name, l.Address, l.Port, l.PlayerCount, l.MaxPlayers,
        l.MissionName, l.CoreFile, l.Version, l.RegisteredUtc, l.LastSeenUtc);
}

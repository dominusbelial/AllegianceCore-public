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
    /// <summary>P894 — native's server location string (<c>c_cbFileName</c>,
    /// <c>lobby/server.h:168</c>); the directory keeps it short too.</summary>
    private const int LocationMax = 128;

    private readonly ConcurrentDictionary<string, ServerListing> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;
    private readonly object _registrationGate = new();
    private readonly int _maxEntriesPerAddress;
    private readonly int _gameFactor;
    private readonly int _maxLoad;

    /// <param name="time">Clock used for <c>RegisteredUtc</c>/<c>LastSeenUtc</c> and expiry checks.</param>
    /// <param name="maxEntries">Hard cap on distinct listings; new registrations are refused once full (heartbeats to existing ids never are).</param>
    /// <param name="maxAge">An entry whose <c>LastSeenUtc</c> is older than this is considered expired.</param>
    /// <param name="maxEntriesPerAddress">Native has no such cap; it bounds one host's share of the directory.</param>
    /// <param name="gameFactor">P894 — native <c>c_GameFactor</c> (5) for the percent-load metric
    /// (<c>lobby/server.h:17,70-73</c>).</param>
    /// <param name="maxLoad">P894 — native's load denominator (300, <c>lobby/server.cpp:457</c>).</param>
    public ServerRegistry(TimeProvider time, int maxEntries = 512, TimeSpan? maxAge = null, int maxEntriesPerAddress = 4,
        int gameFactor = 5, int maxLoad = 300)
    {
        _time = time;
        _maxEntriesPerAddress = Math.Max(1, maxEntriesPerAddress);
        _maxEntries = maxEntries;
        _maxAge = maxAge ?? TimeSpan.FromSeconds(90);
        _gameFactor = Math.Max(1, gameFactor);
        _maxLoad = Math.Max(1, maxLoad);
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
        // Atomic check-and-insert: concurrent POSTs cannot race either cap.
        lock (_registrationGate) return RegisterCore(req);
    }

    private RegisterResponse? RegisterCore(RegisterRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var now = _time.GetUtcNow();

        bool isNew = string.IsNullOrWhiteSpace(req.Token);
        if (isNew)
        {
            PruneExpired(_maxAge);
            if (_entries.Count >= _maxEntries
                || _entries.Values.Count(e => e.Address == Sanitize(req.Address, AddressMax)) >= _maxEntriesPerAddress)
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
                now, now,
                // P761b — names are kept ONLY for an opted-in server.
                req.PublishPlayers ? SanitizePlayers(req.Players) : Array.Empty<string>(),
                req.PublishPlayers,
                // P806 — the mission settings the client's server-list filters and
                // status/skill/settings sort columns read (native MissionInfo).
                ToGameParams(req.Game),
                req.Verified, req.IdentityServerId,
                // P810 — the optional minimum-client-version requirement
                // (absent stays null, not "").
                NullIfEmpty(Sanitize(req.MinClientVersion, VersionMax)), ToMissions(req.Missions), Math.Max(0, req.CurrentGames ?? req.Missions?.Count ?? 0),
                Math.Max(1, req.MaxGames ?? 20),
                // P894 — native's server metadata (location), the paused flag the
                // listing hides on, and the host's stated lobby protocol version.
                Sanitize(req.Location, LocationMax), req.Paused, req.LobbyProtocolVersion);

            if (!_entries.TryAdd(id, listing))
                return null; // id collision is effectively impossible; treat as a transient refusal

            // P894 / LOB-0035 — native logs server connect/disconnect lifecycle
            // events with name and address (LE_ServerConnected /
            // LE_ServerDisconnected, lobby/server.cpp:401-425). The directory keeps
            // the same record on its stdout log.
            Console.WriteLine($"[lobby] server connected: {listing.Name} @ {listing.Address}:{listing.Port} ({id})");
            return new RegisterResponse(id, token);
        }

        // Heartbeat: locate the listing owning this token and refresh it in place.
        var target = _entries.Values.FirstOrDefault(e => e.Token == req.Token);
        if (target is null || target.Address != Sanitize(req.Address, AddressMax))
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
            Players = req.PublishPlayers ? SanitizePlayers(req.Players) : Array.Empty<string>(),
            PublishPlayers = req.PublishPlayers,
            // P806 — the live game settings. A heartbeat refreshes them in place,
            // exactly like the player count (the mission moves NotStarted →
            // Started, so InProgress and the goal bits can change mid-listing).
            Game = ToGameParams(req.Game),
            Missions = ToMissions(req.Missions),
            CurrentGames = Math.Max(0, req.CurrentGames ?? req.Missions?.Count ?? 0),
            MaxGames = Math.Max(1, req.MaxGames ?? 20),
            Verified = req.Verified, IdentityServerId = req.IdentityServerId,
            // P810 — a heartbeat refreshes the requirement too (a server may
            // raise its minimum after a client-breaking update).
            MinClientVersion = NullIfEmpty(Sanitize(req.MinClientVersion, VersionMax)),
            // P894 — location, the paused flag (native FM_S_PAUSE,
            // lobby/server.cpp:266-272) and the stated protocol version refresh
            // in place, exactly like every other advertised fact.
            Location = Sanitize(req.Location, LocationMax),
            Paused = req.Paused,
            LobbyProtocolVersion = req.LobbyProtocolVersion ?? target.LobbyProtocolVersion,
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
        if (_entries.TryRemove(new KeyValuePair<string, ServerListing>(id, current)))
        {
            // P894 / LOB-0035 — native's LE_ServerDisconnected (lobby/server.cpp:420).
            Console.WriteLine($"[lobby] server disconnected: {current.Name} @ {current.Address}:{current.Port} ({id})");
            return true;
        }
        return false;
    }

    /// <summary>
    /// P871 — does <paramref name="token"/> own the listing
    /// <paramref name="id"/>? The server-side proof used by the new-game
    /// wizard's claim relay (the same ownership token as register/delete).
    /// </summary>
    public bool VerifyToken(string id, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return _entries.TryGetValue(id, out var listing) && listing.Token == token;
    }

    /// <summary>
    /// Returns every listing currently in the dictionary as a public DTO.
    ///
    /// <para>Expired entries are hidden immediately. Physical removal happens
    /// during registration or the background sweep; reads remain lock-free.</para>
    ///
    /// <para><b>P894 / LOB-0007 (paused) and LOB-0008 (zero-player games).</b>
    /// Native's advertise predicate is
    /// <c>!pServer-&gt;GetPaused() &amp;&amp; (nNumPlayers &gt; 0 || fMSArena)</c>
    /// (<c>lobby/mission.cpp:77-86</c>) — "never advertize paused games", and a
    /// mission nobody is in is advertised only when it is an <i>arena</i>, i.e. a
    /// server-owned mission that exists independently of any player
    /// (<c>fMSArena</c> is the mission's <c>bObjectModelCreated</c>,
    /// <c>FedSrv/fsmission.cpp:4087</c>). The native client applies the same rule
    /// when it builds the server list: no paused server and no server with an
    /// empty core mask (<c>lobby/client.cpp:356</c>).</para>
    ///
    /// <para>The port's servers ARE persistent, server-owned (arena) missions —
    /// they start empty and stay listed while they heartbeat — so native's own
    /// predicate keeps them visible with zero players, exactly like a native
    /// MSArena server. A <i>client-created</i> game (the P871 create-game relay,
    /// native's non-arena CREATE_MISSION_REQ path) with nobody in it is hidden
    /// until its first player arrives, and a paused host is hidden outright. This
    /// is the native intent without the "hide every empty server" degenerate
    /// reading that would empty the port's list.</para>
    /// </summary>
    public IReadOnlyList<ServerListingDto> List()
    {
        var cutoff = _time.GetUtcNow() - _maxAge;
        return _entries.Values
            .Where(e => e.LastSeenUtc >= cutoff)
            .Where(IsServerAdvertised)
            .Select(ToDto).ToList();
    }

    /// <summary>P894 — native's server-row predicate (<c>lobby/client.cpp:356</c>):
    /// <b>not paused</b>. Native also dropped a server whose static-core mask was 0
    /// (<c>GetStaticCoreMask() != 0</c>), because that lobby routed game CREATION
    /// through the server and a core-less server could not host a new game. This
    /// port's client connects to a listed server directly (LOB-0022/0023 notes),
    /// so an empty core advertisement is "unknown", not "unusable": hiding such a
    /// host would silently drop pre-P810/community servers for no benefit.</summary>
    private static bool IsServerAdvertised(ServerListing l) => !l.Paused;

    /// <summary>P894 — native's mission advertise predicate (<c>lobby/mission.cpp:77-86</c>):
    /// a mission needs a player, unless it is an arena (server-owned) mission, in
    /// which case it is advertised from birth. A host that publishes no
    /// <c>Game</c> params at all is a persistent server whose arena bit is
    /// unknown, so it is treated as an arena (shown) — the safe direction that
    /// keeps pre-P806 hosts listing.</summary>
    public static bool IsMissionAdvertised(int playerCount, GameParamsDto? game)
        => playerCount > 0 || game is not { ObjectModelCreated: false };

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
    /// P761b — the per-name find-player query: every non-expired listing whose
    /// (opt-in) pilot list carries <paramref name="name"/>, matched
    /// case-insensitively and EXACTLY — native's FIND_PLAYER asks the lobby about
    /// one character name and the lobby answers which game that pilot is in
    /// (<c>WinTrek/gamescreen.cpp:1348-1352</c> → <c>FM_C_FIND_PLAYER</c>).
    ///
    /// <para>The answer names the server(s) only. No other pilot's name leaves the
    /// directory, and a server that did not opt in
    /// (<see cref="ServerListing.PublishPlayers"/>) is never searched — its list is
    /// empty, so it can never match.</para>
    /// </summary>
    public IReadOnlyList<FindPlayerResultDto> FindPlayer(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<FindPlayerResultDto>();
        string wanted = name.Trim();
        var cutoff = _time.GetUtcNow() - _maxAge;
        return _entries.Values
            .Where(e => e.PublishPlayers && e.LastSeenUtc >= cutoff)
            .Where(e => e.Players.Any(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new FindPlayerResultDto(
                e.Id, e.Name, e.Address, e.Port, e.MaxPlayers,
                e.MissionName, e.CoreFile, e.Version))
            .ToList();
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
            if (kv.Value.LastSeenUtc < cutoff && _entries.TryRemove(new KeyValuePair<string, ServerListing>(kv.Key, kv.Value)))
            {
                // P894 / LOB-0035 — a missed roll call is also a lifecycle event
                // (native's RollCall drop, lobby/lobbyapp.cpp:426-460).
                Console.WriteLine($"[lobby] server expired (missed roll call): {kv.Value.Name} @ {kv.Value.Address}:{kv.Value.Port} ({kv.Key})");
            }
        }
    }

    /// <summary>P894 / LOB-0009 — the master core list, native's aggregated
    /// <c>BuildStaticCoreInfo</c> set (<c>lobby/server.cpp:100-120</c>) that the
    /// client offers before joining (<c>lobby/client.cpp:377-385</c>). Every live
    /// listing's advertised core is included with its server count; the curated
    /// lobby catalog (<c>LobbySettings.GameCores</c>, P871) is included even when
    /// no server currently advertises it (count 0), so a client can see the cores
    /// the lobby supports.</summary>
    public LobbyCoreListDto CoreList(IReadOnlyList<string>? curated = null)
    {
        var cutoff = _time.GetUtcNow() - _maxAge;
        var live = _entries.Values.Where(e => e.LastSeenUtc >= cutoff && IsServerAdvertised(e)).ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in live)
            if (!string.IsNullOrEmpty(listing.CoreFile))
                counts[listing.CoreFile] = counts.GetValueOrDefault(listing.CoreFile) + 1;
        foreach (var core in curated ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(core) && !counts.ContainsKey(core.Trim()))
                counts[core.Trim()] = 0;
        return new LobbyCoreListDto(
            counts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new LobbyCoreDto(kv.Key, kv.Value)).ToList(),
            live.Count);
    }

    /// <summary>P894 / LOB-0027 — the counters native exported through shared
    /// memory perfmon (<c>LOBBY_COUNTERS</c>/<c>PER_SERVER_COUNTERS</c>,
    /// <c>lobby/counters.h:4-26</c>; <c>UpdatePerfCounters</c>,
    /// <c>lobby/lobbyapp.cpp:396-423</c>): servers, missions, players in missions
    /// and per-server percent load. The hidden counters make the paused/zero-player
    /// suppression (LOB-0007/0008) observable instead of silent.</summary>
    public LobbyStatsDto Stats()
    {
        var cutoff = _time.GetUtcNow() - _maxAge;
        var alive = _entries.Values.Where(e => e.LastSeenUtc >= cutoff).ToList();
        var live = alive.Where(IsServerAdvertised).ToList();
        return new LobbyStatsDto(
            Servers: live.Count,
            Missions: live.Sum(l => l.Missions is { Count: > 0 } missions
                ? missions.Count(m => IsMissionAdvertised(m.PlayerCount, m.Game))
                : Math.Max(0, l.CurrentGames)),
            Players: live.Sum(l => l.PlayerCount),
            PercentLoad: live.Sum(PercentLoad),
            HiddenPausedServers: alive.Count(l => l.Paused),
            HiddenPausedMissions: alive.Where(l => l.Paused).Sum(l => l.Missions?.Count ?? 0),
            HiddenEmptyMissions: alive.Where(l => !l.Paused)
                .Sum(l => l.Missions?.Count(m => !IsMissionAdvertised(m.PlayerCount, m.Game)) ?? 0));
    }

    /// <summary>
    /// The one ingest-point hygiene guard against a malicious/broken self-hoster polluting
    /// the public listing: cap the length and strip control characters from every
    /// caller-supplied string field. Also collapses null to empty and trims surrounding
    /// whitespace.
    /// </summary>
    /// <summary>P810 — an absent optional field stays null (never an empty
    /// string) so the DTO shape matches the pre-P810 wire for hosts that do not
    /// declare a minimum client version.</summary>
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Sanitize(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Strip ASCII + Unicode control characters (invisible junk, newlines, NULs…).
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int n = 0;
        foreach (var ch in value)
        {
            if (!char.IsControl(ch) && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format)
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

    /// <summary>P761 — per-player hygiene for the FIND_PLAYER directory data:
    /// the same control-character/length guard as every string field, capped
    /// per listing (PLAYERINFO names are 24 chars; allow generous room).</summary>
    private const int PlayerNameMax = 32;
    private const int MaxPlayersListed = 64;
    private static IReadOnlyList<string> SanitizePlayers(List<string>? players)
    {
        if (players is null || players.Count == 0) return Array.Empty<string>();
        return players
            .Select(p => Sanitize(p, PlayerNameMax))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPlayersListed)
            .ToList();
    }

    /// <summary>P761b — the public projection: the listing WITHOUT its pilot names
    /// (privacy fix; the names survive only for <see cref="FindPlayer"/>).</summary>
    private ServerListingDto ToDto(ServerListing l) => new(
        l.Id, l.Name, l.Address, l.Port, l.PlayerCount, l.MaxPlayers,
        l.MissionName, l.CoreFile, l.Version, l.RegisteredUtc, l.LastSeenUtc,
        l.Game, l.Verified, l.IdentityServerId, l.MinClientVersion, VisibleMissions(l), l.CurrentGames, l.MaxGames,
        l.Location, PercentLoad(l), StartTimeUtc(l));

    /// <summary>P894 / LOB-0008 — the mission rows a client may see: native drops a
    /// zero-player non-arena mission from the advertisement
    /// (<c>lobby/mission.cpp:77-86</c>). The server row itself stays (native's
    /// SERVERS_LIST is per server, <c>lobby/client.cpp:343-385</c>).</summary>
    private static IReadOnlyList<MissionListingDto>? VisibleMissions(ServerListing l)
        => l.Missions?.Where(m => IsMissionAdvertised(m.PlayerCount, m.Game)).ToArray();

    /// <summary>P894 / LOB-0029 — native <c>CFLServer::GetPercentLoad</c>
    /// (<c>lobby/server.h:70-73</c>): <c>(games*c_GameFactor + players)/m_maxLoad</c>.
    /// A server that advertises no game capacity has no load to report (0).</summary>
    private int PercentLoad(ServerListing l)
        => l.MaxGames <= 0 ? 0 : (l.CurrentGames * _gameFactor + l.PlayerCount) / _maxLoad;

    /// <summary>P894 / LOB-0017 — native normalizes the mission's relative start
    /// time against the moment the lobby info arrived
    /// (<c>plmi-&gt;dwStartTime += Time::Now().clock()</c>,
    /// <c>lobby/mission.cpp:61</c>). The heartbeat's <c>LastSeenUtc</c> is that
    /// moment for the published payload; a zero offset means no start time is
    /// armed (native <c>dwStartTime</c> 0).</summary>
    private static DateTimeOffset? StartTimeUtc(ServerListing l)
        => l.Game is { StartTimeOffsetMs: > 0 } g
            ? l.LastSeenUtc + TimeSpan.FromMilliseconds(g.StartTimeOffsetMs)
            : null;

    /// <summary>
    /// P806 — normalize the caller-supplied game settings. Every omitted field
    /// takes native <c>MissionParams::Reset</c>'s value (<c>Igc/igc.h:1246-1310</c>:
    /// developments on, unlimited lives, scores count, conquest percentage 100
    /// i.e. a conquest game, no other goal, rank window -1..1000), and the
    /// numeric fields are clamped to their native short ranges. A payload with no
    /// <c>Game</c> object at all stays null: the directory then has no settings to
    /// publish and the client cannot evaluate a Yes/No filter against it.
    /// </summary>
    private static IReadOnlyList<MissionListingDto>? ToMissions(List<MissionListingRequest>? missions) => missions?
        .Where(m => m.Cookie != uint.MaxValue).DistinctBy(m => m.Cookie).Take(128)
        .Select(m => new MissionListingDto(m.Cookie, Sanitize(m.Name, NameMax), Math.Max(0, m.PlayerCount), ToGameParams(m.Game))).ToArray();

    private static GameParamsDto? ToGameParams(GameParamsRequest? g)
    {
        if (g is null) return null;
        const int UnlimitedLives = 0x7fff;   // c_cUnlimitedLives, igc.h:1007
        static short ClampShort(int value) => value switch
        {
            < short.MinValue => short.MinValue,
            > short.MaxValue => short.MaxValue,
            _ => (short)value,
        };
        return new GameParamsDto(
            AllowDevelopments: g.AllowDevelopments ?? true,
            Lives: Math.Clamp(g.Lives ?? UnlimitedLives, 0, UnlimitedLives),
            ScoresCount: g.ScoresCount ?? true,
            ObjectModelCreated: g.ObjectModelCreated ?? false,
            InProgress: g.InProgress ?? false,
            GoalConquest: g.GoalConquest ?? true,
            GoalDeathmatch: g.GoalDeathmatch ?? false,
            GoalProsperity: g.GoalProsperity ?? false,
            GoalCountdown: g.GoalCountdown ?? false,
            GoalArtifacts: g.GoalArtifacts ?? false,
            GoalFlags: g.GoalFlags ?? false,
            GoalTerritory: g.GoalTerritory ?? false,
            MinRank: ClampShort(g.MinRank ?? -1),
            MaxRank: ClampShort(g.MaxRank ?? 1000),
            // P856 — native defaults, off (igc.h:1243,1246).
            AllowFriendlyFire: g.AllowFriendlyFire ?? false,
            AllowAlliedViz: g.AllowAlliedViz ?? false,
            // P866 — pfmLobbyMissionInfo's remaining fields
            // (fsmission.cpp:3983-4096). Omitted values take the native
            // MissionParams::Reset shape: no players, no armed countdown,
            // eject pods on (igc.h:1246), no squads.
            NumPlayers: Math.Max(0, g.NumPlayers ?? 0),
            NumNoatPlayers: Math.Max(0, g.NumNoatPlayers ?? 0),
            Teams: ClampShort(g.Teams ?? 0),
            MaxPlayersPerGame: Math.Max(0, g.MaxPlayersPerGame ?? 0),
            MinPlayersPerTeam: ClampShort(g.MinPlayersPerTeam ?? 0),
            MaxPlayersPerTeam: ClampShort(g.MaxPlayersPerTeam ?? 0),
            StartTimeOffsetMs: g.StartTimeOffsetMs ?? 0,
            CountdownStarted: g.CountdownStarted ?? false,
            SquadGame: g.SquadGame ?? false,
            EjectPods: g.EjectPods ?? true,
            InvulnerableStations: g.InvulnerableStations ?? false,
            AnySlotsAvailable: g.AnySlotsAvailable ?? false,
            GuaranteedSlotsAvailable: g.GuaranteedSlotsAvailable ?? false,
            SquadIds: g.SquadIds);
    }
}

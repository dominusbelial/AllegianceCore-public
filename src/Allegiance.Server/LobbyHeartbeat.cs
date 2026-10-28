using System.Net.Http.Json;
using System.Text.Json;
using Allegiance.Core.Identity;

namespace Allegiance.Server;

/// <summary>
/// The opt-in lobby heartbeat client (P176). A self-hosted server announces
/// itself to an <c>Allegiance.Lobby</c> directory by POSTing a register payload
/// (matching <c>Allegiance.Lobby</c>'s <c>RegisterRequest</c> wire shape) on a fixed
/// cadence, refreshing the same listing in place (idempotent heartbeat via the stored
/// ownership token) rather than duplicating it. Drives itself on a
/// <see cref="System.Threading.Timer"/> — never a blocking thread — and is strictly
/// fire-and-forget with respect to the sim:
/// every failure is caught and logged, never thrown, and an unreachable/slow lobby
/// can never wedge anything (short ~5s HTTP timeout).
///
/// Construction requires only the <see cref="LobbyConfig"/> block, a way to read
/// the live player count (a plain <c>Func&lt;int&gt;</c> delegate, so this type has
/// no circular reference to <see cref="Server"/>), and the frozen
/// MissionName/CoreFile/Version strings resolved once at construction. It never
/// touches <c>Server.Ships</c> — that collection is sim-thread owned — it reads
/// only the cross-thread-safe <c>LivePlayerCount</c> snapshot.
/// </summary>
public sealed class LobbyHeartbeat : IDisposable
{
    private readonly HttpClient _http;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _enrollmentFile;
    private DeviceAuthorization? _authorization;
    private DateTimeOffset _nextEnrollmentPoll;
    private ServerEnrollment? _enrollment;
    private readonly string _displayName;
    private readonly string _publicAddress;
    private readonly int _port;
    private readonly Func<int> _livePlayerCount;
    /// <summary>P761 — snapshot of the connected pilots' names, sent with every
    /// register so the directory can answer the client's find-player query
    /// (native FIND_PLAYER, gamescreen.cpp:1348-1352). Optional: older hosts keep
    /// the count-only payload. P761b — only sent when the server opted in
    /// (<see cref="LobbyConfig.PublishPlayerNames"/>); otherwise null.</summary>
    private readonly Func<IReadOnlyList<string>>? _livePlayerNames;
    private readonly string _missionName;
    private readonly string _coreFile;
    private readonly string _version;
    /// <summary>P806 — reads the cross-thread-safe mission-settings snapshot (the
    /// same discipline as <c>LivePlayerCount</c>/<c>LivePlayerNames</c>).</summary>
    private readonly Func<LobbyGameParams?>? _liveGameParams;
    private readonly string? _minClientVersion;
    private readonly int _intervalSeconds;
    private readonly TimeProvider _clock;
    private DateTimeOffset? _lastAttempt;
    private int? _publishedPlayerCount;
    private const double DirtyIntervalSeconds = 10; // FedSrv/fsmission.cpp:21,5606-5621


    // Ownership token/id from the last successful register. Cleared on a 409 so the
    // next tick re-registers fresh (a rejected token means the server-side listing
    // is gone — retrying it forever would be pointless).
    private string? _id;
    private string? _token;
    private bool _disposed;

    /// <summary>
    /// Production constructor: owns its one long-lived <see cref="HttpClient"/>
    /// (short timeout so a slow lobby can never stall anything), created here and
    /// disposed on <see cref="Dispose"/>.
    /// </summary>
    public LobbyHeartbeat(LobbyConfig config, Func<int> livePlayerCount,
        string missionName, string coreFile, string version, int port,
        Func<IReadOnlyList<string>>? livePlayerNames = null,
        Func<LobbyGameParams?>? liveGameParams = null)
        : this(BuildClient(config.Url), ownsHttp: true, config, livePlayerCount, missionName, coreFile, version, port, clock: null, livePlayerNames, liveGameParams)
    {
    }

    /// <summary>
    /// Test seam (internal; reached via InternalsVisibleTo): takes a caller-supplied
    /// <see cref="HttpClient"/> already wired to a test server, so the client does
    /// not own it (disposal of the injected client is the caller's responsibility).
    /// </summary>
    internal LobbyHeartbeat(HttpClient http, LobbyConfig config, Func<int> livePlayerCount,
        string missionName, string coreFile, string version, int port, TimeProvider? clock = null,
        Func<IReadOnlyList<string>>? livePlayerNames = null,
        Func<LobbyGameParams?>? liveGameParams = null)
        : this(http, ownsHttp: false, config, livePlayerCount, missionName, coreFile, version, port, clock, livePlayerNames, liveGameParams)
    {
    }

    private LobbyHeartbeat(HttpClient http, bool ownsHttp, LobbyConfig config, Func<int> livePlayerCount,
        string missionName, string coreFile, string version, int port, TimeProvider? clock = null,
        Func<IReadOnlyList<string>>? livePlayerNames = null,
        Func<LobbyGameParams?>? liveGameParams = null)
    {
        _clock = clock ?? TimeProvider.System;
        _http = http;
        _ownsHttp = ownsHttp;
        _enrollmentFile = config.EnrollmentFile;
        if (_enrollmentFile.Length > 0 && File.Exists(_enrollmentFile))
            _enrollment = JsonSerializer.Deserialize(File.ReadAllText(_enrollmentFile), IdentityJsonContext.Default.ServerEnrollment)
                ?? throw new InvalidOperationException("Invalid server enrollment file");
        _displayName = config.DisplayName;
        _publicAddress = config.PublicAddress;
        _port = port;
        _livePlayerCount = livePlayerCount;
        // P761b — privacy: the roster leaves this server ONLY on an explicit
        // opt-in; the directory's default is "names withheld".
        _livePlayerNames = config.PublishPlayerNames ? livePlayerNames : null;
        _publishPlayers = config.PublishPlayerNames;
        _missionName = missionName;
        _coreFile = coreFile;
        _version = version;
        // P806 — the game settings the directory publishes for the server-list
        // filters (see LobbyGameParams). Null when the caller has none.
        _liveGameParams = liveGameParams;
        // P810 — the advertised minimum-client-version requirement (native
        // enforced it at the lobby via the LOGON crcFileList,
        // clintlib/clintlib.cpp:1418 → lobby/client.cpp:272-281; the port
        // advertises it on the listing and the client gates at browse time).
        _minClientVersion = string.IsNullOrWhiteSpace(config.MinimumClientVersion) ? null : config.MinimumClientVersion;
        _intervalSeconds = config.HeartbeatIntervalSeconds > 0 ? config.HeartbeatIntervalSeconds : 25;

        _timer = new Timer(_ => Fire(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static HttpClient BuildClient(string url) => new()
    {
        BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/", UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Timer callback — re-entrancy-guarded by <see cref="PumpAsync"/> itself.</summary>
    private void Fire()
    {
        _ = PumpAsync(periodic: true);   // PumpAsync swallows everything; it never throws into the timer.
    }

    /// <summary>
    /// One heartbeat cycle. Re-entrancy guarded: if a previous tick's HTTP call is
    /// still in flight this invocation returns immediately (skips) rather than
    /// overlapping. Never throws.
    /// </summary>
    internal async Task PumpAsync(bool periodic = false)
    {
        if (_disposed) return;
        if (!_gate.Wait(0)) return;   // previous tick still in flight — skip, no overlap

        try
        {
            if (_enrollmentFile.Length > 0 && _enrollment is null) await EnrollAsync().ConfigureAwait(false);
            int playerCount = _livePlayerCount();
            DateTimeOffset now = _clock.GetUtcNow();
            if (periodic && _lastAttempt is { } previous)
            {
                double elapsed = (now - previous).TotalSeconds;
                // Dirty mission details coalesce for ten seconds. HTTP leases
                // additionally need a clean refresh; DirectPlay cookie/session
                // handling is replaced by the directory's ownership token.
                if (elapsed < DirtyIntervalSeconds ||
                    (_token is not null && playerCount == _publishedPlayerCount && elapsed < _intervalSeconds)) return;
            }
            _lastAttempt = now;
            // Named payload mirroring Allegiance.Lobby's RegisterRequest wire shape
            // exactly (serialized camelCase by the source-generated ServerJsonContext —
            // byte-identical to the anonymous type System.Net.Http.Json used to emit; the
            // Lobby binds case-insensitively). Kept local — no reference to the Web-SDK
            // Lobby assembly — so Allegiance.Server stays a plain (non-AspNetCore) app.
            var req = new LobbyRegisterRequest(
                _displayName,
                _publicAddress,
                _port,
                (int?)playerCount,
                0,                              // no player-cap concept in ServerConfig today — 0 signals unknown/unbounded
                _missionName,
                _coreFile,
                _version,
                _livePlayerNames?.Invoke(),     // P761 — find-player data (opt-in only, P761b)
                _publishPlayers,                // P761b — the per-server opt-in the directory honours
                _token,                         // null on the first tick, or after a 409 forced a fresh re-register
                _liveGameParams?.Invoke(),      // P806 — game settings for the server-list filters
                _enrollment?.ServerId, _enrollment?.ServerSecret,
                _minClientVersion);             // P810 — advertised minimum client version (null = none)


            HttpResponseMessage resp;
            try
            {
                resp = await _http.PostAsJsonAsync("/api/servers/register", req,
                    ServerJsonContext.Default.LobbyRegisterRequest).ConfigureAwait(false);
            }
            catch (Exception e)       // network error / timeout / connection refused
            {
                Console.WriteLine($"[lobby] heartbeat failed: {e.Message}");
                return;
            }

            using var responseLifetime = resp;
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    var reg = await resp.Content.ReadFromJsonAsync(
                        ServerJsonContext.Default.LobbyRegisterResponse).ConfigureAwait(false);
                    if (reg is null)
                    {
                        Console.WriteLine("[lobby] heartbeat: empty register response");
                        return;
                    }
                    _id = reg.Id;
                    _token = reg.Token;
                    _publishedPlayerCount = playerCount;
                    Console.WriteLine($"[lobby] heartbeat ok -> {reg.Id}, {_livePlayerCount()} player(s)");
                }
                catch (Exception e)   // JSON/deserialization error
                {
                    Console.WriteLine($"[lobby] heartbeat: bad register response: {e.Message}");
                }
            }
            else if ((int)resp.StatusCode == 409)
            {
                // Rejected: directory full (new registration) OR the presented token matched
                // no listing (server-side entry gone). Drop the token so the next tick
                // re-registers fresh instead of repeating the same rejected token forever.
                Console.WriteLine("[lobby] heartbeat rejected (409) — dropping token, re-registering next tick");
                _token = null;
                _id = null;
            }
            else
            {
                Console.WriteLine($"[lobby] heartbeat unexpected status {(int)resp.StatusCode}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnrollAsync()
    {
        if (_clock.GetUtcNow() < _nextEnrollmentPoll) return;
        _nextEnrollmentPoll = _clock.GetUtcNow().AddSeconds(5);
        try
        {
            if (_authorization is null)
            {
                using var start = await _http.PostAsJsonAsync("/api/identity/device", new DeviceStartRequest("server", _displayName), IdentityJsonContext.Default.DeviceStartRequest).ConfigureAwait(false);
                start.EnsureSuccessStatusCode();
                _authorization = await start.Content.ReadFromJsonAsync(IdentityJsonContext.Default.DeviceAuthorization).ConfigureAwait(false);
                if (_authorization is not null)
                    Console.WriteLine($"[lobby] Approve server at {_authorization.VerificationUri}, code {_authorization.UserCode}");
                return;
            }
            using var poll = await _http.PostAsJsonAsync("/api/identity/device/poll", new DevicePollRequest(_authorization.DeviceCode), IdentityJsonContext.Default.DevicePollRequest).ConfigureAwait(false);
            poll.EnsureSuccessStatusCode();
            var result = await poll.Content.ReadFromJsonAsync(IdentityJsonContext.Default.DeviceResult).ConfigureAwait(false);
            if (result?.Status == "expired") { _authorization = null; return; }
            if (result is { Status: "approved", ServerId: not null, ServerSecret: not null })
            {
                var enrollment = new ServerEnrollment(result.ServerId, result.ServerSecret);
                string path = Path.GetFullPath(_enrollmentFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(path, options))
                    await JsonSerializer.SerializeAsync(file, enrollment, IdentityJsonContext.Default.ServerEnrollment).ConfigureAwait(false);
                _enrollment = enrollment;
                _lastAttempt = null; // publish verified status immediately
                Console.WriteLine($"[lobby] Server enrolled: {enrollment.ServerId}");
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException)
        { Console.WriteLine($"[lobby] enrollment unavailable: {e.Message}"); }
    }

    /// <summary>Alias for <see cref="Dispose"/>.</summary>
    public void Stop() => Dispose();

    /// <summary>
    /// Stops the timer and makes one best-effort, short-timeout <c>DELETE</c> with
    /// the current id+token (if one exists) so a clean shutdown removes the listing
    /// immediately rather than waiting for the directory's expiry sweep. Fire-and-
    /// forget: any failure is swallowed and never blocks shutdown beyond the same
    /// short timeout used for every other call.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();

        if (_id is not null && _token is not null)
        {
            try
            {
                string path = $"/api/servers/{Uri.EscapeDataString(_id)}?token={Uri.EscapeDataString(_token)}";
                _http.DeleteAsync(path).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[lobby] deregister failed: {e.Message}");
            }
        }

        // The owned-client constructor created _http; the injected-client test seam did
        // not. Disposing an injected client would yank the test server's handler out
        // from under the test — so only dispose the client we own.
        if (_ownsHttp)
            _http.Dispose();

        // SemaphoreSlim.Wait(0) owns no native handle. An in-flight async pump
        // must still be able to Release after the timer has been disposed.
    }

    /// <summary>
    /// Best-effort, fire-and-forget POST of a finished match's per-player results
    /// to the lobby (P808b), carrying the server's enrollment proof so the lobby
    /// only ingests reports from operator-approved servers (anti-abuse). No-op
    /// when this server has no enrollment (anonymous/standalone) or is shutting
    /// down; every failure is caught and logged, never thrown, so a slow/absent
    /// lobby can never stall the sim. Callers run this on their own async path —
    /// never synchronously on the sim thread.
    /// </summary>
    internal async Task ReportResultsAsync(LobbyMatchReport report)
    {
        if (_disposed || _enrollment is null) return;   // verified servers only
        if (report.ServerId is null) report = report with { ServerId = _enrollment.ServerId, ServerSecret = _enrollment.ServerSecret };
        try
        {
            using var resp = await _http.PostAsJsonAsync("/api/career/results", report,
                ServerJsonContext.Default.LobbyMatchReport).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"[lobby] career report status {(int)resp.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        { Console.WriteLine($"[lobby] career report failed: {e.Message}"); }
    }

    private readonly bool _publishPlayers;
    private readonly bool _ownsHttp;
}

/// <summary>
/// Mirror of <c>Allegiance.Lobby</c>'s <c>RegisterResponse(Id, Token)</c>, kept local so
/// <c>Allegiance.Server</c> does not depend on the (Web-SDK) Lobby assembly — the JSON
/// wire shape is identical and <see cref="LobbyHeartbeat"/> binds it case-insensitively.
/// </summary>
public sealed record LobbyRegisterResponse(string Id, string Token);

/// <summary>
/// Mirror of <c>Allegiance.Lobby</c>'s career <c>MatchReport</c> / one player's
/// <c>MatchResultEntry</c> wire shape (P808b), kept local so
/// <c>Allegiance.Server</c> stays independent of the (Web-SDK) Lobby assembly —
/// serialized camelCase by the source-generated <see cref="ServerJsonContext"/>,
/// and the lobby binds it case-insensitively. Carries the server's enrollment
/// proof (id + secret) so the lobby only ingests verified-server reports.
/// </summary>
public sealed record LobbyMatchResultEntry(
    string AccountId,
    short Civ,
    float Score,
    float CombatRating,
    int Kills,
    int Deaths,
    float BaseKills,
    float BaseCaptures,
    int Minutes,
    bool Win,
    bool Loss,
    bool CommanderWin,
    bool CommanderLoss,
    bool ScoresCount,
    // P844/SIM-0526 — the remaining native CQPlayerResults columns
    // (fsmission.cpp:2969-2986) the report previously dropped. Optional
    // (default zero) so existing constructors keep compiling; the lobby binds
    // the payload case-insensitively and ignores fields its career model does
    // not store yet.
    int Ejections = 0,
    int Rescues = 0,
    int Flags = 0,
    int Artifacts = 0,
    int TechsRecovered = 0,
    int WarpsSpotted = 0,
    int AsteroidsSpotted = 0,
    float TimePlayedSeconds = 0f);

/// <summary>A finished match's results for one server (see
/// <see cref="LobbyMatchResultEntry"/>). <see cref="SquadGame"/> — P855 —
/// carries the finished squad game's standings facts (the mirror of
/// Allegiance.Lobby's <c>SquadGameResult</c>); null for non-squad or
/// non-qualifying games (native never calls RecordSquadGame then,
/// fsmission.cpp:3421-3427).</summary>
public sealed record LobbyMatchReport(
    string MatchId,
    IReadOnlyList<LobbyMatchResultEntry> Players,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ServerId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ServerSecret = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LobbySquadGameResult? SquadGame = null,
    // P844/SIM-0524/0525 — the game-level row and the per-side rows of the
    // finished match (native RecordGameResults/RecordTeamResults). Null when
    // the mission state needed for them is unavailable.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LobbyGameResult? Game = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LobbyTeamResult>? Teams = null);

/// <summary>P844/SIM-0524 — the finished match's game-level result row: the
/// port of native's <c>CQGameResults</c> query payload
/// (<c>CFSMission::RecordGameResults</c>, FedSrv/fsmission.cpp:2757-2836) —
/// game name, winner (side id or NA on a draw + name), duration, and the six
/// goal switches with their configured goal values (conquest percentage,
/// countdown length, team kills, prosperity money, artifacts, flags).
/// Native also posts the same facts plus server/core/reason as a JSON blob
/// to <c>szGameStatsUpdateUrl</c> when set (fsmission.cpp:2810-2818) — that
/// Steam-era export URL is n/a here; the lobby report is the persistence.</summary>
public sealed record LobbyGameResult(
    string? GameName,
    short WinnerSideId,
    string? WinnerName,
    float DurationSeconds,
    bool GoalConquest,
    bool GoalCountdown,
    bool GoalTeamKills,
    bool GoalProsperity,
    bool GoalArtifacts,
    bool GoalFlags,
    short GoalConquestPercentage,
    float GoalCountdownLength,
    short GoalTeamKillsCount,
    float GoalProsperityMoney,
    short GoalArtifactsCount,
    short GoalFlagsCount);

/// <summary>P844/SIM-0525 — one finished side's outcome row: the port of
/// native's <c>CQTeamResults</c> query payload
/// (<c>CFSMission::RecordTeamResults</c>, FedSrv/fsmission.cpp:2845-2915) —
/// team number, name, civilization (name; native posts the civ id), the
/// team kill/death/ejection/base counters, flags and artifacts, and the
/// conquest/territory standings. Native's tech-tree mask, prosperity
/// percentages and <c>TimeEndured</c> have no port-side source yet (the side
/// model does not track them) and are omitted — see
/// docs/reports/P844_CERTIFY.md.</summary>
public sealed record LobbyTeamResult(
    short TeamNumber,
    string Name,
    string? Faction,
    short Kills,
    short BaseKills,
    short BaseCaptures,
    short Deaths,
    short Ejections,
    short Flags,
    short Artifacts,
    byte ConquestPercent,
    byte TerritoryCount);

/// <summary>P855/SIM-0765 — the server's mirror of the lobby contract's
/// <c>SquadGameResult</c>: the winning side's squad id (native
/// <c>squadIDWon</c>, null when the winning side had no squad — native posts
/// NA) and every losing side's squad id (native <c>squadIDLost1..5</c>;
/// sides without a squad are skipped exactly as the native loser loop skips
/// <c>GetSquadID() == NA</c>, FedSrv.CPP:1206-1290).</summary>
public sealed record LobbySquadGameResult(string? WinnerSquadId, IReadOnlyList<string> LoserSquadIds);

/// <summary>
/// Mirror of <c>Allegiance.Lobby</c>'s <c>RegisterRequest</c> wire shape, promoted from
/// the anonymous type the heartbeat used to POST so the source-generated
/// <see cref="ServerJsonContext"/> has a named type to attach
/// <c>[JsonSerializable]</c> to (P185 — required for Native AOT). Kept local so
/// <c>Allegiance.Server</c> stays independent of the (Web-SDK) Lobby assembly;
/// serialized camelCase exactly as the old anonymous payload was, so the bytes on
/// the wire are unchanged.
/// </summary>
internal sealed record LobbyRegisterRequest(
    string Name,
    string Address,
    int Port,
    int? PlayerCount,
    int MaxPlayers,
    string MissionName,
    string CoreFile,
    string Version,
    IReadOnlyList<string>? Players,
    // P761b — the directory stores/answers the pilot names ONLY for a server that
    // opts in (default off: the lobby cannot authenticate a self-hosted server).
    bool PublishPlayers,
    string? Token,
    // P806 — the mission settings the client's server-list filters and sort
    // columns read (mirror of Allegiance.Lobby's GameParamsRequest).
    LobbyGameParams? Game = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? IdentityServerId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? IdentityServerSecret = null,
    // P810 — the optional minimum-client-version requirement this server
    // enforces (advertised form of native's LOGON crcFileList gate,
    // clintlib/clintlib.cpp:1418 → lobby/client.cpp:272-281). Absent = none.
    string? MinClientVersion = null);

/// <summary>
/// P806 — the game settings a server publishes to the directory, mirroring
/// native <c>MissionInfo</c>'s accessors (<c>clintlib/clintlib.h:540-557</c>).
/// The native client receives these inside the mission definition the lobby
/// relays; in this port the HTTP directory is the lobby, so they ride the
/// register payload in exactly the same read-only, lobby-owned way.
/// <para>Every value here is something the SERVER genuinely knows about its own
/// mission: <c>Mission.AllowDevelopments</c> (<c>MissionParams.bAllowDevelopments</c>,
/// igc.h:1128), <c>Mission.Lives</c> (<c>iLives</c>, igc.h:1185), the P191/P302/
/// P303 win flags (<c>IsConquestGame</c>/<c>IsDeathMatchGame</c>/…, igc.h:1511-1540),
/// <c>Mission.CountdownDuration</c> (<c>dtGameLength</c>, igc.h:1162),
/// <c>Mission.Stage</c> (<c>fInProgress</c>) and — P856 — the two create-game
/// options <c>bAllowFriendlyFire</c> (<c>Mission.AllowFriendlyFire</c>,
/// igc.h:1149) and <c>bAllowAlliedViz</c> (<c>Mission.AllowAlliedViz</c>,
/// igc.h:1127).</para>
/// </summary>
public sealed record LobbyGameParams(
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
    // P856 — the two native create-game options, published so the directory
    // (and through it the client's GAME DETAILS panel) carries the same facts
    // the native client reads out of MissionParams. Optional trailing members so
    // pre-P856 constructions keep their shape; both default to native's off.
    bool AllowFriendlyFire = false,
    bool AllowAlliedViz = false)
{
    /// <summary>Native <c>c_cUnlimitedLives</c> (<c>igc.h:1007</c>).</summary>
    public const int UnlimitedLives = 0x7fff;

    /// <summary>
    /// Snapshot one live mission. Values with no port equivalent take the native
    /// <c>MissionParams::Reset</c> default and are documented as such:
    /// <c>bScoresCount = true</c> (igc.h:1141/Reset and the game types' Apply),
    /// <c>bObjectModelCreated = true</c> (igc.h:1150: the mission was created by
    /// the server app — every port mission is), <c>iMinRank = -1</c> /
    /// <c>iMaxRank = 1000</c> (igc.h:1301-1302: the port has no rank window).
    /// </summary>
    public static LobbyGameParams FromMission(Allegiance.Core.Model.Mission m) => new(
        AllowDevelopments: m.AllowDevelopments,
        Lives: m.Lives,
        ScoresCount: true,
        ObjectModelCreated: true,
        InProgress: m.Stage is Allegiance.Core.Sim.MissionStage.Starting or Allegiance.Core.Sim.MissionStage.Started,
        GoalConquest: m.ConquestEnabled,
        GoalDeathmatch: m.KillsEnabled,
        GoalProsperity: m.ProsperityEnabled,
        GoalCountdown: m.CountdownDuration is > 0,
        GoalArtifacts: m.ArtifactEnabled,
        GoalFlags: m.FlagsEnabled,
        GoalTerritory: m.TerritoryEnabled,
        MinRank: -1,
        MaxRank: 1000,
        // P856 — native bAllowFriendlyFire / bAllowAlliedViz (igc.h:1149,1127).
        AllowFriendlyFire: m.AllowFriendlyFire,
        AllowAlliedViz: m.AllowAlliedViz);
}

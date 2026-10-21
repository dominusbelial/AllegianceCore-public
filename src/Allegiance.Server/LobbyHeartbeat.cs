using System.Net.Http.Json;

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
        Func<IReadOnlyList<string>>? livePlayerNames = null)
        : this(BuildClient(config.Url), ownsHttp: true, config, livePlayerCount, missionName, coreFile, version, port, clock: null, livePlayerNames)
    {
    }

    /// <summary>
    /// Test seam (internal; reached via InternalsVisibleTo): takes a caller-supplied
    /// <see cref="HttpClient"/> already wired to a test server, so the client does
    /// not own it (disposal of the injected client is the caller's responsibility).
    /// </summary>
    internal LobbyHeartbeat(HttpClient http, LobbyConfig config, Func<int> livePlayerCount,
        string missionName, string coreFile, string version, int port, TimeProvider? clock = null,
        Func<IReadOnlyList<string>>? livePlayerNames = null)
        : this(http, ownsHttp: false, config, livePlayerCount, missionName, coreFile, version, port, clock, livePlayerNames)
    {
    }

    private LobbyHeartbeat(HttpClient http, bool ownsHttp, LobbyConfig config, Func<int> livePlayerCount,
        string missionName, string coreFile, string version, int port, TimeProvider? clock = null,
        Func<IReadOnlyList<string>>? livePlayerNames = null)
    {
        _clock = clock ?? TimeProvider.System;
        _http = http;
        _ownsHttp = ownsHttp;
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
                _token);                        // null on the first tick, or after a 409 forced a fresh re-register

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
    string? Token);

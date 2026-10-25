using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Server;

/// <summary>
/// P808c — the server's short-TTL, fail-open cache of the lobby's REGISTERED
/// (account-held) callsigns, so an ANONYMOUS logon cannot take the name of a
/// registered player (reserved-callsign protection, P808a follow-up).
///
/// The server learns the registered names by polling <c>GET /api/identity/callsigns</c>
/// on the lobby directory. Rules (all load-bearing):
/// <list type="bullet">
/// <item><b>Short TTL.</b> The list is refreshed every <see cref="RefreshSeconds"/>
///      (60 s) so a newly-registered callsign is protected within a minute and a
///      removed one stops being protected quickly.</item>
/// <item><b>Fail open.</b> If the lobby is unreachable or the fetch fails, the cache
///      keeps whatever snapshot it last had — or stays EMPTY before the first success —
///      and <see cref="Contains"/> returns false for everything. Anonymous play must
///      never break because the lobby is down: an unreachable lobby protects no names
///      rather than locking everyone out.</item>
/// <item><b>Sim-thread safety.</b> The logon path runs on the sim thread while the
///      timer refreshes on its own thread; readers only ever see an immutable
///      <see cref="HashSet{T}"/> swapped atomically on refresh.</item>
/// </list>
/// The cache is constructed only when the server has a Lobby config (the same gate
/// as the heartbeat), and is enforced only when server identity is configured.
/// </summary>
public sealed class RegisteredCallsignCache : IDisposable
{
    /// <summary>Refresh / TTL interval — short, per the brief.</summary>
    public static readonly TimeSpan RefreshSeconds = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Timer _timer;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlySet<string> _normalized = new HashSet<string>(StringComparer.Ordinal);
    private DateTimeOffset _lastAttempt;
    private bool _disposed;

    /// <summary>Production constructor: owns one long-lived short-timeout client.</summary>
    public RegisteredCallsignCache(LobbyConfig config, TimeProvider? clock = null)
        : this(BuildClient(config.Url), ownsHttp: true, clock)
    {
    }

    /// <summary>Test seam (internal, via InternalsVisibleTo): caller-supplied client.</summary>
    internal RegisteredCallsignCache(HttpClient http, TimeProvider? clock = null)
        : this(http, ownsHttp: false, clock)
    {
    }

    private RegisteredCallsignCache(HttpClient http, bool ownsHttp, TimeProvider? clock)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _clock = clock ?? TimeProvider.System;
        _timer = new Timer(_ => Fire(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static HttpClient BuildClient(string url) => new()
    {
        BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/", UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private void Fire() => _ = PumpAsync();

    /// <summary>One refresh cycle; re-entrancy-guarded and never throws.</summary>
    internal async Task PumpAsync()
    {
        if (_disposed) return;
        if (!_gate.Wait(0)) return;
        try
        {
            // At least RefreshSeconds between attempts (a failed lobby is not hammered).
            var now = _clock.GetUtcNow();
            if (_lastAttempt != default && (now - _lastAttempt) < RefreshSeconds) return;
            _lastAttempt = now;

            string[]? names;
            try
            {
                using var resp = await _http.GetAsync("/api/identity/callsigns").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[identity] reserved-callsign fetch status {(int)resp.StatusCode} — keeping last snapshot");
                    return;
                }
                names = await resp.Content.ReadFromJsonAsync(ServerJsonContext.Default.StringArray).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
            {
                Console.WriteLine($"[identity] reserved-callsign fetch failed ({e.Message}) — fail open, protecting last snapshot");
                return;
            }
            if (names is null) return;
            _normalized = new HashSet<string>(names.Where(n => n.Length > 0).Select(n => n.ToUpperInvariant()), StringComparer.Ordinal);
            Console.WriteLine($"[identity] reserved-callsign cache refreshed with {names.Length} registered callsign(s)");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether the given (case-insensitive) name is a registered callsign.
    /// False when the cache is empty (never fetched / lobby unreachable) — fail open.</summary>
    public bool Contains(string name) => name.Length > 0 && _normalized.Contains(name.ToUpperInvariant());

    /// <summary>Immutable snapshot of the currently-known normalized names (tests / diagnostics).</summary>
    public IReadOnlyCollection<string> Snapshot => _normalized;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}

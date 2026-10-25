using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Thin HTTP client for the lobby's zone-events endpoints (P820b) — the port's
/// replacement for the native club zone-events feed
/// (<c>WinTrek/zoneeventsscreen.cpp</c>). The event list and the Zone web link
/// are public (players read only); events are operator-managed lobby-side, so
/// this client is read-only. Mirrors
/// <c>Allegiance.Lobby.Events.EventEndpoints</c> routes.
/// </summary>
public sealed class EventApiClient : IDisposable
{
    private readonly HttpClient _http;

    public EventApiClient(string origin)
    {
        var uri = new Uri(origin);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Lobby events require HTTPS or localhost");
        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(5) };
    }

    public EventApiClient(HttpClient http) => _http = http;

    public async Task<EventDto.ZoneEvent[]> GetEventsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/events", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(EventJsonContext.EventArray, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>The configurable Zone web link, or <c>null</c> when the lobby
    /// has none configured or is unreachable.</summary>
    public async Task<string?> GetZoneWebUrlAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/config/zone-web", ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            var zw = await resp.Content.ReadFromJsonAsync(EventJsonContext.ZoneWebInfo, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(zw?.Url) ? null : zw.Url;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

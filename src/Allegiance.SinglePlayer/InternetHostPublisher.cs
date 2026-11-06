using System.Net;
using System.Net.Http.Json;
using Allegiance.Core.Transport;

namespace Allegiance.SinglePlayer;

/// <summary>Owns a player listing and its dedicated introduction poll. Separate
/// from dedicated-server heartbeats: their cadence, payloads and defaults stay intact.</summary>
public sealed class InternetHostPublisher : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly UdpTransport _transport;
    private readonly Func<int> _players;
    private readonly string _name;
    private readonly int _port, _maxPlayers;
    private readonly CancellationTokenSource _stop = new();
    private (IPEndPoint Primary, IPEndPoint Secondary) _servers;
    private PlayerHostRegistered? _registration;
    private Task? _pump;
    private string _status = "Connecting to lobby";
    private int _disposed;
    public string Status => Volatile.Read(ref _status);
    public string? ListingId => _registration?.Id;

    private InternetHostPublisher(UdpTransport transport, HttpClient http, bool ownsHttp, string name, int port, int maxPlayers, Func<int> players)
    { _transport = transport; _http = http; _ownsHttp = ownsHttp; _name = name; _port = port; _maxPlayers = maxPlayers; _players = players; }

    public static async Task<InternetHostPublisher> StartAsync(UdpTransport transport, string lobbyUrl, string name, int port,
        int maxPlayers, Func<int> players, CancellationToken ct = default, HttpClient? http = null)
    {
        var publisher = new InternetHostPublisher(transport, http ?? NatTraversalClient.CreateHttp(lobbyUrl), http is null, name, port, maxPlayers, players);
        try
        {
            publisher._servers = await NatTraversalClient.ConfigurationAsync(publisher._http, ct).ConfigureAwait(false);
            await publisher.RefreshAsync(ct).ConfigureAwait(false);
            publisher._pump = publisher.RunAsync();
            return publisher;
        }
        catch { publisher.Dispose(); throw; }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var observation = await NatTraversalClient.DiscoverStableAsync(_transport, _servers, ct).ConfigureAwait(false);
        var request = new PlayerHostRegistration(_name, _port, _registration?.Token, observation.Receipt!, _players(), _maxPlayers, "PCore001.igc");
        using var response = await _http.PostAsJsonAsync("/api/servers/register", request, NatJsonContext.Default.PlayerHostRegistration, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) { _registration = null; response.EnsureSuccessStatusCode(); }
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new IOException("Lobby refused player hosting. Check playerHostedGames and the approved-server filter.");
        response.EnsureSuccessStatusCode();
        _registration = await response.Content.ReadFromJsonAsync(NatJsonContext.Default.PlayerHostRegistered, ct).ConfigureAwait(false)
            ?? throw new IOException("Empty player-host registration");
        Volatile.Write(ref _status, "Listed on lobby: " + _name + " (UDP direct)");
    }

    private async Task RunAsync()
    {
        long nextRefresh = Environment.TickCount64 + 5000;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                if (_registration is null || Environment.TickCount64 >= nextRefresh)
                {
                    nextRefresh = Environment.TickCount64 + 5000;
                    await RefreshAsync(_stop.Token).ConfigureAwait(false);
                }
                var registration = _registration!;
                using var response = await _http.PostAsJsonAsync($"/api/servers/{Uri.EscapeDataString(registration.Id)}/punch/poll",
                    new PunchPollRequest(registration.Token), NatJsonContext.Default.PunchPollRequest, _stop.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) { _registration = null; continue; }
                response.EnsureSuccessStatusCode();
                var batch = await response.Content.ReadFromJsonAsync(NatJsonContext.Default.PunchBatch, _stop.Token).ConfigureAwait(false);
                if (batch?.Introductions.Length > 0)
                    await Task.WhenAll(batch.Introductions.Take(32).Select(ProbeAsync)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
            { Volatile.Write(ref _status, "Internet listing unavailable: " + ex.Message); }
        }
    }
    private async Task ProbeAsync(PunchIntroduction introduction)
    {
        try { await _transport.PunchAsync(introduction, _stop.Token).ConfigureAwait(false); }
        catch (IOException) { } // a failed guest path does not take the host offline
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _pump?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        // Listings expire after 20s even if the final best-effort delete is lost.
        if (_registration is { } registration)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                using var response = _http.DeleteAsync($"/api/servers/{Uri.EscapeDataString(registration.Id)}?token={Uri.EscapeDataString(registration.Token)}", timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        }
        if (_ownsHttp) _http.Dispose();
        _stop.Dispose();
    }
}

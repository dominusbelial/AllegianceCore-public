using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Games;

/// <summary>
/// P871 / lobby::Create game — the client's HTTP face of the lobby's new-game
/// wizard (<c>Allegiance.Lobby.Games.GameEndpoints</c>, the port of native
/// <c>CREATE_MISSION_REQ</c>/<c>ACK</c>/<c>NACK</c>, <c>lobby/client.cpp:390-465</c>).
/// </summary>
public sealed class LobbyGameClient
{
    public sealed record CatalogDto(IReadOnlyList<ServerChoiceDto>? Servers,
        IReadOnlyList<string>? Cores, IReadOnlyList<string>? Maps);
    public sealed record ServerChoiceDto(string Id, string Name, string Address, int Port,
        string Core, string MissionName, int CurrentGames = 0, int MaxGames = 20);
    // P871b — the mission parameters travel as native's packed `MissionParams`
    // struct, base64'd (`MissionParamsMsg.Encode()`), exactly as the games
    // endpoint documents. Ordinary JSON (properties only) carries the rest, so
    // the lobby's global serializer keeps its default contract.
    public sealed record CreateBody(string ServerId, string Core, string Map, string GameName,
        string ParamsWire);
    public sealed record EditBody(string ParamsWire);
    public sealed record GameDto(string Id, string GameName, string State, string? ParamsWire, uint? Cookie = null);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        // The lobby's endpoints speak camelCase (the framework default); the
        // response side stays case-insensitive.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly Func<string?>? _bearer;

    public LobbyGameClient(HttpClient http, string lobbyUrl, Func<string?>? bearer = null)
    {
        _http = http;
        _bearer = bearer;
        _base = new Uri(lobbyUrl.EndsWith('/') ? lobbyUrl : lobbyUrl + "/", UriKind.Absolute);
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (_bearer?.Invoke() is { Length: > 0 } token)
            request.Headers.Authorization = new("Bearer", token);
    }

    /// <summary>The authoritative server/core/map catalog.</summary>
    public async Task<CreateGameWizard.Catalog?> GetCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "api/games/catalog"));
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[create] lobby catalog: {request.RequestUri} -> {(int)response.StatusCode}");
                return null;
            }
            var dto = await response.Content.ReadFromJsonAsync<CatalogDto>(Json, ct).ConfigureAwait(false);
            if (dto is null) return null;
            return new CreateGameWizard.Catalog(
                (dto.Servers ?? []).Select(s => new CreateGameWizard.ServerChoice(
                    s.Id, s.Name, s.Address, s.Port, s.Core, s.MissionName, s.CurrentGames, s.MaxGames)).ToArray(),
                dto.Cores ?? [],
                dto.Maps ?? []);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"[create] lobby catalog unavailable: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>Submit the wizard's create request (native CREATE_MISSION_REQ).
    /// Returns the request id on an ACK, null on a NACK/unreachable lobby.
    /// <paramref name="error"/> carries the lobby's refusal reason.</summary>
    public async Task<(string? Id, string? Error)> CreateAsync(CreateGameWizard.CreateGameRequest request,
        CancellationToken ct = default)
    {
        string? error = null;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "api/games"))
            {
                Content = JsonContent.Create(new CreateBody(request.ServerId, request.Core, request.Map,
                    request.GameName, System.Convert.ToBase64String(request.Params.Encode())), options: Json),
            };
            Authorize(message);
            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    error = "Sign in to create a game.";
                else
                {
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    error = body.Length > 0 ? body : "The lobby refused the request.";
                }
                return (null, error);
            }
            var game = await response.Content.ReadFromJsonAsync<GameDto>(Json, ct).ConfigureAwait(false);
            return game?.Id is { } id ? (id, null) : (null, "The lobby returned no request id.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, "Lobby unreachable.");
        }
    }

    /// <summary>Wait for the server's CREATE_MISSION_ACK equivalent, including its cookie.
    /// The initial POST only queues a request; it cannot identify a joinable world.</summary>
    public async Task<uint?> WaitForMissionAsync(string id, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "api/games"));
                Authorize(request);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var games = await response.Content.ReadFromJsonAsync<GameDto[]>(Json, timeout.Token).ConfigureAwait(false);
                var game = games?.FirstOrDefault(g => g.Id == id);
                if (game?.State == "Ready" && game.Cookie is { } cookie) return cookie;
                if (game?.State is "Failed" or "Cancelled") return null;
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { return null; }
    }

    /// <summary>Owner-only parameter edit of a created game (native kept editing
    /// the advertised MissionParams). Returns true on success.</summary>
    public async Task<bool> UpdateParamsAsync(string requestId, MissionParamsMsg parameters,
        CancellationToken ct = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, $"api/games/{requestId}/params"))
            {
                Content = JsonContent.Create(
                    new EditBody(System.Convert.ToBase64String(parameters.Encode())), options: Json),
            };
            Authorize(message);
            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

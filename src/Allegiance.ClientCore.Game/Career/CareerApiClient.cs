using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Thin HTTP client for the lobby's career endpoints (P808b) — character info,
/// the leaderboard, and profile-description editing. Mirrors the lobby's
/// <c>/api/career/*</c> routes (<c>Allegiance.Lobby.Career.CareerEndpoints</c>)
/// and deserializes the local <see cref="CareerDto"/> wire mirrors with the
/// source-generated <see cref="CareerJsonContext"/>. Reads are public; the
/// description edit requires a bearer session token set on this client.
/// </summary>
public sealed class CareerApiClient : IDisposable
{
    private readonly HttpClient _http;

    public CareerApiClient(string origin)
    {
        var uri = new Uri(origin);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Lobby career requires HTTPS or localhost");
        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Inject an existing client (test seam / share the identity session).</summary>
    public CareerApiClient(HttpClient http) => _http = http;

    /// <summary>Set the bearer session so <see cref="SetDescriptionAsync"/> acts as the signed-in player.</summary>
    public void SetSession(string? bearer) =>
        _http.DefaultRequestHeaders.Authorization = bearer is null ? null : new AuthenticationHeaderValue("Bearer", bearer);

    /// <summary>Fetch a player's character info (native <c>REQ_CHARACTER_INFO</c> →
    /// <c>CHARACTER_INFO_GENERAL</c> + <c>CHARACTER_INFO_BY_CIV</c>).</summary>
    public async Task<CareerDto.CareerInfo?> GetCareerAsync(string accountId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/career/{Uri.EscapeDataString(accountId)}", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(CareerJsonContext.Info, ct).ConfigureAwait(false);
    }

    /// <summary>Query the leaderboard (native <c>LEADER_BOARD_QUERY</c>).</summary>
    public async Task<CareerDto.LeaderboardResponse?> GetLeaderboardAsync(
        short civ, CareerDto.LeaderBoardSort sort, string? basis = null, string? basisAccountId = null, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder($"/api/career/leaderboard?civ={civ}&sort={sort}");
        if (!string.IsNullOrEmpty(basis)) sb.Append("&basis=").Append(Uri.EscapeDataString(basis));
        if (!string.IsNullOrEmpty(basisAccountId)) sb.Append("&basisAccountId=").Append(Uri.EscapeDataString(basisAccountId));
        using var resp = await _http.GetAsync(sb.ToString(), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // LEADER_BOARD_QUERY_FAIL (ClubMessages.h:352-354): surface the reason.
            var err = await resp.Content.ReadFromJsonAsync<LeaderboardError>(ct).ConfigureAwait(false);
            return new CareerDto.LeaderboardResponse([], null, null, err?.Error ?? "not found");
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(CareerJsonContext.Board, ct).ConfigureAwait(false);
    }

    /// <summary>Edit the signed-in player's description (native
    /// <c>CHARACTER_INFO_EDIT_DESCRIPTION</c>). True on success; false on
    /// unauthorized (not signed in / not the owner).</summary>
    public async Task<bool> SetDescriptionAsync(string accountId, string description, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync($"/api/career/{Uri.EscapeDataString(accountId)}/description",
            new CareerDto.DescriptionRequest(description), CareerJsonContext.Desc, ct).ConfigureAwait(false);
        return resp.StatusCode == HttpStatusCode.NoContent;
    }

    public void Dispose() => _http.Dispose();

    internal sealed record LeaderboardError(string? Error);
}

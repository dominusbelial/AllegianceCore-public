using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Thin HTTP client for the lobby's squad endpoints (P820a) — the port's
/// replacement for the native Club <c>SQUAD_*</c> socket messages
/// (<c>Inc/ClubMessages.h:129-301</c>). Reads are public; every mutation
/// requires a signed-in session (set via <see cref="SetSession"/>) and is
/// rejected by the lobby with 401 when unsigned. Mirrors
/// <c>Allegiance.Lobby.Squad.SquadEndpoints</c> routes.
/// </summary>
public sealed class SquadApiClient : IDisposable
{
    private readonly HttpClient _http;

    public SquadApiClient(string origin)
    {
        var uri = new Uri(origin);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Lobby squads require HTTPS or localhost");
        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(5) };
    }

    public SquadApiClient(HttpClient http) => _http = http;

    public void SetSession(string? bearer) =>
        _http.DefaultRequestHeaders.Authorization = bearer is null ? null : new AuthenticationHeaderValue("Bearer", bearer);

    public void ClearSession() => _http.DefaultRequestHeaders.Authorization = null;

    // ---- Public reads ----
    public async Task<SquadDto.SquadSummary[]> GetAllAsync(SquadDto.SquadSortColumn sort, string? basisId, int count = 21, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder($"/api/squads/?sort={sort}&count={count}");
        if (basisId is not null) sb.Append("&basisId=").Append(Uri.EscapeDataString(basisId));
        using var resp = await _http.GetAsync(sb.ToString(), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(SquadJsonContext.SummaryArray, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<SquadDto.SquadSummary[]?> GetMineAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/squads/mine", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(SquadJsonContext.SummaryArray, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<SquadDto.SquadSummary[]> GetByPlayerAsync(string accountId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/squads/by-player/{Uri.EscapeDataString(accountId)}", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(SquadJsonContext.SummaryArray, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<SquadDto.SquadSummary[]> FindAsync(string q, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/squads/find?q=" + Uri.EscapeDataString(q), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(SquadJsonContext.SummaryArray, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<SquadDto.SquadDetails?> GetDetailsAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/squads/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(SquadJsonContext.Details, ct).ConfigureAwait(false);
    }

    public async Task<SquadDto.SquadLogEntry[]?> GetLogAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/squads/{Uri.EscapeDataString(id)}/log", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(SquadJsonContext.LogArray, ct).ConfigureAwait(false) ?? [];
    }

    // ---- Mutations (signed-in) ----
    public async Task<SquadDto.SquadActionResult?> CreateAsync(SquadDto.CreateSquadRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/squads/", req, SquadJsonContext.Create, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }

    public async Task<SquadDto.SquadActionResult?> EditAsync(string id, SquadDto.EditSquadRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync($"/api/squads/{Uri.EscapeDataString(id)}", req, SquadJsonContext.Edit, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }

    public async Task<SquadDto.SquadActionResult?> JoinAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/join", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }
    public async Task<SquadDto.SquadActionResult?> CancelJoinAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/cancel-join", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }
    public async Task<SquadDto.SquadActionResult?> AcceptAsync(string id, string memberId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/accept/{Uri.EscapeDataString(memberId)}", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }
    public async Task<SquadDto.SquadActionResult?> RejectAsync(string id, string memberId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/reject/{Uri.EscapeDataString(memberId)}", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }
    public async Task<SquadDto.SquadActionResult?> PromoteAsync(string id, string memberId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/promote/{Uri.EscapeDataString(memberId)}", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }
    public async Task<SquadDto.SquadActionResult?> DemoteAsync(string id, string memberId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/demote/{Uri.EscapeDataString(memberId)}", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }
    public async Task<SquadDto.SquadActionResult?> TransferAsync(string id, string memberId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/transfer/{Uri.EscapeDataString(memberId)}", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }
    public async Task<SquadDto.SquadActionResult?> QuitAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/squads/{Uri.EscapeDataString(id)}/quit", null, ct).ConfigureAwait(false);
        return await ReadAction(resp, ct).ConfigureAwait(false);
    }

    private static async Task<SquadDto.SquadActionResult?> ReadAction(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return new SquadDto.SquadActionResult(false, "Not signed in.");
        if (resp.StatusCode == HttpStatusCode.NotFound) return new SquadDto.SquadActionResult(false, "Squad not found.");
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            var err = await resp.Content.ReadFromJsonAsync<SquadError>(ct).ConfigureAwait(false);
            return new SquadDto.SquadActionResult(false, err?.Error ?? "Request refused.");
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(SquadJsonContext.Action, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    internal sealed record SquadError(string? Error);
}

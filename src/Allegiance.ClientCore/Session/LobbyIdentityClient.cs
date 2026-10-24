using System.Net.Http.Headers;
using System.Net.Http.Json;
using Allegiance.Core.Identity;

namespace Allegiance.ClientCore.Session;

/// <summary>Device authorization and a memory-only, revocable lobby session. No browser credential enters the game.</summary>
public sealed class LobbyIdentityClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _session;
    public IdentityAccount? Account { get; private set; }
    public LobbyIdentityClient(string origin)
    {
        var uri = new Uri(origin);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Lobby sign-in requires HTTPS or localhost");
        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(5) };
    }
    public async Task<DeviceAuthorization> StartAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("/api/identity/device", new DeviceStartRequest("player", "Allegiance game client"), IdentityJsonContext.Default.DeviceStartRequest, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(IdentityJsonContext.Default.DeviceAuthorization, ct))!;
    }
    public async Task<string> PollAsync(DeviceAuthorization device, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("/api/identity/device/poll", new DevicePollRequest(device.DeviceCode), IdentityJsonContext.Default.DevicePollRequest, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(IdentityJsonContext.Default.DeviceResult, ct);
        if (result is { Status: "approved", Session: not null, Account: not null })
        {
            _session = result.Session; Account = result.Account;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        }
        return result?.Status ?? "expired";
    }
    public async Task<string> JoinTokenAsync(string serverId, CancellationToken ct = default)
    {
        if (_session is null) throw new InvalidOperationException("Sign in to the lobby first");
        using var response = await _http.PostAsJsonAsync("/api/identity/join-token", new JoinTokenRequest(serverId), IdentityJsonContext.Default.JoinTokenRequest, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(IdentityJsonContext.Default.JoinTokenResponse, ct))!.Token;
    }
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        // Keep the credential until revocation succeeds so a failed network request can be retried.
        using var response = await _http.PostAsJsonAsync("/api/identity/signout", new DevicePollRequest(""), IdentityJsonContext.Default.DevicePollRequest, ct);
        response.EnsureSuccessStatusCode();
        _session = null; Account = null; _http.DefaultRequestHeaders.Authorization = null;
    }
    public void Dispose() => _http.Dispose();
}

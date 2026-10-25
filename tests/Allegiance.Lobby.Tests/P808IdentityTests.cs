using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

public sealed class P808IdentityTests
{
    private static HttpClient Client(LobbyTestApp app)
    {
        var http = app.CreateClient(); http.DefaultRequestHeaders.Add("Origin", "http://localhost:5000"); return http;
    }
    [Fact]
    public async Task Passkey_account_is_stable_unique_persistent_and_requires_real_proof()
    {
        using var app = new LobbyTestApp(); using var http = Client(app); using var authenticator = new VirtualPasskey();
        using var created = await authenticator.Register(http, "P808Pilot");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var account = (await created.Content.ReadFromJsonAsync<IdentityAccount>())!;
        Assert.Equal("P808Pilot", account.Callsign);
        Assert.True(Guid.TryParseExact(account.Id, "N", out _));
        using var signedOut = await http.PostAsJsonAsync("/api/identity/signout", new { });
        using var refused = await http.GetAsync("/api/identity/me"); Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var wrongOrigin = await authenticator.Login(http, "https://attacker.invalid"); Assert.Equal(HttpStatusCode.BadRequest, wrongOrigin.StatusCode);
        using var forged = await authenticator.Login(http, forge: true); Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var loggedIn = await authenticator.Login(http); Assert.Equal(account, await loggedIn.Content.ReadFromJsonAsync<IdentityAccount>());
        using var other = new VirtualPasskey();
        using var duplicate = await other.Register(http, "p808pilot"); Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var settings = app.Services.GetRequiredService<IdentitySettings>();
        var reopened = new IdentityStore(settings, TimeProvider.System); reopened.Migrate(); Assert.Equal(account, reopened.Account(account.Id));
        using var existingKey = new IdentitySigningKey(settings, app.Services.GetRequiredService<SquadStore>());
        Assert.Equal(app.Services.GetRequiredService<IdentitySigningKey>().PublicKey, existingKey.PublicKey);
    }
    [Fact]
    public async Task Device_approval_token_verification_expiry_replay_revocation_and_server_enrollment()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = new LobbyTestApp(clock); using var browser = Client(app); using var device = Client(app); using var authenticator = new VirtualPasskey();
        using var registered = await authenticator.Register(browser, "DevicePilot"); registered.EnsureSuccessStatusCode();
        var account = (await registered.Content.ReadFromJsonAsync<IdentityAccount>())!;
        var store = app.Services.GetRequiredService<IdentityStore>();
        var grant = await Start(device, "player");
        Assert.Equal("pending", (await Poll(device, grant)).Status);
        Assert.Equal("slow_down", (await Poll(device, grant)).Status);
        using var unauthorized = await device.PostAsJsonAsync("/api/identity/device/approve", new { userCode = grant.UserCode }); Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var approved = await browser.PostAsJsonAsync("/api/identity/device/approve", new { userCode = grant.UserCode }); approved.EnsureSuccessStatusCode();
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await Poll(device, grant); Assert.Equal("approved", result.Status); Assert.Equal(account, result.Account);
        Assert.Equal("expired", (await Poll(device, grant)).Status); // redeemed once
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Session);
        var serverGrant = await Start(device, "server");
        using var nonOperator = await browser.PostAsJsonAsync("/api/identity/device/approve", new { userCode = serverGrant.UserCode }); Assert.Equal(HttpStatusCode.Forbidden, nonOperator.StatusCode);
        app.Services.GetRequiredService<IdentitySettings>().OperatorAccountIds = [account.Id];
        using var operatorApproval = await browser.PostAsJsonAsync("/api/identity/device/approve", new { userCode = serverGrant.UserCode }); operatorApproval.EnsureSuccessStatusCode();
        var server = await Poll(device, serverGrant); Assert.NotNull(server.ServerId); Assert.NotNull(server.ServerSecret);
        using var registeredServer = await device.PostAsJsonAsync("/api/servers/register", new { name = "Test server", port = 6073, identityServerId = server.ServerId, identityServerSecret = server.ServerSecret }); registeredServer.EnsureSuccessStatusCode();
        var listings = (await device.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!; Assert.True(Assert.Single(listings).Verified);
        using var issued = await device.PostAsJsonAsync("/api/identity/join-token", new JoinTokenRequest(server.ServerId!)); issued.EnsureSuccessStatusCode();
        string token = (await issued.Content.ReadFromJsonAsync<JoinTokenResponse>())!.Token;
        using var publicKey = ECDsa.Create(); publicKey.ImportFromPem(app.Services.GetRequiredService<IdentitySigningKey>().PublicKey);
        var claims = JoinTokens.Verify(token, publicKey, "http://localhost:5000", server.ServerId!, clock.GetUtcNow());
        Assert.Equal(account.Id, claims?.Sub); Assert.Equal(account.Callsign, claims?.Callsign);
        Assert.Null(JoinTokens.Verify(token, publicKey, "https://other.invalid", server.ServerId!, clock.GetUtcNow()));
        Assert.Null(JoinTokens.Verify(token, publicKey, "http://localhost:5000", "wrong-server", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(60)); Assert.Null(JoinTokens.Verify(token, publicKey, "http://localhost:5000", server.ServerId!, clock.GetUtcNow()));
        using var signout = await device.PostAsJsonAsync("/api/identity/signout", new { }); signout.EnsureSuccessStatusCode(); Assert.Null(store.Authenticate(result.Session));
        using var afterSignout = await device.PostAsJsonAsync("/api/identity/join-token", new JoinTokenRequest(server.ServerId!)); Assert.Equal(HttpStatusCode.Unauthorized, afterSignout.StatusCode);
    }
    [Fact]
    public async Task Grants_expire_and_only_one_concurrent_poll_consumes_approval()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); using var app = new LobbyTestApp(clock); using var http = Client(app);
        var store = app.Services.GetRequiredService<IdentityStore>(); var account = store.CreateAccount("RacePilot");
        var grant = await Start(http, "player"); Assert.True(store.Approve(grant.UserCode, account.Id));
        var polls = await Task.WhenAll(Task.Run(() => store.Poll(grant.DeviceCode)), Task.Run(() => store.Poll(grant.DeviceCode)));
        Assert.Single(polls, p => p.Status == "approved");
        var expired = await Start(http, "player"); clock.Advance(TimeSpan.FromSeconds(300)); Assert.False(store.Approve(expired.UserCode, account.Id)); Assert.Equal("expired", store.Poll(expired.DeviceCode).Status);
        string challenge = store.Challenge("test", "options"); clock.Advance(TimeSpan.FromSeconds(300)); Assert.Null(store.ConsumeChallenge(challenge, "test"));
    }
    [Fact]
    public async Task Browser_csrf_optional_providers_and_listing_spoofing_are_refused()
    {
        using var app = new LobbyTestApp(); using var http = Client(app);
        Assert.Empty((await http.GetFromJsonAsync<string[]>("/api/identity/providers"))!);
        using var disabled = await http.GetAsync("/api/identity/external/GitHub"); Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin"); http.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        using var csrf = await http.PostAsJsonAsync("/api/identity/device", new DeviceStartRequest("player", "Browser")); Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        using var spoof = await http.PostAsJsonAsync("/api/servers/register", new { name = "Spoof", port = 6073, verified = true }); spoof.EnsureSuccessStatusCode();
        Assert.False(Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!).Verified);
        using var forged = await http.PostAsJsonAsync("/api/servers/register", new { name = "Forged", port = 6074, identityServerId = "forged", identityServerSecret = "forged" }); Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }
    private static async Task<DeviceAuthorization> Start(HttpClient http, string purpose)
    {
        using var r = await http.PostAsJsonAsync("/api/identity/device", new DeviceStartRequest(purpose, "P808 test")); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<DeviceAuthorization>())!;
    }
    private static async Task<DeviceResult> Poll(HttpClient http, DeviceAuthorization grant)
    {
        using var r = await http.PostAsJsonAsync("/api/identity/device/poll", new DevicePollRequest(grant.DeviceCode)); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<DeviceResult>())!;
    }
}

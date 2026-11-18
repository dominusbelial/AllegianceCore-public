using System.Net.Http.Json;
using Allegiance.Core.Transport;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Tests;

public sealed class P1052RouteSecurityTests
{
    [Theory]
    [InlineData("wss://127.0.0.1/game")]
    [InlineData("wss://localhost/game")]
    [InlineData("wss://localhost./game")]
    [InlineData("wss://localhost.localdomain/game")]
    [InlineData("wss://localhost6.localdomain6/game")]
    [InlineData("wss://\u200d.invalid/game")]
    [InlineData("wss://\ufffd.invalid/game")]
    [InlineData("wss://a\u00a0.invalid/game")]
    [InlineData("wss://a_.invalid/game")]
    [InlineData("wss://-a.invalid/game")]
    [InlineData("wss://localhost\uff0e/game")]
    [InlineData("wss://player.localhost/game")]
    [InlineData("wss://router.local/game")]
    [InlineData("wss://router.home.arpa/game")]
    [InlineData("wss://router/game")]
    [InlineData("wss://10.0.0.1/game")]
    [InlineData("wss://172.16.0.1/game")]
    [InlineData("wss://172.31.255.255/game")]
    [InlineData("wss://192.168.0.1/game")]
    [InlineData("wss://169.254.1.1/game")]
    [InlineData("wss://100.64.0.1/game")]
    [InlineData("wss://0.0.0.0/game")]
    [InlineData("wss://224.0.0.1/game")]
    [InlineData("wss://[::]/game")]
    [InlineData("wss://[::1]/game")]
    [InlineData("wss://[fc00::1]/game")]
    [InlineData("wss://[fe80::1]/game")]
    [InlineData("wss://[fec0::1]/game")]
    [InlineData("wss://[ff02::1]/game")]
    [InlineData("wss://[::ffff:192.168.1.1]/game")]
    [InlineData("wss://[::ffff:127.0.0.1]/game")]
    [InlineData("wss://2130706433/game")]
    [InlineData("wss://0x7f.1/game")]
    [InlineData("ws://127.0.0.1/game")]
    public async Task Verified_public_registrant_drops_local_private_or_malformed_routes_on_create_and_refresh(string route)
    {
        Assert.Throws<ArgumentException>(() => WebSocketEndpoint.FromServerListing(route));
        using var app = new LobbyTestApp(sourceIp: "203.0.113.5"); using var http = app.CreateClient();
        var req = EnrolledRequest(app); req.WebSocketUrl = route;
        using var created = await http.PostAsJsonAsync("/api/servers/register", req); created.EnsureSuccessStatusCode();
        var response = (await created.Content.ReadFromJsonAsync<RegisterResponse>())!;
        Assert.Null(Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!).WebSocketUrl);
        req.Token = response.Token; req.WebSocketUrl = "wss://game.invalid/game";
        using var valid = await http.PostAsJsonAsync("/api/servers/register", req); valid.EnsureSuccessStatusCode();
        Assert.Equal(req.WebSocketUrl, Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!).WebSocketUrl);
        req.WebSocketUrl = route;
        using var invalid = await http.PostAsJsonAsync("/api/servers/register", req); invalid.EnsureSuccessStatusCode();
        Assert.DoesNotContain("webSocketUrl", await http.GetStringAsync("/api/servers"));
    }

    [Theory]
    [InlineData("127.0.0.1", "wss://127.0.0.1/game", true)]
    [InlineData("::1", "wss://[::1]/game", true)]
    [InlineData("::ffff:127.0.0.1", "wss://10.0.0.1/game", true)]
    [InlineData("127.0.0.1", "ws://127.0.0.1/game", false)]
    [InlineData("127.0.0.1", "wss://\u200d.invalid/game", false)]
    [InlineData("127.0.0.1", "wss://a\u00a0.invalid/game", false)]
    [InlineData("203.0.113.5", "wss://game.invalid/game?version=1", true)]
    [InlineData("203.0.113.5", "wss://b\u00fccher.invalid/game", true)]
    [InlineData("203.0.113.5", "wss://192.0.2.1/game", true)]
    [InlineData("203.0.113.5", "wss://[2001:db8:1::1]/game", true)]
    [InlineData("203.0.113.5", "https://game.invalid/game", false)]
    [InlineData("203.0.113.5", "wss://user@game.invalid/game", false)]
    public async Task Verified_registration_host_exception_requires_a_loopback_source_and_always_wss(string source, string route, bool accepted)
    {
        using var app = new LobbyTestApp(sourceIp: source); using var http = app.CreateClient();
        var req = EnrolledRequest(app); req.WebSocketUrl = route;
        using var post = await http.PostAsJsonAsync("/api/servers/register", req); post.EnsureSuccessStatusCode();
        var listing = Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
        Assert.True(listing.Verified);
        Assert.Equal(accepted ? new Uri(route).AbsoluteUri : null, listing.WebSocketUrl);
        if (accepted && source == "203.0.113.5") Assert.Equal(new Uri(route), WebSocketEndpoint.FromServerListing(route).Uri);
        if (route.StartsWith("ws:", StringComparison.Ordinal)) Assert.Equal(new Uri(route), WebSocketEndpoint.FromConfiguration(route).Uri);
    }

    [Fact]
    public async Task Claimed_verification_and_address_do_not_grant_route_trust_and_refresh_drops_prior_route()
    {
        using var app = new LobbyTestApp(sourceIp: "203.0.113.5"); using var http = app.CreateClient();
        var req = EnrolledRequest(app); req.WebSocketUrl = "wss://game.invalid/game";
        using var created = await http.PostAsJsonAsync("/api/servers/register", req); created.EnsureSuccessStatusCode();
        req.Token = (await created.Content.ReadFromJsonAsync<RegisterResponse>())!.Token;
        req.IdentityServerId = null; req.IdentityServerSecret = null; req.Verified = true; req.Address = "127.0.0.1";
        using var refreshed = await http.PostAsJsonAsync("/api/servers/register", req); refreshed.EnsureSuccessStatusCode();
        var listing = Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
        Assert.False(listing.Verified); Assert.Equal("203.0.113.5", listing.Address); Assert.Null(listing.WebSocketUrl);
    }

    private static RegisterRequest EnrolledRequest(LobbyTestApp app)
    {
        var store = app.Services.GetRequiredService<IdentityStore>();
        var account = store.CreateAccount("P1052Operator");
        string id = Guid.NewGuid().ToString("N"), secret = IdentityStore.Secret();
        store.Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)", id, IdentityStore.Hash(secret), account.Id, "P1052");
        return new RegisterRequest { Name = "P1052", Port = 6073, IdentityServerId = id, IdentityServerSecret = secret };
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Builder;

namespace Allegiance.Lobby.Tests;

public sealed class P655DirectoryIntegrityTests
{
    [Fact]
    public async Task H23_Claimed_address_and_untrusted_forwarded_headers_cannot_spoof_listing()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.77");
        var response = await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Name = "Real\u202Ename", Address = "victim.example", Port = 6073 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listing = Assert.Single((await client.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
        Assert.Equal("127.0.0.1", listing.Address);
        Assert.Equal("Realname", listing.Name);
        var reg = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Token = reg.Token, Address = "127.0.0.1", Port = 6073 })).StatusCode);
    }

    [Fact]
    public async Task H23_Per_source_cap_blocks_slot_exhaustion_but_heartbeats_work()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        RegisterResponse? first = null;
        for (int i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Name = "Server", Address = "127.0.0.1", Port = 6073 + i });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            first ??= await response.Content.ReadFromJsonAsync<RegisterResponse>();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Address = "elsewhere", Port = 7000 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Token = first!.Token, Port = 6073 })).StatusCode);
    }

    [Fact]
    public void H23_Expired_entries_are_not_listed_before_background_sweep()
    {
        var clock = new FakeTimeProvider();
        var registry = new ServerRegistry(clock);
        var req = new RegisterRequest { Name = "Server", Address = "127.0.0.1", Port = 6073 };
        var first = registry.Register(req)!;
        Assert.Single(registry.List());
        clock.Advance(TimeSpan.FromSeconds(91));
        Assert.Empty(registry.List());
        req.Token = first.Token;
        Assert.NotNull(registry.Register(req));
        Assert.Single(registry.List());
    }
    [Fact]
    public async Task H23_Request_body_limit_refuses_large_payload_normal_registration_works()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Name = new string('x', 20000), Port = 6073 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Name = "Normal", Port = 6073 })).StatusCode);
    }

    [Fact]
    public void H23_Concurrent_registrations_cannot_race_the_source_cap_other_sources_work()
    {
        var registry = new ServerRegistry(TimeProvider.System);
        Parallel.For(0, 100, i => registry.Register(new RegisterRequest { Address = "192.0.2.1", Port = 6073 + i }));
        Assert.Equal(4, registry.Count);
        Assert.NotNull(registry.Register(new RegisterRequest { Address = "192.0.2.2", Port = 6073 }));
    }
    [Fact]
    public async Task H23_Only_configured_proxy_may_supply_the_forwarded_source()
    {
        using var app = new LobbyTestApp();
        using var proxyApp = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                options.KnownProxies.Add(IPAddress.Loopback);
            })));
        using var client = proxyApp.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.77");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Port = 6073 })).StatusCode);
        Assert.Equal("203.0.113.77", Assert.Single((await client.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!).Address);
    }

    [Fact]
    public async Task H23_Registration_budget_throttles_heartbeats_without_blocking_initial_normal_request()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Port = 6073 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        for (int i = 0; i < 29; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Port = 6073, Token = first.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Port = 6073, Token = first.Token })).StatusCode);
    }
}

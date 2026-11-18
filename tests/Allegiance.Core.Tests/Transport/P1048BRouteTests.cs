using System.Net.Http.Json;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;
using Allegiance.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1048BRouteTests
{
    private sealed record OldClient(string Id, string Name, string Address, int Port);
    [Theory]
    [InlineData(false, null, null)]
    [InlineData(false, "wss://game.invalid/game", null)]
    [InlineData(true, null, null)]
    [InlineData(true, "wss://game.invalid/game", "wss://game.invalid/game")]
    [InlineData(true, "ws://127.0.0.1:6080/game", "ws://127.0.0.1:6080/game")]
    public void Route_requires_enabled_listener_and_explicit_public_configuration(bool enabled, string? configured, string? expected)
    {
        var config = P1048WebSocketTests.Settings(); config.Enabled = enabled; config.PublicUrl = configured;
        config.Validate(); Assert.Equal(expected, config.AdvertisedUrl);
    }
    [Theory]
    [InlineData("ws://game.invalid/game")]
    [InlineData("wss://user@game.invalid/game")]
    [InlineData("https://game.invalid/game")]
    public void Invalid_public_configuration_fails_closed(string route)
    {
        var config = P1048WebSocketTests.Settings(); config.PublicUrl = route;
        Assert.Throws<ArgumentException>(config.Validate);
    }
    [Fact]
    public async Task Heartbeat_to_directory_relays_optional_route_and_old_clients_ignore_it()
    {
        using var app = new LobbyHarness();
        using var http = new HttpClient(app.Server.CreateHandler()) { BaseAddress = app.ClientOptions.BaseAddress };
        var identities = app.Services.GetRequiredService<IdentityStore>();
        var account = identities.CreateAccount("P1048B-" + Guid.NewGuid().ToString("N")[..16]);
        string serverId = Guid.NewGuid().ToString("N"), secret = IdentityStore.Secret();
        identities.Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)", serverId, IdentityStore.Hash(secret), account.Id, "P1048B");
        using var heartbeat = new LobbyHeartbeat(http, new LobbyConfig
        { Url = http.BaseAddress!.AbsoluteUri, PublicAddress = "127.0.0.1", DisplayName = "P1048B" }, () => 0, "Mission", "PCore001.igc", "1", 6073);
        // In-memory enrollment keeps the real heartbeat/HTTP path without
        // writing a bearer credential to disk.
        typeof(LobbyHeartbeat).GetField("_enrollment", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(heartbeat, new ServerEnrollment(serverId, secret));
        await heartbeat.PumpAsync();
        string json = await http.GetStringAsync("/api/servers");
        Assert.DoesNotContain("webSocketUrl", json);
        Assert.Null(Assert.Single(JsonSerializer.Deserialize<List<ServerListingDto>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).WebSocketUrl);
        heartbeat.WebSocketUrl = "wss://game.invalid/game";
        await heartbeat.PumpAsync();
        json = await http.GetStringAsync("/api/servers");
        var listing = Assert.Single(JsonSerializer.Deserialize<List<ServerListingDto>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Equal(heartbeat.WebSocketUrl, listing.WebSocketUrl);
        var old = Assert.Single(JsonSerializer.Deserialize<List<OldClient>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Equal(listing.Id, old.Id); Assert.Equal(6073, old.Port);
        heartbeat.WebSocketUrl = null; await heartbeat.PumpAsync();
        Assert.DoesNotContain("webSocketUrl", await http.GetStringAsync("/api/servers"));
    }
    [Theory]
    [InlineData("ws://remote.invalid/game")]
    [InlineData("wss://user:password@remote.invalid/game")]
    [InlineData("wss://remote.invalid/game#fragment")]
    public async Task Directory_does_not_publish_invalid_routes_from_untrusted_register_payloads(string route)
    {
        using var app = new LobbyHarness(); using var http = app.CreateClient();
        var response = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
        { Name = "P1048B", Address = "127.0.0.1", Port = 6073, WebSocketUrl = route });
        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain("webSocketUrl", await http.GetStringAsync("/api/servers"));
    }
}

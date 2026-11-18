using System.Text.Json;
using Allegiance.ClientCore.Browser.Pages;

namespace Allegiance.ClientCore.Game.Browser.Tests;

public sealed class P1052BrowserListingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("wss://game.invalid/game", "wss://game.invalid/game")]
    [InlineData("ws://127.0.0.1/game", null)]
    [InlineData("wss://127.0.0.1/game", null)]
    [InlineData("wss://192.168.1.1/game", null)]
    [InlineData("wss://[fc00::1]/game", null)]
    [InlineData("wss://router.local/game", null)]
    [InlineData("future://game.invalid/game", null)]
    [InlineData("wss://\u200d.invalid/game", null)]
    [InlineData("wss://a\u00a0.invalid/game", null)]
    public void Browser_listing_ingestion_uses_the_public_WSS_rule_and_preserves_old_servers(string? route, string? expected)
    {
        string field = route is null ? "" : ",\"webSocketUrl\":" + JsonSerializer.Serialize(route);
        string json = "[{\"id\":\"P1052\"" + field + "}]";
        var listing = Assert.Single(JsonSerializer.Deserialize(json, BrowserJoinJson.Default.BrowserServerArray)!);
        Assert.Equal("P1052", listing.Id);
        Assert.Equal(expected, listing.Route(loopbackDirectory: false));
        Assert.Equal(expected, (listing with { WebSocketUrl = route }).Route(loopbackDirectory: false));
    }

    // P1051 merge: a lobby on loopback (local development) may list its own loopback
    // route; private/LAN routes and odd schemes are still refused; public WSS still works.
    [Theory]
    [InlineData("ws://127.0.0.1:8048/game", "ws://127.0.0.1:8048/game")]
    [InlineData("wss://localhost/game", "wss://localhost/game")]
    [InlineData("ws://[::1]:8048/game", "ws://[::1]:8048/game")]
    [InlineData("wss://game.invalid/game", "wss://game.invalid/game")]
    [InlineData("ws://192.168.1.1/game", null)]
    [InlineData("wss://192.168.1.1/game", null)]
    [InlineData("ws://game.invalid/game", null)]
    [InlineData("future://127.0.0.1/game", null)]
    public void Loopback_lobby_listing_accepts_only_loopback_or_public_WSS_routes(string route, string? expected)
    {
        var listing = new BrowserServer("P1051", "local", 6073, "m", "PCore001.igc", true, "id", route);
        Assert.Equal(expected, listing.Route(loopbackDirectory: true));
    }
}

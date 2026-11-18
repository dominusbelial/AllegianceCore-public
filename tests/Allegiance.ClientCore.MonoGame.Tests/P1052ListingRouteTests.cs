using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1052ListingRouteTests
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
    public void Desktop_listing_ingestion_drops_untrusted_routes_and_preserves_the_server(string? route, string? expected)
    {
        string json = JsonSerializer.Serialize(new { id = "P1052", webSocketUrl = route });
        var listing = JsonSerializer.Deserialize<ServerListing>(json, ServerListing.JsonOptions)!;
        Assert.Equal("P1052", listing.Id);
        Assert.Equal(expected, listing.WebSocketUrl);
        Assert.Equal(expected, (listing with { WebSocketUrl = route }).WebSocketUrl);
    }
}

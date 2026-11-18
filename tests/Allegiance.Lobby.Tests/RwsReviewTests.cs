using System.Net.Http.Json;

namespace Allegiance.Lobby.Tests;

/// <summary>RWS — adversarial review of the advertised WebSocket route (P1048b).
/// Each test states the behaviour the directory SHOULD have; all were run unskipped
/// and failed before being marked pending. See RWS_REPORT.md.</summary>
public class RwsReviewTests
{
    // The directory pins a listing's Address to the registrant's socket source, but the
    // browser route is free text: an anonymous, unverified registrant on a public address
    // can publish a route to the PLAYER'S OWN loopback or to an unrelated third-party host.
    [Theory(Skip = "RWS finding F3 — pending fix")]
    [InlineData("ws://127.0.0.1:6080/game")]
    [InlineData("ws://localhost:9229/json")]
    [InlineData("wss://third-party.invalid/anything?x=1")]
    public async Task F3_unverified_public_registrant_cannot_publish_a_route_it_does_not_own(string route)
    {
        using var app = new LobbyTestApp(sourceIp: "203.0.113.5");
        using var client = app.CreateClient();
        var post = await client.PostAsJsonAsync("/api/servers/register", new
        { name = "RWS", port = 6073, missionName = "m", coreFile = "PCore001.igc", version = "1", playerCount = 1, webSocketUrl = route });
        post.EnsureSuccessStatusCode();
        string json = await client.GetStringAsync("/api/servers");
        Assert.Contains("203.0.113.5", json);
        Assert.DoesNotContain("webSocketUrl", json);
    }
}

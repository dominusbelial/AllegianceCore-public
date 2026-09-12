using System.Net;
using System.Net.Http.Json;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// The idempotent-heartbeat path: a second register with the correct token must update
/// the SAME listing, never create a second one; and a token that matches nothing must be
/// rejected, not silently turned into a fresh registration.
/// </summary>
public class HeartbeatTests
{
    private static object Payload(int playerCount, string? token) => new
    {
        name = "Test Server",
        address = "1.2.3.4",
        port = 6073,
        playerCount,
        maxPlayers = 64,
        missionName = "Wing War",
        coreFile = "PCore001.igc",
        version = "0.1.0",
        token,
    };

    [Fact]
    public async Task Heartbeat_with_correct_token_updates_the_same_entry()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var first = await (await client.PostAsJsonAsync("/api/servers/register", Payload(5, null)))
            .Content.ReadFromJsonAsync<RegisterResponse>();

        // Second register with the same token, but different live population.
        var second = await (await client.PostAsJsonAsync("/api/servers/register", Payload(23, first!.Token)))
            .Content.ReadFromJsonAsync<RegisterResponse>();

        Assert.Equal(first.Token, second!.Token);
        Assert.Equal(first.Id, second.Id);

        var servers = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
        // Exactly one listing owns that id — the heartbeat refreshed it, not doubled it.
        var mine = Assert.Single(servers!, s => s.Id == first.Id);
        Assert.Equal(23, mine.PlayerCount);
    }

    [Fact]
    public async Task Register_with_a_token_matching_nothing_is_rejected()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        // Seed one real listing so we have a baseline count.
        var seed = await (await client.PostAsJsonAsync("/api/servers/register", Payload(5, null)))
            .Content.ReadFromJsonAsync<RegisterResponse>();
        var before = (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!.Count;

        // A token that exists nowhere in the directory.
        var bogus = await client.PostAsJsonAsync("/api/servers/register", Payload(9, "deadbeef-no-such-token"));

        // Rejected — not silently accepted as a brand-new registration.
        Assert.NotEqual(HttpStatusCode.OK, bogus.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, bogus.StatusCode);

        var after = (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!.Count;
        Assert.Equal(before, after);
    }
}

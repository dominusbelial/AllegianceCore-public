using System.Net;
using System.Net.Http.Json;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// Token-guarded deletion: only the owner may remove a listing. Wrong or missing token is
/// rejected and the listing survives.
/// </summary>
public class DeregisterTests
{
    private static object Payload() => new
    {
        name = "Test Server",
        address = "1.2.3.4",
        port = 6073,
        playerCount = 5,
        maxPlayers = 64,
        missionName = "Wing War",
        coreFile = "PCore001.igc",
        version = "0.1.0",
        token = (string?)null,
    };

    private static async Task<RegisterResponse> Register(HttpClient client)
        => (await (await client.PostAsJsonAsync("/api/servers/register", Payload()))
            .Content.ReadFromJsonAsync<RegisterResponse>())!;

    [Fact]
    public async Task Delete_with_correct_token_removes_the_listing()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var reg = await Register(client);

        var del = await client.DeleteAsync($"/api/servers/{reg.Id}?token={reg.Token}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.DoesNotContain(reg.Id, (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!
            .Select(s => s.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/servers/{reg.Id}")).StatusCode);
    }

    [Fact]
    public async Task Delete_with_wrong_token_is_rejected_and_listing_remains()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var reg = await Register(client);

        var del = await client.DeleteAsync($"/api/servers/{reg.Id}?token=wrong-token");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);

        Assert.Contains(reg.Id, (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!
            .Select(s => s.Id));
    }

    [Fact]
    public async Task Delete_with_missing_token_is_rejected()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var reg = await Register(client);

        var del = await client.DeleteAsync($"/api/servers/{reg.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);

        Assert.Contains(reg.Id, (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!
            .Select(s => s.Id));
    }
}

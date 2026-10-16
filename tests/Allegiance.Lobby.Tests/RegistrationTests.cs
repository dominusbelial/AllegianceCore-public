using System.Net;
using System.Net.Http.Json;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P174 Stage 1 — the core register→list round trip over a real in-process host
/// (<see cref="LobbyTestApp"/>, i.e. <c>WebApplicationFactory&lt;Program&gt;</c>).
/// </summary>
public class RegistrationTests
{
    private static object BasicPayload(string? token = null) => new
    {
        name = "Test Server",
        address = "1.2.3.4",
        port = 6073,
        playerCount = 5,
        maxPlayers = 64,
        missionName = "Wing War",
        coreFile = "PCore001.igc",
        version = "0.1.0",
        token = token,
    };

    [Fact]
    public async Task Fresh_registration_returns_a_token_and_appears_in_the_list()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", BasicPayload());
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var resp = await post.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(resp);
        Assert.False(string.IsNullOrWhiteSpace(resp!.Id));
        Assert.False(string.IsNullOrWhiteSpace(resp.Token));

        var servers = await ListServers(client);
        var mine = Assert.Single(servers, s => s.Id == resp.Id);
        Assert.Equal("Test Server", mine.Name);
        Assert.Equal("127.0.0.1", mine.Address);
        Assert.Equal(6073, mine.Port);
        Assert.Equal(5, mine.PlayerCount);
        Assert.Equal(64, mine.MaxPlayers);
        Assert.Equal("Wing War", mine.MissionName);
        Assert.Equal("PCore001.igc", mine.CoreFile);
    }

    [Fact]
    public async Task Detail_endpoint_returns_the_listing_and_missing_id_is_404()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", BasicPayload());
        var resp = await post.Content.ReadFromJsonAsync<RegisterResponse>();

        var detail = await client.GetAsync($"/api/servers/{resp!.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var dto = await detail.Content.ReadFromJsonAsync<ServerListingDto>();
        Assert.Equal(resp.Id, dto!.Id);

        var missing = await client.GetAsync("/api/servers/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Hygiene_caps_length_and_strips_control_characters()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var evilName = new string('A', 500) + "\n\r\u0000<script>alert(1)</script>";
        var post = await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = evilName,
            address = "10.0.0.1",
            port = 7000,
            playerCount = 0,
            maxPlayers = 1,
            missionName = (string?)null,
            coreFile = "",
            version = "v1",
            token = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var resp = await post.Content.ReadFromJsonAsync<RegisterResponse>();

        var servers = await ListServers(client);
        var mine = Assert.Single(servers, s => s.Id == resp!.Id);

        // Name must be capped at 100 chars and free of control characters / markup.
        Assert.Equal(100, mine.Name.Length);
        Assert.DoesNotContain('<', mine.Name);
        Assert.DoesNotContain('\n', mine.Name);
        Assert.All(mine.Name, c => Assert.False(char.IsControl(c)));
        // Null/blank optional fields collapse to empty, not null.
        Assert.Equal(string.Empty, mine.MissionName);
        Assert.Equal(string.Empty, mine.CoreFile);
    }

    private static async Task<List<ServerListingDto>> ListServers(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
        return list ?? [];
    }
}

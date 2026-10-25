using System.Net.Http;
using System.Net.Http.Json;
using Allegiance.Update;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P810 — <c>GET /api/update/info</c>, the HTTP analogue of native's
/// AUTO_UPDATE_INFO(253) (<c>lobby/messageslc.h:45-52</c>): what the lobby
/// advertises, and that an unconfigured lobby advertises nothing (the default
/// answer leaves every client untouched, like a native lobby with AutoUpdate
/// unconfigured). Also the register-payload's optional
/// <c>minClientVersion</c>, the advertised form of native's LOGON
/// <c>crcFileList</c> gate (<c>lobby/client.cpp:272-281</c>).
/// </summary>
public class UpdateInfoTests
{
    [Fact]
    public async Task An_unconfigured_lobby_advertises_nothing()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var info = await client.GetFromJsonAsync<LobbyUpdateInfo>("/api/update/info");

        Assert.NotNull(info);
        Assert.Null(info!.CurrentVersion);
        Assert.Null(info.MinimumVersion);
        Assert.Null(info.FeedUrl);
    }

    [Fact]
    public async Task A_configured_lobby_advertises_versions_and_feed()
    {
        using var app = new LobbyTestApp(configure: s =>
        {
            s.CurrentClientVersion = "2.0.0";
            s.MinimumClientVersion = "1.9.0";
            s.UpdateFeedUrl = "https://example.invalid/feed/";
        });
        using var client = app.CreateClient();

        var info = await client.GetFromJsonAsync<LobbyUpdateInfo>("/api/update/info");

        Assert.Equal("2.0.0", info!.CurrentVersion);
        Assert.Equal("1.9.0", info.MinimumVersion);
        Assert.Equal("https://example.invalid/feed/", info.FeedUrl);
    }

    [Fact]
    public async Task Empty_strings_are_advertised_as_absent()
    {
        using var app = new LobbyTestApp(configure: s => s.CurrentClientVersion = "");
        using var client = app.CreateClient();

        var info = await client.GetFromJsonAsync<LobbyUpdateInfo>("/api/update/info");

        Assert.Null(info!.CurrentVersion);
    }

    [Fact]
    public async Task A_server_can_require_a_minimum_client_version()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = "Strict Server",
            address = "1.2.3.4",
            port = 6073,
            playerCount = 1,
            maxPlayers = 8,
            missionName = "m",
            coreFile = "PCore001.igc",
            version = "0.1.0",
            minClientVersion = "2.0.0",
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, post.StatusCode);
        var resp = await post.Content.ReadFromJsonAsync<RegisterResponse>();

        var servers = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
        var mine = Assert.Single(servers!, s => s.Id == resp!.Id);
        Assert.Equal("2.0.0", mine.MinClientVersion);
    }

    [Fact]
    public async Task A_server_without_a_requirement_carries_null()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = "Chill Server",
            address = "1.2.3.4",
            port = 6074,
            playerCount = 0,
            maxPlayers = 8,
            missionName = "m",
            coreFile = "PCore001.igc",
            version = "0.1.0",
        });
        var resp = await post.Content.ReadFromJsonAsync<RegisterResponse>();

        var servers = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
        var mine = Assert.Single(servers!, s => s.Id == resp!.Id);
        Assert.Null(mine.MinClientVersion);
    }
}

using System.Net;
using System.Net.Http.Json;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P761/P761b — the directory's find-player data. A server may report its
/// connected pilots' names (<c>RegisterRequest.Players</c>), but only when it
/// opts in (<c>publishPlayers</c>): the names are <b>never</b> part of the public
/// <c>/api/servers</c> listing (they stay server-side), and they answer exactly
/// one thing — the per-name find-player query
/// (<c>GET /api/servers/findplayer?name=</c>), which is the port of native's
/// FIND_PLAYER round trip (<c>WinTrek/gamescreen.cpp:1348-1352</c> →
/// <c>FM_C_FIND_PLAYER</c>): the lobby is asked about ONE pilot and answers
/// which game that pilot is in.
/// </summary>
public class P761PlayerListTests
{
    private static object Payload(string? token = null, string[]? players = null,
        bool publishPlayers = true, string name = "Test Server")
        => new
        {
            name,
            address = "1.2.3.4",
            port = 6073,
            playerCount = players?.Length ?? 0,
            maxPlayers = 64,
            missionName = "Wing War",
            coreFile = "PCore001.igc",
            version = "0.1.0",
            players,
            publishPlayers,
            token,
        };

    [Fact]
    public async Task PublicListing_CarriesNoPilotNames_EvenWhenTheServerReportsThem()
    {
        // P761b privacy fix: native's directory asked about ONE pilot, so the
        // open listing must not publish every server's roster.
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register",
            Payload(players: ["PilotOne", "PilotTwo"]));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        string raw = await client.GetStringAsync("/api/servers");
        Assert.DoesNotContain("PilotOne", raw);
        Assert.DoesNotContain("PilotTwo", raw);
        Assert.DoesNotContain("players\"", raw);      // the field itself is gone

        var servers = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
        Assert.Equal(2, Assert.Single(servers!).PlayerCount);

        // The single-listing read is the same projection: no names either.
        var one = await client.GetStringAsync($"/api/servers/{servers![0].Id}");
        Assert.DoesNotContain("PilotOne", one);
    }

    [Fact]
    public async Task FindPlayer_AnswersWithOnlyTheServerTheNamedPilotIsOn()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        await client.PostAsJsonAsync("/api/servers/register",
            Payload(players: ["PilotOne", "PilotTwo"], name: "Server A"));
        await client.PostAsJsonAsync("/api/servers/register",
            Payload(players: ["PilotThree"], name: "Server B"));

        // Exact, case-insensitive match (native's find is by character name).
        var hit = await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
            "/api/servers/findplayer?name=pilottwo");
        var mine = Assert.Single(hit!);
        Assert.Equal("Server A", mine.Name);
        Assert.Equal("Wing War", mine.MissionName);

        // A partial name is NOT a hit: FIND_PLAYER is an exact-character match.
        var partial = await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
            "/api/servers/findplayer?name=Pilot");
        Assert.Empty(partial!);

        // An unknown pilot, and a missing/blank name.
        Assert.Empty((await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
            "/api/servers/findplayer?name=Nobody"))!);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/servers/findplayer?name=")).StatusCode);
    }

    [Fact]
    public async Task FindPlayer_NeverMatches_AServerThatDidNotOptIn()
    {
        // Default off: the lobby cannot authenticate a self-hosted server, so a
        // server that does not ask to publish keeps its roster private even
        // though it reported the names.
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        await client.PostAsJsonAsync("/api/servers/register",
            Payload(players: ["PrivatePilot"], publishPlayers: false));

        var hit = await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
            "/api/servers/findplayer?name=PrivatePilot");
        Assert.Empty(hit!);
    }

    [Fact]
    public async Task Heartbeat_refreshes_the_player_list_in_place()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var first = await client.PostAsJsonAsync("/api/servers/register",
            Payload(players: ["PilotOne"]));
        var resp = await first.Content.ReadFromJsonAsync<RegisterResponse>();

        var second = await client.PostAsJsonAsync("/api/servers/register",
            Payload(token: resp!.Token, players: ["PilotOne", "PilotThree"]));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var hit = await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
            "/api/servers/findplayer?name=PilotThree");
        Assert.Single(hit!);
        // ... and the one who left no longer matches.
        Assert.Empty((await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
            "/api/servers/findplayer?name=PilotTwo"))!);
    }

    [Fact]
    public async Task Player_names_are_sanitized_and_capped()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        string longName = new('x', 400);
        var post = await client.PostAsJsonAsync("/api/servers/register", Payload(players:
        [
            "ok", "", "  ", longName, "ok", "bad\u0001ctrl",
        ]));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        // Blank dropped, duplicates collapsed, control chars stripped, capped
        // length (32): the sanitized names are what the query answers.
        foreach (var name in new[] { "ok", "badctrl", new string('x', 32) })
            Assert.Single((await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
                "/api/servers/findplayer?name=" + Uri.EscapeDataString(name)))!);
        Assert.Empty((await client.GetFromJsonAsync<List<FindPlayerResultDto>>(
            "/api/servers/findplayer?name=" + Uri.EscapeDataString(new string('x', 33))))!);
    }
}

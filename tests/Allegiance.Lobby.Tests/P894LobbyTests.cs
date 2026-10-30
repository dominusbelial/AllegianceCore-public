using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P894 — the lobby-directory half of the version gate and the server allow-list:
/// <list type="bullet">
/// <item><b>LOB-0004</b> — a registering server whose lobby protocol version differs
/// from the lobby's is refused with native's <c>LOGON_SERVER_NACK</c> reason
/// (<c>lobby/server.cpp:93,141-151</c>); a pre-P894 host that omits the field still
/// registers.</item>
/// <item><b>LOB-0005</b> — native's approved-server list
/// (<c>IsServerAllowed</c>, <c>lobby/server.cpp:24-84</c>; loopback always allowed at
/// <c>:131</c>, refusal reason at <c>:138</c>). Empty list = allow all (the default,
/// so community servers keep listing).</item>
/// </list>
/// </summary>
public class P894VersionAndAllowListTests
{
    private static object Payload(int? lobbyProtocolVersion = null, string core = "PCore001.igc") => new
    {
        name = "P894 Server",
        address = "1.2.3.4",
        port = 6073,
        playerCount = 3,
        maxPlayers = 32,
        missionName = "Wing War",
        coreFile = core,
        version = "0.1.0",
        lobbyProtocolVersion,
    };

    [Fact]
    public async Task A_server_without_a_stated_protocol_version_still_registers()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload());

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var servers = await ListServers(client);
        Assert.Single(servers);
    }

    [Fact]
    public async Task A_matching_protocol_version_registers()
    {
        using var app = new LobbyTestApp(configure: s => s.LobbyProtocolVersion = 7);
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload(lobbyProtocolVersion: 7));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task An_older_server_is_refused_with_native_update_reason()
    {
        using var app = new LobbyTestApp(configure: s => s.LobbyProtocolVersion = 4);
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload(lobbyProtocolVersion: 3));

        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        // Native's text for verLobby < LOBBYVER_LS (lobby/server.cpp:143-144).
        Assert.Contains("did not get auto-updated properly", body);
        Assert.Contains("server protocol 3", body);
        Assert.Contains("lobby protocol 4", body);
        Assert.Empty(await ListServers(client));
    }

    [Fact]
    public async Task A_newer_server_is_refused_with_the_lobby_is_older_reason()
    {
        using var app = new LobbyTestApp(configure: s => s.LobbyProtocolVersion = 4);
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload(lobbyProtocolVersion: 5));

        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
        // Native's text for verLobby > LOBBYVER_LS (lobby/server.cpp:141-142).
        Assert.Contains("older than your version", await post.Content.ReadAsStringAsync());
        Assert.Empty(await ListServers(client));
    }

    [Fact]
    public async Task An_unconfigured_allow_list_accepts_every_source()
    {
        using var app = new LobbyTestApp(sourceIp: "198.51.100.7");
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload());

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task An_allow_list_refuses_an_unapproved_source_with_the_native_message()
    {
        using var app = new LobbyTestApp(configure: s => s.AllowedServers = ["10.0.0.42"], sourceIp: "198.51.100.7");
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload());

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(ServerAllowList.RejectionReason, await post.Content.ReadAsStringAsync());
        Assert.Empty(await ListServers(client));
    }

    [Fact]
    public async Task An_allow_list_admits_a_listed_address()
    {
        using var app = new LobbyTestApp(configure: s => s.AllowedServers = ["10.0.0.42"], sourceIp: "10.0.0.42");
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload());

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var servers = await ListServers(client);
        Assert.Equal("10.0.0.42", Assert.Single(servers).Address);
    }

    [Fact]
    public async Task Loopback_is_always_allowed_even_when_the_allow_list_excludes_it()
    {
        using var app = new LobbyTestApp(configure: s => s.AllowedServers = ["10.0.0.42"]);
        using var client = app.CreateClient();   // LobbyTestApp's default source is loopback

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload());

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task Block_mode_refuses_a_listed_source_and_admits_the_rest()
    {
        using var blocked = new LobbyTestApp(configure: s =>
        {
            s.AllowedServers = ["10.0.0.42"];
            s.ServerFilterMode = "Block";
        }, sourceIp: "10.0.0.42");
        using var blockedClient = blocked.CreateClient();
        var refused = await blockedClient.PostAsJsonAsync("/api/servers/register", Payload());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var open = new LobbyTestApp(configure: s =>
        {
            s.AllowedServers = ["10.0.0.42"];
            s.ServerFilterMode = "Block";
        }, sourceIp: "198.51.100.7");
        using var openClient = open.CreateClient();
        var admitted = await openClient.PostAsJsonAsync("/api/servers/register", Payload());
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
    }

    [Theory]
    [InlineData(null, null, true)]                    // no list configured: allow all
    [InlineData("10.0.0.42", "10.0.0.42", true)]      // exact entry
    [InlineData("10.0.0.42", "10.0.0.42:6073", true)] // native prefix rule: the ENTRY starts with the IP
    [InlineData("10.0.0.42", "10.0.0.", false)]       // a shorter netblock prefix: native never matched it either
    [InlineData("10.0.0.42", "", true)]               // a blank entry is treated as "not configured" (allow all), never as "refuse everyone"
    public void The_allow_list_rule_matches_native(string? source, string? entry, bool allowed)
    {
        var entries = entry is null ? [] : new List<string> { entry };
        Assert.Equal(allowed, ServerAllowList.IsAllowed(source, entries, "allow"));
    }

    [Fact]
    public void Loopback_detection_matches_native()
    {
        Assert.True(ServerAllowList.IsLoopback("127.0.0.1"));
        Assert.True(ServerAllowList.IsLoopback("::1"));
        Assert.False(ServerAllowList.IsLoopback("10.0.0.1"));
        Assert.False(ServerAllowList.IsLoopback(null));
    }

    private static async Task<List<ServerListingDto>> ListServers(HttpClient client)
        => await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers") ?? [];
}

/// <summary>
/// P894 — the listing-content rows: LOB-0007 (paused hidden), LOB-0008 (zero-player
/// games hidden unless the mission is a server-owned arena), LOB-0018 (location),
/// LOB-0029 (game counts + percent load), LOB-0017 (start time normalized against the
/// lobby's clock), LOB-0009 (master core list) and LOB-0027 (counters).
/// </summary>
public class P894ListingFieldsTests
{
    private static object Payload(string name, int players = 0, string core = "PCore001.igc",
        string? location = null, int currentGames = 0, int maxGames = 20, bool paused = false,
        bool? objectModelCreated = null, List<object>? missions = null, uint startOffsetMs = 0) => new
    {
        name,
        address = "1.2.3.4",
        port = 6073,
        playerCount = players,
        maxPlayers = 32,
        missionName = name,
        coreFile = core,
        version = "0.1.0",
        location,
        paused,
        currentGames,
        maxGames,
        missions,
        game = objectModelCreated is null ? null : new
        {
            objectModelCreated,
            inProgress = false,
            startTimeOffsetMs = startOffsetMs,
        },
    };

    [Fact]
    public async Task A_paused_server_is_not_advertised()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var resp = await Register(client, Payload("Paused", players: 4, paused: true));

        Assert.Empty(await ListServers(client));
        var stats = await Stats(client);
        Assert.Equal(1, stats.HiddenPausedServers);
        Assert.Equal(0, stats.Servers);
        Assert.Equal(0, stats.Players);
        // The by-id detail still answers (the listing exists; it is just hidden),
        // exactly like a paused native server that stops being broadcast.
        var detail = await client.GetAsync($"/api/servers/{resp.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task A_zero_player_persistent_arena_server_stays_listed()
    {
        // The port's servers are persistent, server-owned (arena) missions: native
        // advertises those at zero players (`m_plmi->fMSArena`,
        // lobby/mission.cpp:77-86). `objectModelCreated: true` is the arena bit
        // (FedSrv/fsmission.cpp:4087).
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        await Register(client, Payload("Persistent", players: 0, objectModelCreated: true));

        var mine = Assert.Single(await ListServers(client));
        Assert.Equal("Persistent", mine.Name);
        Assert.Equal(0, mine.PlayerCount);
    }

    [Fact]
    public async Task A_zero_player_client_created_mission_is_hidden_until_someone_joins()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var resp = await Register(client, Payload("Created game", players: 0,
            objectModelCreated: false,
            missions: [new { cookie = 7u, name = "Created game", playerCount = 0, game = new { objectModelCreated = false } }]));

        // The server row survives (native's SERVERS_LIST is per server), but the
        // zero-player non-arena mission is not advertised (lobby/mission.cpp:77-86).
        var listed = Assert.Single(await ListServers(client));
        Assert.NotNull(listed.Missions);
        Assert.Empty(listed.Missions!);
        var stats = await Stats(client);
        Assert.Equal(1, stats.HiddenEmptyMissions);

        // A heartbeat with a player in it advertises the mission again.
        await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = "Created game",
            address = "1.2.3.4",
            port = 6073,
            playerCount = 1,
            missionName = "Created game",
            coreFile = "PCore001.igc",
            token = resp.Token,
            missions = new[] { new { cookie = 7u, name = "Created game", playerCount = 1, game = new { objectModelCreated = false } } },
        });
        listed = Assert.Single(await ListServers(client));
        Assert.Single(listed.Missions!);
    }

    [Fact]
    public async Task Location_is_recorded_and_exposed()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        await Register(client, Payload("Located", location: "EU North"));

        Assert.Equal("EU North", Assert.Single(await ListServers(client)).Location);
    }

    [Fact]
    public async Task Game_counts_and_percent_load_follow_native_c_GameFactor()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        await Register(client, Payload("Loaded", players: 10, currentGames: 2, maxGames: 20));

        var mine = Assert.Single(await ListServers(client));
        Assert.Equal(2, mine.CurrentGames);
        Assert.Equal(20, mine.MaxGames);
        // Native (games*c_GameFactor + players)/m_maxLoad = (2*5 + 10)/300 = 0.
        Assert.Equal(0, mine.PercentLoad);
    }

    [Fact]
    public async Task Percent_load_is_the_native_formula()
    {
        using var app = new LobbyTestApp(configure: s => s.MaxLoad = 10);
        using var client = app.CreateClient();

        await Register(client, Payload("Loaded", players: 3, currentGames: 1, maxGames: 20));

        // (1*5 + 3)/10 = 0 with integer division; double the games to cross 1.
        Assert.Equal(0, Assert.Single(await ListServers(client)).PercentLoad);

        using var app2 = new LobbyTestApp(configure: s => s.MaxLoad = 10);
        using var client2 = app2.CreateClient();
        await Register(client2, Payload("Loaded2", players: 9, currentGames: 2, maxGames: 20));
        // (2*5 + 9)/10 = 1.
        Assert.Equal(1, Assert.Single(await ListServers(client2)).PercentLoad);
    }

    [Fact]
    public async Task Start_time_is_normalized_against_the_directory_clock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2031, 5, 4, 12, 0, 0, TimeSpan.Zero));
        using var app = new LobbyTestApp(time);
        using var client = app.CreateClient();

        await Register(client, Payload("Timed", players: 2, objectModelCreated: true, startOffsetMs: 60_000));

        var mine = Assert.Single(await ListServers(client));
        Assert.Equal(time.GetUtcNow().AddMinutes(1), mine.StartTimeUtc);
    }

    [Fact]
    public async Task No_armed_start_time_is_advertised_as_null()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        await Register(client, Payload("Untimed", players: 1, objectModelCreated: true, startOffsetMs: 0));

        Assert.Null(Assert.Single(await ListServers(client)).StartTimeUtc);
    }

    [Fact]
    public async Task The_core_list_relays_every_live_core_plus_the_curated_catalog()
    {
        using var app = new LobbyTestApp(configure: s => s.GameCores = ["PCore001.igc", "PCore002.igc"]);
        using var client = app.CreateClient();
        await Register(client, Payload("A", core: "PCore001.igc"));
        await Register(client, Payload("B", core: "PCore003.igc"));

        var cores = await client.GetFromJsonAsync<LobbyCoreListDto>("/api/servers/cores");

        Assert.NotNull(cores);
        Assert.Equal(2, cores!.Servers);
        var byName = cores.Cores.ToDictionary(c => c.File, c => c.Servers);
        Assert.Equal(1, byName["PCore001.igc"]);
        Assert.Equal(1, byName["PCore003.igc"]);
        Assert.Equal(0, byName["PCore002.igc"]);   // curated, no server yet
    }

    [Fact]
    public async Task Stats_report_servers_missions_players_and_load()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        await Register(client, Payload("One", players: 4, currentGames: 1));
        await Register(client, Payload("Two", players: 6, currentGames: 2));

        var stats = await Stats(client);

        Assert.Equal(2, stats.Servers);
        Assert.Equal(3, stats.Missions);       // no Missions arrays: CurrentGames (1 + 2)
        Assert.Equal(10, stats.Players);
    }

    private static async Task<RegisterResponse> Register(HttpClient client, object payload)
    {
        var post = await client.PostAsJsonAsync("/api/servers/register", payload);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        return (await post.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }

    private static async Task<List<ServerListingDto>> ListServers(HttpClient client)
        => await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers") ?? [];

    private static async Task<LobbyStatsDto> Stats(HttpClient client)
        => (await client.GetFromJsonAsync<LobbyStatsDto>("/api/servers/stats"))!;
}

using System.Net;
using System.Net.Http.Json;
using Allegiance.ClientCore.MonoGame;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P806 — the SERVER-LIST FILTER LOOPBACK: a real in-process lobby
/// (<see cref="LobbyTestApp"/>), three servers registering MIXED games (the
/// native <c>MissionParams</c> the directory now publishes), the real
/// <c>GET /api/servers</c> round trip and then the REAL client models that back
/// the browser (<see cref="ServerListing"/>, <see cref="LobbyGameFilters"/>,
/// <see cref="LobbyGameSort"/>) evaluating exactly what a player would see.
///
/// <para>The three games are the three cases the native list distinguishes with
/// its filter/status/settings columns: a running CONQUEST game that is full, a
/// waiting TERRITORIAL game with a rank window, and a waiting game with a
/// finite life budget and flags. On master the directory DTO carried none of
/// this, so the eleven filters excluded nothing and the status/skill/settings
/// columns were not selectable.</para>
/// </summary>
public sealed class P806ServerListLoopbackTests
{
    private static object Registration(string name, string mission, int players, int maxPlayers,
        object game) => new
        {
            name,
            address = "10.0.0.1",
            port = 6777,
            playerCount = players,
            maxPlayers,
            missionName = mission,
            coreFile = "PCore001.igc",
            version = "0.1.0",
            game,
        };

    /// <summary>Three mixed games registered through the real register endpoint.</summary>
    private static async Task<HttpClient> RegisterThree(HttpClient client)
    {
        // 1) running, full, conquest, developments on, unlimited lives.
        var runningConquest = Registration("alpha", "Conquest Arena", players: 8, maxPlayers: 8, new
        {
            allowDevelopments = true,
            lives = 0x7fff,
            scoresCount = true,
            objectModelCreated = true,
            inProgress = true,
            goalConquest = true,
            goalDeathmatch = false,
            goalProsperity = false,
            goalCountdown = false,
            goalArtifacts = false,
            goalFlags = false,
            goalTerritory = false,
            minRank = -1,
            maxRank = 1000,
        });
        // 2) waiting, territorial, developments on (the native Territorial preset
        // requires them, gametypes.cpp:157-160), rank window 3..9.
        var waitingTerritorial = Registration("bravo", "Territory War", players: 2, maxPlayers: 20, new
        {
            allowDevelopments = true,
            lives = 0x7fff,
            scoresCount = true,
            objectModelCreated = true,
            inProgress = false,
            goalConquest = false,
            goalDeathmatch = false,
            goalProsperity = false,
            goalCountdown = false,
            goalArtifacts = false,
            goalFlags = false,
            goalTerritory = true,
            minRank = 3,
            maxRank = 9,
        });
        // 3) waiting, flags + finite lives (limited lives), countdown (game length),
        // developments off (the native Capture-the-Flag preset requires that).
        var waitingFlags = Registration("charlie", "Flag Run", players: 0, maxPlayers: 10, new
        {
            allowDevelopments = false,
            lives = 3,
            scoresCount = true,
            objectModelCreated = true,
            inProgress = false,
            goalConquest = false,
            goalDeathmatch = false,
            goalProsperity = false,
            goalCountdown = true,
            goalArtifacts = false,
            goalFlags = true,
            goalTerritory = false,
            minRank = -1,
            maxRank = 1000,
        });

        foreach (var payload in new[] { runningConquest, waitingTerritorial, waitingFlags })
        {
            var resp = await client.PostAsJsonAsync("/api/servers/register", payload);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        return client;
    }

    private static async Task<List<ServerListing>> Fetch(HttpClient client)
    {
        var listings = await client.GetFromJsonAsync<List<ServerListing>>(
            "/api/servers", ServerListing.JsonOptions);
        Assert.NotNull(listings);
        return listings!;
    }

    [Fact]
    public async Task Registered_game_settings_reach_the_client_listing()
    {
        using var app = new LobbyTestApp();
        using var client = await RegisterThree(app.CreateClient());

        var listings = await Fetch(client);
        Assert.Equal(3, listings.Count);

        var conquest = listings.Single(l => l.Name == "alpha");
        Assert.NotNull(conquest.Game);
        Assert.True(conquest.Game!.InProgress);
        Assert.True(conquest.Game.GoalConquest);
        Assert.Equal(GameParams.UnlimitedLives, conquest.Game.Lives);
        Assert.False(conquest.Game.LimitedLives);

        var territorial = listings.Single(l => l.Name == "bravo");
        Assert.True(territorial.Game!.AllowDevelopments);
        Assert.True(territorial.Game.GoalTerritory);
        Assert.Equal(3, territorial.Game.MinRank);
        Assert.Equal(9, territorial.Game.MaxRank);

        var flags = listings.Single(l => l.Name == "charlie");
        Assert.True(flags.Game!.GoalFlags);
        Assert.True(flags.Game.GoalCountdown);
        Assert.True(flags.Game.LimitedLives);
        Assert.Equal(3, flags.Game.Lives);
    }

    [Fact]
    public async Task The_eleven_native_filters_select_the_matching_mixed_games()
    {
        using var app = new LobbyTestApp();
        using var client = await RegisterThree(app.CreateClient());
        var listings = await Fetch(client);

        var filters = new LobbyGameFilters();
        // Developments = No → only the flags/countdown game.
        filters.Developments = LobbyGameSort.BooleanFilter.No;
        var shown = listings.Where(filters.Apply).Select(l => l.Name).ToArray();
        Assert.Equal(new[] { "charlie" }, shown);
        filters.Reset();

        // Lives = Yes (LimitedLives) → only the finite-lives game.
        filters.Lives = LobbyGameSort.BooleanFilter.Yes;
        Assert.Equal(new[] { "charlie" }, listings.Where(filters.Apply).Select(l => l.Name).ToArray());
        filters.Reset();

        // Territorial = Yes → only the territorial game.
        filters.Territorial = LobbyGameSort.BooleanFilter.Yes;
        Assert.Equal(new[] { "bravo" }, listings.Where(filters.Apply).Select(l => l.Name).ToArray());
        filters.Reset();

        // Flags + Countdown = Yes → only the flag-run game (both bits together).
        filters.Flags = LobbyGameSort.BooleanFilter.Yes;
        filters.Countdown = LobbyGameSort.BooleanFilter.Yes;
        Assert.Equal(new[] { "charlie" }, listings.Where(filters.Apply).Select(l => l.Name).ToArray());
        filters.Reset();

        // Conquest = Yes → the running game.
        filters.Conquest = LobbyGameSort.BooleanFilter.Yes;
        Assert.Equal(new[] { "alpha" }, listings.Where(filters.Apply).Select(l => l.Name).ToArray());
        filters.Reset();

        // DontCare everywhere → all three.
        Assert.Equal(3, listings.Count(filters.Apply));
    }

    [Fact]
    public async Task Status_skill_and_settings_sort_columns_order_the_mixed_games()
    {
        using var app = new LobbyTestApp();
        using var client = await RegisterThree(app.CreateClient());
        var listings = await Fetch(client);

        // STATUS (col 0): MissionStatusColor dominates — the open (green) games
        // sort before the full (red) alpha, even though alpha is the one running
        // (StatusCompare's InProgress tie-break only applies inside a colour).
        var status = new LobbyGameSort();
        status.Click(LobbyGameSort.ColStatus);
        var byStatus = status.Apply(listings, (l, c) => LobbyGameSort.ListingKey(l, c))
            .Select(l => l.Name).ToArray();
        Assert.Equal("alpha", byStatus[^1]);
        Assert.Equal(new[] { "bravo", "charlie" }, byStatus[..^1].OrderBy(n => n).ToArray());

        // SKILL (col 3): the rank window 3..9 has the LOWEST max rank, so it sorts
        // first; the two unlimited-window games tie (stable order).
        var skill = new LobbyGameSort();
        skill.Click(LobbyGameSort.ColSkill);
        var bySkill = skill.Apply(listings, (l, c) => LobbyGameSort.ListingKey(l, c))
            .Select(l => l.Name).ToArray();
        Assert.Equal("bravo", bySkill[0]);
        Assert.Equal(new[] { "alpha", "charlie" }, bySkill[1..].OrderBy(n => n).ToArray());

        // SETTINGS (col 7): game-type index DESCENDING (native SettingsCompare
        // returns "a after b" for index(a) < index(b)). charlie matches no preset
        // (list-end index 6), bravo is Territorial (4), alpha is Conquest (0).
        var settings = new LobbyGameSort();
        settings.Click(LobbyGameSort.ColSettings);
        var bySettings = settings.Apply(listings, (l, c) => LobbyGameSort.ListingKey(l, c))
            .Select(l => l.Name).ToArray();
        Assert.Equal(0, listings.Single(l => l.Name == "alpha").Game!.GameTypeIndex);
        Assert.Equal(4, listings.Single(l => l.Name == "bravo").Game!.GameTypeIndex);
        Assert.Equal("charlie", bySettings[0]);   // custom (list-end index) first
        Assert.Equal("bravo", bySettings[1]);     // territorial (4)
        Assert.Equal("alpha", bySettings[2]);     // conquest (0)
    }

    [Fact]
    public async Task A_host_that_publishes_no_settings_still_registers_and_lists()
    {
        // Backward compatibility: an old heartbeat payload (no game object) is
        // accepted, lists normally, and the client treats its metadata as unknown.
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = "legacy",
            address = "10.0.0.9",
            port = 6778,
            playerCount = 1,
            maxPlayers = 8,
            missionName = "Old Game",
            coreFile = "PCore001.igc",
            version = "0.0.9",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var listing = Assert.Single(await Fetch(client));
        Assert.Null(listing.Game);
        var filters = new LobbyGameFilters { Conquest = LobbyGameSort.BooleanFilter.Yes };
        Assert.False(filters.Apply(listing));       // cannot prove it, so it is hidden
        filters.Reset();
        Assert.True(filters.Apply(listing));
    }

    [Fact]
    public async Task Heartbeat_refreshes_the_published_settings_in_place()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        var first = await client.PostAsJsonAsync("/api/servers/register", Registration(
            "alpha", "Conquest Arena", 0, 8, new
            {
                allowDevelopments = true,
                lives = 0x7fff,
                scoresCount = true,
                objectModelCreated = true,
                inProgress = false,
                goalConquest = true,
                goalDeathmatch = false,
                goalProsperity = false,
                goalCountdown = false,
                goalArtifacts = false,
                goalFlags = false,
                goalTerritory = false,
                minRank = -1,
                maxRank = 1000,
            }));
        var id = (await first.Content.ReadFromJsonAsync<RegisterResponse>())!;

        // The mission started: the same listing (token) republishes InProgress.
        var second = await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = "alpha",
            address = "10.0.0.1",
            port = 6777,
            playerCount = 5,
            maxPlayers = 8,
            missionName = "Conquest Arena",
            coreFile = "PCore001.igc",
            version = "0.1.0",
            token = id.Token,
            game = new
            {
                allowDevelopments = true,
                lives = 0x7fff,
                scoresCount = true,
                objectModelCreated = true,
                inProgress = true,
                goalConquest = true,
                goalDeathmatch = false,
                goalProsperity = false,
                goalCountdown = false,
                goalArtifacts = false,
                goalFlags = false,
                goalTerritory = false,
                minRank = -1,
                maxRank = 1000,
            },
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var listing = Assert.Single(await Fetch(client));
        Assert.Equal(id.Id, listing.Id);
        Assert.True(listing.Game!.InProgress);
        Assert.Equal(5, listing.PlayerCount);
    }
}

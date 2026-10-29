using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Allegiance.Core.Identity;
using Allegiance.Core.Wire;
using Allegiance.Lobby.Games;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P871 — the lobby-directory chat relay and the new-game wizard, tested over
/// the REAL in-process lobby: authenticated posting, relay feedback, native
/// mute/filter semantics, the authoritative server/core/map catalog, the
/// create request (native <c>CREATE_MISSION_REQ</c>), owner-only parameter
/// editing and the game server's claim.
/// </summary>
public sealed class P871ChatAndGamesTests
{
    private sealed record Identity(HttpClient Http, IdentityAccount Account);

    private static Identity SignIn(LobbyTestApp app, string callsign)
    {
        var store = app.Services.GetRequiredService<IdentityStore>();
        var account = store.CreateAccount(callsign);
        string session = store.Session(account.Id);
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session);
        return new Identity(http, account);
    }

    private static HttpClient Anonymous(LobbyTestApp app) => app.CreateClient();

    /// <summary>P871b — the mission parameters ride the native wire struct as a
    /// base64 blob (<c>paramsWire</c>), so ordinary property JSON is enough; the
    /// lobby's serializer stays on the default contract.</summary>
    private static System.Net.Http.Json.JsonContent Body(object body) =>
        System.Net.Http.Json.JsonContent.Create(body);

    /// <summary>The base64 native <c>MissionParams</c> blob the endpoints take.</summary>
    private static string Wire(MissionParamsMsg parameters) =>
        Convert.ToBase64String(parameters.Encode());

    // ── directory chat ─────────────────────────────────────────────────────

    [Fact]
    public async Task Directory_chat_requires_an_authenticated_sender()
    {
        using var app = new LobbyTestApp();
        using var anon = Anonymous(app);
        using var refused = await anon.PostAsJsonAsync("/api/chat", new { channel = "directory", text = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Directory_chat_relays_to_readers_and_does_not_echo_the_sender()
    {
        using var app = new LobbyTestApp();
        using var alice = SignIn(app, "ChatAlice").Http;
        using var bob = SignIn(app, "ChatBob").Http;

        using var posted = await alice.PostAsJsonAsync("/api/chat", new { channel = "directory", text = "hello lobby" });
        posted.EnsureSuccessStatusCode();
        var ack = (await posted.Content.ReadFromJsonAsync<Ack>())!;
        Assert.Equal(1, ack.Sequence);
        Assert.Equal("ChatAlice", ack.Line.Sender);

        // Bob reads the live relay, and adds his own line.
        var bobSees = (await bob.GetFromJsonAsync<Line[]>("/api/chat?channel=directory&since=0"))!;
        Assert.Equal("hello lobby", Assert.Single(bobSees).Text);
        (await bob.PostAsJsonAsync("/api/chat", new { channel = "directory", text = "bob says" })).EnsureSuccessStatusCode();
        // Alice's live read does not echo her own line back (native's sidSender check).
        var aliceLive = (await alice.GetFromJsonAsync<Line[]>($"/api/chat?channel=directory&since={ack.Sequence}"))!;
        Assert.Equal("bob says", Assert.Single(aliceLive).Text);
        // But a history rebuild from 0 still contains it.
        var aliceHistory = (await alice.GetFromJsonAsync<Line[]>("/api/chat?channel=directory&since=0"))!;
        Assert.Equal(2, aliceHistory.Length);
        Assert.Contains(aliceHistory, l => l.Text == "hello lobby");

        // Invalid channel / empty text are refused.
        using var badChannel = await alice.PostAsJsonAsync("/api/chat", new { channel = "elsewhere", text = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);
        using var empty = await alice.PostAsJsonAsync("/api/chat", new { channel = "directory", text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Directory_chat_mute_suppresses_a_sender_for_that_reader_only()
    {
        using var app = new LobbyTestApp();
        var alice = SignIn(app, "MuteAlice");
        var bob = SignIn(app, "MuteBob");
        using var carol = SignIn(app, "MuteCarol").Http;

        using var muted = await bob.Http.PostAsJsonAsync("/api/chat/mute", new { accountId = alice.Account.Id });
        muted.EnsureSuccessStatusCode();
        (await alice.Http.PostAsJsonAsync("/api/chat", new { channel = "directory", text = "alice line" })).EnsureSuccessStatusCode();
        (await carol.PostAsJsonAsync("/api/chat", new { channel = "directory", text = "carol line" })).EnsureSuccessStatusCode();

        var bobSees = (await bob.Http.GetFromJsonAsync<Line[]>("/api/chat?channel=directory&since=0"))!;
        Assert.Equal("carol line", Assert.Single(bobSees).Text);   // muted Alice absent
        var carolSees = (await carol.GetFromJsonAsync<Line[]>("/api/chat?channel=directory&since=0"))!;
        Assert.Equal(2, carolSees.Length);                          // everybody else still sees both

        using var unmuted = await bob.Http.PostAsJsonAsync("/api/chat/unmute", new { accountId = alice.Account.Id });
        unmuted.EnsureSuccessStatusCode();
        var bobAgain = (await bob.Http.GetFromJsonAsync<Line[]>("/api/chat?channel=directory&since=0"))!;
        Assert.Equal(2, bobAgain.Length);
        // Self-mute is refused (native mute is about other players).
        using var self = await bob.Http.PostAsJsonAsync("/api/chat/mute", new { accountId = bob.Account.Id });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
    }

    // ── create game wizard ─────────────────────────────────────────────────

    private static async Task<(string ServerId, string Token)> RegisterServer(LobbyTestApp app, HttpClient http)
    {
        var registry = app.Services.GetRequiredService<ServerRegistry>();
        var response = registry.Register(new RegisterRequest
        {
            Name = "WizardHost", Address = "127.0.0.1", Port = 6073, PlayerCount = 0, MaxPlayers = 0,
            MissionName = "Wizard Mission", CoreFile = "PCore001.igc", Version = "1.0.0",
        });
        Assert.NotNull(response);
        await Task.CompletedTask;
        return (response!.Id, response.Token);
    }

    private static MissionParamsMsg ValidParams() => new()
    {
        StrGameName = "Wizard Game",
        SzIgcStaticFile = "PCore001.igc",
        NTems = 2,
        NTotalMaxPlayersPerGame = 32,
        NMaxPlayersPerTeam = 16,
        IMinRank = -1,
        IMaxRank = 1000,
        ILives = MissionDef.UnlimitedLives,
        // Native defaults: conquest on, territory OFF (100 = disabled).
        IGoalConquestPercentage = 100,
        IGoalTerritoryPercentage = 100,
        Bits = { [MissionParamsMsg.BitObjectModelCreated] = true, [MissionParamsMsg.BitScoresCount] = true },
    };

    [Fact]
    public async Task The_catalog_lists_registered_servers_with_their_cores_and_the_configured_maps()
    {
        using var app = new LobbyTestApp(configure: s =>
        {
            s.GameCores = ["PCore001.igc"];
            s.GameMaps = ["XXV_simple.igc", "Brawl.igc"];
        });
        using var http = Anonymous(app);
        await RegisterServer(app, http);
        var catalog = (await http.GetFromJsonAsync<Catalog>("/api/games/catalog"))!;
        var server = Assert.Single(catalog.Servers);
        Assert.Equal("WizardHost", server.Name);
        Assert.Equal("PCore001.igc", server.Core);
        Assert.Equal(["PCore001.igc"], catalog.Cores);
        Assert.Equal(["XXV_simple.igc", "Brawl.igc"], catalog.Maps);
    }

    [Fact]
    public async Task Creating_a_game_requires_identity_and_a_matching_core_then_allows_owner_edits()
    {
        using var app = new LobbyTestApp(configure: s =>
        {
            s.GameCores = ["PCore001.igc"];
            s.GameMaps = ["XXV_simple.igc"];
        });
        using var anon = Anonymous(app);
        var (serverId, token) = await RegisterServer(app, anon);

        // Anonymous create is refused (native's creator is a lobby-session client).
        using var anonymous = await anon.PostAsync("/api/games", Body(
            new { serverId, core = "PCore001.igc", map = "XXV_simple.igc", gameName = "G", paramsWire = Wire(ValidParams()) }));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var owner = SignIn(app, "GameOwner");
        using var badCore = await owner.Http.PostAsync("/api/games", Body(
            new { serverId, core = "PCore999.igc", map = "XXV_simple.igc", gameName = "G", paramsWire = Wire(ValidParams()) }));
        Assert.Equal(HttpStatusCode.BadRequest, badCore.StatusCode);
        using var badMap = await owner.Http.PostAsync("/api/games", Body(
            new { serverId, core = "PCore001.igc", map = "NoSuchMap.igc", gameName = "G", paramsWire = Wire(ValidParams()) }));
        Assert.Equal(HttpStatusCode.BadRequest, badMap.StatusCode);

        using var created = await owner.Http.PostAsync("/api/games", Body(
            new { serverId, core = "PCore001.igc", map = "XXV_simple.igc", gameName = "Owner Game", paramsWire = Wire(ValidParams()) }));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var game = (await created.Content.ReadFromJsonAsync<GameDto>())!;
        Assert.Equal("Queued", game.State);
        Assert.Equal("Owner Game", game.GameName);
        Assert.Equal("GameOwner", game.Owner);

        // The relay feedback lists it for everyone.
        var listed = (await anon.GetFromJsonAsync<GameDto[]>("/api/games"))!;
        Assert.Equal(game.Id, Assert.Single(listed).Id);

        // Owner parameter editing: an invalid draft is refused, a valid one applies.
        var invalid = ValidParams(); invalid.NTems = 1;   // mission params require 2..6 teams
        using var refused = await owner.Http.PostAsync($"/api/games/{game.Id}/params", Body(new { paramsWire = Wire(invalid) }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var edited = ValidParams(); edited.NTems = 4; edited.IMaxRank = 900;
        using var editedResponse = await owner.Http.PostAsync($"/api/games/{game.Id}/params", Body(new { paramsWire = Wire(edited) }));
        editedResponse.EnsureSuccessStatusCode();
        var updated = (await editedResponse.Content.ReadFromJsonAsync<GameDto>())!;
        Assert.Equal((short)4, MissionParamsMsg.Decode(Convert.FromBase64String(updated.ParamsWire)).NTems);

        // A different signed-in player cannot edit or cancel it.
        var other = SignIn(app, "NotOwner");
        using var foreignEdit = await other.Http.PostAsync($"/api/games/{game.Id}/params", Body(new { paramsWire = Wire(edited) }));
        Assert.Equal(HttpStatusCode.BadRequest, foreignEdit.StatusCode);
        using var foreignCancel = await other.Http.PostAsJsonAsync($"/api/games/{game.Id}/cancel", new { });
        Assert.Equal(HttpStatusCode.NotFound, foreignCancel.StatusCode);

        // The selected game server claims it with its registration token.
        using var wrongClaim = await anon.PostAsJsonAsync($"/api/games/{game.Id}/claim",
            new { serverId, token = "not-the-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongClaim.StatusCode);
        using var claim = await anon.PostAsJsonAsync($"/api/games/{game.Id}/claim", new { serverId, token });
        claim.EnsureSuccessStatusCode();
        Assert.Equal("Claimed", (await claim.Content.ReadFromJsonAsync<GameDto>())!.State);

        // The owner can still cancel after the claim (native's create-game UI).
        using var cancel = await owner.Http.PostAsJsonAsync($"/api/games/{game.Id}/cancel", new { });
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Empty((await anon.GetFromJsonAsync<GameDto[]>("/api/games"))!);
    }

    private sealed record Ack(long Sequence, Line Line);
    private sealed record Line(long Sequence, DateTimeOffset At, string Channel, string SenderId,
        string Sender, string Text);
    private sealed record Catalog(ServerChoice[] Servers, string[] Cores, string[] Maps);
    private sealed record ServerChoice(string Id, string Name, string Address, int Port, string Core,
        string MissionName);
    private sealed record GameDto(string Id, string Owner, string ServerId, string ServerName,
        string Address, int Port, string Core, string Map, string GameName,
        string ParamsWire, string State, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Games;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P871b — the review item on P871's global
/// <c>ConfigureHttpJsonOptions(... IncludeFields = true)</c>: the new-game
/// wizard's <c>MissionParamsMsg</c> is a native packed struct of public FIELDS,
/// and P871 taught the WHOLE lobby serializer to emit fields so the games
/// endpoints could bind it — which changed (or could change) the JSON of every
/// other endpoint: identity, career, squad, events and the server directory.
///
/// <para><b>Fix.</b> The mission parameters now cross the games endpoints as
/// native's packed struct, base64'd (<c>paramsWire</c>, see
/// <see cref="GameEndpoints"/>), so the lobby keeps the framework's default JSON
/// contract. These snapshots pin one response per endpoint family to its exact
/// bytes; the two games-endpoint assertions pin the new shape.</para>
/// </summary>
public sealed class P871bJsonContractTests
{
    private static HttpClient SignIn(LobbyTestApp app, string callsign, out IdentityAccount account)
    {
        var store = app.Services.GetRequiredService<IdentityStore>();
        account = store.CreateAccount(callsign);
        string session = store.Session(account.Id);
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session);
        return http;
    }

    [Fact]
    public async Task The_identity_career_squad_and_event_endpoints_keep_their_exact_json()
    {
        using var app = new LobbyTestApp();
        using var http = SignIn(app, "ContractPilot", out var account);

        // identity — GET /api/identity/me (IdentityAccount record)
        Assert.Equal(
            $$"""{"id":"{{account.Id}}","callsign":"ContractPilot"}""",
            await Json(http, "/api/identity/me"));

        // career — GET /api/career/{accountId}: a fresh account, native defaults
        string career = await Json(http, $"/api/career/{account.Id}");
        Assert.Equal(
            $$"""{"accountId":"{{account.Id}}","callsign":"ContractPilot","description":"","rating":0,"civs":[],"allCivsRank":0}""",
            career);

        // squad — GET /api/squads (empty directory)
        Assert.Equal("[]", await Json(http, "/api/squads?sort=Name&count=84"));

        // events — GET /api/events/ (empty feed)
        Assert.Equal("[]", await Json(http, "/api/events/"));

        // the server directory — GET /api/servers (empty)
        Assert.Equal("[]", await Json(http, "/api/servers"));
    }

    [Fact]
    public async Task The_create_game_endpoints_carry_the_native_missionparams_as_a_wire_blob()
    {
        using var app = new LobbyTestApp(configure: s =>
        {
            s.GameCores = ["PCore001.igc"];
            s.GameMaps = ["XXV_simple.igc"];
        });
        var registry = app.Services.GetRequiredService<ServerRegistry>();
        var server = registry.Register(new RegisterRequest
        {
            Name = "ContractHost", Address = "127.0.0.1", Port = 6073, PlayerCount = 0, MaxPlayers = 32,
            MissionName = "Contract Mission", CoreFile = "PCore001.igc", Version = "0.1.0",
        })!;

        using var owner = SignIn(app, "ContractOwner", out _);
        // The same native defaults P871ChatAndGamesTests uses: MissionParams::Reset.
        var parameters = new Allegiance.Core.Wire.MissionParamsMsg
        {
            NTems = 2,
            NTotalMaxPlayersPerGame = 32,
            NMaxPlayersPerTeam = 16,
            IMinRank = -1,
            IMaxRank = 1000,
            ILives = Allegiance.Core.Wire.MissionDef.UnlimitedLives,
            IGoalConquestPercentage = 100,
            IGoalTerritoryPercentage = 100,
            Bits =
            {
                [Allegiance.Core.Wire.MissionParamsMsg.BitObjectModelCreated] = true,
                [Allegiance.Core.Wire.MissionParamsMsg.BitScoresCount] = true,
            },
        };
        using var created = await owner.PostAsJsonAsync("/api/games", new
        {
            serverId = server.Id, core = "PCore001.igc", map = "XXV_simple.igc",
            gameName = "Contract Game", paramsWire = Convert.ToBase64String(parameters.Encode()),
        });
        created.EnsureSuccessStatusCode();

        // The property names are the framework defaults (camelCase, no fields).
        var json = await created.Content.ReadAsStringAsync();
        Assert.Contains("\"paramsWire\":", json);
        Assert.DoesNotContain("\"StrGameName\"", json);      // no field-name leakage
        Assert.DoesNotContain("\"bits\":", json);
        Assert.DoesNotContain("\"NTems\"", json);

        // ...and the blob is the native struct: round-trips through the codec.
        var game = (await created.Content.ReadFromJsonAsync<GameEndpoints.GameDto>())!;
        var decoded = Allegiance.Core.Wire.MissionParamsMsg.Decode(Convert.FromBase64String(game.ParamsWire));
        Assert.Equal((short)2, decoded.NTems);
        Assert.Equal("Contract Game", decoded.StrGameName);
        Assert.Equal("PCore001.igc", decoded.SzIgcStaticFile);

        // A garbage blob is refused rather than deserialized into nonsense.
        using var bad = await owner.PostAsJsonAsync("/api/games", new
        {
            serverId = server.Id, core = "PCore001.igc", map = "XXV_simple.igc",
            gameName = "Bad Blob", paramsWire = "not-base64",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static async Task<string> Json(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

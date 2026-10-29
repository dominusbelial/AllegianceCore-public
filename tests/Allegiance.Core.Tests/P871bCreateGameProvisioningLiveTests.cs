using System.Net.Http.Json;
using System.Net.Http.Headers;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Lobby;
using Allegiance.Lobby.Games;
using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Srv = Allegiance.Server.Server;
using Lhb = Allegiance.Server.LobbyHeartbeat;
using Lcfg = Allegiance.Server.LobbyConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P871b — the new-game wizard's SERVER half, end to end over real HTTP against
/// the in-process lobby: a signed-in player posts CREATE_MISSION_REQ, a registered
/// game server CLAIMS it with its own registration token, provisions the mission
/// from the wizard's native <c>MissionParams</c>, and a joiner lands in it
/// (<c>lobby/client.cpp:390-465</c>).
///
/// <para>Also pins SIM-0146's wiring: the mission this path creates is
/// <see cref="Mission.PlayerCreated"/> (so native's ten-second empty-mission reap
/// applies, <c>FedSrv.CPP:7871-7910</c>) while every configured/admin/showcase/
/// skirmish world stays persistent.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P871bCreateGameProvisioningLiveTests
{
    private const string Map = "m2xspiderw.igc";

    private static HttpClient LobbyClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ServerRegistry> app)
        => new(app.Server.CreateHandler())
    {
        BaseAddress = app.ClientOptions.BaseAddress,
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>A one-game dedicated server config on the real core/art corpus.</summary>
    private static Allegiance.Server.ServerConfig Config(string artRoot) => new()
    {
        ArtPath = artRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Allegiance.Server.GameConfig
            {
                Name = "Configured World", Map = Map, Teams = 2,
                CivIds = [-1, -1], SideNames = ["Team 1", "Team 2"],
                MaxPlayersPerTeam = 32,
            },
        ],
    };

    private static Allegiance.Core.Wire.MissionParamsMsg WizardParams() => new()
    {
        StrGameName = "P871b Wizard Game",
        SzIgcStaticFile = "PCore001.igc",
        NTems = 3,                                  // the wizard's own team count
        NTotalMaxPlayersPerGame = 32,
        NMaxPlayersPerTeam = 12,
        IMinRank = -1,
        IMaxRank = 1000,
        ILives = Allegiance.Core.Wire.MissionDef.UnlimitedLives,
        IGoalConquestPercentage = 100,
        IGoalTerritoryPercentage = 100,
        NGoalTeamKills = 7,                          // a goal the configured world does not have
        Bits =
        {
            [Allegiance.Core.Wire.MissionParamsMsg.BitObjectModelCreated] = true,
            [Allegiance.Core.Wire.MissionParamsMsg.BitScoresCount] = true,
        },
    };

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_claimed_create_game_request_becomes_ready_or_fails_if_capacity_races(bool raced)
    {
        Require.Artwork();
        // A private identity database per run (the lobby's default file is shared).
        string directory = Path.Combine(Directory.GetCurrentDirectory(), ".scratch",
            "p871b-create-" + Guid.NewGuid().ToString("N"));
        var identity = new IdentitySettings
        {
            ConnectionString = "Data Source=" + Path.Combine(directory, "accounts.db"),
            SigningKeyFile = Path.Combine(directory, "signing.pem"),
            PublicKeyFile = Path.Combine(directory, "public.pem"),
        };
        using var harness = new LobbyHarness();
        using var app = harness.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IdentitySettings>();
            services.AddSingleton(identity);
        }));
        using var lobby = LobbyClient(app);
        var servers = app.Services.GetRequiredService<ServerRegistry>();

        // The game server boots with the lobby enabled and the heartbeat wired to
        // this in-process lobby (the production ctor would need a real socket), so
        // the FIRST heartbeat tick registers its directory listing — the id+token
        // the claim path uses are the ones it just earned.
        var config = Config(TestArtRoot.Value);
        config.MaxGames = 2; // configured world + one player-created world
        config.Lobby = new Lcfg
        {
            Url = app.ClientOptions.BaseAddress.AbsoluteUri,
            DisplayName = "Wizard Host",
            PublicAddress = "127.0.0.1",
            HeartbeatIntervalSeconds = 25,
        };
        using var server = new Srv();
        server.HeartbeatFactory = cfg => new Lhb(lobby, cfg, () => 0, "Configured World",
            config.StaticCore, "1.0.0", 6073,
            canHostPlayerGame: () => server.CanHostPlayerGame());
        server.Boot(config);
        Assert.Single(server.Missions);
        Assert.False(server.Missions[0].PlayerCreated);   // a configured world persists (SIM-0146)
        await server.PumpLobbyAsync();                    // tick 1: register the listing
        var listing = (await lobby.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!.Single();
        Assert.Equal("Wizard Host", listing.Name);
        Assert.Equal(1, listing.CurrentGames);
        Assert.Equal(2, listing.MaxGames);
        Assert.True(server.CanHostPlayerGame());
        var catalog = (await lobby.GetFromJsonAsync<GameEndpoints.CatalogDto>("/api/games/catalog"))!;
        Assert.Equal(1, Assert.Single(catalog.Servers).CurrentGames);
        Assert.Equal(2, catalog.Servers[0].MaxGames);

        // The player signs in and submits the wizard (native's creator is a
        // lobby-session client; POST /api/games is authenticated).
        var store = app.Services.GetRequiredService<IdentityStore>();
        var account = store.CreateAccount("WizardPlayer");
        lobby.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Session(account.Id));
        var parameters = WizardParams();
        using var created = await lobby.PostAsJsonAsync("/api/games", new
        {
            serverId = listing.Id,
            core = "PCore001.igc",
            map = Map,
            gameName = "P871b Wizard Game",
            paramsWire = Convert.ToBase64String(parameters.Encode()),
        });
        created.EnsureSuccessStatusCode();
        var request = (await created.Content.ReadFromJsonAsync<GameEndpoints.GameDto>())!;
        Assert.Equal("Queued", request.State);

        // ── the relay: tick 2 sees the queued request for its own id and claims
        //    it with the registration token the heartbeat holds ────────────────
        await server.PumpLobbyAsync();
        var relayed = (await lobby.GetFromJsonAsync<GameEndpoints.GameDto[]>("/api/games"))!;
        Assert.Equal("Claimed", Assert.Single(relayed).State);

        // A different creation can fill the last slot AFTER the predicate allowed
        // this claim, before the sim thread drains it (CreateGameDialog.h:20).
        if (raced)
        {
            var winner = server.ProvisionPlayerGame(new Allegiance.Server.ClaimedGame("winner", listing.Id,
                "PCore001.igc", Map, "Racing winner", Convert.ToBase64String(parameters.Encode()), "Claimed"));
            Assert.NotNull(winner);
            Assert.False(server.CanHostPlayerGame());
            var before = server.Missions.ToArray();
            var configs = config.Games.ToArray();
            server.TickSim();
            Assert.Equal(before, server.Missions);
            Assert.Equal(configs, config.Games);
            await server.PumpLobbyAsync();
            var failed = Assert.Single((await lobby.GetFromJsonAsync<GameEndpoints.GameDto[]>("/api/games"))!);
            Assert.Equal("Failed", failed.State);
            Assert.Null(failed.Cookie);
            var full = (await lobby.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!.Single();
            Assert.Equal(2, full.CurrentGames);
            Assert.Equal(2, full.MaxGames);
            // Stale clients also get the full-server refusal at the HTTP boundary.
            using var refused = await lobby.PostAsJsonAsync("/api/games", new
            {
                serverId = listing.Id, core = "PCore001.igc", map = Map,
                gameName = "Too many", paramsWire = Convert.ToBase64String(parameters.Encode()),
            });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("full", await refused.Content.ReadAsStringAsync());
            return;
        }

        // ── provisioning: the sim thread assembles the wizard's mission ───────
        server.TickSim();
        Assert.Equal(2, server.Missions.Count);
        Assert.False(server.CanHostPlayerGame());
        var mission = server.Missions[1];                 // appended game, addressed by its cookie
        Assert.True(mission.PlayerCreated);              // SIM-0146 reap armed
        Assert.False(server.Missions[0].PlayerCreated);  // the configured world is untouched
        Assert.Equal(3, mission.Sides.Count);            // the wizard's team count
        Assert.Equal(7, mission.Goals.TeamKills);        // the wizard's winning condition
        Assert.Equal(MissionStage.NotStarted, mission.Stage);
        Assert.NotEmpty(mission.Sectors);
        Assert.NotEmpty(mission.Warps);
        Assert.Equal("P871b Wizard Game", config.Games[1].Name);
        Assert.Equal(12, config.Games[1].MaxPlayersPerTeam);
        Assert.Equal(3, config.Games[1].Teams);
        // The creator uses the ready cookie; NA keeps targeting the first world.
        Assert.Same(mission, server.Missions[1]);

        // ── a claimed request is consumed: a further tick creates nothing ─────
        await server.PumpLobbyAsync();
        var ready = Assert.Single((await lobby.GetFromJsonAsync<GameEndpoints.GameDto[]>("/api/games"))!);
        Assert.Equal("Ready", ready.State);
        Assert.Equal(mission.Cookie, ready.Cookie);
        var registered = (await lobby.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!.Single();
        Assert.Equal(server.Missions.Select(m => m.Cookie), registered.Missions!.Select(m => m.Cookie));
        Assert.Equal("P871b Wizard Game", registered.Missions![1].Name);
        Assert.Equal(2, registered.CurrentGames);
        Assert.Equal(2, registered.MaxGames);
        server.TickSim();
        Assert.Equal(2, server.Missions.Count);

        // ── SIM-0146: an empty player-created mission is reaped after 10 s, and
        //    the configured world behind it survives ─────────────────────────
        AdvanceSim(server, seconds: 12);
        Assert.Single(server.Missions);
        Assert.False(server.Missions[0].PlayerCreated);
        Assert.Single(config.Games);                     // the config entry left with it
        Assert.True(server.CanHostPlayerGame());
        await server.PumpLobbyAsync();
        var reaped = (await lobby.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!.Single();
        Assert.Equal(1, reaped.CurrentGames);
        Assert.Equal(2, reaped.MaxGames);
    }

    /// <summary>Run the sim forward without wall-clock waiting: every tick adds
    /// one period (1 / UpdatesPerSecond).</summary>
    private static void AdvanceSim(Srv server, double seconds)
    {
        int ticks = (int)Math.Ceiling(seconds * 20);
        for (int i = 0; i < ticks; i++) server.TickSim();
    }
}

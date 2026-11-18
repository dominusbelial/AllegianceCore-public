using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Allegiance.Core.Identity;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Lobby.Chat;
using Allegiance.Lobby.Games;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>R1 review (public lobby inbound handling) — one proof per lobby
/// finding in <c>R1_REPORT.md</c>. Each was skipped until its finding was fixed;
/// P1024 unskips them all with the inputs and assertion meanings unchanged.</summary>
public sealed class R1LobbyReviewTests
{
    private static (HttpClient Http, IdentityAccount Account) SignIn(LobbyTestApp app, string callsign)
    {
        var store = app.Services.GetRequiredService<IdentityStore>();
        var account = store.CreateAccount(callsign);
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Session(account.Id));
        return (http, account);
    }

    private static MissionParamsMsg ValidParams() => new()
    {
        StrGameName = "R1 Game", SzIgcStaticFile = "PCore001.igc", NTems = 2,
        NTotalMaxPlayersPerGame = 32, NMaxPlayersPerTeam = 16, IMinRank = -1, IMaxRank = 1000,
        ILives = MissionDef.UnlimitedLives, IGoalConquestPercentage = 100, IGoalTerritoryPercentage = 100,
        Bits = { [MissionParamsMsg.BitObjectModelCreated] = true, [MissionParamsMsg.BitScoresCount] = true },
    };

    /// <summary>F9 — a soft hyphen (U+00AD) or a trailing no-break space (U+00A0)
    /// makes a second account whose callsign renders exactly like a registered
    /// one; the lobby signs join tokens for it and the game server seats it under
    /// that name.</summary>
    [Theory]
    [InlineData("Ad\u00admin")]
    [InlineData("Admin\u00a0")]
    public void A_callsign_that_renders_like_a_registered_one_is_refused(string lookalike)
    {
        using var app = new LobbyTestApp();
        var store = app.Services.GetRequiredService<IdentityStore>();
        store.CreateAccount("Admin");
        Assert.False(JoinTokens.ValidCallsign(lookalike), "the lookalike is accepted as a valid callsign");
        Assert.ThrowsAny<Exception>(() => store.CreateAccount(lookalike));
    }

    /// <summary>F10 — game-creation requests are never removed: after 128 of them
    /// (from anyone, ever) every later create is refused until the lobby restarts.
    /// P1024 (R1 F10) — the directory cap is still filled to <c>MaxRequests</c> from
    /// one burst, but each account now holds only its small in-flight allowance
    /// (<c>MaxLiveRequestsPerAccount</c>), so the fill is spread over the accounts a
    /// real directory would have. The scenario, the 24-hour jump, the refreshed
    /// listing and the final assertion are unchanged.</summary>
    [Fact]
    public async Task Stale_game_requests_do_not_block_new_games_forever()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = new LobbyTestApp(time, s =>
        {
            s.GameCores = ["PCore001.igc"]; s.GameMaps = ["XXV_simple.igc"];
            // P1049b — the request store prunes on the final Create. Its age
            // check needs a 24-hour clock jump, not 2,880 unrelated periodic
            // registry sweeps; registration below still prunes expired listings.
            s.SweepIntervalSeconds = 48 * 60 * 60;
        });
        var registry = app.Services.GetRequiredService<ServerRegistry>();
        var settings = app.Services.GetRequiredService<LobbySettings>();
        var games = app.Services.GetRequiredService<GameCreationStore>();
        var listed = registry.Register(new RegisterRequest
        {
            Name = "R1Host", Address = "127.0.0.1", Port = 6073, PlayerCount = 0, MaxPlayers = 0,
            MissionName = "R1", CoreFile = "PCore001.igc", Version = "1.0.0",
        })!;
        var catalog = new GameCreationStore.Catalog(settings.GameCores.ToArray(), settings.GameMaps.ToArray());
        int accounts = (GameCreationStore.MaxRequests + GameCreationStore.MaxLiveRequestsPerAccount - 1)
            / GameCreationStore.MaxLiveRequestsPerAccount;
        // P1049b — this prefill already calls the store directly, which only
        // needs owner keys. Do not create 32 unused HTTP clients and persist 32
        // accounts/sessions just to fill it. The final POST still signs in a real
        // player and exercises authentication and expiry through the HTTP route.
        var owners = Enumerable.Range(0, accounts)
            .Select(a => new IdentityAccount("stale-owner-" + a, "Spammer" + a)).ToArray();
        for (int i = 0; i < GameCreationStore.MaxRequests; i++)
        {
            var owner = owners[i % accounts];
            Assert.NotNull(games.Create(owner.Id, owner.Callsign, registry.Get(listed.Id)!,
                "PCore001.igc", "XXV_simple.igc", "junk" + i, ValidParams(), catalog, out _));
        }
        Assert.Equal(GameCreationStore.MaxRequests, games.QueuedFor(listed.Id).Count);

        // A day later nobody has claimed any of them; the listing is refreshed.
        time.Advance(TimeSpan.FromHours(24));
        listed = registry.Register(new RegisterRequest
        {
            Name = "R1Host", Address = "127.0.0.1", Port = 6073, PlayerCount = 0, MaxPlayers = 0,
            MissionName = "R1", CoreFile = "PCore001.igc", Version = "1.0.0",
        })!;
        var player = SignIn(app, "Player");
        using var created = await player.Http.PostAsync("/api/games", JsonContent.Create(new
        {
            serverId = listed.Id, core = "PCore001.igc", map = "XXV_simple.igc", gameName = "Real Game",
            paramsWire = Convert.ToBase64String(ValidParams().Encode()),
        }));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
    }

    /// <summary>F12a — the mute list stores any caller-supplied string (up to the
    /// 16 KB body cap), 256 per account, for the life of the process.</summary>
    [Fact]
    public async Task Mute_accepts_only_an_account_id()
    {
        using var app = new LobbyTestApp();
        var user = SignIn(app, "Muter");
        using var reply = await user.Http.PostAsJsonAsync("/api/chat/mute", new { accountId = new string('x', 8000) });
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
    }

    /// <summary>F12b — one account can occupy all 512 channel slots with made-up
    /// names; afterwards a real server's channel is refused until restart.</summary>
    [Fact]
    public void Made_up_channels_cannot_lock_out_a_real_servers_channel()
    {
        var chat = new DirectoryChat(new FakeTimeProvider(DateTimeOffset.UtcNow));
        for (int i = 0; i < 600; i++) chat.Post("spammer", "Spammer", "server:junk" + i, "x", out _);
        Assert.NotNull(chat.Post("player", "Player", "server:real-server", "hello", out string? error));
        Assert.Null(error);
    }

    /// <summary>F13 — listing counts are unbounded, and the stats endpoint sums
    /// them with overflow checking: two listings make it answer 500.</summary>
    [Fact]
    public async Task Absurd_player_counts_do_not_break_the_stats_endpoint()
    {
        using var app = new LobbyTestApp();
        using var http = app.CreateClient();
        foreach (int port in new[] { 6073, 6074 })
        {
            using var post = await http.PostAsJsonAsync("/api/servers/register", new
            {
                name = "R1 " + port, address = "1.2.3.4", port, playerCount = int.MaxValue, maxPlayers = 32,
                missionName = "R1", coreFile = "PCore001.igc", version = "0.1.0",
            });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        }
        using var stats = await http.GetAsync("/api/servers/stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
    }

    /// <summary>F11 — the STUN responder spends its global budget before it
    /// checks the per-source limit, so one source's refused requests starve
    /// everyone else.</summary>
    [Fact]
    public void One_noisy_source_cannot_exhaust_the_global_STUN_budget()
    {
        var observations = new NatObservationStore(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = StunBinding.CreateRequest(); var output = new byte[256];
        var noisy = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 4000);
        for (int i = 0; i < 600; i++) observations.Respond(request, noisy, true, output);
        Assert.Equal(52, observations.Respond(request, new IPEndPoint(IPAddress.Parse("10.0.0.2"), 4000), true, output));
    }
}

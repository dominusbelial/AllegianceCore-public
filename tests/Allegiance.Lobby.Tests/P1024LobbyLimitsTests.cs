using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Allegiance.Core.Identity;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Lobby.Chat;
using Allegiance.Lobby.Games;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P1024 — edges of the five R1 lobby limits (F9 callsign lookalikes, F10 stale
/// create requests, F11 STUN budget order, F12 chat mutes/channels, F13 listing
/// counters). Each class proves the boundary and that ordinary use is unchanged.
/// </summary>
public sealed class P1024CallsignTests
{
    private static IdentityStore Store(LobbyTestApp app) => app.Services.GetRequiredService<IdentityStore>();

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Élan Pilot", true)]
    [InlineData("A B", true)]
    [InlineData("Ad\u00admin", false)]          // soft hyphen (Format)
    [InlineData("Admin\u00a0", false)]          // no-break space
    [InlineData("\u00a0Admin", false)]
    [InlineData("Ad\u200bmin", false)]          // zero-width space
    [InlineData("Admin\u200e", false)]          // left-to-right mark
    [InlineData("Admin\t", false)]              // tab
    [InlineData(" Admin", false)]
    [InlineData("Admin ", false)]
    public void Only_printable_names_without_invisibles_can_be_registered(string callsign, bool valid) =>
        Assert.Equal(valid, JoinTokens.ValidCallsign(callsign));

    [Theory]
    [InlineData("ad\u00admin", "ADMIN")]
    [InlineData("Admin\u00a0", "ADMIN")]
    [InlineData("Admin\u200b", "ADMIN")]
    [InlineData("  admin   smith ", "ADMIN SMITH")]
    [InlineData("Admin\tSmith", "ADMIN SMITH")]
    [InlineData("", "")]
    public void Skeleton_strips_invisibles_collapses_spaces_and_case_folds(string name, string skeleton) =>
        Assert.Equal(skeleton, JoinTokens.CallsignSkeleton(name));

    [Fact]
    public void Null_skeleton_is_empty() => Assert.Equal("", JoinTokens.CallsignSkeleton(null));

    [Fact]
    public void Every_variant_of_a_registered_name_is_refused_while_the_name_itself_still_works()
    {
        using var app = new LobbyTestApp();
        var store = Store(app);
        store.CreateAccount("Admin");

        foreach (string lookalike in new[] { "Ad\u00admin", "Admin\u00a0", "\u00a0Admin", "Admin\u200b" })
            Assert.ThrowsAny<Exception>(() => store.CreateAccount(lookalike));

        // Whitespace runs collapse in the uniqueness key: a second "Admin  Smith"
        // cannot hide behind its extra spaces when "Admin Smith" exists.
        store.CreateAccount("Admin Smith");
        Assert.ThrowsAny<Exception>(() => store.CreateAccount("Admin  Smith"));

        // Ordinary names and case-insensitive duplicates behave exactly as before.
        var second = store.CreateAccount("AdminTwo");
        Assert.Equal("AdminTwo", second.Callsign);
        Assert.ThrowsAny<Exception>(() => store.CreateAccount("admintwo"));
        Assert.NotNull(store.Authenticate(store.Session(second.Id)));
    }

    /// <summary>
    /// P1024 (R1 F9) — a callsign stored before the strict rule cannot be
    /// registered again, but it must keep logging in: the lobby still signs a join
    /// token for it (native seated the ticket's canonical name as-is,
    /// <c>FedSrv/FedSrv.CPP:5877-5878</c>) and the token verifies with the name
    /// unchanged. Exactly what was asked: existing names keep working; only NEW
    /// lookalikes are refused.
    /// </summary>
    [Fact]
    public void A_name_stored_before_the_rule_still_logs_in_and_still_blocks_a_new_lookalike()
    {
        using var app = new LobbyTestApp();
        var store = Store(app);
        const string legacy = "Ad\u00admin";
        string id = Guid.NewGuid().ToString("N");

        // The pre-P1024 row exactly as an existing database holds it: the original
        // bytes and the OLD upper-case-only uniqueness key.
        Assert.Equal(1, store.Execute("INSERT INTO accounts(id,callsign,normalized) VALUES(@p0,@p1,@p2)",
            id, legacy, legacy.ToUpperInvariant()));
        Assert.NotEqual(JoinTokens.CallsignSkeleton(legacy), legacy.ToUpperInvariant());

        // It is not registrable today …
        Assert.False(JoinTokens.ValidCallsign(legacy));
        // … but the stored account authenticates and its session works.
        var account = store.Account(id);
        Assert.NotNull(account);
        Assert.Equal(legacy, account!.Callsign);
        string session = store.Session(id);
        Assert.Equal(legacy, store.Authenticate(session)!.Callsign);

        // … and the lobby can still sign and verify its join token (seated as-is).
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        string token = JoinTokens.Issue(key, "issuer", "server", id, account.Callsign, now);
        var verified = JoinTokens.Verify(token, key, "issuer", "server", now);
        Assert.NotNull(verified);
        Assert.Equal(legacy, verified!.Callsign);
        Assert.Equal(id, verified.Sub);

        // No new lookalike of the legacy name can appear either, even though the
        // stored row keeps the old key: "Admin" shares its skeleton, and the scan
        // over stored callsigns refuses it. A genuinely different name is fine.
        Assert.True(store.CallsignTaken("Admin"));
        Assert.ThrowsAny<Exception>(() => store.CreateAccount("Admin"));
        Assert.False(store.CallsignTaken("AdminTwo"));
        Assert.Equal("AdminTwo", store.CreateAccount("AdminTwo").Callsign);
        Assert.Contains(legacy, store.RegisteredCallsigns());
    }
}

public sealed class P1024GameRequestTests
{
    private static MissionParamsMsg ValidParams() => new()
    {
        StrGameName = "R1 Game", SzIgcStaticFile = "PCore001.igc", NTems = 2,
        NTotalMaxPlayersPerGame = 32, NMaxPlayersPerTeam = 16, IMinRank = -1, IMaxRank = 1000,
        ILives = MissionDef.UnlimitedLives, IGoalConquestPercentage = 100, IGoalTerritoryPercentage = 100,
        Bits = { [MissionParamsMsg.BitObjectModelCreated] = true, [MissionParamsMsg.BitScoresCount] = true },
    };

    private static readonly GameCreationStore.Catalog Catalog = new(["PCore001.igc"], ["XXV_simple.igc"]);

    private static (ServerRegistry Registry, GameCreationStore Games, string ServerId, string Token) Fixture(TimeProvider time)
    {
        var registry = new ServerRegistry(time);
        var listed = registry.Register(new RegisterRequest
        {
            Name = "R1Host", Address = "127.0.0.1", Port = 6073, CoreFile = "PCore001.igc", Version = "1.0.0",
        })!;
        return (registry, new GameCreationStore(time, registry), listed.Id, listed.Token);
    }

    /// <summary>Refresh a listing exactly as a live host's heartbeat does.</summary>
    private static void Heartbeat(ServerRegistry registry, string serverId, string token, string name) =>
        Assert.NotNull(registry.Register(new RegisterRequest
        {
            Name = name, Address = "127.0.0.1", Port = 6073, CoreFile = "PCore001.igc", Version = "1.0.0", Token = token,
        }));

    private static GameCreationStore.GameCreationRequest? Create(GameCreationStore games, ServerRegistry registry,
        string serverId, string accountId, string name, out string? error) =>
        games.Create(accountId, "Pilot", registry.Get(serverId)!, "PCore001.igc", "XXV_simple.igc", name,
            ValidParams(), Catalog, out error);

    [Fact]
    public void One_account_holds_only_its_in_flight_allowance_and_expiry_frees_it()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (registry, games, serverId, token) = Fixture(time);
        const string owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        for (int i = 0; i < GameCreationStore.MaxLiveRequestsPerAccount; i++)
            Assert.NotNull(Create(games, registry, serverId, owner, "g" + i, out _));

        Assert.Null(Create(games, registry, serverId, owner, "g-limit", out var refused));
        Assert.Equal("too many live game requests for this account", refused);

        // Another account is unaffected by that one account's allowance.
        Assert.NotNull(Create(games, registry, serverId, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "other", out _));

        // Ten minutes later the queued rows are stale: the same account may create
        // again. The host kept heartbeating, so only the request's age freed the slot.
        time.Advance(GameCreationStore.QueuedMaxAge + TimeSpan.FromSeconds(1));
        Heartbeat(registry, serverId, token, "R1Host");
        Assert.Empty(games.All());
        Assert.NotNull(Create(games, registry, serverId, owner, "fresh", out _));
    }

    [Fact]
    public void Cancelling_or_finishing_a_request_frees_the_allowance()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (registry, games, serverId, _) = Fixture(time);
        const string owner = "cccccccccccccccccccccccccccccccc";
        var requests = new List<string>();
        for (int i = 0; i < GameCreationStore.MaxLiveRequestsPerAccount; i++)
            requests.Add(Create(games, registry, serverId, owner, "g" + i, out _)!.Id);
        Assert.Null(Create(games, registry, serverId, owner, "over", out _));

        // A cancelled request is removed outright (R1 F10: "remove cancelled rows").
        Assert.True(games.Cancel(owner, requests[0]));
        Assert.DoesNotContain(games.All(), r => r.Id == requests[0]);
        Assert.NotNull(Create(games, registry, serverId, owner, "after-cancel", out _));
        Assert.Null(Create(games, registry, serverId, owner, "over-again", out _));

        // A finished (ready) request is feedback, not an outstanding request.
        Assert.Equal(GameCreationStore.State.Claimed, games.Claim(serverId, requests[1])!.RequestState);
        Assert.Equal(GameCreationStore.State.Ready, games.Complete(serverId, requests[1], 7)!.RequestState);
        Assert.NotNull(Create(games, registry, serverId, owner, "after-ready", out _));
    }

    [Fact]
    public void Requests_for_a_listing_that_disappeared_are_dropped()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (registry, games, serverId, token) = Fixture(time);
        for (int i = 0; i < GameCreationStore.MaxLiveRequestsPerAccount; i++)
            Assert.NotNull(Create(games, registry, serverId, "dddddddddddddddddddddddddddddddd", "g" + i, out _));
        Assert.Equal(GameCreationStore.MaxLiveRequestsPerAccount, games.All().Count);

        Assert.True(registry.Deregister(serverId, token));
        Assert.Empty(games.All());
        Assert.Empty(games.QueuedFor(serverId));

        // The same account may create against a fresh listing at once.
        var listed = registry.Register(new RegisterRequest
        {
            Name = "R1Host2", Address = "127.0.0.1", Port = 6074, CoreFile = "PCore001.igc", Version = "1.0.0",
        })!;
        Assert.NotNull(Create(games, registry, listed.Id, "dddddddddddddddddddddddddddddddd", "new-listing", out _));
    }

    [Fact]
    public void Claimed_and_finished_rows_expire_by_age()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (registry, games, serverId, token) = Fixture(time);
        var claimed = Create(games, registry, serverId, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "claimed", out _)!;
        Assert.NotNull(games.Claim(serverId, claimed.Id));
        time.Advance(GameCreationStore.ClaimedMaxAge + TimeSpan.FromSeconds(1));
        Heartbeat(registry, serverId, token, "R1Host");      // the host is still live
        Assert.Empty(games.All());

        var ready = Create(games, registry, serverId, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "ready", out _)!;
        Assert.NotNull(games.Claim(serverId, ready.Id));
        Assert.NotNull(games.Complete(serverId, ready.Id, 3));
        time.Advance(GameCreationStore.FinishedMaxAge + TimeSpan.FromSeconds(1));
        Heartbeat(registry, serverId, token, "R1Host");
        Assert.Empty(games.All());
        Assert.NotNull(Create(games, registry, serverId, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "later", out _));
    }

    [Fact]
    public void The_directory_cap_is_still_enforced_across_accounts()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (registry, games, serverId, _) = Fixture(time);
        int accounts = (GameCreationStore.MaxRequests + GameCreationStore.MaxLiveRequestsPerAccount - 1)
            / GameCreationStore.MaxLiveRequestsPerAccount;
        for (int i = 0; i < GameCreationStore.MaxRequests; i++)
        {
            string owner = (i % accounts).ToString("x32");
            Assert.NotNull(Create(games, registry, serverId, owner, "g" + i, out _));
        }
        Assert.Null(Create(games, registry, serverId, "ffffffffffffffffffffffffffffffff", "over", out var error));
        Assert.Equal("too many game requests", error);
    }

    /// <summary>The HTTP create path reports the per-account allowance as a 400.</summary>
    [Fact]
    public async Task The_lobby_create_endpoint_reports_the_per_account_cap()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = new LobbyTestApp(time, s => { s.GameCores = ["PCore001.igc"]; s.GameMaps = ["XXV_simple.igc"]; });
        var registry = app.Services.GetRequiredService<ServerRegistry>();
        var store = app.Services.GetRequiredService<IdentityStore>();
        var account = store.CreateAccount("CapOwner");
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Session(account.Id));
        var listed = registry.Register(new RegisterRequest
        {
            Name = "CapHost", Address = "127.0.0.1", Port = 6073, CoreFile = "PCore001.igc", Version = "1.0.0",
        })!;

        for (int i = 0; i < GameCreationStore.MaxLiveRequestsPerAccount; i++)
        {
            using var created = await http.PostAsync("/api/games", JsonContent.Create(new
            {
                serverId = listed.Id, core = "PCore001.igc", map = "XXV_simple.igc", gameName = "G" + i,
                paramsWire = Convert.ToBase64String(ValidParams().Encode()),
            }));
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        }
        using var refused = await http.PostAsync("/api/games", JsonContent.Create(new
        {
            serverId = listed.Id, core = "PCore001.igc", map = "XXV_simple.igc", gameName = "over",
            paramsWire = Convert.ToBase64String(ValidParams().Encode()),
        }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("too many live game requests", await refused.Content.ReadAsStringAsync());
    }
}

public sealed class P1024StunBudgetTests
{
    private static readonly IPEndPoint Noisy = new(IPAddress.Parse("10.0.0.1"), 4000);
    private static readonly IPEndPoint Other = new(IPAddress.Parse("10.0.0.2"), 4000);
    private static readonly IPEndPoint Third = new(IPAddress.Parse("10.0.0.3"), 4000);

    [Fact]
    public void A_noisy_sources_refused_requests_spend_no_global_budget()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var observations = new NatObservationStore(time);
        var request = StunBinding.CreateRequest(); var output = new byte[256];
        for (int i = 0; i < 600; i++) observations.Respond(request, Noisy, true, output);

        // The noisy source spent only its own 16 tokens; the shared bucket is intact.
        Assert.Equal(52, observations.Respond(request, Other, true, output));
        Assert.Equal(52, observations.Respond(request, Third, true, output));
    }

    [Fact]
    public void One_sources_allowance_recovers_after_the_window()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var observations = new NatObservationStore(time);
        var request = StunBinding.CreateRequest(); var output = new byte[256];
        for (int i = 0; i < NatObservationStore.SourcePerSecond; i++) Assert.Equal(52, observations.Respond(request, Noisy, true, output));
        Assert.Equal(0, observations.Respond(request, Noisy, true, output));
        time.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < NatObservationStore.SourcePerSecond; i++) Assert.Equal(52, observations.Respond(request, Noisy, true, output));
        Assert.Equal(0, observations.Respond(request, Noisy, true, output));
    }

    [Fact]
    public void The_global_budget_still_bounds_answered_requests()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var observations = new NatObservationStore(time);
        var request = StunBinding.CreateRequest(); var output = new byte[256];
        int served = 0;
        for (int source = 0; served < NatObservationStore.GlobalPerSecond; source++)
        {
            var endpoint = new IPEndPoint(IPAddress.Parse("10.1." + (source / 250) + "." + (source % 250 + 1)), 4000);
            for (int i = 0; i < NatObservationStore.SourcePerSecond && served < NatObservationStore.GlobalPerSecond; i++, served++)
                Assert.Equal(52, observations.Respond(request, endpoint, true, output));
        }
        // The shared bucket is spent: a fresh source is answered only after the window.
        Assert.Equal(0, observations.Respond(request, new IPEndPoint(IPAddress.Parse("10.9.9.9"), 4000), true, output));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(52, observations.Respond(request, new IPEndPoint(IPAddress.Parse("10.9.9.9"), 4000), true, output));
    }
}

public sealed class P1024ChatLimitTests
{
    private static string AccountId(int i) => i.ToString("x").PadLeft(32, '0');

    [Fact]
    public void Mutes_accept_only_well_formed_account_ids()
    {
        var chat = new DirectoryChat(new FakeTimeProvider(DateTimeOffset.UtcNow));
        string reader = AccountId(1), other = AccountId(2);
        Assert.False(chat.Mute(reader, new string('x', 8000)));
        Assert.False(chat.Mute(reader, "not-an-account-id"));
        Assert.False(chat.Mute(reader, ""));
        Assert.False(chat.Mute(reader, reader));                       // self-mute stays refused
        Assert.True(chat.Mute(reader, other));
        Assert.True(chat.Mute(reader, other));                         // idempotent, no extra row
        Assert.Equal(new[] { other }, chat.Muted(reader));
        // Upper-case ids are the same account, not a second row.
        Assert.True(chat.Mute(reader, other.ToUpperInvariant()));
        Assert.Single(chat.Muted(reader));
        Assert.True(chat.Unmute(reader, other.ToUpperInvariant()));
        Assert.Empty(chat.Muted(reader));
    }

    [Fact]
    public void The_mute_table_is_bounded_in_total_across_accounts()
    {
        var chat = new DirectoryChat(new FakeTimeProvider(DateTimeOffset.UtcNow));
        int accounts = DirectoryChat.MaxMuteEntries / DirectoryChat.MaxMutedPerAccount;
        int target = 0;
        for (int a = 0; a < accounts; a++)
            for (int i = 0; i < DirectoryChat.MaxMutedPerAccount; i++)
                Assert.True(chat.Mute(AccountId(a + 1), AccountId(1000 + target++)));
        Assert.Equal(DirectoryChat.MaxMuteEntries, target);
        // The directory-wide bound refuses further rows, whatever the account.
        Assert.False(chat.Mute(AccountId(accounts + 1), AccountId(900000)));
        Assert.False(chat.Mute(AccountId(1), AccountId(900001)));
        // Removing one frees exactly one row.
        Assert.True(chat.Unmute(AccountId(1), AccountId(1000)));
        Assert.True(chat.Mute(AccountId(accounts + 1), AccountId(900000)));
    }

    [Fact]
    public void Made_up_channels_cannot_lock_out_a_real_channel_and_history_is_kept()
    {
        var chat = new DirectoryChat(new FakeTimeProvider(DateTimeOffset.UtcNow));
        for (int i = 0; i < 600; i++) chat.Post("spammer", "Spammer", "server:junk" + i, "x", out _);
        Assert.NotNull(chat.Post("player", "Player", "server:real-server", "hello", out var error));
        Assert.Null(error);

        // Only channels that still carry a line hold a slot: never more than the
        // bounded history plus the directory channel.
        var channels = chat.Channels();
        Assert.Contains("server:real-server", channels);
        Assert.Contains(DirectoryChat.DirectoryChannel, channels);
        Assert.True(channels.Count < DirectoryChat.MaxChannels, $"channel table grew to {channels.Count}");

        // The newest junk channel still has its line and its slot; the oldest ones
        // released theirs rather than keeping the table full for good.
        Assert.Contains("server:junk599", channels);
        Assert.Single(chat.Read("server:junk599", 0, null));
        Assert.Equal("hello", Assert.Single(chat.Read("server:real-server", 0, null)).Text);
    }

    [Fact]
    public void Mute_suppresses_a_sender_for_that_reader_only()
    {
        var chat = new DirectoryChat(new FakeTimeProvider(DateTimeOffset.UtcNow));
        string reader = AccountId(7), sender = AccountId(8), bystander = AccountId(9);
        Assert.True(chat.Mute(reader, sender));
        chat.Post(sender, "Sender", DirectoryChat.DirectoryChannel, "hidden", out _);
        chat.Post(bystander, "Bystander", DirectoryChat.DirectoryChannel, "seen", out _);
        Assert.Equal("seen", Assert.Single(chat.Read(DirectoryChat.DirectoryChannel, 0, reader)).Text);
        Assert.Equal(2, chat.Read(DirectoryChat.DirectoryChannel, 0, bystander).Count);
    }
}

public sealed class P1024ListingCounterTests
{
    [Fact]
    public void Absurd_and_negative_counts_are_clamped_on_register_and_refresh()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new ServerRegistry(time);
        var listed = registry.Register(new RegisterRequest
        {
            Name = "Absurd", Address = "127.0.0.1", Port = 6073, CoreFile = "PCore001.igc", Version = "1.0.0",
            PlayerCount = int.MaxValue, MaxPlayers = int.MaxValue, CurrentGames = int.MaxValue, MaxGames = int.MaxValue,
            Missions = [new() { Cookie = 1, Name = "m", PlayerCount = int.MaxValue }],
        })!;
        var dto = registry.Get(listed.Id)!;
        Assert.Equal(ServerRegistry.MaxPlayerCount, dto.PlayerCount);
        Assert.Equal(ServerRegistry.MaxPlayersCap, dto.MaxPlayers);
        Assert.Equal(ServerRegistry.MaxGamesPerServer, dto.CurrentGames);
        Assert.Equal(ServerRegistry.MaxGamesPerServer, dto.MaxGames);
        Assert.Equal(ServerRegistry.MaxPlayerCount, Assert.Single(dto.Missions!).PlayerCount);

        // A heartbeat cannot re-inject an absurd value either.
        Assert.NotNull(registry.Register(new RegisterRequest
        {
            Name = "Absurd", Address = "127.0.0.1", Port = 6073, CoreFile = "PCore001.igc", Version = "1.0.0",
            Token = listed.Token, PlayerCount = int.MinValue, MaxPlayers = int.MaxValue,
            CurrentGames = int.MaxValue, MaxGames = int.MaxValue,
        }));
        dto = registry.Get(listed.Id)!;
        Assert.Equal(0, dto.PlayerCount);                            // floored at zero
        Assert.Equal(ServerRegistry.MaxPlayersCap, dto.MaxPlayers);
        Assert.Equal(ServerRegistry.MaxGamesPerServer, dto.CurrentGames);
        Assert.Equal(ServerRegistry.MaxGamesPerServer, dto.MaxGames);
    }

    [Fact]
    public void Ordinary_counts_are_untouched()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new ServerRegistry(time);
        var listed = registry.Register(new RegisterRequest
        {
            Name = "Normal", Address = "127.0.0.1", Port = 6073, CoreFile = "PCore001.igc", Version = "1.0.0",
            PlayerCount = 7, MaxPlayers = 64, CurrentGames = 2, MaxGames = 20,
        })!;
        var dto = registry.Get(listed.Id)!;
        Assert.Equal(7, dto.PlayerCount);
        Assert.Equal(64, dto.MaxPlayers);
        Assert.Equal(2, dto.CurrentGames);
        Assert.Equal(20, dto.MaxGames);
        // Native percent load (2*5 + 7)/300 = 0.
        Assert.Equal(0, dto.PercentLoad);
    }

    [Fact]
    public void Stats_sums_stay_in_range_with_absurd_listings()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new ServerRegistry(time);
        for (int port = 6073; port < 6075; port++)
            Assert.NotNull(registry.Register(new RegisterRequest
            {
                Name = "Absurd " + port, Address = "127.0.0." + (port - 6072), Port = port,
                CoreFile = "PCore001.igc", Version = "1.0.0", PlayerCount = int.MaxValue, MaxPlayers = int.MaxValue,
            }));
        var stats = registry.Stats();
        Assert.Equal(2, stats.Servers);
        Assert.Equal(2 * ServerRegistry.MaxPlayerCount, stats.Players);
        Assert.True(stats.PercentLoad >= 0);
    }

    [Fact]
    public async Task The_stats_endpoint_answers_200_for_absurd_listings()
    {
        using var app = new LobbyTestApp();
        using var http = app.CreateClient();
        foreach (int port in new[] { 6073, 6074 })
        {
            using var post = await http.PostAsJsonAsync("/api/servers/register", new
            {
                name = "Absurd " + port, address = "1.2.3." + (port - 6072), port,
                playerCount = int.MaxValue, maxPlayers = int.MaxValue, currentGames = int.MaxValue, maxGames = int.MaxValue,
                missionName = "R1", coreFile = "PCore001.igc", version = "0.1.0",
            });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        }
        using var stats = await http.GetAsync("/api/servers/stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        var body = (await stats.Content.ReadFromJsonAsync<LobbyStatsDto>())!;
        Assert.Equal(2, body.Servers);
        Assert.Equal(2 * ServerRegistry.MaxPlayerCount, body.Players);
    }
}

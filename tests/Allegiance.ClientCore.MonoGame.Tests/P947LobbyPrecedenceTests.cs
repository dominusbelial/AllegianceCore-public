using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Chat;
using Allegiance.ClientCore.MonoGame.Career;
using Allegiance.ClientCore.MonoGame.Games;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P947 — an explicit <c>--lobby</c> must win over the saved connect profile
/// and the saved player.json preferences. P871b fixed the preferences override
/// (<c>Game1.IntroScreen.cs</c>) but the connect-profile load in the
/// <c>Game1</c> constructor still overwrote the command line unconditionally —
/// the P936 internet-test failure (both clients started with
/// <c>--lobby https://&lt;vps&gt;</c>, the host internet form dialled
/// <c>http://127.0.0.1:5000</c>, "Connection refused").
///
/// Precedence under test: explicit <c>--lobby</c> (this session) &gt; saved
/// connect profile / preferences &gt; <see cref="ServerListing.LobbyUrlDefault"/>.
/// One test per consumer found in the P947 trace: the host internet form
/// (<c>HostLanScreenModel.UseInternetLobby</c>), the Play Online server fetch
/// (<c>FetchServersAsync</c>), the directory chat relay
/// (<c>UpdateDirectoryChat</c>), the career screen clients
/// (<c>EnterCareerScreen</c>), the F2 identity client
/// (<c>UpdateIdentity</c>), the new-game wizard
/// (<c>EnterCreateGame</c> → <see cref="LobbyGameClient"/>) and the P944
/// internet join target (<c>ConfigureClientTransport</c>).
/// All headless: uninitialized <see cref="Game1"/> + reflection, and loopback
/// URLs only (nothing here contacts a real host).
/// </summary>
public sealed class P947LobbyPrecedenceTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    // Loopback stand-ins for the four tiers (distinct ports, never contacted
    // except by the loopback-refusing identity start below).
    private const string ExplicitUrl = "http://127.0.0.1:6101";
    private const string ProfileUrl = "http://127.0.0.1:6102";
    private const string PrefsUrl = "http://127.0.0.1:6103";
    private const string DefaultUrl = "http://127.0.0.1:5000";   // ServerListing.LobbyUrlDefault

    private static void Set(Game1 game, string name, object? value) =>
        (typeof(Game1).GetField(name, Hidden) ?? throw new InvalidOperationException(name)).SetValue(game, value);

    private static T Get<T>(Game1 game, string name) =>
        (T)(typeof(Game1).GetField(name, Hidden) ?? throw new InvalidOperationException(name)).GetValue(game)!;

    private static object? Call(Game1 game, string name, params object?[] args) =>
        typeof(Game1).GetMethods(Hidden).Single(m => m.Name == name).Invoke(game, args);

    // ------------------------------------------------------------------
    // LobbyUrl.Resolve — the precedence the constructor now applies.
    // ------------------------------------------------------------------

    [Fact]
    public void An_explicit_lobby_beats_the_saved_profile_and_preferences()
    {
        var (url, source) = LobbyUrl.Resolve(ExplicitUrl, commandLineExplicit: true, ProfileUrl, PrefsUrl);
        Assert.Equal(ExplicitUrl, url);
        Assert.Equal(LobbyUrl.FromCommandLine, source);
    }

    [Fact]
    public void Without_the_command_line_the_saved_profile_wins()
    {
        var (url, source) = LobbyUrl.Resolve(DefaultUrl, commandLineExplicit: false, ProfileUrl, PrefsUrl);
        Assert.Equal(ProfileUrl, url);
        Assert.Equal(LobbyUrl.FromSaved, source);
    }

    [Fact]
    public void Without_the_command_line_and_profile_the_saved_preferences_win()
    {
        var (url, source) = LobbyUrl.Resolve(DefaultUrl, commandLineExplicit: false, profileUrl: null, PrefsUrl);
        Assert.Equal(PrefsUrl, url);
        Assert.Equal(LobbyUrl.FromSaved, source);
    }

    [Fact]
    public void With_nothing_saved_the_built_in_default_is_used()
    {
        var (url, source) = LobbyUrl.Resolve(DefaultUrl, commandLineExplicit: false, profileUrl: null, prefsUrl: null);
        Assert.Equal(DefaultUrl, url);
        Assert.Equal(LobbyUrl.FromDefault, source);
    }

    [Fact]
    public void An_empty_saved_profile_stays_empty_desktop_preferences_are_not_consulted()
    {
        // Android (connect store present): an empty profile LobbyUrl means
        // "manual connect only" — the constructor feeds no preferences tier.
        var (url, _) = LobbyUrl.Resolve(commandLineUrl: "", commandLineExplicit: false, profileUrl: "", prefsUrl: null);
        Assert.Equal("", url);
    }

    [Fact]
    public void An_explicit_blank_lobby_stays_pinned_manual_only()
    {
        // `--lobby=` (blank) means "no lobby this session"; neither saved tier
        // may re-enable polling behind the player's back.
        var (url, source) = LobbyUrl.Resolve("", commandLineExplicit: true, ProfileUrl, PrefsUrl);
        Assert.Equal("", url);
        Assert.Equal(LobbyUrl.FromDefault, source);
    }

    [Fact]
    public void The_startup_log_labels_each_tier()
    {
        Assert.Equal(LobbyUrl.FromCommandLine, LobbyUrl.DescribeSource(ExplicitUrl, commandLineExplicit: true));
        Assert.Equal(LobbyUrl.FromSaved, LobbyUrl.DescribeSource(ProfileUrl, commandLineExplicit: false));
        Assert.Equal(LobbyUrl.FromDefault, LobbyUrl.DescribeSource(DefaultUrl, commandLineExplicit: false));
        Assert.Equal(LobbyUrl.FromDefault, LobbyUrl.DescribeSource("", commandLineExplicit: false));
    }

    [Fact]
    public void The_empty_resolved_url_falls_back_to_the_default_for_lobby_backed_features()
    {
        Assert.Equal(ServerListing.LobbyUrlDefault, DefaultUrl);   // keep the stand-in honest
        Assert.Equal(DefaultUrl, LobbyUrl.EffectiveUrl(""));
        Assert.Equal(ProfileUrl, LobbyUrl.EffectiveUrl(ProfileUrl));
        Assert.Equal("(none)", LobbyUrl.Display(""));
    }

    // ------------------------------------------------------------------
    // Consumer 1 — the host internet form (Game1.IntroScreen.cs →
    // HostLanScreenModel.UseInternetLobby, shown by Game1.HostLan.cs).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, ExplicitUrl)]
    [InlineData(2, ProfileUrl)]
    [InlineData(3, DefaultUrl)]
    public void The_host_internet_form_shows_the_resolved_lobby(int scenario, string expected)
    {
        string resolved = Resolve(scenario);
        var model = new HostLanScreenModel();
        model.UseInternetLobby(LobbyUrl.EffectiveUrl(resolved));
        Assert.True(model.Options.HostInternet);
        Assert.Equal(expected, model.Options.InternetLobbyUrl);
    }

    // ------------------------------------------------------------------
    // Consumer 2 — the Play Online server fetch (Game1.Session.cs
    // FetchServersAsync over the _http base built from the resolved URL).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, ExplicitUrl)]
    [InlineData(2, ProfileUrl)]
    [InlineData(3, DefaultUrl)]
    public async Task The_play_online_fetch_polls_the_resolved_lobby(int scenario, string expected)
    {
        var recorder = new Recorder();
        string lobbyBase = LobbyUrl.EffectiveUrl(Resolve(scenario));
        var http = new HttpClient(recorder)
        {
            BaseAddress = new Uri(lobbyBase.EndsWith('/') ? lobbyBase : lobbyBase + "/", UriKind.Absolute),
        };
        var game = HeadlessGame();
        Set(game, "_http", http);
        Set(game, "_lobbyUrl", lobbyBase);

        await (Task)Call(game, "FetchServersAsync")!;

        lock (recorder.Requests)
        {
            Assert.NotEmpty(recorder.Requests);
            Assert.All(recorder.Requests, r => Assert.Equal(new Uri(expected).Authority, r.Host));
            Assert.Contains(recorder.Requests, r => r.PathAndQuery == "/api/servers");
        }
    }

    // ------------------------------------------------------------------
    // Consumer 3 — the directory chat relay (Game1.DirectoryChat.cs).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, ExplicitUrl)]
    [InlineData(2, ProfileUrl)]
    [InlineData(3, DefaultUrl)]
    public async Task The_directory_chat_relay_polls_the_resolved_lobby(int scenario, string expected)
    {
        var recorder = new Recorder();
        var game = HeadlessGame(new HttpClient(recorder));
        Set(game, "_lobbyUrl", Resolve(scenario));

        Call(game, "UpdateDirectoryChat");
        var inFlight = Get<Task?>(game, "_directoryChatPollInFlight");
        Assert.NotNull(inFlight);
        await inFlight!;

        lock (recorder.Requests)
        {
            Assert.NotEmpty(recorder.Requests);
            Assert.All(recorder.Requests, r => Assert.Equal(new Uri(expected).Authority, r.Host));
        }
    }

    // ------------------------------------------------------------------
    // Consumer 4 — the career screen clients (Game1.Career.cs
    // EnterCareerScreen builds CareerApiClient/SquadApiClient/EventApiClient
    // from the resolved origin).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, ExplicitUrl)]
    [InlineData(2, ProfileUrl)]
    [InlineData(3, DefaultUrl)]
    public void The_career_screen_clients_target_the_resolved_lobby(int scenario, string expected)
    {
        var game = HeadlessGame();
        Set(game, "_events", new EventsScreenModel());
        Set(game, "_squads", new SquadScreenModel());
        Set(game, "_lobbyUrl", Resolve(scenario));

        Call(game, "EnterCareerScreen");

        Assert.Equal(expected, Get<string>(game, "_careerOrigin"));
        Assert.NotNull(Get<object?>(game, "_careerClient"));
        Assert.NotNull(Get<object?>(game, "_squadClient"));
        Assert.NotNull(Get<object?>(game, "_eventClient"));
    }

    // ------------------------------------------------------------------
    // Consumer 5 — the F2 identity client (Game1.Identity.cs
    // UpdateIdentity builds LobbyIdentityClient from the resolved origin).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, ExplicitUrl)]
    [InlineData(2, ProfileUrl)]
    [InlineData(3, DefaultUrl)]
    public void The_identity_client_targets_the_resolved_lobby(int scenario, string expected)
    {
        var game = HeadlessGame();
        Set(game, "_identityScreen", new IdentityScreenModel());
        Set(game, "_lobbyUrl", Resolve(scenario));

        // F2 press: the origin is captured synchronously before the (loopback,
        // refusing) device-start request leaves — enough to pin the wiring.
        Call(game, "UpdateIdentity", new KeyboardState(Keys.F2));

        Assert.Equal(expected, Get<string>(game, "_identityOrigin"));
        Assert.NotNull(Get<object?>(game, "_identityTask"));
    }

    // ------------------------------------------------------------------
    // Consumer 6 — the new-game wizard (Game1.CreateGame.cs EnterCreateGame
    // builds LobbyGameClient from the resolved _lobbyUrl).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, ExplicitUrl)]
    [InlineData(2, ProfileUrl)]
    [InlineData(3, DefaultUrl)]
    public async Task The_new_game_wizard_catalog_comes_from_the_resolved_lobby(int scenario, string expected)
    {
        var recorder = new Recorder { Json = "{}" };
        var game = HeadlessGame(new HttpClient(recorder));
        Set(game, "_createGame", new CreateGameWizard());
        Set(game, "_createGameLock", new object());
        Set(game, "_lobbyUrl", Resolve(scenario));

        Call(game, "EnterCreateGame");

        var client = Get<object?>(game, "_gameClient");
        Assert.NotNull(client);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (recorder.Requests)
                if (recorder.Requests.Any(r => r.PathAndQuery == "/api/games/catalog"))
                    break;
            await Task.Delay(20);
        }

        lock (recorder.Requests)
        {
            var catalog = Assert.Single(recorder.Requests, r => r.PathAndQuery == "/api/games/catalog");
            Assert.Equal(new Uri(expected).Authority, catalog.Host);
        }
    }

    // ------------------------------------------------------------------
    // Consumer 7 — the P944 internet join target (Game1.Session.cs
    // ConfigureClientTransport records the resolved lobby for the traversal).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, ExplicitUrl)]
    [InlineData(2, ProfileUrl)]
    [InlineData(3, DefaultUrl)]
    public void The_player_hosted_join_target_carries_the_resolved_lobby(int scenario, string expected)
    {
        var game = HeadlessGame();
        Set(game, "_session", new ConnectionSession());
        var listing = new ServerListing("ph1", "Player Host", "203.0.113.7", 6073,
            PlayerCount: 1, MaxPlayers: 20, MissionName: "m", CoreFile: "PCore001.igc", Version: "1",
            RegisteredUtc: DateTimeOffset.UnixEpoch, LastSeenUtc: DateTimeOffset.UnixEpoch, PlayerHosted: true);
        Set(game, "_connectListingTarget", listing);
        Set(game, "_lobbyUrl", Resolve(scenario));

        Call(game, "ConfigureClientTransport", "203.0.113.7", (ushort)6073, 0u, null);

        var session = Get<ConnectionSession>(game, "_session");
        Assert.Equal(new InternetJoin(expected, "ph1"), session.InternetJoinTarget);
        Assert.Equal("udp", session.TransportProtocol);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    /// <summary>The three precedence scenarios, as the Game1 constructor
    /// resolves them via <see cref="LobbyUrl.Resolve"/>: (1) explicit
    /// <c>--lobby</c> with a saved profile AND preferences holding different
    /// lobbies; (2) no <c>--lobby</c>, saved values present; (3) nothing
    /// saved.</summary>
    private static string Resolve(int scenario) => scenario switch
    {
        1 => LobbyUrl.Resolve(ExplicitUrl, commandLineExplicit: true, ProfileUrl, PrefsUrl).Url,
        2 => LobbyUrl.Resolve(DefaultUrl, commandLineExplicit: false, ProfileUrl, PrefsUrl).Url,
        _ => LobbyUrl.Resolve(DefaultUrl, commandLineExplicit: false, profileUrl: null, prefsUrl: null).Url,
    };

    /// <summary>A headless Game1 with no session (intro-side state).</summary>
    private static Game1 HeadlessGame(HttpClient? http = null)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        if (http is not null) Set(game, "_http", http);
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_chatBuffer", new System.Text.StringBuilder());
        Set(game, "_directoryChat", new DirectoryChatLog());
        return game;
    }

    /// <summary>Records every request; answers an empty body.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string PathAndQuery, string Host)> Requests = new();
        public string Json = "[]";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string json = request.Content is null
                ? Json
                : await request.Content.ReadAsStringAsync(ct);
            lock (Requests)
                Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.RequestUri.Authority));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}

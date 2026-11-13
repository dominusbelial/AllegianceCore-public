using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1004LobbyBrowserTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static GameParams Params(bool slots, int cap = 200) => new(true, 0x7fff, true, true, true,
        true, false, false, false, false, false, false, -1, 1000,
        MaxPlayersPerGame: cap, AnySlotsAvailable: slots, GuaranteedSlotsAvailable: slots);
    private static ServerListing Listing(bool slots = true, int players = 0, bool password = false) => new(
        "id", "Game", "127.0.0.1", 6073, players, 200, "Game", "Core", "1",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Game: Params(slots), PasswordRequired: password);

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Protected_full_and_no_slot_games_remain_visible_and_have_truthful_status(HudStyle style)
    {
        _ = style; // shared model consumed by DrawLobby in either style
        var guarded = Listing(password: true);
        var full = Listing(false, players: 200);
        var locked = Listing(false);
        var filters = new LobbyGameFilters();
        foreach (var listing in new[] { guarded, full, locked }) Assert.True(filters.Apply(listing));
        Assert.True(guarded.HasOpenTeamSlots);
        Assert.Contains("[password required] [team slots open]", guarded.FormatRow());
        Assert.Contains("0/200", guarded.FormatRow());
        Assert.Contains("[full]", full.FormatRow());
        Assert.Contains("[no team slots]", locked.FormatRow());
        Assert.True(LobbyGameSort.ListingKey(guarded, LobbyGameSort.ColStatus).CompareTo(LobbyGameSort.ListingKey(locked, LobbyGameSort.ColStatus)) < 0);
        Assert.Null(LobbyBrowserStatus.ListingMessage([guarded], [0]));
        Assert.Contains("no open team slots", LobbyBrowserStatus.ListingMessage([full, locked], [0, 1]));
        Assert.Contains("ENTER requests entry", LobbyBrowserStatus.ListingMessage([locked], [0]));
        Assert.Contains("filters", LobbyBrowserStatus.ListingMessage([guarded], []));
        Assert.Contains("No games are currently listed", LobbyBrowserStatus.ListingMessage([], []));
        // Old servers without P866 metadata retain unknown/unbounded capacity.
        Assert.True((guarded with { Game = null, MaxPlayers = 0 }).HasOpenTeamSlots);
        Assert.Contains("[slots unknown]", (guarded with { Game = null, MaxPlayers = 0 }).FormatRow());
        Assert.False((full with { Game = null }).HasOpenTeamSlots);
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Expanding_multiple_games_uses_each_missions_capacity_and_password(HudStyle style)
    {
        _ = style;
        var server = Listing() with { MaxPlayers = 212, PasswordRequired = true,
            Missions = [new(1, "Guarded", 0, Params(true), true), new(2, "Full", 12, Params(false, 12), false)] };
        var rows = ServerListing.ExpandMissions([server]).ToArray();
        Assert.Equal(200, rows[0].MaxPlayers);
        Assert.Equal(12, rows[1].MaxPlayers);
        Assert.True(rows[0].PasswordRequired);
        Assert.False(rows[1].PasswordRequired);
        Assert.Contains("12/12", rows[1].FormatRow());
        Assert.Contains("[full]", rows[1].FormatRow());
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Enter_on_a_protected_browser_selection_prompts_and_required_password_enter_requests_join(HudStyle style)
    {
        var game = BareGame(style);
        Set(game, "_displayServers", new List<ServerListing> { Listing(password: true) });
        Set(game, "_visibleRows", new List<int> { 0 });
        Call(game, "TryJoinSelected");
        Assert.True(Get<bool>(game, "_editingLanPassword"));
        Assert.Contains("requires a password", Get<string>(game, "_lobbyError"));
        Assert.Equal("Lobby", Get<object>(game, "_state").ToString());
        Set(game, "_enterWasDown", true); // the key that opened the prompt is still held
        Call(game, "HandleLobbyPassword", new KeyboardState(Keys.Enter), false);
        Assert.True(Get<bool>(game, "_editingLanPassword"));
        Call(game, "HandleLobbyPassword", default(KeyboardState), false);
        // Use an invalid-port sentinel to observe TryJoinSelected being reached
        // without opening a socket in this headless UI unit test.
        Get<List<ServerListing>>(game, "_displayServers")[0] = Listing(password: true) with { Port = 0 };
        Get<StringBuilder>(game, "_lanJoinPassword").Append("fixture");
        Call(game, "HandleLobbyPassword", new KeyboardState(Keys.Enter), false);
        Assert.False(Get<bool>(game, "_editingLanPassword"));
        Assert.Contains("no valid port", Get<string>(game, "_lobbyError"));
        Assert.True(Get<bool>(game, "_enterWasDown")); // held Enter cannot double-submit
        // An optional password retains the existing two-step editing behavior.
        Set(game, "_editingLanPassword", true);
        Set(game, "_enterWasDown", false);
        Set(game, "_lobbyError", "sentinel");
        Get<List<ServerListing>>(game, "_displayServers")[0] = Listing() with { Port = 0 };
        Call(game, "HandleLobbyPassword", new KeyboardState(Keys.Enter), false);
        Assert.Equal("sentinel", Get<string>(game, "_lobbyError"));
    }

    [Theory]
    [InlineData("down", "Cannot reach the lobby")]
    [InlineData("timeout", "timed out")]
    [InlineData("http", "Lobby returned HTTP 503")]
    [InlineData("invalid", "invalid server list")]
    [InlineData("null", "invalid server list")]
    [InlineData("empty", null)]
    [InlineData("password", null)]
    public async Task Real_fetch_distinguishes_directory_reply_from_transport_failure(string scenario, string? expected)
    {
        var game = BareGame(HudStyle.Classic);
        using var handler = new FetchHandler(scenario);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        Set(game, "_http", http);
        Set(game, "_lobbyUrl", "http://localhost");
        await (Task)Call(game, "FetchServersAsync")!;
        Assert.True(Get<bool>(game, "_lobbyResultPending"));
        Assert.False(Get<bool>(game, "_lobbyFetchInFlight"));
        var error = Get<string?>(game, "_lobbyResultError");
        if (expected is null)
        {
            Assert.Null(error);
            var data = Get<List<ServerListing>>(game, "_lobbyResultData");
            if (scenario == "password") Assert.True(Assert.Single(data).PasswordRequired);
            else Assert.Empty(data);
        }
        else Assert.Contains(expected, error);
    }

    private sealed class FetchHandler(string scenario) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/cores", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (scenario == "down") throw new HttpRequestException("Connection refused");
            if (scenario == "timeout") throw new TaskCanceledException();
            return Task.FromResult(new HttpResponseMessage(scenario == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(scenario switch
                {
                    "invalid" => "{not-json", "null" => "null",
                    "password" => System.Text.Json.JsonSerializer.Serialize(new[] { Listing(password: true) }), _ => "[]",
                }, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static Game1 BareGame(HudStyle style)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_lanJoinPassword", new StringBuilder());
        Set(game, "_state", Enum.Parse(typeof(Game1).GetField("_state", Hidden)!.FieldType, "Lobby"));
        return game;
    }
    private static void Set(Game1 game, string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private static object? Call(Game1 game, string method, params object[] args) => typeof(Game1).GetMethod(method, Hidden)!.Invoke(game, args);
}

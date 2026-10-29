using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Allegiance.ClientCore.MonoGame.Chat;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P871b — the P871 review fixes for the lobby-directory chat slice:
/// <list type="bullet">
/// <item>the relay is polled from the TOP-LEVEL frame update, so it runs on the
/// intro/browser/lobby screens too (P871 only polled inside the in-flight
/// update);</item>
/// <item>posts and mutes carry the signed-in identity bearer (P871 sent none, so
/// the lobby answered 401);</item>
/// <item>the per-player mute has a real UI (the native context-menu toggle's
/// analogue on the Options → Chat page, plus the native
/// <c>!mute</c>/<c>!unmute</c> shell command);</item>
/// <item>a directory line is no longer cut off when the game chat already fills
/// the pane's <see cref="ChatRenderer.MaxVisible"/> window.</item>
/// </list>
/// Native has no directory chat at all (<c>Igc/igc.h:2731-2734</c>: every chat
/// target is in-mission), so the channel itself is a documented port extension
/// and only the borrowed relay/mute/echo rules are native's.
/// </summary>
public sealed class P871bDirectoryChatTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Set(Game1 game, string name, object value) =>
        (typeof(Game1).GetField(name, Hidden)
         ?? throw new InvalidOperationException($"no field {name}")).SetValue(game, value);

    private static object? Call(Game1 game, string name, params object[] args) =>
        (typeof(Game1).GetMethod(name, Hidden)
         ?? throw new InvalidOperationException($"no method {name}")).Invoke(game, args);

    /// <summary>A headless Game1 with NO session: the intro/browser state.</summary>
    private static Game1 HeadlessIntro(HttpClient http)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_http", http);
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_chatBuffer", new StringBuilder());
        Set(game, "_directoryChat", new DirectoryChatLog());
        return game;
    }

    private static Game1 HeadlessFlying(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_moderation", new ModerationModel());
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_chatBuffer", new StringBuilder());
        Set(game, "_chatRecipient", new ChatSubjects());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        return game;
    }

    /// <summary>Records every request and answers a scripted JSON body.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Uri, string? Auth, string? Body)> Requests = new();
        public string Json = "[]";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests)
                Requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task The_directory_relay_polls_on_the_intro_screens_without_a_session()
    {
        // P871 called UpdateDirectoryChat from UpdateFlying only, so a player on
        // the intro / server-browser / lobby screens never saw directory chat.
        // The relay is now driven from the top-level frame update; this test keeps
        // the method itself session-free.
        var recorder = new Recorder();
        var game = HeadlessIntro(new HttpClient(recorder));
        Assert.Null(typeof(Game1).GetField("_session", Hidden)!.GetValue(game));

        Call(game, "UpdateDirectoryChat");
        var inFlight = (Task?)typeof(Game1).GetField("_directoryChatPollInFlight", Hidden)!.GetValue(game);
        Assert.NotNull(inFlight);
        await inFlight!;
        Call(game, "UpdateDirectoryChat");   // second call is inside the poll window

        lock (recorder.Requests)
        {
            var get = Assert.Single(recorder.Requests);
            Assert.Equal(HttpMethod.Get, get.Method);
            Assert.StartsWith("/api/chat?channel=directory&since=", get.Uri);
        }
    }

    [Fact]
    public async Task Every_mutating_directory_call_carries_the_identity_bearer()
    {
        // P871's LobbyChatClient sent no Authorization header, so the lobby's
        // IdentityEndpoints gate answered 401 for every post and mute.
        var recorder = new Recorder { Json = "{\"sequence\":7}" };
        var client = new Chat.LobbyChatClient(new HttpClient(recorder), "http://127.0.0.1:5000",
            bearer: () => "session-token");

        var sequence = await client.SendAsync("hello lobby");
        Assert.Equal(7, sequence);
        Assert.True(await client.MuteAsync("acct-bob"));
        Assert.True(await client.UnmuteAsync("acct-bob"));
        await client.PollAsync(new DirectoryChatLog(), ownAccountId: null);

        lock (recorder.Requests)
        {
            Assert.Equal(4, recorder.Requests.Count);
            Assert.All(recorder.Requests, r => Assert.Equal("Bearer session-token", r.Auth));
            Assert.Contains(recorder.Requests, r => r.Method == HttpMethod.Post && r.Uri == "/api/chat");
            Assert.Contains(recorder.Requests, r => r.Uri == "/api/chat/mute" && r.Body!.Contains("acct-bob"));
            Assert.Contains(recorder.Requests, r => r.Uri == "/api/chat/unmute");
        }
    }

    [Fact]
    public void A_muted_directory_sender_is_dropped_from_the_window()
    {
        // Native drops a muted player's line before it reaches the pane
        // (trekigc.cpp:4492: ppi->GetMute()), PlayerInfo::SetMute being the
        // per-player client-side mute (clintlib/clintlib.h:282,326).
        var log = new DirectoryChatLog();
        var at = DateTimeOffset.UnixEpoch;
        log.Accept([
            new DirectoryChatLine(1, "directory", "acct-bob", "Bob", "before", at),
            new DirectoryChatLine(2, "directory", "acct-eve", "Eve", "eve line", at),
        ], ownAccountId: null);
        Assert.Equal(2, log.Lines.Count);

        log.Mute("acct-bob");
        log.Accept([
            new DirectoryChatLine(3, "directory", "acct-bob", "Bob", "after", at),
            new DirectoryChatLine(4, "directory", "acct-eve", "Eve", "again", at),
        ], ownAccountId: null);

        Assert.DoesNotContain(log.Lines, l => l.Text == "after");
        Assert.Contains(log.Lines, l => l.Text == "again");
        Assert.True(log.IsMuted("acct-bob"));
        log.Unmute("acct-bob");
        Assert.False(log.IsMuted("acct-bob"));
    }

    [Fact]
    public void The_options_chat_page_offers_the_native_mute_toggle()
    {
        // Native's only mute UI is the player context menu's 'M' item
        // (idmContextMutePlayer, WinTrek.cpp:4310 -> contextMute at :3731-3734),
        // which toggles PlayerInfo::SetMute. The port has no context menu on a
        // chat line, so the same toggle lives on the Chat/Mute/Filter page and
        // acts on the newest directory sender.
        var menu = new OptionsMenuModel();
        menu.Reset();
        Assert.Equal(OptionsMenuModel.Page.Root, menu.Current);
        var chat = OpenChatPage(menu, EmptyState());

        var nobody = chat.Single(r => r.Command == OptionsMenuModel.OptionsCommand.ToggleMuteChatSender);
        Assert.Equal("Mute (no directory chat yet)", nobody.Label);
        Assert.Equal('M', nobody.Hotkey);

        var withTarget = new OptionsMenuModel.OptionsState(
            MusicOn: false, FilterChatsToAll: true, LinearControls: false, FlipY: false,
            VirtualJoystick: false, Deadzone: 0f, ChatMuteTarget: "Bob", ChatMuteTargetMuted: false);
        var rows = PageRows(menu, withTarget);
        var muteRow = rows.Single(r => r.Command == OptionsMenuModel.OptionsCommand.ToggleMuteChatSender);
        Assert.Equal("Mute Bob", muteRow.Label);

        var muted = withTarget with { ChatMuteTargetMuted = true };
        Assert.Equal("Unmute Bob", PageRows(menu, muted)
            .Single(r => r.Command == OptionsMenuModel.OptionsCommand.ToggleMuteChatSender).Label);
    }

    [Fact]
    public void A_directory_line_survives_a_game_chat_log_that_fills_the_window()
    {
        // P871 appended directory lines only `while (lines.Count < MaxVisible)`,
        // so on a busy pane (>= 7 game lines, ChatRenderer.MaxVisible) every
        // directory line was dropped. The window now trims the OLDEST overflow.
        using var session = new ConnectionSession();
        var world = session.World;
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "pilot" });
        for (int i = 0; i < ChatRenderer.MaxVisible + 5; i++)
            world.Apply(new ChatMessage { Message = $"game line {i}", ChatTarget = (int)ChatTarget.Team, SidSender = 10 });

        var game = HeadlessFlying(session);
        var log = new DirectoryChatLog();
        log.Accept([
            new DirectoryChatLine(1, "directory", "acct-bob", "Bob", "directory line one", DateTimeOffset.UnixEpoch),
            new DirectoryChatLine(2, "directory", "acct-eve", "Eve", "directory line two", DateTimeOffset.UnixEpoch),
        ], ownAccountId: null);
        Set(game, "_directoryChat", log);

        var lines = (List<string>)Call(game, "BuildChatLines")!;
        Assert.Equal(ChatRenderer.MaxVisible, lines.Count);
        Assert.Contains(lines, l => l.Contains("directory line one"));
        Assert.Contains(lines, l => l.Contains("directory line two"));
        // The oldest game lines are the ones the window gave up.
        Assert.DoesNotContain(lines, l => l.Contains("game line 0"));
        Assert.Contains(lines, l => l.Contains($"game line {ChatRenderer.MaxVisible + 4}"));
    }

    private static OptionsMenuModel.OptionsState EmptyState() => new(
        MusicOn: false, FilterChatsToAll: false, LinearControls: false, FlipY: false,
        VirtualJoystick: false, Deadzone: 0f);

    private static IReadOnlyList<OptionsMenuModel.Row> OpenChatPage(OptionsMenuModel menu,
        OptionsMenuModel.OptionsState state)
    {
        menu.Reset();
        // root: 0 Graphics | 1 Sound | 2 Game | 3 Map Keys
        menu.Row0 = 2;
        Assert.Null(menu.Activate(state));                       // into Game
        Assert.Equal(OptionsMenuModel.Page.Game, menu.Current);
        // game: 0 Chat/Mute/Filter | …
        menu.Row0 = 0;
        Assert.Null(menu.Activate(state));                       // into Chat
        Assert.Equal(OptionsMenuModel.Page.Chat, menu.Current);
        return menu.Rows(state);
    }

    private static IReadOnlyList<OptionsMenuModel.Row> PageRows(OptionsMenuModel menu,
        OptionsMenuModel.OptionsState state)
    {
        Assert.Equal(OptionsMenuModel.Page.Chat, menu.Current);
        return menu.Rows(state);
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using Xunit;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P757 — the chat pane rows: the history/autoscroll/channel-filter model
/// (native <c>WinTrek/chatpane.cpp</c>) and the production compose/send/focus
/// flow (native <c>TK_StartChat</c> + <c>UpdateComposedChat</c>).
/// </summary>
public sealed class P757ChatPaneTests
{
    // ── pure model: native ChatListPaneImpl ────────────────────────────────

    [Fact]
    public void NonCriticalMessage_rule_matches_native_chatpane_226_233()
    {
        var pane = new ChatPaneModel();
        Assert.False(pane.PlayerChatsOnly);   // port default: the pane shows all
        pane.SetPlayerChatsOnly(true);        // native default m_bPlayerChatsOnly
        // !fromPlayer && !fromObjectModel && target != IndividualNoFilter
        Assert.True(pane.IsNonCritical(false, false, ChatTarget.Team));
        Assert.False(pane.IsNonCritical(false, false, ChatTarget.IndividualNoFilter));
        Assert.False(pane.IsNonCritical(true, false, ChatTarget.Team));
        Assert.False(pane.IsNonCritical(false, true, ChatTarget.Team));
    }

    [Fact]
    public void PassesFilter_requires_selected_channel_when_any_selected()
    {
        var pane = new ChatPaneModel();
        // No channels selected: everything but non-critical passes (chatpane.cpp:307-310).
        Assert.True(pane.PassesFilter(false, false, ChatTarget.Team));
        pane.AddChannel(ChatTarget.Team);
        // Channels selected: only those channels pass (chatpane.cpp:311-320).
        Assert.True(pane.PassesFilter(false, false, ChatTarget.Team));
        Assert.False(pane.PassesFilter(false, false, ChatTarget.Everyone));
        pane.AddChannel(ChatTarget.Everyone);
        Assert.True(pane.PassesFilter(false, false, ChatTarget.Everyone));
        pane.RemoveChannel(ChatTarget.Team);
        Assert.False(pane.PassesFilter(false, false, ChatTarget.Team));
        pane.ResetChannels();
        Assert.True(pane.PassesFilter(false, false, ChatTarget.Everyone));
    }

    [Fact]
    public void Filter_change_rebuilds_and_snaps_to_bottom_with_autoscroll_on()
    {
        // Native AddChannel/RemoveChannel/ResetChannels -> UpdateContents:
        // ScrollBottom + AutoscrollOn (chatpane.cpp:286-297, 340-358).
        var pane = new ChatPaneModel();
        pane.PageUp(40, 8, nowSeconds: 0);
        Assert.False(pane.Autoscroll);
        Assert.True(pane.Offset > 0);
        pane.AddChannel(ChatTarget.Team);
        Assert.Equal(0, pane.Offset);
        Assert.True(pane.Autoscroll);
        Assert.Null(pane.AutoscrollResumeAt);
    }

    [Fact]
    public void PageUp_pages_older_and_arms_the_ten_second_resume_timer()
    {
        // Native PageUp: ScrollPageUp + AutoscrollOff (chatpane.cpp:255-259);
        // AutoscrollOff arms a 10 s timer (chatpane.cpp:173-183).
        var pane = new ChatPaneModel();
        pane.PageUp(40, 8, nowSeconds: 100.0);
        Assert.Equal(8, pane.Offset);
        Assert.False(pane.Autoscroll);
        Assert.Equal(110.0, pane.AutoscrollResumeAt);
        // Before the timer fires nothing changes.
        pane.Tick(109.9);
        Assert.False(pane.Autoscroll);
        Assert.Equal(8, pane.Offset);
        // OnAutoscrollTimeout: ScrollBottom + AutoscrollOn (chatpane.cpp:192-198).
        pane.Tick(110.0);
        Assert.True(pane.Autoscroll);
        Assert.Equal(0, pane.Offset);
        Assert.Null(pane.AutoscrollResumeAt);
    }

    [Fact]
    public void PageDown_returns_autoscroll_only_when_the_newest_line_is_visible()
    {
        // Native PageDown: scroll a page newer, then AutoscrollOn only when the
        // last item is visible (chatpane.cpp:261-270).
        var pane = new ChatPaneModel();
        pane.PageUp(40, 8, 0);
        pane.PageDown(40, 8, 0);
        Assert.Equal(0, pane.Offset);
        Assert.True(pane.Autoscroll);
        // Still away from the bottom: stay paused (OnScroll, chatpane.cpp:272-283).
        pane.PageUp(40, 8, 0);
        pane.PageUp(40, 8, 0);
        pane.PageDown(40, 8, 5.0);
        Assert.Equal(8, pane.Offset);
        Assert.False(pane.Autoscroll);
    }

    [Fact]
    public void New_message_scrolls_only_while_autoscroll_and_keeps_content_when_paused()
    {
        // Native AddChatItem appends and scrolls to bottom only when
        // m_bAutoscroll (chatpane.cpp:323-339); a paused pane keeps showing the
        // same lines, so the offset from the newest edge grows with the log.
        var pane = new ChatPaneModel();
        pane.OnMessagesAppended(30, 30, 8);
        Assert.Equal(0, pane.Offset);
        pane.PageUp(30, 8, 0);
        int topVisibleBefore = 30 - 8 - pane.Offset;
        pane.OnMessagesAppended(2, 32, 8);
        Assert.Equal(0, 32 - 8 - pane.Offset - topVisibleBefore);   // same top line
        Assert.False(pane.Autoscroll);
        // ... and a following pane still sits at the live edge.
        pane.Tick(11.0);
        pane.OnMessagesAppended(1, 33, 8);
        Assert.Equal(0, pane.Offset);
    }

    // ── production wiring: the Game1 chat pipeline ─────────────────────────

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static object? Call(Game1 game, string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private static Game1 Headless(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_moderation", new ModerationModel());
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_chatBuffer", new System.Text.StringBuilder());
        Set(game, "_chatTarget", ChatTargetLogic.DefaultTarget);
        // P761b — the handler now also drives the character-sheet profile
        // editor's Enter/Backspace edges, so its model must exist too.
        Set(game, "_pilotProfile", new Rendering.PilotInfoLayout.ProfileEditor());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        return game;
    }

    private sealed class Site : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        { foreach (var message in FedPacket.Unpack(bytes)) Received.Enqueue(message); }
    }

    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(5, timeout.Token);
    }

    private static void Type(Game1 game, string text)
    {
        foreach (var c in text)
            Call(game, "OnTextInput", null!, new Microsoft.Xna.Framework.TextInputEventArgs(c, XnaKeys.None));
    }

    [Fact]
    public async Task Compose_and_send_flow_through_the_production_handler_and_real_wire()
    {
        // Native TK_StartChat opens the composer and defocuses flight input
        // (WinTrek.cpp:9441-9446); the composed line is typed + "|" with the
        // recipient shown (console.cpp:1282-1338); Enter sends the line to the
        // cycled recipient. This drives the REAL production seam
        // (HandleChatCompose + OnTextInput + SendChatLine) over a real TCP
        // session and asserts the exact CHATMESSAGE that reaches the transport.
        var site = new Site();
        var host = new TcpTransport(site);
        using var session = new ConnectionSession();
        host.Host(0);
        session.Connect(host.BoundPort, "P757 chat");
        await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var game = Headless(session);

        // 1) Enter opens the composer (StartChat edge).
        Call(game, "HandleChatCompose", new KeyboardState(XnaKeys.Enter));
        Assert.True(Get<bool>(game, "_chatActive"));

        // 2) Typed text lands in the buffer through the real TextInput path.
        Type(game, "wave");
        Assert.Equal("wave", Get<System.Text.StringBuilder>(game, "_chatBuffer").ToString());

        // 3) Tab with typed text does NOT cycle (native OnTab, console.cpp:2747-2755).
        Call(game, "HandleChatCompose", new KeyboardState(XnaKeys.Tab));
        Assert.Equal(ChatTarget.Team, Get<ChatTarget>(game, "_chatTarget"));

        // 4) Backspace deletes the previous character (raw key, allow-listed).
        Call(game, "HandleChatCompose", new KeyboardState(XnaKeys.Back));
        Assert.Equal("wav", Get<System.Text.StringBuilder>(game, "_chatBuffer").ToString());
        Type(game, "e");

        // 5) Enter sends: the compose closes and a real CHATMESSAGE(23) with the
        //    current recipient + c_cidNone reaches the transport.
        Call(game, "HandleChatCompose", new KeyboardState(XnaKeys.Enter));
        Assert.False(Get<bool>(game, "_chatActive"));
        ChatMessage? sent = null;
        await Until(() =>
        {
            while (site.Received.TryDequeue(out var m))
                if (FedMessageHeader.Read(m).Fmid == ChatMessage.Fmid)
                    sent = ChatMessage.Decode(m);
            return sent is not null;
        });
        Assert.NotNull(sent);
        Assert.Equal("wave", sent!.Message);
        Assert.Equal((int)ChatTarget.Team, sent.ChatTarget);
        Assert.Equal(OrderChange.CidNone, sent.CommandID);

        // 6) A fresh compose cycles the recipient with an empty buffer, and the
        //    next send carries the cycled target (the recipient persists across
        //    compose sessions, native m_chsChat).
        Call(game, "HandleChatCompose", new KeyboardState());
        Call(game, "HandleChatCompose", new KeyboardState(XnaKeys.Enter));
        Call(game, "HandleChatCompose", new KeyboardState(XnaKeys.Tab));
        Assert.Equal(ChatTargetLogic.Next(ChatTarget.Team), Get<ChatTarget>(game, "_chatTarget"));
        Type(game, "hello");
        Call(game, "HandleChatCompose", new KeyboardState(XnaKeys.Enter));
        ChatMessage? sent2 = null;
        await Until(() =>
        {
            while (site.Received.TryDequeue(out var m))
                if (FedMessageHeader.Read(m).Fmid == ChatMessage.Fmid)
                    sent2 = ChatMessage.Decode(m);
            return sent2 is not null;
        });
        Assert.Equal((int)ChatTargetLogic.Next(ChatTarget.Team), sent2!.ChatTarget);
        Assert.Equal("hello", sent2.Message);
    }

    [Fact]
    public void P806_chat_pane_pipeline_uses_the_two_native_filters()
    {
        // P806 — the production pane config, not an Alt+C cycle. Native has TWO
        // independent filters (chatpane.cpp):
        //   * m_bPlayerChatsOnly — drops non-player chatter, everywhere;
        //   * the LOBBY channel list, set from the chat-target buttons
        //     (teamscreen.cpp:3085-3118) and EMPTY in flight (console.cpp:3439-3442).
        using var session = new ConnectionSession();
        var world = session.World;
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "pilot" });
        world.Apply(new ChatMessage { Message = "player line", ChatTarget = (int)ChatTarget.Team, SidSender = 10 });
        world.Apply(new ChatMessage { Message = "drone chatter", ChatTarget = (int)ChatTarget.Team, SidSender = 77 });
        world.Apply(new ChatMessage { Message = "everyone line", ChatTarget = (int)ChatTarget.Everyone, SidSender = 10 });
        var game = Headless(session);

        // IN FLIGHT, players-only off: every line, no channel restriction.
        Set(game, "_chatPlayerChatsOnly", false);
        Call(game, "ApplyChatPaneFilter");
        Assert.Equal(3, Call(game, "FilteredChatCount"));
        var lines = Call(game, "BuildChatLines") as List<string>;
        Assert.NotNull(lines);
        Assert.Equal(3, lines!.Count);

        // IN FLIGHT, players-only on (native chatpane.cpp:146): the drone line
        // (no character name, not an object model) is non-critical and drops.
        Set(game, "_chatPlayerChatsOnly", true);
        Call(game, "ApplyChatPaneFilter");
        Assert.Equal(2, Call(game, "FilteredChatCount"));
        lines = Call(game, "BuildChatLines") as List<string>;
        Assert.DoesNotContain(lines!, l => l.Contains("drone chatter"));
        Assert.Contains(lines!, l => l.Contains("player line"));
        // ...and the in-flight pane still has NO channels (native console pane).
        Assert.Empty(Get<ChatPaneModel>(game, "_chatPane").Channels);

        // TEAM LOBBY (ReadyCheck), native OnButtonBarChat with the Team button:
        // ResetChannels + [Team, Individual, IndividualNoFilter, Admin] — the
        // everyone line drops out, team/individual/admin stay.
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "ReadyCheck"));
        Set(game, "_lobbyChatTarget", ChatTarget.Team);
        Set(game, "_chatPlayerChatsOnly", false);
        Call(game, "ApplyChatPaneFilter");
        lines = Call(game, "BuildChatLines") as List<string>;
        Assert.DoesNotContain(lines!, l => l.Contains("everyone line"));
        Assert.Contains(lines!, l => l.Contains("player line"));
        Assert.Equal(
            new[] { ChatTarget.Team, ChatTarget.Individual, ChatTarget.IndividualNoFilter, ChatTarget.Admin }
                .OrderBy(c => c).ToArray(),
            Get<ChatPaneModel>(game, "_chatPane").Channels.OrderBy(c => c).ToArray());
    }

    [Fact]
    public void P806_filter_chats_to_all_drops_everyone_lines_in_game()
    {
        // Native m_bFilterChatsToAll (WinTrek.cpp:3271-3272, default true) drops a
        // CHAT_EVERYONE message while in game (ReceiveChat, trekigc.cpp:4493).
        using var session = new ConnectionSession();
        var world = session.World;
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(10) });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "pilot" });
        world.Apply(new ChatMessage { Message = "everyone line", ChatTarget = (int)ChatTarget.Everyone, SidSender = 10 });
        world.Apply(new ChatMessage { Message = "team line", ChatTarget = (int)ChatTarget.Team, SidSender = 10 });
        Assert.NotNull(world.MyShipId);   // in game

        var game = Headless(session);
        Set(game, "_chatPlayerChatsOnly", false);
        Set(game, "_chatFilterChatsToAll", true);
        Call(game, "ApplyChatPaneFilter");
        var lines = Call(game, "BuildChatLines") as List<string>;
        Assert.DoesNotContain(lines!, l => l.Contains("everyone line"));
        Assert.Contains(lines!, l => l.Contains("team line"));

        // Off (the native toggle) restores the everyone line.
        Set(game, "_chatFilterChatsToAll", false);
        lines = Call(game, "BuildChatLines") as List<string>;
        Assert.Contains(lines!, l => l.Contains("everyone line"));
    }

    private static ServerShipUpdate Pose(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity,
            Allegiance.Core.Collision.Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    [Fact]
    public void P806_lobby_chat_target_cycle_is_the_native_button_set()
    {
        // Native's team-screen chat buttons (teamscreen.cpp:3082-3118): Everyone,
        // Team, and Leaders only for the side's team leader.
        Assert.Equal(ChatTarget.Team, ChatTargetLogic.NextLobby(ChatTarget.Everyone, isTeamLeader: false));
        Assert.Equal(ChatTarget.Everyone, ChatTargetLogic.NextLobby(ChatTarget.Team, isTeamLeader: false));
        Assert.Equal(ChatTarget.Leaders, ChatTargetLogic.NextLobby(ChatTarget.Team, isTeamLeader: true));
        Assert.Equal(ChatTarget.Everyone, ChatTargetLogic.NextLobby(ChatTarget.Leaders, isTeamLeader: true));

        // The pane rule itself: reset + the four native channels; the Leaders
        // button does nothing at all for a non-leader (:3104-3116).
        var pane = new ChatPaneModel();
        pane.AddChannel(ChatTarget.Everyone);
        Assert.False(pane.ApplyLobbyChatTarget(ChatTarget.Leaders, isTeamLeader: false));
        Assert.Equal(new[] { ChatTarget.Everyone }, pane.Channels.OrderBy(c => c).ToArray());
        Assert.True(pane.ApplyLobbyChatTarget(ChatTarget.Leaders, isTeamLeader: true));
        Assert.Equal(
            new[] { ChatTarget.Leaders, ChatTarget.Individual, ChatTarget.IndividualNoFilter, ChatTarget.Admin }
                .OrderBy(c => c).ToArray(),
            pane.Channels.OrderBy(c => c).ToArray());
    }

}

/// <summary>
/// P757 — the teleport pane's Back/failure recovery: a refused teleport
/// (BOARD_NACK) releases the pending intentional action (native EndLockDown +
/// OnBoardFailed, appmsg.cpp:1899-1910, teleportpane.cpp:276-294) so the pane
/// and the docked shop stay usable.
/// </summary>
public sealed class P757TeleportRecoveryTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static object? Call(Game1 game, string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private static Game1 Headless(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_hangarLaunch", new HangarLaunchSequence());
        Set(game, "_hangarEdges", new ActionPressEdges());
        Set(game, "_chatPane", new ChatPaneModel());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        Set(game, "_dockedActive", true);
        return game;
    }

    [Fact]
    public void Board_refusal_releases_the_pending_teleport_latch_for_reselection()
    {
        using var session = new ConnectionSession();
        var game = Headless(session);
        // A pending teleport: the pane acted (BOARD_SHIP sent, pane closed,
        // launch latch set — ActivateTeleport's tail).
        Set(game, "_launchSent", true);
        Set(game, "_teleportActive", false);
        Set(game, "_teleportBoardFailure", session.World.BoardFailureRevision);

        // The production recovery seam HandleDockedScreen calls every docked frame.
        Call(game, "ReleaseTeleportOnBoardRefusal");
        Assert.True(Get<bool>(game, "_launchSent"));   // no nack yet — still pending

        // BOARD_NACK arrives (the wire path P638 pins the message/cue; here the
        // pane recovery): the latch releases so T reopens the pane and the shop
        // keys work again.
        session.World.Apply(new EnterGame());
        session.World.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = -1, State = ShipStatusMessage.StateDocked });
        long revision = session.World.BoardFailureRevision;
        session.World.Apply(new BoardNack { SidRequestedParent = 77 });
        Assert.Equal(revision + 1, session.World.BoardFailureRevision);
        Call(game, "ReleaseTeleportOnBoardRefusal");
        Assert.False(Get<bool>(game, "_launchSent"));
    }
}

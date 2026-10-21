using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Core;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using Xunit;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P761 — the P743 follow-up handler-level test for <c>TK_StartChat</c>
/// (WinTrek/WinTrek.cpp:9441-9446): StartChat through the flight handler opens
/// compose, composing blocks flight input, Enter sends, Esc cancels and returns
/// focus. The seam is the production handler
/// <c>Game1.HandleChatCompose</c> (P757's extraction — P761b dropped its own
/// duplicate <c>StepChatCompose</c> extraction and grew the handler instead)
/// plus <see cref="Game1.GateCompose"/> and the existing
/// <c>MainMenuRules.Escape</c> chain — pinned here over a headless
/// <see cref="Game1"/> (the P757 test idiom).
/// </summary>
public class P761StartChatComposeTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static void Call(Game1 game, string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private static KeyboardState Ks(params Keys[] keys) => new(keys);

    /// <summary>The P757 headless Game1: the fields the chat/profile handlers
    /// touch, no graphics device.</summary>
    private static Game1 Headless(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_moderation", new ModerationModel());
        Set(game, "_chatPane", new ChatPaneModel());
        Set(game, "_chatBuffer", new System.Text.StringBuilder());
        Set(game, "_chatTarget", ChatTargetLogic.DefaultTarget);
        Set(game, "_pilotProfile", new PilotInfoLayout.ProfileEditor());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        return game;
    }

    private static void Type(Game1 game, string text)
    {
        foreach (var c in text)
            Call(game, "OnTextInput", null!, new Microsoft.Xna.Framework.TextInputEventArgs(c, XnaKeys.None));
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

    [Fact]
    public void StartChatEdge_OpensCompose_WhileNothingModalOwnsTheKeys()
    {
        using var session = new ConnectionSession();
        var game = Headless(session);

        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
        Assert.True(Get<bool>(game, "_chatActive"));   // native StartChat(), WinTrek.cpp:9441-9446
        Assert.Equal("", Get<System.Text.StringBuilder>(game, "_chatBuffer").ToString());
    }

    [Fact]
    public void HeldEnter_DoesNotReFire()
    {
        using var session = new ConnectionSession();
        var game = Headless(session);

        // A held StartChat through the handler: the edge fires once (open) and
        // the second identical frame is ignored, so compose cannot re-open or
        // immediately re-send.
        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
        Assert.True(Get<bool>(game, "_chatActive"));
        Type(game, "abc");
        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
        Assert.True(Get<bool>(game, "_chatActive"));
        Assert.Equal("abc", Get<System.Text.StringBuilder>(game, "_chatBuffer").ToString());
    }

    [Fact]
    public void ModalSurfaces_SuppressOpenAndSend()
    {
        using var session = new ConnectionSession();
        var game = Headless(session);

        // The quick-chat menu / key-bind popup / main menu own Enter: no open.
        foreach (var flag in new[] { "_qcMenuOpen", "_keyBindVisible", "_mainMenuVisible", "_menuConsumedFrame" })
        {
            Set(game, flag, true);
            Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
            Assert.False(Get<bool>(game, "_chatActive"));
            Set(game, flag, false);
            Call(game, "HandleChatCompose", Ks());
        }
    }

    [Fact]
    public void Backspace_EdgesOnlyWhileComposing_AndOnlyWhenThereIsText()
    {
        using var session = new ConnectionSession();
        var game = Headless(session);
        var buffer = Get<System.Text.StringBuilder>(game, "_chatBuffer");

        // Not composing: Backspace is not a chat edit (the gate is _chatActive).
        // Typed text only reaches the compose buffer while composing
        // (Window.TextInput is gated on the composer owning the keyboard), so a
        // non-composing Backspace finds an empty buffer and must stay one.
        Call(game, "HandleChatCompose", Ks(XnaKeys.Back));
        Assert.Equal("", buffer.ToString());

        // Composing: one edge deletes one character.
        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));   // open (fresh edge)
        Call(game, "HandleChatCompose", Ks());                // release
        Type(game, "abc");
        Call(game, "HandleChatCompose", Ks(XnaKeys.Back));
        Assert.Equal("ab", buffer.ToString());

        // Held backspace does not re-fire.
        Call(game, "HandleChatCompose", Ks(XnaKeys.Back));
        Assert.Equal("ab", buffer.ToString());
    }

    [Fact]
    public void TabCyclesRecipient_OnlyWhileTheBufferIsEmpty()
    {
        using var session = new ConnectionSession();
        var game = Headless(session);

        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));   // open
        Call(game, "HandleChatCompose", Ks());                // release

        // Empty buffer: Tab cycles the recipient (native OnTab, console.cpp:2747-2755).
        Call(game, "HandleChatCompose", Ks(XnaKeys.Tab));
        Assert.Equal(ChatTargetLogic.Next(ChatTargetLogic.DefaultTarget), Get<ChatTarget>(game, "_chatTarget"));
        Call(game, "HandleChatCompose", Ks());                // release

        // With typed text native does name completion instead — a no-op here.
        Type(game, "wave");
        Call(game, "HandleChatCompose", Ks(XnaKeys.Tab));
        Assert.Equal(ChatTargetLogic.Next(ChatTargetLogic.DefaultTarget), Get<ChatTarget>(game, "_chatTarget"));
    }

    [Fact]
    public async Task EnterWhileComposing_SendsTheLineOverTheRealTransport()
    {
        // Native UpdateComposedChat (console.cpp:1282-1338): Enter closes the
        // composer and sends the line to the current recipient. The real
        // handler drives the real CHATMESSAGE(23) onto the wire.
        var site = new Site();
        var host = new TcpTransport(site);
        using var session = new ConnectionSession();
        host.Host(0);
        session.Connect(host.BoundPort, "P761 chat");
        await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var game = Headless(session);

        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
        Call(game, "HandleChatCompose", Ks());                // release the edge
        Type(game, "p761 hello");
        Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
        Assert.False(Get<bool>(game, "_chatActive"));

        ChatMessage? sent = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sent is null && DateTime.UtcNow < deadline)
        {
            while (site.Received.TryDequeue(out var m))
                if (FedMessageHeader.Read(m).Fmid == ChatMessage.Fmid)
                    sent = ChatMessage.Decode(m);
            if (sent is null) Thread.Sleep(5);
        }
        Assert.NotNull(sent);
        Assert.Equal("p761 hello", sent!.Message);
        Assert.Equal((int)ChatTargetLogic.DefaultTarget, sent.ChatTarget);
    }

    [Fact]
    public void ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed()
    {
        // P761 — while the character sheet's profile editor is up it owns the
        // text-entry keys: Backspace edits the draft and Enter applies it
        // (native OnButtonApply, CharInfoScreen.cpp:839-858) instead of opening
        // or sending chat. The applied text is stored locally (no club-server
        // channel in this port).
        string prefs = IntroPreferences.FilePath;
        string? before = File.Exists(prefs) ? File.ReadAllText(prefs) : null;
        try
        {
            using var session = new ConnectionSession();
            var game = Headless(session);
            var editor = Get<PilotInfoLayout.ProfileEditor>(game, "_pilotProfile");
            editor.BeginEdit();                       // the Ctrl+P / Edit branch
            Call(game, "OnTextInput", null!, new Microsoft.Xna.Framework.TextInputEventArgs('f', XnaKeys.None));
            Call(game, "OnTextInput", null!, new Microsoft.Xna.Framework.TextInputEventArgs('l', XnaKeys.None));

            Call(game, "HandleChatCompose", Ks(XnaKeys.Back));
            Assert.Equal("f", editor.Draft);
            Call(game, "HandleChatCompose", Ks());    // release

            Call(game, "HandleChatCompose", Ks(XnaKeys.Enter));
            Assert.False(editor.Editing);             // Apply swapped the buttons back
            Assert.Equal("f", editor.Description);
            Assert.False(Get<bool>(game, "_chatActive"));   // chat never opened

            Assert.Equal("f", IntroPreferences.Load().PilotDescription);
        }
        finally
        {
            if (before is null) File.Delete(prefs);
            else File.WriteAllText(prefs, before);
        }
    }

    [Fact]
    public void ComposeGate_BlocksFlightInput_KeepsTheThrottle()
    {
        // Typing a word must never also thrust/turn the ship: the composing
        // gate neutralises every keystroke flag but carries the persistent
        // throttle through (UpdateFlying's composing ternary).
        var live = new FlightInputState(true, false, true, false, true, false,
            true, false, false, true, Afterburner: true, Fire: true, Throttle: 0.4f);
        var gated = Game1.GateCompose(composing: true, live);
        Assert.False(gated.ThrottleForward);
        Assert.False(gated.StrafeLeft);
        Assert.False(gated.PitchUp);
        Assert.False(gated.YawLeft);
        Assert.False(gated.RollRight);
        Assert.False(gated.Fire);
        Assert.False(gated.Afterburner);
        Assert.Equal(0.4f, gated.Throttle);          // the throttle axis rides through

        // Not composing: passthrough.
        var passthrough = Game1.GateCompose(composing: false, live);
        Assert.Equal(live, passthrough);
    }

    [Fact]
    public void EscWhileComposing_CancelsAndReturnsFocus()
    {
        // The Escape chain (Update's Escape handling): while composing chat it
        // cancels the input line — MainMenuRules.Escape reports Chat — which
        // clears _chatActive (focus returns to the flight controls).
        var action = MainMenuRules.Escape(
            briefing: false, chat: true, quickChat: false, controls: false,
            help: false, credits: false, mainMenuPressed: false, overrideView: false,
            investment: false, teleport: false, loadout: false,
            mainMenuOpen: false, playerPings: false);
        Assert.Equal(EscapeUiAction.Chat, action);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SendRoundTrip_ComposeSendOverLoopback_ReachesTheLog()
    {
        Require.Artwork();
        // The wire half of "Enter sends": a real ConnectionSession composes and
        // sends a line to itself over loopback (the server echoes the sender's
        // own chat back — the thing that actually displays it).
        using var server = new global::Allegiance.Server.Server();
        server.Boot(new global::Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc",
            Port = 0,
            UpdatesPerSecond = 20,
            Games = [new global::Allegiance.Server.GameConfig
            {
                Name = "P761 chat", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"],
            }],
        });
        using var session = new global::Allegiance.ClientCore.Session.ConnectionSession();
        session.Connect(server.BoundPort, "p761chatter");
        session.SendLogonRequest(new LogonRequest
        {
            CharacterName = "p761chatter", ZoneTicket = [1, 2, 3, 4],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, Time = 446u, DwCookie = 0xC761u,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
            SteamID = 90200000000000076ul, DrmHash = "p761chat",
        });
        // P281 (master merge) — logged-on peers wait for a post-connect side
        // choice; POSITIONREQ(NA) auto-picks, exactly as the real client does.
        session.SendPositionReq(-1);
        int n = 0;
        while (session.World.MyShipId is null && n++ < 8000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(session.World.MyShipId is not null, "must be seated to chat");
        n = 0;
        while (session.World.Ships[session.World.MyShipId!.Value].Player is null && n++ < 8000)
        { server.TickSim(); Thread.Sleep(1); }   // PlayerInfo must exist for the side routing

        // The composed line (what the handler's Enter-send sends).
        session.SendChat("p761 compose send works", ChatTarget.Team, commandId: OrderChange.CidNone);
        n = 0;
        while (n++ < 8000 && !session.World.Events.Chats.Any(c => c.Message.Contains("p761 compose send works")))
        { server.TickSim(); Thread.Sleep(1); }
        Assert.Contains(session.World.Events.Chats, c => c.Message.Contains("p761 compose send works"));
    }
}

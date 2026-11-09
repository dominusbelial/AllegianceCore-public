using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P974ConnectingUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string field, object value)
        => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private static object? Get(Game1 game, string field) => typeof(Game1).GetField(field, Hidden)!.GetValue(game);
    private static void Call(Game1 game, string method, params object?[] arguments)
        => typeof(Game1).GetMethod(method, Hidden)!.Invoke(game, arguments);

    private static Game1 NewGame(HudStyle style, ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_moderation", new ModerationModel());
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_lanJoinPassword", new StringBuilder());
        Set(game, "_lobbyUrl", "");
        Set(game, "_playerName", "P974");
        Set(game, "_sessionRecording", true);
        Set(game, "_introLobbyLock", new object());
        Set(game, "_servers", new List<ServerListing>());
        Set(game, "_returnToIntroOnFailure", true);
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Intro"));
        return game;
    }

    private static void Begin(Game1 game, ushort port, string origin = "Intro")
        => Call(game, "BeginConnect", "127.0.0.1", port, uint.MaxValue, null, false, null, null,
            Enum.Parse(typeof(Game1).GetField("_joinOrigin", Hidden)!.FieldType, origin));

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public async Task Stalled_join_keeps_updates_returning_and_Escape_cancels(HudStyle style)
    {
        // A real TCP endpoint accepts the SYN but never answers our logon.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        using var session = new ConnectionSession();
        var game = NewGame(style, session);
        var watch = Stopwatch.StartNew();
        Begin(game, port);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"BeginConnect froze the UI for {watch.Elapsed}");
        Assert.Equal("Connecting", Get(game, "_state")!.ToString());
        for (int i = 0; i < 20; i++)
        {
            watch.Restart();
            Call(game, "UpdateConnecting", default(KeyboardState));
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        watch.Restart();
        Call(game, "UpdateConnecting", new KeyboardState(Keys.Escape));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(100), "Escape waited for the network");
        var pending = (Task)Get(game, "_connectOperation")!;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        await ((Task)Get(game, "_connectCleanup")!).WaitAsync(TimeSpan.FromSeconds(3));
        Call(game, "UpdateConnecting", default(KeyboardState));
        Assert.Equal("Intro", Get(game, "_state")!.ToString());
        Assert.False(session.Connected);
        Assert.False(session.Reconnecting);
        // Polling an already consumed operation cannot resurrect SideSelect.
        Call(game, "UpdateConnecting", default(KeyboardState));
        Assert.Equal("Intro", Get(game, "_state")!.ToString());
    }

    [Theory]
    [InlineData(HudStyle.Classic, "Intro", "Intro")]
    [InlineData(HudStyle.Modern, "Intro", "Intro")]
    [InlineData(HudStyle.Classic, "Lobby", "Lobby")]
    [InlineData(HudStyle.Modern, "Lobby", "Lobby")]
    [InlineData(HudStyle.Classic, "JoinLink", "Intro")]
    [InlineData(HudStyle.Modern, "JoinLink", "Intro")]
    public async Task Async_password_refusal_returns_to_the_origin_and_retry_admits(HudStyle style, string origin, string expected)
    {
        using var session = new ConnectionSession();
        var game = NewGame(style, session);
        var site = new JoinSite();
        var host = new TcpTransport(site);
        site.Transport = host;
        host.Host();
        try
        {
            GetBuffer(game).Append("wrong");
            Begin(game, host.BoundPort, origin);
            await ((Task)Get(game, "_connectOperation")!).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Connecting", Get(game, "_state")!.ToString()); // worker never changes screens
            Call(game, "UpdateConnecting", default(KeyboardState));
            Assert.Equal(expected, Get(game, "_state")!.ToString());
            Assert.Equal("wrong", GetBuffer(game).ToString());
            Assert.True((bool)Get(game, "_editingLanPassword")!);

            GetBuffer(game).Clear().Append("right");
            Begin(game, host.BoundPort, origin);
            await ((Task)Get(game, "_connectOperation")!).WaitAsync(TimeSpan.FromSeconds(3));
            Call(game, "UpdateConnecting", default(KeyboardState));
            Assert.Equal("SideSelect", Get(game, "_state")!.ToString());
            Assert.True(session.Connected);
        }
        finally { host.Shutdown(); }
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public async Task Refused_logon_keeps_the_retry_screen_and_reason(HudStyle style)
    {
        using var session = new ConnectionSession();
        var game = NewGame(style, session);
        var site = new JoinSite { RefuseLogon = true };
        var host = new TcpTransport(site);
        site.Transport = host;
        host.Host();
        try
        {
            Begin(game, host.BoundPort);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ((Task)Get(game, "_connectOperation")!).WaitAsync(TimeSpan.FromSeconds(3)));
            Call(game, "UpdateConnecting", default(KeyboardState));
            Assert.Equal("ConnectFailed", Get(game, "_state")!.ToString());
            Assert.Contains("test logon refusal", (string)Get(game, "_connectFailureReason")!);
            Assert.False(session.Connected);
        }
        finally { host.Shutdown(); }
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public async Task Joining_after_logoff_creates_a_fresh_session_owner(HudStyle style)
    {
        using var prior = new ConnectionSession();
        var game = NewGame(style, prior);
        prior.Dispose();
        var site = new JoinSite();
        var host = new TcpTransport(site);
        site.Transport = host;
        host.Host();
        try
        {
            GetBuffer(game).Append("right");
            Begin(game, host.BoundPort);
            Assert.NotSame(prior, Get(game, "_session"));
            await ((Task)Get(game, "_connectOperation")!).WaitAsync(TimeSpan.FromSeconds(3));
            Call(game, "UpdateConnecting", default(KeyboardState));
            Assert.Equal("SideSelect", Get(game, "_state")!.ToString());
            Assert.False(prior.Connected);
        }
        finally
        {
            ((ConnectionSession)Get(game, "_session")!).Dispose();
            host.Shutdown();
        }
    }

    private static StringBuilder GetBuffer(Game1 game) => (StringBuilder)Get(game, "_lanJoinPassword")!;

    private sealed class JoinSite : ITransportSite
    {
        internal ITransport Transport = null!;
        internal bool RefuseLogon;
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            foreach (var message in FedPacket.Unpack(data))
            {
                if (FedMessageHeader.Read(message).Fmid != LogonRequest.Fmid) continue;
                var request = LogonRequest.Decode(message);
                var ack = new LogonAck { FValidated = !RefuseLogon, ShipID = 74,
                    CharNameOrFailureReason = RefuseLogon ? "test logon refusal" : "P974" };
                var answer = request.MissionPassword == "right" ? new SideRoster().Encode()
                    : new DelPositionReq { ShipId = 74, SideId = -1, Reason = DelPositionReqReason.BadPassword }.Encode();
                byte[][] messages = RefuseLogon ? [ack.Encode()] : [ack.Encode(), answer];
                Transport.Send(from, FedPacket.Pack(messages), true);
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Discovery;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P897ClientTransportTests
{
    public static IEnumerable<object[]> PolicyRows()
    {
        foreach (var setting in Enum.GetValues<ClientTransportSetting>())
        foreach (var source in Enum.GetValues<DiscoverySource>())
        foreach (int port in new[] { 0, 6073, -1, 65536 })
        foreach (var client in Enum.GetValues<NetworkLink>())
        foreach (var host in Enum.GetValues<NetworkLink>())
        {
            bool local = source is DiscoverySource.Lan or DiscoverySource.Multicast or DiscoverySource.Subnet;
            string expected = setting switch
            {
                ClientTransportSetting.TCP => "tcp",
                ClientTransportSetting.UDP => "udp",
                _ => port != 6073 || (local && client == NetworkLink.Wired && host == NetworkLink.Wired) ? "tcp" : "udp",
            };
            yield return [setting, source, port, client, host, expected];
        }
    }

    [Theory, MemberData(nameof(PolicyRows))]
    public void Policy_table(ClientTransportSetting setting, DiscoverySource source, int port,
        NetworkLink client, NetworkLink host, string expected)
    {
        var choice = ClientTransportPolicy.Choose(setting, source, port == 0 ? null : port, client, host);
        Assert.Equal(expected, choice.Protocol);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    [Theory]
    [InlineData(ClientTransportSetting.Auto, "udp", "udp")]
    [InlineData(ClientTransportSetting.Auto, " TCP ", "tcp")]
    [InlineData(ClientTransportSetting.Auto, "", "tcp")]
    [InlineData(ClientTransportSetting.TCP, "udp", "tcp")]
    [InlineData(ClientTransportSetting.UDP, "tcp", "udp")]
    public void Environment_overrides_Auto(ClientTransportSetting setting, string env, string expected) =>
        Assert.Equal(expected, ClientTransportPolicy.Resolve(setting, DiscoverySource.Lobby, null,
            NetworkLink.Unknown, NetworkLink.Unknown, env).Protocol);

    [Fact]
    public void Discovery_optional_fields_preserve_old_contract_and_unknown_future_link()
    {
        var old = Encoding.UTF8.GetBytes("""{"magic":"allegiance-disc","v":1,"gamePort":6073} """);
        var reply = Assert.IsType<DiscoveryProtocol.Reply>(DiscoveryProtocol.TryParseReply(old, old.Length));
        Assert.Null(reply.UdpPort); Assert.Equal(NetworkLink.Unknown, NetworkLinkDetector.Parse(reply.HostLink));
        using var responder = new DiscoveryResponder(() => new("P897", "", 0, 8, "1", 6073, 6074, NetworkLink.Wired), 0);
        var req = DiscoveryProtocol.EncodeRequest("old-client");
        var data = responder.ProcessRequest(req, req.Length)!;
        // An old client reads only its original fields and ignores additions.
        using var doc = JsonDocument.Parse(data);
        Assert.Equal(6073, doc.RootElement.GetProperty("GamePort").GetInt32());
        var parsed = DiscoveryProtocol.TryParseReply(data, data.Length)!;
        Assert.Equal(6074, parsed.UdpPort); Assert.Equal("wired", parsed.HostLink);
        Assert.Equal("old-client", parsed.Nonce);
        Assert.Equal(NetworkLink.Unknown, NetworkLinkDetector.Parse("future-link"));
    }

    [Theory]
    [InlineData(ClientTransportSetting.Auto, true, "udp", false)]
    [InlineData(ClientTransportSetting.Auto, true, "tcp", true)]
    [InlineData(ClientTransportSetting.TCP, true, "tcp", false)]
    [InlineData(ClientTransportSetting.UDP, true, "udp", false)]
    [InlineData(ClientTransportSetting.Auto, false, "tcp", false)]
    [InlineData(ClientTransportSetting.UDP, false, "tcp", false)]
    public void Real_discovery_and_session_use_selected_protocol(ClientTransportSetting setting, bool advertiseUdp, string expected, bool blocked)
    {
        var site = new Site();
        var host = new DualTransport(site, new TcpTransport(site, TransportMode.Pool, 1), 0, null, null, new ServerCounters(), false);
        host.Host();
        using var blackhole = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int advertised = advertiseUdp && !blocked ? host.UdpPort : ((IPEndPoint)blackhole.Client.LocalEndPoint!).Port;
        using var responder = new DiscoveryResponder(() => new("P897", "", 0, 8, "1", host.BoundPort,
            setting == ClientTransportSetting.UDP || advertiseUdp ? advertised : null), 0, multicastGroup: "");
        responder.Start();
        using var engine = new DiscoveryEngine();
        var found = Assert.IsType<DiscoveredServer>(engine.ProbeTargetAsync(new("127.0.0.1", responder.BoundPort, DiscoverySource.Lan)));
        var game = FakeGame(new HudSettings { Transport = setting });
        using var session = new ConnectionSession();
        Set(game, "_session", session);
        Set(game, "_introFound", new List<DiscoveredServer> { found });
        var before = Stopwatch.StartNew();
        try
        {
            Call(game, "ConfigureClientTransport", found.Address, (ushort)found.Port, uint.MaxValue, found);
            session.Connect((ushort)found.Port, "P897", found.Address);
            Assert.Equal(expected, session.ActiveTransportProtocol);
            session.SendPing();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref site.Receives) > 0, 2000));
            Assert.True(before.Elapsed < TimeSpan.FromSeconds(6), $"handshake/fallback took {before.Elapsed}");
            Assert.False(session.Lost);
            // Reconnect freezes the initial preference and port even if the UI changes.
            string chosen = session.TransportProtocol!;
            session.TransportProtocol = chosen == "udp" ? "tcp" : "udp";
            Assert.Equal(chosen, Get<string>(session, "_joinedProtocol"));
            Assert.Equal(found.UdpPort is int p ? (ushort?)p : (ushort)found.Port, Get<ushort?>(session, "_joinedUdpPort"));
        }
        finally { session.Dispose(); host.Shutdown(); }
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("udp")]
    public void Reconnect_reuses_initial_protocol_even_after_preference_changes(string protocol)
    {
        var site = new Site();
        var host = new DualTransport(site, new TcpTransport(site, TransportMode.Pool, 1), 0, null, null, new ServerCounters(), false);
        host.Host();
        using var session = new ConnectionSession { TransportProtocol = protocol, UdpPort = host.UdpPort };
        site.Receive = id => host.Send(id, FedPacket.Pack(new LogonAck { FValidated = true, ShipID = 1 }.Encode()), true);
        try
        {
            session.EnableAutoReconnect(5);
            session.Connect(host.BoundPort, "P897-resume");
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref site.Joins) == 1, 2000));
            session.TransportProtocol = protocol == "tcp" ? "udp" : "tcp";
            session.UdpPort = 1;
            ((ITransportSite)session).OnSessionLost();
            Assert.True(SpinWait.SpinUntil(() => session.ResumeSuccesses == 1 && !session.Reconnecting, 4000));
            Assert.Equal(protocol, session.ActiveTransportProtocol);
            Assert.False(session.Lost);
        }
        finally { session.Dispose(); host.Shutdown(); }
    }

    [Fact]
    public void Default_session_and_bot_remain_TCP()
    {
        string? old = Environment.GetEnvironmentVariable("ALLEGIANCE_TRANSPORT_PROTOCOL");
        Environment.SetEnvironmentVariable("ALLEGIANCE_TRANSPORT_PROTOCOL", null);
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.Pool, 1); host.Host();
        using var session = new ConnectionSession();
        using var bot = new Allegiance.Bot.Bot();
        try
        {
            Assert.Null(session.TransportProtocol); Assert.Null(bot.TransportProtocol);
            session.Connect(host.BoundPort, "P897-default-session"); session.SendPing();
            bot.Connect(host.BoundPort, "P897-default-bot"); bot.SendSuicide();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref site.Joins) == 2 && Volatile.Read(ref site.Receives) >= 2, 2000));
            Assert.Equal("tcp", session.ActiveTransportProtocol);
            Assert.IsType<TcpTransport>(Get<ITransport>(bot, "_transport"));
        }
        finally
        {
            bot.Dispose(); session.Dispose(); host.Shutdown();
            Environment.SetEnvironmentVariable("ALLEGIANCE_TRANSPORT_PROTOCOL", old);
        }
    }

    [Fact]
    public void Detection_is_safe_on_loopback_and_unresolvable_input()
    {
        Assert.Equal(NetworkLink.Unknown, NetworkLinkDetector.Detect(IPAddress.Loopback, 6073));
        Assert.Equal(NetworkLink.Unknown, NetworkLinkDetector.Detect("", 6073));
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Settings_arrows_cycle_and_persist_for_both_styles(HudStyle style)
    {
        string dir = Path.Combine(Path.GetTempPath(), "p897-" + Guid.NewGuid());
        string? oldHud = Environment.GetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS");
        string? oldGraphics = Environment.GetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS");
        var settings = new HudSettings { Style = style };
        var game = FakeGame(settings);
        Set(game, "_graphicsSettingsVisible", true); Set(game, "_graphicsRow", 21);
        Set(game, "_flightGc", new FlightGcLatency(false)); Set(game, "_upscaling", new UpscalingSettings());
        Set(game, "_resolutionController", new DynamicResolutionController());
        try
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", Path.Combine(dir, "hud.json"));
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", Path.Combine(dir, "graphics.json"));
            Assert.Equal(ClientTransportSetting.Auto, settings.Transport);
            foreach (var expected in new[] { ClientTransportSetting.TCP, ClientTransportSetting.UDP, ClientTransportSetting.Auto })
            {
                Call(game, "UpdateGraphicsSettings", new KeyboardState(Keys.Right));
                Assert.Equal(expected, settings.Transport);
                Assert.Equal(expected, HudSettings.Load(HudSettings.DefaultPath, true).Transport);
                Call(game, "UpdateGraphicsSettings", new KeyboardState());
            }
            Call(game, "UpdateGraphicsSettings", new KeyboardState(Keys.Left));
            Assert.Equal(ClientTransportSetting.UDP, settings.Transport);
            Assert.Equal(style, HudSettings.Load(HudSettings.DefaultPath, true).Style);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", oldHud);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", oldGraphics);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static Game1 FakeGame(HudSettings settings)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_hudSettings", settings); Set(game, "_introFoundLock", new object()); Set(game, "_introLobbyLock", new object());
        Set(game, "_introFound", new List<DiscoveredServer>()); Set(game, "_servers", new List<ServerListing>());
        return game;
    }
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object o, string field, object value) => o.GetType().GetField(field, Hidden)!.SetValue(o, value);
    private static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, Hidden)!.GetValue(o)!;
    private static object? Call(object o, string method, params object[] args) => o.GetType().GetMethod(method, Hidden)!.Invoke(o, args);
    private sealed class Site : ITransportSite
    {
        public int Joins;
        public void OnPeerJoined(ConnectionId id, string name) => Interlocked.Increment(ref Joins);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public int Receives;
        public Action<ConnectionId>? Receive;
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> data)
        {
            Interlocked.Increment(ref Receives);
            Receive?.Invoke(id);
        }
    }
}

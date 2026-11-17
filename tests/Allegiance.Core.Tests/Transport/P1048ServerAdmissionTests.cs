using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Allegiance.Core.Identity;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P1048ServerAdmissionTests
{
    private sealed class Peer : IAsyncDisposable
    {
        internal readonly ClientWebSocket Socket;
        internal readonly ConcurrentQueue<byte[]> Messages = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reader;
        internal LogonAck? Ack => Messages.Where(m => FedMessageHeader.Read(m).Fmid == LogonAck.Fmid).Select(m => LogonAck.Decode(m)).LastOrDefault();
        private Peer(ClientWebSocket socket) { Socket = socket; _reader = ReadLoop(); }
        internal static async Task<Peer> Connect(Srv server, IReadOnlyDictionary<string, string>? headers = null, string origin = "http://localhost") =>
            new(await P1048WebSocketTests.Connect(server.WebSocketPort, origin, headers: headers));
        internal Task Send(IFedMessage message) => P1048WebSocketTests.Send(Socket, FedPacket.Pack([message.Encode()]));
        private async Task ReadLoop()
        {
            byte[] buffer = new byte[8192];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var packet = new MemoryStream();
                    while (true)
                    {
                        var part = await Socket.ReceiveAsync(buffer, _stop.Token);
                        if (part.MessageType == WebSocketMessageType.Close) return;
                        Assert.Equal(WebSocketMessageType.Binary, part.MessageType);
                        packet.Write(buffer, 0, part.Count); Assert.True(packet.Length <= 16 * 1024 * 1024);
                        if (part.EndOfMessage) break;
                    }
                    foreach (var message in FedPacket.Unpack(packet.ToArray())) Messages.Enqueue(message);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); Socket.Abort(); await _reader; Socket.Dispose(); _stop.Dispose(); }
    }
    private sealed class World : IDisposable
    {
        internal readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1048", Guid.NewGuid().ToString("N"));
        internal World(bool identity = true, Action<ServerConfig>? configure = null)
        {
            Require.Artwork(); Directory.CreateDirectory(_directory);
            string file = Path.Combine(_directory, "identity.pem"); File.WriteAllText(file, Key.ExportSubjectPublicKeyInfoPem());
            var settings = new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                BindAddress = "127.0.0.1", Discovery = new() { Enabled = false }, WebSocket = P1048WebSocketTests.Settings(),
                Identity = identity ? new() { Issuer = "issuer", Audience = "game", PublicKeyFile = file, AllowAnonymous = false } : null,
                Games = [new() { Name = "P1048", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false }],
            };
            configure?.Invoke(settings); Server.Boot(settings);
        }
        internal string Token(string name = "CanonicalPilot", ECDsa? key = null, string issuer = "issuer", string audience = "game", int ageSeconds = 0) =>
            JoinTokens.Issue(key ?? Key, issuer, audience, "0123456789abcdef0123456789abcdef", name, DateTimeOffset.UtcNow.AddSeconds(ageSeconds));
        internal async Task Pump(Func<bool> done)
        {
            using var deadline = new CancellationTokenSource(5000);
            while (!done() && !deadline.IsCancellationRequested) { Server.TickSim(); await Task.Delay(2); }
            Assert.True(done(), "Server admission did not complete");
        }
        public void Dispose() { Server.Dispose(); Key.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
    private static LogonRequest Request(string? token = null, string name = "SpoofedName", string cdKey = "") => new()
    {
        CharacterName = name, ZoneTicket = token is null ? null : Encoding.UTF8.GetBytes(token), CDKey = cdKey,
        FedsrvVer = LogonRequest.MsgVer, Time = 1, DwCookie = uint.MaxValue, CrcFileList = -1,
    };

    [SkippableFact]
    public void Default_off_configuration_constructs_no_WebSocket_listener()
    {
        using var world = new World(identity: false, configure: config => config.WebSocket = new());
        Assert.Equal(0, world.Server.WebSocketPort);
        Assert.IsType<TcpTransport>(world.Server.Host.Transport);
    }

    [SkippableFact]
    public async Task Ticket_binds_canonical_identity_replay_is_refused_and_three_listeners_route_independently()
    {
        using var world = new World(configure: c => c.Transport = new() { Protocol = "udp" });
        var server = world.Server;
        await using var peer = await Peer.Connect(server);
        string token = world.Token(); var packet = FedPacket.Pack([Request(token).Encode()]);
        await P1048WebSocketTests.Send(peer.Socket, packet[..5], false);
        await P1048WebSocketTests.Send(peer.Socket, packet[5..]);
        await world.Pump(() => peer.Ack is not null); Assert.True(peer.Ack!.FValidated);
        Assert.Equal("CanonicalPilot", peer.Ack.CharNameOrFailureReason);
        Assert.Equal("0123456789abcdef0123456789abcdef", server.AccountId(new ConnectionId(1)));
        Assert.Equal("websocket", server.Host.Transport!.PeerProtocolName(new(1)));
        Assert.False(server.Host.Transport.IsPeerLoopback(new(1)));
        await using var replay = await Peer.Connect(server);
        await replay.Send(Request(token)); await world.Pump(() => replay.Ack is not null);
        Assert.False(replay.Ack!.FValidated); Assert.Contains("already used", replay.Ack.CharNameOrFailureReason);
        await peer.Send(new PositionReq { SideId = 0 }); await world.Pump(() => server.Ships.Count == 1);
        Assert.Contains("CanonicalPilot", server.LivePlayerNames);
        // Unchanged TCP/UDP paths coexist with WS and receive their own responses.
        using var tcp = new Allegiance.Bot.Bot { AutoSelectSideAfterLogon = false };
        using var udp = new Allegiance.Bot.Bot { AutoSelectSideAfterLogon = false, TransportProtocol = "udp" };
        tcp.Connect(server.BoundPort, "TCP"); tcp.SendLogonRequest(Request(world.Token("TcpPilot")));
        udp.Connect(server.UdpGameTransport!.BoundPort, "UDP");
        udp.SendLogonRequest(Request(world.Token("UdpPilot")));
        await world.Pump(() => tcp.Ack is not null && udp.Ack is not null);
        Assert.True(tcp.Ack!.FValidated); Assert.True(udp.Ack!.FValidated);
        Assert.Equal("TcpPilot", tcp.Ack.CharNameOrFailureReason); Assert.Equal("UdpPilot", udp.Ack.CharNameOrFailureReason);
        Assert.Equal(3, new[] { peer.Ack.ShipID, tcp.Ack.ShipID, udp.Ack.ShipID }.Distinct().Count());
    }

    [SkippableTheory]
    [InlineData("signature")] [InlineData("expired")] [InlineData("issuer")] [InlineData("audience")] [InlineData("malformed")] [InlineData("missing")]
    public async Task Bad_expired_wrong_scope_and_missing_tickets_cannot_authenticate(string type)
    {
        using var world = new World(); using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string? token = type switch
        {
            "signature" => world.Token(key: attacker), "expired" => world.Token(ageSeconds: -120),
            "issuer" => world.Token(issuer: "other"), "audience" => world.Token(audience: "other"),
            "malformed" => "invalid.token.value", _ => null,
        };
        await using var peer = await Peer.Connect(world.Server); await peer.Send(Request(token)); await world.Pump(() => peer.Ack is not null);
        Assert.False(peer.Ack!.FValidated); Assert.Equal(0, peer.Ack.ShipID);
        Assert.Null(world.Server.AccountId(new(1))); Assert.Empty(world.Server.Ships);
    }

    [SkippableTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task Closing_during_authentication_retires_state_and_does_not_consume_an_unprocessed_ticket(bool fragmented)
    {
        using var world = new World(); string token = world.Token();
        using var client = await P1048WebSocketTests.Connect(world.Server.WebSocketPort);
        await P1048WebSocketTests.Until(() => !world.Server.Host.Transport!.IsPeerClosed(new(1)));
        byte[] packet = FedPacket.Pack([Request(token).Encode()]); long before = world.Server.Host.PacketsReceived;
        await P1048WebSocketTests.Send(client, fragmented ? packet[..5] : packet, !fragmented);
        if (!fragmented) await P1048WebSocketTests.Until(() => world.Server.Host.PacketsReceived > before);
        client.Abort(); await P1048WebSocketTests.Until(() => world.Server.Host.Transport!.IsPeerClosed(new(1)));
        world.Server.TickSim(); Assert.Empty(world.Server.Ships); Assert.Null(world.Server.AccountId(new(1)));
        await using var replacement = await Peer.Connect(world.Server); await replacement.Send(Request(token));
        await world.Pump(() => replacement.Ack is not null); Assert.True(replacement.Ack!.FValidated);
    }

    [SkippableTheory]
    [InlineData("crc")] [InlineData("cbmsg")] [InlineData("count")]
    public async Task Native_packet_CRC_message_bounds_and_count_are_still_enforced(string type)
    {
        using var world = new World(identity: false); await using var peer = await Peer.Connect(world.Server);
        byte[] message = new PositionReq { SideId = 0 }.Encode();
        byte[] packet;
        if (type == "count") packet = FedPacket.Pack(Enumerable.Repeat(message, FedPacket.MaxMessagesPerPacket + 1));
        else if (type == "cbmsg") { message[0] = 0xff; message[1] = 0xff; packet = FedPacket.Pack([message]); }
        else { packet = FedPacket.Pack([message]); packet[^1] ^= 0xff; }
        await P1048WebSocketTests.Send(peer.Socket, packet);
        await world.Pump(() => world.Server.Host.Transport!.IsPeerClosed(new(1)));
        Assert.Empty(world.Server.Ships);
        Assert.Equal(1, world.Server.Host.Transport!.DrainDisconnectCounts()[DisconnectReason.BadCrc]);
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task WebSocket_loopback_and_proxied_loopback_never_grant_local_roles(bool proxy)
    {
        using var world = new World(identity: false, configure: config =>
        {
            config.LiveSettings = new() { AdminCheats = new() { Enabled = true, AllowNames = ["ADMIN"] },
                TestHarness = new() { Enabled = true, CharacterNamePrefix = "ADMIN", Money = 123456 } };
            if (proxy) { config.WebSocket.KnownProxies = ["127.0.0.1"]; config.WebSocket.AllowLoopbackDevelopment = false;
                config.WebSocket.AllowedOrigins = ["https://lobby.invalid"]; }
        });
        await using var peer = await Peer.Connect(world.Server, proxy ? new Dictionary<string, string>
        { ["X-Forwarded-For"] = "127.0.0.1", ["X-Forwarded-Proto"] = "https" } : null, proxy ? "https://lobby.invalid" : "http://localhost");
        await peer.Send(Request(name: "ADMIN")); await world.Pump(() => peer.Ack is not null); Assert.True(peer.Ack!.FValidated);
        Assert.False(world.Server.Host.Transport!.IsPeerLoopback(new(1)));
        var privilege = typeof(Srv).GetMethod("PrivilegedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.False((bool)privilege.Invoke(world.Server, [new ConnectionId(1), null, new HashSet<ConnectionId>()])!);
    }

    [SkippableFact]
    public async Task Credential_failures_group_by_trusted_external_source_and_not_the_proxy()
    {
        using var world = new World(identity: false, configure: config =>
        {
            config.WebSocket.KnownProxies = ["127.0.0.1"]; config.WebSocket.AllowLoopbackDevelopment = false;
            config.WebSocket.AllowedOrigins = ["https://lobby.invalid"];
            config.LiveSettings = new() { AdminCheats = new() { Enabled = true, AllowNames = ["ADMIN*"], Secret = "correct-test-bearer" } };
        });
        for (int i = 1; i <= 5; i++)
        {
            await using var peer = await Peer.Connect(world.Server, new Dictionary<string, string>
            { ["X-Forwarded-For"] = "198.51.100.7", ["X-Forwarded-Proto"] = "https" }, "https://lobby.invalid");
            await peer.Send(Request(name: $"ADMIN{i}", cdKey: "wrong-test-bearer")); await world.Pump(() => peer.Ack is not null);
            Assert.Equal("198.51.100.7", world.Server.AddressKeyOf(new((uint)i)));
        }
        await using var sameSource = await Peer.Connect(world.Server, new Dictionary<string, string>
        { ["X-Forwarded-For"] = "198.51.100.7", ["X-Forwarded-Proto"] = "https" }, "https://lobby.invalid");
        await using var otherSource = await Peer.Connect(world.Server, new Dictionary<string, string>
        { ["X-Forwarded-For"] = "198.51.100.8", ["X-Forwarded-Proto"] = "https" }, "https://lobby.invalid");
        // A receive establishes each socket in the tick's join bookkeeping.
        await sameSource.Send(new Ping { TimeClient = 1 }); await otherSource.Send(new Ping { TimeClient = 2 });
        await world.Pump(() => sameSource.Messages.Count > 0 && otherSource.Messages.Count > 0);
        Assert.True(world.Server.CredentialGuessBlocked(new(6))); Assert.False(world.Server.CredentialGuessBlocked(new(7)));
    }
}

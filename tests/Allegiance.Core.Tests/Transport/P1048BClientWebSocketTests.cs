using System.Collections.Concurrent;
using System.Net;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1048BClientWebSocketTests
{
    internal sealed class ClientSite : ITransportSite
    {
        internal readonly ConcurrentQueue<byte[]> Packets = new();
        internal readonly ConcurrentQueue<string> Events = new();
        internal int Lost;
        public void OnPeerJoined(ConnectionId id, string name) => Events.Enqueue("join");
        public void OnPeerLeft(ConnectionId id) => Events.Enqueue("left");
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes) { Assert.Equal(ConnectionId.Server, id); Events.Enqueue("receive"); Packets.Enqueue(bytes.ToArray()); }
        public void OnSessionLost() { Events.Enqueue("lost"); Interlocked.Increment(ref Lost); }
    }
    [Theory]
    [InlineData("wss://game.invalid/game", true)]
    [InlineData("wss://game.invalid:443/game?version=1", true)]
    [InlineData("ws://127.0.0.1:6080/game", true)]
    [InlineData("ws://localhost/game", true)]
    [InlineData("ws://[::1]/game", true)]
    [InlineData("ws://127.1/game", true)]
    [InlineData("ws://game.invalid/game", false)]
    [InlineData("ws://192.0.2.1/game", false)]
    [InlineData("wss://user:password@game.invalid/game", false)]
    [InlineData("wss://@game.invalid/game", false)]
    [InlineData("wss://game.invalid:0/game", false)]
    [InlineData("wss://game.invalid/path@name", true)]
    [InlineData("wss://game.invalid/game#fragment", false)]
    [InlineData("wss://game.invalid/game#", false)]
    [InlineData("https://game.invalid/game", false)]
    [InlineData("tcp://localhost:6073", false)]
    [InlineData("/game", false)]
    [InlineData("", false)]
    [InlineData(" wss://game.invalid/game", false)]
    [InlineData("wss://game.invalid\\game", false)]
    public void Endpoint_validation_for_listing_and_configuration(string input, bool accepted)
    {
        foreach (var parse in new Func<string, WebSocketEndpoint>[] { WebSocketEndpoint.FromServerListing, WebSocketEndpoint.FromConfiguration })
        {
            if (!accepted) Assert.Throws<ArgumentException>(() => parse(input));
            else
            {
                var endpoint = parse(input);
                using var client = Assert.IsType<WebSocketTransport>(TransportFactory.CreateClient(new ClientSite(), webSocketEndpoint: endpoint));
                Assert.Equal(endpoint.Uri.Scheme, client.ProtocolName);
            }
        }
    }
    [Fact]
    public void Ws_protocol_without_an_advertised_or_configured_endpoint_is_rejected()
        => Assert.Throws<ArgumentException>(() => TransportFactory.CreateClient(new ClientSite(), "ws"));

    [Fact]
    public async Task Real_client_preserves_packet_order_and_reports_loss_once_without_peer_callbacks()
    {
        var serverSite = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(serverSite, P1048WebSocketTests.Settings()); host.Host(0, IPAddress.Loopback);
        var site = new ClientSite();
        await using var client = new WebSocketTransport(site, WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{host.BoundPort}/game"), "http://localhost");
        Assert.False(client.IsConnected);
        await client.JoinAsync("ignored", 0, "ignored"); Assert.True(client.IsConnected); Assert.Equal("ws", client.ProtocolName);
        await P1048WebSocketTests.Until(() => serverSite.Joins.Count == 1);
        var id = serverSite.Joins.Single();
        for (short i = 0; i < 10; i++)
        {
            byte[] packet = FedPacket.Pack([new PositionReq { SideId = i }.Encode()]);
            client.Send(ConnectionId.Server, packet, true); host.Send(id, packet, true);
        }
        await P1048WebSocketTests.Until(() => site.Packets.Count == 10 && serverSite.Receives.Count == 10);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (short)i), site.Packets.Select(p => PositionReq.Decode(FedPacket.Unpack(p).Single()).SideId));
        Assert.Equal(site.Packets.Select(Convert.ToHexString), serverSite.Receives.Select(p => Convert.ToHexString(p.Data)));
        host.ClosePeer(id); await P1048WebSocketTests.Until(() => site.Lost == 1);
        Assert.False(client.IsConnected); Assert.Equal("lost", site.Events.Last());
        Assert.DoesNotContain("join", site.Events); Assert.DoesNotContain("left", site.Events);
        client.Shutdown(); Assert.Equal(1, site.Lost);
    }
    [Fact]
    public async Task Intentional_shutdown_and_failed_handshake_do_not_report_loss()
    {
        var serverSite = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(serverSite, P1048WebSocketTests.Settings()); host.Host(0, IPAddress.Loopback);
        var endpoint = WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{host.BoundPort}/game");
        var site = new ClientSite();
        await using (var client = new WebSocketTransport(site, endpoint, "http://localhost"))
        { await client.JoinAsync("", 0, ""); client.Shutdown(); client.Shutdown(); Assert.False(client.IsConnected); }
        Assert.Equal(0, site.Lost);
        await using var failed = new WebSocketTransport(site, endpoint, "http://wrong.invalid");
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => failed.JoinAsync("", 0, ""));
        Assert.False(failed.IsConnected); Assert.Equal(0, site.Lost);
    }
    [Fact]
    public async Task Cancelled_establishment_is_not_a_published_session()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var site = new ClientSite();
        await using var client = new WebSocketTransport(site, WebSocketEndpoint.FromConfiguration("ws://127.0.0.1:1/game"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.JoinAsync("", 0, "", cancelled.Token));
        Assert.False(client.IsConnected); Assert.Equal(0, site.Lost);
    }
    [Theory]
    [InlineData(4, true)] [InlineData(65536, true)] [InlineData(65537, false)] [InlineData(3, false)]
    public async Task Receive_bound_accepts_exact_cap_and_rejects_short_or_oversized_messages(int bytes, bool accepted)
    {
        var serverSite = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(serverSite, P1048WebSocketTests.Settings()); host.Host(0, IPAddress.Loopback);
        var site = new ClientSite();
        await using var client = new WebSocketTransport(site, WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{host.BoundPort}/game"), "http://localhost");
        await client.JoinAsync("", 0, "");
        await P1048WebSocketTests.Until(() => serverSite.Joins.Count == 1);
        host.Send(serverSite.Joins.Single(), new byte[bytes], true);
        await P1048WebSocketTests.Until(() => site.Packets.Count != 0 || site.Lost != 0);
        Assert.Equal(accepted ? 1 : 0, site.Packets.Count);
        Assert.Equal(accepted ? 0 : 1, site.Lost);
        if (accepted) Assert.Equal(bytes, site.Packets.Single().Length);
    }
    [Fact]
    public async Task Session_resume_freezes_route_and_origin_after_UI_selection_changes()
    {
        var serverSite = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(serverSite, P1048WebSocketTests.Settings()); host.Host(0, IPAddress.Loopback);
        var first = WebSocketEndpoint.FromServerListing($"ws://127.0.0.1:{host.BoundPort}/game");
        serverSite.Receive = (id, _) => host.Send(id, FedPacket.Pack(new LogonAck { FValidated = true, ShipID = 1 }.Encode()), true);
        await using var session = new Allegiance.ClientCore.Session.ConnectionSession { WebSocketEndpoint = first, WebSocketOrigin = "http://localhost" };
        session.EnableAutoReconnect(5);
        await session.ConnectAsync(host.BoundPort, "P1048B-resume");
        await P1048WebSocketTests.Until(() => serverSite.Joins.Count == 1);
        session.WebSocketEndpoint = WebSocketEndpoint.FromServerListing("ws://127.0.0.1:1/changed");
        session.WebSocketOrigin = "http://wrong.invalid";
        host.ClosePeer(serverSite.Joins.Single());
        await P1048WebSocketTests.Until(() => session.ResumeSuccesses == 1 && !session.Reconnecting);
        Assert.Equal("ws", session.ActiveTransportProtocol); Assert.Equal(2, serverSite.Joins.Count); Assert.False(session.Lost);
    }
    [Fact]
    public void Queue_replaces_in_place_preserves_guarantee_age_and_counts_inflight()
    {
        var queue = new WebSocketClientSendQueue(12, 3);
        Assert.True(queue.Enqueue([1, 1, 1, 1], 0, true, 1));
        Assert.True(queue.Enqueue([2, 2, 2, 2], 8, true, 2));
        Assert.True(queue.Enqueue([3, 3, 3, 3], 0, true, 3));
        Assert.True(queue.Enqueue([4, 4, 4, 4], 8, false, 100));
        Assert.Equal(12, queue.Bytes); Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.Take()!.Data[0]); Assert.Equal(12, queue.Bytes);
        Assert.False(queue.Enqueue([5], 0, true, 101)); // in-flight still owns budget
        queue.Complete(); var replacement = queue.Take()!;
        Assert.Equal(4, replacement.Data[0]); Assert.True(replacement.Guaranteed); Assert.Equal(2, replacement.Since);
        queue.Complete(); Assert.Equal(3, queue.Take()!.Data[0]); queue.Complete(); Assert.Equal(0, queue.Bytes);
    }
    [Fact]
    public void Queue_sheds_only_unsent_non_guaranteed_streams_and_key_zero_stays_reliable()
    {
        var queue = new WebSocketClientSendQueue(8, 2);
        Assert.True(queue.Enqueue([1, 1, 1, 1], 7, false, 0));
        Assert.True(queue.Enqueue([2, 2, 2, 2], 0, false, 0));
        Assert.True(queue.Enqueue([3, 3, 3, 3], 0, false, 0));
        Assert.Equal(2, queue.Take()!.Data[0]); queue.Complete();
        Assert.Equal(3, queue.Take()!.Data[0]);
        Assert.True(queue.Enqueue(new byte[20], 9, false, 0)); // dropped, no allocation retained
        Assert.Equal(4, queue.Bytes); Assert.False(queue.Enqueue(new byte[20], 0, false, 0));
    }
}

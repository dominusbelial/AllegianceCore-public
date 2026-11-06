using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Transport;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests.Transport;

public sealed class P936StunCodecTests
{
    [Theory]
    [InlineData("192.0.2.27", 54321)]
    [InlineData("2001:db8::1234", 6073)]
    public void Binding_roundtrip_uses_cookie_transaction_and_XOR_address(string address, int port)
    {
        var request = StunBinding.CreateRequest();
        Assert.Equal(64, request.Length); Assert.True(StunBinding.IsRequest(request));
        var observed = new IPEndPoint(IPAddress.Parse(address), port);
        var receipt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var response = new byte[256];
        int length = StunBinding.WriteSuccess(request, observed, receipt, response);
        Assert.InRange(length, 52, request.Length);
        Assert.Equal((ushort)0x0020, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(20)));
        Assert.Equal((ushort)(port ^ 0x2112), BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(26)));
        Assert.True(StunBinding.TryReadSuccess(response.AsSpan(0, length), request.AsSpan(8, 12), out var mapped, out var proof));
        Assert.Equal(observed, mapped); Assert.Equal(Convert.ToHexString(receipt), proof);
        request[8] ^= 1;
        Assert.False(StunBinding.TryReadSuccess(response.AsSpan(0, length), request.AsSpan(8, 12), out _, out _));
    }
    [Fact]
    public void Malformed_oversize_wrong_cookie_length_type_and_required_attributes_are_rejected()
    {
        for (int size = 0; size < 64; size++) Assert.False(StunBinding.IsRequest(new byte[size]));
        var valid = StunBinding.CreateRequest();
        foreach (int offset in new[] { 0, 2, 4, 7, 22 })
        {
            var changed = valid.ToArray(); changed[offset] ^= 1;
            Assert.False(StunBinding.IsRequest(changed));
        }
        var huge = new byte[260]; valid.CopyTo(huge, 0);
        BinaryPrimitives.WriteUInt16BigEndian(huge.AsSpan(2), 240);
        Assert.False(StunBinding.IsRequest(huge));
        valid[20] = 0; valid[21] = 1;
        Assert.False(StunBinding.IsRequest(valid));
        var response = new byte[256]; var request = StunBinding.CreateRequest();
        int n = StunBinding.WriteSuccess(request, new(IPAddress.Loopback, 1234), new byte[16], response);
        response[25] = 3;
        Assert.False(StunBinding.TryReadSuccess(response.AsSpan(0, n), request.AsSpan(8, 12), out _, out _));
    }
    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    public void Responses_never_exceed_the_request_so_short_Binding_requests_are_dropped(string address)
    {
        // Unverified sources must not get amplification: a standard 20-byte
        // Binding would earn a 32/44-byte reply, so it gets none.
        var observed = new IPEndPoint(IPAddress.Parse(address), 6073);
        var output = new byte[256];
        var shortRequest = StunBinding.CreateRequest()[..20]; shortRequest[2] = 0; shortRequest[3] = 0;
        Assert.True(StunBinding.IsRequest(shortRequest));
        Assert.Equal(0, StunBinding.WriteSuccess(shortRequest, observed, new byte[16], output));
        var request = StunBinding.CreateRequest();
        Assert.InRange(StunBinding.WriteSuccess(request, observed, new byte[16], output), 1, request.Length);
    }
    [Theory]
    [InlineData("https://lobby.example.org", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://lobby.example.org", false)]
    [InlineData("https://user:secret@lobby.example.org", false)]
    public void Internet_signaling_requires_TLS_except_loopback(string url, bool valid)
    {
        if (valid) NatTraversalClient.ValidateLobbyUrl(url);
        else Assert.Throws<ArgumentException>(() => NatTraversalClient.ValidateLobbyUrl(url));
    }
}

[Collection("LiveNetwork")]
public sealed class P936TraversalTests
{
    private sealed class Site : ITransportSite
    {
        public readonly ConcurrentQueue<ConnectionId> Joins = new();
        public readonly ConcurrentQueue<byte[]> Data = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joins.Enqueue(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Data.Enqueue(data.ToArray());
        public void OnSessionLost() { }
    }
    [Theory]
    [InlineData(P936NatKind.FullCone)]
    [InlineData(P936NatKind.PortRestricted)]
    public async Task Same_game_sockets_punch_both_NATs_and_deliver_reliable_bidirectional_records(P936NatKind kind)
    {
        using var primary = new P936BindingFake(); using var secondary = new P936BindingFake();
        var hostSite = new Site(); var clientSite = new Site();
        using var host = new UdpTransport(hostSite) { RequireIntroductions = true, AllowUnintroducedLoopback = false }; host.Host();
        using var client = new UdpTransport(clientSite); client.PrepareTraversal();
        using var hostNat = new P936NatShim(host, kind); using var clientNat = new P936NatShim(client, kind);
        var servers = (primary.Endpoint, secondary.Endpoint);
        var hostMapping = await NatTraversalClient.DiscoverStableAsync(host, servers);
        var clientMapping = await NatTraversalClient.DiscoverStableAsync(client, servers);
        var secret = Guid.NewGuid().ToString("N"); var expires = DateTimeOffset.UtcNow.AddSeconds(10);
        await Task.WhenAll(host.PunchAsync(new(NatEndpoint.From(clientMapping.Endpoint), secret, expires)),
            client.PunchAsync(new(NatEndpoint.From(hostMapping.Endpoint), secret, expires)));
        ushort beforeJoin = client.SocketPort;
        client.Join(hostMapping.Endpoint.Address.ToString(), (ushort)hostMapping.Endpoint.Port, "P936 pilot");
        Assert.Equal(beforeJoin, client.SocketPort);
        await Until(() => hostSite.Joins.Count == 1);
        byte[] request = [1, 2, 3, 4, 5]; byte[] reply = [9, 8, 7];
        client.Send(ConnectionId.Server, request, true);
        host.Send(hostSite.Joins.Single(), reply, true);
        await Until(() => hostSite.Data.Count == 1 && clientSite.Data.Count == 1);
        Assert.Equal(request, hostSite.Data.Single()); Assert.Equal(reply, clientSite.Data.Single());
        Assert.True(hostNat.Forwarded > 0 && clientNat.Forwarded > 0);
    }
    [Fact]
    public async Task Symmetric_NAT_is_detected_on_either_game_socket_with_actionable_failure()
    {
        using var primary = new P936BindingFake(); using var secondary = new P936BindingFake();
        using var host = new UdpTransport(new Site()); host.Host();
        using var client = new UdpTransport(new Site()); client.PrepareTraversal();
        using var hostNat = new P936NatShim(host, P936NatKind.Symmetric);
        using var clientNat = new P936NatShim(client, P936NatKind.Symmetric);
        foreach (var socket in new[] { host, client })
        {
            var failure = await Assert.ThrowsAsync<DirectConnectionException>(() => NatTraversalClient.DiscoverStableAsync(socket, (primary.Endpoint, secondary.Endpoint)));
            Assert.Contains("Direct connection not possible", failure.Message); Assert.Contains("Tailscale", failure.Message);
        }
    }
    [Fact]
    public async Task Unintroduced_game_Hello_and_unexpected_or_expired_probes_receive_no_reply()
    {
        using var primary = new P936BindingFake();
        var site = new Site();
        using var host = new UdpTransport(site) { RequireIntroductions = true, AllowUnintroducedLoopback = false }; host.Host();
        using var nat = new P936NatShim(host, P936NatKind.FullCone);
        var mapped = await host.DiscoverAsync(primary.Endpoint);
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var hello = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(hello, UdpTransport.Magic); hello[4] = 1;
        await stranger.SendAsync(hello, mapped.Endpoint);
        var probe = new byte[32]; BinaryPrimitives.WriteUInt32LittleEndian(probe, 0x36333950); probe[4] = 1; probe[5] = 1;
        await stranger.SendAsync(probe, mapped.Endpoint);
        using var timeout = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stranger.ReceiveAsync(timeout.Token));
        Assert.Empty(site.Joins); Assert.Empty(site.Data);
        var expired = new PunchIntroduction(new("127.0.0.1", ((IPEndPoint)stranger.Client.LocalEndPoint!).Port), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(-1));
        await Assert.ThrowsAsync<IOException>(() => host.PunchAsync(expired));
        Assert.True(nat.Forwarded >= 3); // Binding + unsolicited packets reached the transport
    }
    [Fact]
    public async Task Blocked_discovery_is_bounded_reports_advice_and_cancellation_releases_the_socket()
    {
        using var blackhole = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new UdpTransport(new Site()); client.PrepareTraversal();
        var endpoint = (IPEndPoint)blackhole.Client.LocalEndPoint!;
        var failure = await Assert.ThrowsAsync<DirectConnectionException>(() => NatTraversalClient.DiscoverStableAsync(client, (endpoint, endpoint)));
        Assert.Contains("Tailscale", failure.Message);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DiscoverAsync(endpoint, canceled.Token));
        ushort port = client.SocketPort;
        client.Shutdown();
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(5000);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

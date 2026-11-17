using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1048WebSocketTests
{
    internal sealed class Site : ITransportSite
    {
        internal readonly ConcurrentQueue<ConnectionId> Joins = new();
        internal readonly ConcurrentQueue<(ConnectionId Id, byte[] Data)> Receives = new();
        internal readonly ConcurrentQueue<string> Events = new();
        internal Action<ConnectionId, byte[]>? Receive { get; set; }
        internal int Left;
        public void OnPeerJoined(ConnectionId id, string name) { Assert.Equal("", name); Events.Enqueue("join"); Joins.Enqueue(id); }
        public void OnPeerLeft(ConnectionId id) { Events.Enqueue("leave"); Interlocked.Increment(ref Left); }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        { Events.Enqueue("receive"); var bytes = data.ToArray(); Receives.Enqueue((from, bytes)); Receive?.Invoke(from, bytes); }
    }
    internal static WebSocketConfig Settings() => new()
    {
        Enabled = true, Port = 0, AllowedOrigins = ["http://localhost"], AllowLoopbackDevelopment = true,
    };
    internal static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        using var deadline = new CancellationTokenSource(milliseconds);
        while (!condition() && !deadline.IsCancellationRequested) await Task.Delay(5);
        Assert.True(condition(), "WebSocket condition did not become true before the bounded deadline");
    }
    internal static async Task<ClientWebSocket> Connect(ushort port, string? origin = "http://localhost",
        string? protocol = WebSocketServerTransport.Subprotocol, IReadOnlyDictionary<string, string>? headers = null)
    {
        var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        if (protocol is not null) socket.Options.AddSubProtocol(protocol);
        if (headers is not null) foreach (var h in headers) socket.Options.SetRequestHeader(h.Key, h.Value);
        using var timeout = new CancellationTokenSource(5000);
        try { await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/game"), timeout.Token); return socket; }
        catch { socket.Dispose(); throw; }
    }
    internal static async Task Send(ClientWebSocket socket, byte[] data, bool end = true, WebSocketMessageType type = WebSocketMessageType.Binary)
    {
        using var timeout = new CancellationTokenSource(5000);
        await socket.SendAsync(data, type, end, timeout.Token);
    }
    internal static async Task<byte[]> Read(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(5000);
        using var stream = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Binary, part.MessageType);
            stream.Write(buffer, 0, part.Count);
            Assert.True(stream.Length <= 16 * 1024 * 1024);
            if (part.EndOfMessage) return stream.ToArray();
        }
    }
    private static WebSocketServerTransport Host(Site site, WebSocketConfig? settings = null)
    { var host = new WebSocketServerTransport(site, settings ?? Settings()); host.Host(0, IPAddress.Loopback); return host; }

    [Fact]
    public async Task Fragmented_FedPacket_is_reassembled_once_after_join_and_sent_without_a_greeting()
    {
        var site = new Site(); using var host = Host(site); using var client = await Connect(host.BoundPort);
        byte[] packet = FedPacket.Pack([new PositionReq { SideId = 0 }.Encode()]);
        await Send(client, packet[..2], false); await Send(client, packet[2..5], false);
        Assert.Empty(site.Receives);
        await Send(client, packet[5..]); await Until(() => site.Receives.Count == 1);
        Assert.Equal(packet, site.Receives.Single().Data);
        Assert.Equal(new[] { "join", "receive" }, site.Events);
        var id = site.Joins.Single(); Assert.False(host.IsPeerLoopback(id)); Assert.Equal(IPAddress.Loopback, host.PeerAddress(id));
        host.SetPeerHuman(id, true); Assert.True(host.IsPeerHuman(id));
        host.Send(id, packet, true); Assert.Equal(packet, await Read(client));
        host.ClosePeerAfterFlush(id); await Until(() => site.Left == 1);
        Assert.Equal(new[] { "join", "receive", "leave" }, site.Events);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Oversized_pre_and_post_logon_messages_close_even_without_EndOfMessage(bool authenticated, bool end)
    {
        var site = new Site(); using var host = Host(site); using var client = await Connect(host.BoundPort);
        await Until(() => site.Joins.Count == 1); var id = site.Joins.Single();
        if (authenticated) host.MarkPeerAuthenticated(id);
        int cap = authenticated ? WebSocketServerTransport.InboundMessageCap : Settings().PreLogonMaxMessageBytes;
        try { await Send(client, new byte[cap + 1], end); } catch (WebSocketException) { }
        await Until(() => site.Left == 1);
        Assert.Empty(site.Receives); Assert.Equal(1, host.DrainDisconnectCounts()[DisconnectReason.ProtocolError]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Exact_message_cap_is_accepted_before_and_after_logon(bool authenticated)
    {
        var site = new Site(); using var host = Host(site); using var client = await Connect(host.BoundPort);
        await Until(() => site.Joins.Count == 1);
        if (authenticated) host.MarkPeerAuthenticated(site.Joins.Single());
        var packet = new byte[authenticated ? WebSocketServerTransport.InboundMessageCap : Settings().PreLogonMaxMessageBytes];
        await Send(client, packet); await Until(() => site.Receives.Count == 1);
        Assert.Equal(packet.Length, site.Receives.Single().Data.Length);
    }

    [Fact]
    public async Task Never_ending_empty_fragments_hit_fragment_count_cap()
    {
        var settings = Settings(); settings.MaxFragments = 4;
        var site = new Site(); using var host = Host(site, settings); using var client = await Connect(host.BoundPort);
        try { for (int i = 0; i < 5; i++) await Send(client, [], false); } catch (WebSocketException) { }
        await Until(() => site.Left == 1); Assert.Empty(site.Receives);
        Assert.Equal(1, host.DrainDisconnectCounts()[DisconnectReason.ProtocolError]);
    }

    [Fact]
    public async Task Stalled_fragment_hits_progress_deadline()
    {
        var settings = Settings(); settings.FragmentProgressTimeoutSeconds = .15;
        var site = new Site(); using var host = Host(site, settings); using var client = await Connect(host.BoundPort);
        await Send(client, [1], false); await Until(() => site.Left == 1);
        Assert.Empty(site.Receives);
    }

    [Fact]
    public async Task Continuous_fragment_progress_cannot_restart_absolute_message_deadline()
    {
        var settings = Settings(); settings.MessageTimeoutSeconds = .15; settings.FragmentProgressTimeoutSeconds = 2;
        var site = new Site(); using var host = Host(site, settings); using var client = await Connect(host.BoundPort);
        try
        {
            for (int i = 0; i < 30 && site.Left == 0; i++) { await Send(client, [1], false); await Task.Delay(20); }
        }
        catch (WebSocketException) { }
        await Until(() => site.Left == 1); Assert.Empty(site.Receives);
    }

    [Fact]
    public async Task Authentication_deadline_is_absolute_despite_complete_valid_packets()
    {
        var settings = Settings(); settings.AuthenticationTimeoutSeconds = .15;
        var site = new Site(); using var host = Host(site, settings); using var client = await Connect(host.BoundPort);
        try
        {
            byte[] packet = FedPacket.Pack([new PositionReq { SideId = 0 }.Encode()]);
            for (int i = 0; i < 30 && site.Left == 0; i++) { await Send(client, packet); await Task.Delay(20); }
        }
        catch (WebSocketException) { }
        await Until(() => site.Left == 1);
        Assert.Equal(1, host.DrainDisconnectCounts()[DisconnectReason.PreLogonTimeout]);
    }

    [Fact]
    public async Task Text_frames_are_refused()
    {
        var site = new Site(); using var host = Host(site); using var client = await Connect(host.BoundPort);
        await Send(client, Encoding.UTF8.GetBytes("not FedPacket"), type: WebSocketMessageType.Text);
        await Until(() => site.Left == 1); Assert.Empty(site.Receives);
    }

    [Theory]
    [InlineData(null)] [InlineData("http://wrong.invalid")] [InlineData("null")]
    public async Task Origin_is_required_and_exactly_allowlisted(string? origin)
    {
        var site = new Site(); using var host = Host(site);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort, origin));
        Assert.Empty(site.Joins);
    }

    [Theory]
    [InlineData(null)] [InlineData("allegiance-fed-v2")]
    public async Task Missing_and_unknown_subprotocols_are_refused(string? protocol)
    {
        var site = new Site(); using var host = Host(site);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort, protocol: protocol)); Assert.Empty(site.Joins);
    }

    [Fact]
    public async Task Plaintext_requires_explicit_loopback_development()
    {
        var settings = Settings(); settings.AllowLoopbackDevelopment = false; settings.AllowedOrigins = ["https://lobby.invalid"];
        var site = new Site(); using var host = Host(site, settings);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort, "https://lobby.invalid")); Assert.Empty(site.Joins);
    }

    [Theory]
    [InlineData("198.51.100.8")] [InlineData("127.0.0.1")]
    public async Task Only_explicit_trusted_proxy_supplies_source_and_TLS_but_never_local_privileges(string source)
    {
        var settings = Settings(); settings.AllowLoopbackDevelopment = false;
        settings.KnownProxies = ["127.0.0.1"]; settings.AllowedOrigins = ["https://lobby.invalid"];
        var site = new Site(); using var host = Host(site, settings);
        using var client = await Connect(host.BoundPort, "https://lobby.invalid", headers: new Dictionary<string, string>
        { ["X-Forwarded-For"] = source, ["X-Forwarded-Proto"] = "https" });
        await Until(() => site.Joins.Count == 1);
        Assert.Equal(IPAddress.Parse(source), host.PeerAddress(site.Joins.Single()));
        Assert.False(host.IsPeerLoopback(site.Joins.Single()));
    }

    [Fact]
    public async Task Untrusted_headers_cannot_supply_external_source_or_TLS()
    {
        var settings = Settings(); var site = new Site(); using var host = Host(site, settings);
        using var client = await Connect(host.BoundPort, headers: new Dictionary<string, string>
        { ["X-Forwarded-For"] = "198.51.100.8", ["X-Forwarded-Proto"] = "https", ["X-Forwarded-Host"] = "evil.invalid" });
        await Until(() => site.Joins.Count == 1); Assert.Equal(IPAddress.Loopback, host.PeerAddress(site.Joins.Single()));
        settings = Settings(); settings.AllowLoopbackDevelopment = false; settings.AllowedOrigins = ["https://lobby.invalid"];
        using var tlsHost = Host(new Site(), settings);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(tlsHost.BoundPort, "https://lobby.invalid", headers: new Dictionary<string, string>
        { ["X-Forwarded-For"] = "127.0.0.1", ["X-Forwarded-Proto"] = "https" }));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Connection_flood_hits_global_or_per_IP_cap_and_slots_are_reclaimed(bool global)
    {
        var settings = Settings(); settings.MaxConnections = global ? 2 : 8; settings.MaxConnectionsPerIp = global ? 8 : 2;
        var site = new Site(); using var host = Host(site, settings);
        using var a = await Connect(host.BoundPort); using var b = await Connect(host.BoundPort);
        await Until(() => site.Joins.Count == 2);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort)); Assert.Equal(2, site.Joins.Count);
        a.Abort(); await Until(() => host.PhysicalAdmission.Live == 1 && host.SourceAdmission.Live == 1);
        using var replacement = await Connect(host.BoundPort); await Until(() => site.Joins.Count == 3);
        Assert.Equal(2, host.SamplePeerQueues().Peers);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Rejected_upgrade_handshake_flood_is_capped_by_source_and_globally(bool global)
    {
        var settings = Settings(); settings.HandshakeAttemptsPerWindow = global ? 2 : 20;
        settings.HandshakeAttemptsPerIpPerWindow = global ? 20 : 2;
        var site = new Site(); using var host = Host(site, settings);
        for (int i = 0; i < 2; i++) await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort, "http://wrong.invalid"));
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort)); Assert.Empty(site.Joins);
        Assert.True(host.PhysicalAdmission.Sources <= 1);
    }

    [Fact]
    public async Task Partial_HTTP_handshake_has_absolute_deadline_and_pending_cap()
    {
        var settings = Settings(); settings.HandshakeTimeoutSeconds = .2; settings.MaxPendingHandshakes = 1;
        var site = new Site(); using var host = Host(site, settings);
        using var raw = new TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, host.BoundPort);
        await raw.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /game HTTP/1.1\r\nHost: localhost\r\n"));
        await Until(() => host.PhysicalAdmission.Pending == 1);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort));
        await Until(() => host.PhysicalAdmission.Live == 0);
        using var healthy = await Connect(host.BoundPort); await Until(() => site.Joins.Count == 1);
        Assert.Equal(0, host.PhysicalAdmission.Pending);
    }

    [Fact]
    public void Admission_table_is_bounded_windows_expire_and_live_slots_do_not_expire()
    {
        var settings = Settings(); settings.MaxTrackedSources = 2; settings.HandshakeAttemptsPerWindow = 100;
        var guard = new WebSocketAdmission(settings); var a = IPAddress.Parse("198.51.100.1");
        Assert.True(guard.TryAttempt(a, 0)); using var lease = guard.TryEnter(a); Assert.NotNull(lease); lease.Promoted();
        Assert.True(guard.TryAttempt(IPAddress.Parse("198.51.100.2"), 0));
        Assert.False(guard.TryAttempt(IPAddress.Parse("198.51.100.3"), 0)); Assert.Equal(2, guard.Snapshot.Sources);
        Assert.True(guard.TryAttempt(IPAddress.Parse("198.51.100.3"), 60_001));
        Assert.Equal(1, guard.Snapshot.Live); Assert.Equal(2, guard.Snapshot.Sources);
    }

    [Fact]
    public void Queue_preserves_zero_key_order_and_both_latest_wins_streams_and_guaranteed_age()
    {
        var settings = Settings(); var queue = new WebSocketSendQueue(settings);
        Assert.Equal(DisconnectReason.None, queue.Enqueue([1], 0, true, 0));
        queue.Enqueue([2], 17, false, 10); queue.Enqueue([3], 18, true, 20);
        queue.Enqueue([4], 0, false, 30); queue.Enqueue([5], 17, false, 40); queue.Enqueue([6], 18, true, 50);
        Assert.Equal((2, 2, 4L, 100L), queue.Sample(100));
        foreach (byte b in new byte[] { 1, 5, 6, 4 }) { Assert.Equal(new byte[] { b }, queue.Take()!.Data); queue.Complete(); }
        Assert.Null(queue.Take()); Assert.Equal(0, queue.Sample(100).Bytes); queue.Ready.Dispose();
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Queue_caps_include_inflight_and_shed_only_non_guaranteed(bool bytes)
    {
        var settings = Settings(); settings.MaxQueuedBytes = bytes ? 2 : 100; settings.MaxQueuedEntries = bytes ? 100 : 2;
        var queue = new WebSocketSendQueue(settings);
        queue.Enqueue([1], 0, true, 0); queue.Take(); queue.Enqueue([2], 7, false, 0);
        Assert.Equal(DisconnectReason.None, queue.Enqueue([3], 8, true, 0));
        Assert.Equal((1, 1, 2L, 10L), queue.Sample(10));
        Assert.Equal(DisconnectReason.QueueHardCap, queue.Enqueue([4], 0, true, 0));
        queue.Complete(); Assert.Equal(new byte[] { 3 }, queue.Take()!.Data); queue.Ready.Dispose();
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Slow_consumer_is_disconnected_on_guaranteed_age_or_send_deadline(bool age)
    {
        var settings = Settings(); settings.MaxGuaranteedAgeSeconds = age ? .15 : 10; settings.SendTimeoutSeconds = age ? 10 : .15;
        var site = new Site(); using var host = Host(site, settings);
        host.BeforeWriteForTesting = token => Task.Delay(Timeout.Infinite, token);
        using var client = await Connect(host.BoundPort); await Until(() => site.Joins.Count == 1);
        host.SetPeerHuman(site.Joins.Single(), true);
        host.SendGuaranteedCoalescing(site.Joins.Single(), [1], 10);
        await Until(() => site.Left == 1);
        Assert.Equal(1, host.DrainDisconnectCounts()[age ? DisconnectReason.NoWriteProgress : DisconnectReason.SendTimeout]);
        Assert.Equal(0, host.SamplePeerQueues().TotalQueuedBytes);
    }

    [Fact]
    public async Task Slow_consumer_is_disconnected_on_entry_or_byte_overflow()
    {
        var settings = Settings(); settings.MaxQueuedEntries = 2;
        var site = new Site(); using var host = Host(site, settings);
        host.BeforeWriteForTesting = token => Task.Delay(Timeout.Infinite, token);
        using var client = await Connect(host.BoundPort); await Until(() => site.Joins.Count == 1);
        var id = site.Joins.Single();
        host.Send(id, [1], true); host.Send(id, [2], true); host.Send(id, [3], true);
        await Until(() => site.Left == 1); Assert.Equal(1, host.DrainDisconnectCounts()[DisconnectReason.QueueHardCap]);
    }

    [Fact]
    public void Replacing_a_guaranteed_stream_cannot_restart_its_oldest_age()
    {
        var queue = new WebSocketSendQueue(Settings());
        queue.Enqueue([1], 7, true, 0); queue.Enqueue([2], 7, true, 99);
        Assert.Equal(100, queue.Sample(100).Age); Assert.Equal(new byte[] { 2 }, queue.Take()!.Data);
        queue.Complete(); queue.Ready.Dispose();
    }

    [Fact]
    public async Task Human_marking_does_not_authenticate_but_validated_logon_ends_auth_deadline()
    {
        var settings = Settings(); settings.AuthenticationTimeoutSeconds = .2;
        var site = new Site(); using var host = Host(site, settings);
        using var a = await Connect(host.BoundPort); using var b = await Connect(host.BoundPort);
        await Until(() => site.Joins.Count == 2); var ids = site.Joins.ToArray();
        host.SetPeerHuman(ids[0], true); host.MarkPeerAuthenticated(ids[1]);
        await Until(() => site.Left == 1); Assert.True(host.IsPeerClosed(ids[0])); Assert.False(host.IsPeerClosed(ids[1]));
        await Send(b, FedPacket.Pack(Array.Empty<byte[]>())); await Until(() => site.Receives.Count == 1);
        Assert.Equal(ids[1], site.Receives.Single().Id);
    }

    [Fact]
    public async Task A_smaller_existing_transport_frame_cap_is_not_weakened()
    {
        uint old = TcpTransport.ServerInboundMaxFrame;
        try
        {
            TcpTransport.ServerInboundMaxFrame = 512;
            var site = new Site(); using var host = Host(site); using var client = await Connect(host.BoundPort);
            await Send(client, new byte[512]); await Until(() => site.Receives.Count == 1);
            await Send(client, new byte[513]); await Until(() => site.Left == 1);
            Assert.Single(site.Receives);
        }
        finally { TcpTransport.ServerInboundMaxFrame = old; }
    }

    [Fact]
    public async Task Multiple_subprotocols_and_duplicate_Origin_headers_are_rejected()
    {
        var site = new Site(); using var host = Host(site);
        using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", "http://localhost");
        socket.Options.AddSubProtocol(WebSocketServerTransport.Subprotocol); socket.Options.AddSubProtocol("unknown-version");
        using var timeout = new CancellationTokenSource(5000);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{host.BoundPort}/game"), timeout.Token));
        using var raw = new TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, host.BoundPort);
        var request = Encoding.ASCII.GetBytes("GET /game HTTP/1.1\r\nHost: localhost\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Protocol: allegiance-fed-v1\r\nOrigin: http://localhost\r\nOrigin: http://wrong.invalid\r\n\r\n");
        await raw.GetStream().WriteAsync(request, timeout.Token); var response = new byte[2048];
        int n = await raw.GetStream().ReadAsync(response, timeout.Token);
        Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(response, 0, n)); Assert.Empty(site.Joins);
    }

    [Fact]
    public void Admission_global_and_IP_windows_reset_at_the_absolute_boundary()
    {
        var settings = Settings(); settings.HandshakeAttemptsPerWindow = 2; settings.HandshakeAttemptsPerIpPerWindow = 1;
        var guard = new WebSocketAdmission(settings); var a = IPAddress.Parse("198.51.100.1"); var b = IPAddress.Parse("198.51.100.2");
        Assert.True(guard.TryAttempt(a, 0)); Assert.False(guard.TryAttempt(a, 1)); Assert.True(guard.TryAttempt(b, 2));
        Assert.False(guard.TryAttempt(b, 59999)); Assert.True(guard.TryAttempt(a, 60000));
        Assert.False(guard.TryAttempt(b, 60000)); Assert.True(guard.TryAttempt(b, 60002));
    }

    [Fact]
    public async Task WebSocket_accounting_uses_native_message_counts_and_actual_carrier_overhead()
    {
        var site = new Site(); var egress = new EgressAccounting(); var reliable = new ReliableTelemetry(); var counters = new ServerCounters();
        using var host = new WebSocketServerTransport(site, Settings()) { Egress = egress, Reliable = reliable, ServerCounters = counters };
        host.Host(0, IPAddress.Loopback); using var client = await Connect(host.BoundPort); await Until(() => site.Joins.Count == 1);
        var id = site.Joins.Single(); host.SetPeerHuman(id, true);
        byte[] message = new PositionReq { SideId = 0 }.Encode(), packet = FedPacket.Pack([message]);
        host.Send(id, packet, true); Assert.Equal(packet, await Read(client));
        await Until(() => host.TotalWrittenBytes == packet.Length + 2);
        var window = egress.DrainWindow();
        Assert.Equal(message.Length, window.Rows.Single(r => r.Fmid == PositionReq.Fmid).SentBytes);
        Assert.Equal(6, window.Rows.Single(r => r.Fmid == EgressAccounting.OverheadFmid).SentBytes);
        Assert.Equal(packet.Length + 2, window.Rows.Sum(r => r.SentBytes));
        Assert.Equal(1, counters.CPacketsOut); Assert.Equal(message.Length, counters.CBytesOut);
        Assert.Equal(1, reliable.DrainWindow().OneShotParked);
    }

    [Fact]
    public void Guaranteed_queue_entries_are_never_shed_and_latest_wins_is_reported()
    {
        var events = new List<(byte Value, EgressEvent Event)>(); var settings = Settings(); settings.MaxQueuedEntries = 2;
        var queue = new WebSocketSendQueue(settings, (data, key, guaranteed, ev) => events.Add((data[0], ev)));
        queue.Enqueue([1], 0, true, 0); queue.Enqueue([2], 8, true, 0); queue.Enqueue([3], 8, true, 1);
        Assert.Equal(DisconnectReason.QueueHardCap, queue.Enqueue([4], 0, true, 2));
        Assert.Equal(new[] { ((byte)2, EgressEvent.Coalesced) }, events);
        Assert.Equal(DisconnectReason.None, queue.Enqueue([5], 9, false, 2));
        Assert.Equal(new[] { ((byte)2, EgressEvent.Coalesced), ((byte)5, EgressEvent.Dropped) }, events);
        Assert.Equal(new byte[] { 1 }, queue.Take()!.Data); queue.Complete();
        Assert.Equal(new byte[] { 3 }, queue.Take()!.Data); queue.Complete(); queue.Ready.Dispose();
    }

    [Fact]
    public async Task Framework_auto_forwarding_environment_cannot_bypass_explicit_proxy_trust()
    {
        const string variable = "ASPNETCORE_FORWARDEDHEADERS_ENABLED"; string? old = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "true");
            var settings = Settings(); settings.AllowLoopbackDevelopment = false; settings.AllowedOrigins = ["https://lobby.invalid"];
            var site = new Site(); using var host = Host(site, settings);
            await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort, "https://lobby.invalid", headers: new Dictionary<string, string>
            { ["X-Forwarded-For"] = "198.51.100.8", ["X-Forwarded-Proto"] = "https" }));
            Assert.Empty(site.Joins);
        }
        finally { Environment.SetEnvironmentVariable(variable, old); }
    }

    [Fact]
    public async Task Close_after_flush_retires_input_immediately_and_delivers_all_final_replies_in_order()
    {
        var site = new Site(); using var host = Host(site);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeWriteForTesting = token => release.Task.WaitAsync(token);
        using var client = await Connect(host.BoundPort); await Until(() => site.Joins.Count == 1);
        var id = site.Joins.Single(); host.Send(id, [1], true); host.Send(id, [2], true);
        host.ClosePeerAfterFlush(id); Assert.True(host.IsPeerClosed(id));
        await Send(client, FedPacket.Pack([new PositionReq { SideId = 0 }.Encode()]));
        host.Send(id, [3], true); release.SetResult();
        Assert.Equal(new byte[] { 1 }, await Read(client)); Assert.Equal(new byte[] { 2 }, await Read(client));
        using var timeout = new CancellationTokenSource(5000);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        await Until(() => site.Left == 1); Assert.Empty(site.Receives);
        Assert.Equal(1, host.DrainDisconnectCounts()[DisconnectReason.Shutdown]);
    }

    [Fact]
    public void A_rate_blocked_source_cannot_exhaust_the_global_attempt_budget_for_other_sources()
    {
        var settings = Settings(); settings.HandshakeAttemptsPerWindow = 2; settings.HandshakeAttemptsPerIpPerWindow = 1;
        var guard = new WebSocketAdmission(settings); var abusive = IPAddress.Parse("198.51.100.1");
        Assert.True(guard.TryAttempt(abusive, 0));
        for (int i = 0; i < 16; i++) Assert.False(guard.TryAttempt(abusive, 1));
        Assert.True(guard.TryAttempt(IPAddress.Parse("198.51.100.2"), 2));
        Assert.False(guard.TryAttempt(IPAddress.Parse("198.51.100.3"), 3));
    }

    [SkippableFact]
    public async Task Physical_source_rate_rejections_leave_admission_capacity_for_another_loopback_source()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressNotAvailable)
        { Skip.If(true, "This OS has no secondary loopback address; the pure source/global quota test still runs"); }
        var settings = Settings(); settings.HandshakeAttemptsPerWindow = 2; settings.HandshakeAttemptsPerIpPerWindow = 1;
        var site = new Site(); using var host = Host(site, settings);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort, "http://wrong.invalid"));
        for (int i = 0; i < 4; i++) await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort));
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await socket.ConnectAsync(IPAddress.Loopback, host.BoundPort, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        } };
        using var invoker = new HttpMessageInvoker(handler); using var healthy = new ClientWebSocket();
        healthy.Options.SetRequestHeader("Origin", "http://localhost"); healthy.Options.AddSubProtocol(WebSocketServerTransport.Subprotocol);
        using var timeout = new CancellationTokenSource(5000);
        await healthy.ConnectAsync(new Uri($"ws://127.0.0.1:{host.BoundPort}/game"), invoker, timeout.Token);
        await Until(() => site.Joins.Count == 1);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), host.PeerAddress(site.Joins.Single()));
    }

    [Fact]
    public async Task Shutdown_retires_all_sockets_and_stops_the_listener()
    {
        var site = new Site(); var host = Host(site);
        using var client = await Connect(host.BoundPort); await Until(() => site.Joins.Count == 1);
        host.Shutdown(); host.Shutdown(); Assert.False(host.IsConnected);
        Assert.Equal(1, site.Left); Assert.Equal(0, host.PhysicalAdmission.Live); Assert.Equal(0, host.SourceAdmission.Live);
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(host.BoundPort));
    }
}

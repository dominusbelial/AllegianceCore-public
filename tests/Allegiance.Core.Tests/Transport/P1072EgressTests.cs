using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1072EgressTests
{
    [Fact]
    public void Transport_windows_split_partial_tcp_frames_ws_drops_and_high_water()
    {
        var accounting = new EgressAccounting(true);
        byte[] packet = FedPacket.Pack([new PositionReq { SideId = 0 }.Encode()]);
        byte[] tcp = new byte[packet.Length + 5]; tcp[0] = 2; packet.CopyTo(tcp, 5);
        accounting.Record(tcp, false, false, EgressEvent.Sent, 0, 3);
        accounting.Record(tcp, false, false, EgressEvent.Sent, 3, tcp.Length - 3);
        accounting.RecordFedPacket(packet, false, false, EgressEvent.Sent);
        accounting.RecordFedPacket(packet, false, true, EgressEvent.Dropped);
        accounting.ObserveTransportQueue("tcp", 123, 4);
        accounting.ObserveTransportQueue("tcp", 100, 2);
        accounting.RecordTransportDisconnect("ws", DisconnectReason.ReadErrorOrEof, "idle");
        accounting.RecordTransportDisconnect("ws", DisconnectReason.ProtocolError, "fragment");
        var windows = accounting.DrainTransportWindows();
        var t = windows.Single(w => w.Protocol == "tcp");
        var ws = windows.Single(w => w.Protocol == "ws");
        Assert.Equal((1L, tcp.Length, 123L, 4L), (t.Sends, (int)t.Bytes, t.QueueBytesHighWater, t.QueueEntriesHighWater));
        Assert.Equal(1, ws.Sends); Assert.Equal(packet.Length + 2, ws.Bytes);
        Assert.Equal(1, ws.Drops); Assert.Equal(packet.Length + 2, ws.DroppedBytes);
        Assert.Equal(1, ws.Disconnects["IdleTimeout"]); Assert.Equal(1, ws.Disconnects["FragmentTimeout"]);
        Assert.All(accounting.DrainTransportWindows(), w => { Assert.Equal(0, w.Bytes); Assert.Empty(w.Disconnects); });
        Assert.NotEmpty(accounting.DrainWindow().Rows); // independent existing FMID records
        Assert.Empty(new EgressAccounting(false).DrainTransportWindows());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_ws_timeout_cause_is_measured_without_changing_security_reasons(bool fragment)
    {
        var egress = new EgressAccounting(true);
        var site = new P1048WebSocketTests.Site();
        var config = P1048WebSocketTests.Settings();
        config.IdleTimeoutSeconds = fragment ? 2 : .15; config.FragmentProgressTimeoutSeconds = .15;
        using var server = new WebSocketServerTransport(site, config) { Egress = egress };
        server.Host(0, System.Net.IPAddress.Loopback);
        using var client = await P1048WebSocketTests.Connect(server.BoundPort);
        if (fragment) await P1048WebSocketTests.Send(client, [1], false);
        await P1048WebSocketTests.Until(() => site.Left == 1);
        var ws = egress.DrainTransportWindows().Single(w => w.Protocol == "ws");
        Assert.Equal(1, ws.Disconnects[fragment ? "FragmentTimeout" : "IdleTimeout"]);
        Assert.Equal(1, server.DrainDisconnectCounts()[DisconnectReason.ReadErrorOrEof]);
    }

    [Fact]
    public async Task One_ip_fleet_hits_64_live_and_128_attempt_defaults_raised_config_admits_100()
    {
        async Task<int> Open(int count, WebSocketConfig config)
        {
            var site = new P1048WebSocketTests.Site();
            using var host = new WebSocketServerTransport(site, config);
            host.Host(0, System.Net.IPAddress.Loopback);
            var clients = new List<System.Net.WebSockets.ClientWebSocket>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    try { clients.Add(await P1048WebSocketTests.Connect(host.BoundPort)); }
                    catch (System.Net.WebSockets.WebSocketException) { break; }
                }
                return clients.Count;
            }
            finally { foreach (var client in clients) client.Dispose(); }
        }
        Assert.Equal(64, await Open(100, P1048WebSocketTests.Settings()));
        var raised = P1048WebSocketTests.Settings();
        raised.MaxConnectionsPerIp = 128; raised.HandshakeAttemptsPerIpPerWindow = 256;
        Assert.Equal(100, await Open(100, raised));
        // Live permits retire, but the same source's attempt budget persists.
        var settings = P1048WebSocketTests.Settings();
        var admission = new WebSocketAdmission(settings);
        for (int i = 0; i < 128; i++)
        {
            Assert.True(admission.TryAttempt(System.Net.IPAddress.Loopback, 1));
            using var lease = admission.TryEnter(System.Net.IPAddress.Loopback);
            Assert.NotNull(lease);
        }
        Assert.False(admission.TryAttempt(System.Net.IPAddress.Loopback, 1));
        var more = new WebSocketAdmission(raised);
        for (int i = 0; i < 200; i++) Assert.True(more.TryAttempt(System.Net.IPAddress.Loopback, 1));
    }
}

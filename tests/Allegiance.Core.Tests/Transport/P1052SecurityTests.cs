using System.Net;
using System.Net.WebSockets;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1052SecurityTests
{
    private static Dictionary<string, string> Forwarded(string source) => new()
    { ["X-Forwarded-For"] = source, ["X-Forwarded-Proto"] = "https" };

    [Fact]
    public async Task Two_forwarded_clients_each_get_their_full_attempt_and_connection_budget()
    {
        var settings = P1048WebSocketTests.Settings();
        settings.KnownProxies = ["127.0.0.1"]; settings.AllowLoopbackDevelopment = false;
        settings.AllowedOrigins = ["https://lobby.invalid"];
        settings.MaxConnectionsPerIp = 2; settings.HandshakeAttemptsPerIpPerWindow = 3;
        var site = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(site, settings); host.Host(0, IPAddress.Loopback);
        var held = new List<ClientWebSocket>();
        try
        {
            foreach (string source in new[] { "198.51.100.1", "198.51.100.2" })
            {
                for (int i = 0; i < 2; i++)
                    held.Add(await P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded(source)));
                // Third attempt fits the attempt budget but not the live cap.
                await Assert.ThrowsAsync<WebSocketException>(() => P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded(source)));
            }
            await P1048WebSocketTests.Until(() => site.Joins.Count == 4);
            foreach (var socket in held) socket.Dispose();
            await P1048WebSocketTests.Until(() => host.PhysicalAdmission.Live == 0 && host.SourceAdmission.Live == 0);
            // Disconnects release slots, never attempt credits.
            foreach (string source in new[] { "198.51.100.1", "198.51.100.2" })
                await Assert.ThrowsAsync<WebSocketException>(() => P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded(source)));
            Assert.Equal(4, site.Joins.Count);
        }
        finally { foreach (var socket in held) socket.Dispose(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Direct_client_still_has_per_IP_caps_and_cannot_forge_forwarding(bool attempts)
    {
        var settings = P1048WebSocketTests.Settings();
        // Proxy trust is configured, but this socket is not that proxy.
        settings.KnownProxies = ["192.0.2.10"];
        settings.MaxConnectionsPerIp = attempts ? 8 : 1;
        settings.HandshakeAttemptsPerIpPerWindow = attempts ? 1 : 8;
        var site = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(site, settings); host.Host(0, IPAddress.Loopback);
        // A forged HTTPS scheme cannot grant the direct plaintext exception.
        await Assert.ThrowsAsync<WebSocketException>(() => P1048WebSocketTests.Connect(host.BoundPort, headers: Forwarded("198.51.100.1")));
        Assert.Empty(site.Joins);
        // A separate direct-development listener proves both address retention
        // and live caps despite different forged client addresses.
        settings.KnownProxies = [];
        using var direct = new WebSocketServerTransport(site, settings); direct.Host(0, IPAddress.Loopback);
        using var first = await P1048WebSocketTests.Connect(direct.BoundPort, headers: Forwarded("198.51.100.1"));
        await P1048WebSocketTests.Until(() => site.Joins.Count == 1);
        Assert.Equal(IPAddress.Loopback, direct.PeerAddress(site.Joins.Single()));
        await Assert.ThrowsAsync<WebSocketException>(() => P1048WebSocketTests.Connect(direct.BoundPort, headers: Forwarded("198.51.100.2")));
        Assert.False(direct.IsPeerLoopback(site.Joins.Single()));
    }

    [Fact]
    public void Proxy_exemption_keeps_global_attempt_live_pending_and_table_caps()
    {
        var settings = P1048WebSocketTests.Settings();
        settings.HandshakeAttemptsPerWindow = 3; settings.HandshakeAttemptsPerIpPerWindow = 1;
        settings.MaxConnections = 2; settings.MaxConnectionsPerIp = 1; settings.MaxPendingHandshakes = 1;
        settings.MaxTrackedSources = 1;
        var proxy = IPAddress.Parse("192.0.2.1");
        var guard = new WebSocketAdmission(settings, [proxy]);
        Assert.True(guard.TryAttempt(proxy, 1));
        using var first = guard.TryEnter(proxy); Assert.NotNull(first);
        Assert.True(guard.TryAttempt(proxy, 2)); // proxy ignores per-IP attempts
        Assert.Null(guard.TryEnter(proxy)); // pending cap
        first.Promoted();
        using var second = guard.TryEnter(proxy); Assert.NotNull(second); second.Promoted();
        Assert.Null(guard.TryEnter(proxy)); // global live cap
        Assert.False(guard.TryAttempt(IPAddress.Parse("198.51.100.1"), 3)); // table cap
        Assert.True(guard.TryAttempt(proxy, 3));
        Assert.False(guard.TryAttempt(proxy, 4)); // global attempts
        first.Dispose(); second.Dispose();
        Assert.Equal((0, 0, 1), guard.Snapshot);
    }

    [Fact]
    public void Proxy_trust_is_an_exact_address_even_when_budget_keys_share_a_prefix()
    {
        var settings = P1048WebSocketTests.Settings(); settings.HandshakeAttemptsPerIpPerWindow = 1;
        var proxy = IPAddress.Parse("2001:db8:1:1::1");
        var guard = new WebSocketAdmission(settings, [proxy]);
        Assert.True(guard.TryAttempt(proxy, 1));
        Assert.True(guard.TryAttempt(proxy, 2));
        Assert.False(guard.TryAttempt(IPAddress.Parse("2001:db8:1:1::2"), 3));
    }

    [Theory]
    [InlineData("2001:db8:1:1::1", "2001:db8:1:1:ffff::2", "2001:db8:1:2::1")]
    [InlineData("192.0.2.1", "::ffff:192.0.2.1", "192.0.2.2")]
    [InlineData("2001:db8:1:1::1%1", "2001:db8:1:1::2%2", "2001:db8:1:2::1")]
    public void Attempts_and_live_slots_share_one_source_budget_and_leave_other_prefixes_independent(string first, string same, string other)
    {
        var settings = P1048WebSocketTests.Settings();
        settings.HandshakeAttemptsPerIpPerWindow = 1; settings.MaxConnectionsPerIp = 1;
        var guard = new WebSocketAdmission(settings);
        var a = IPAddress.Parse(first); var b = IPAddress.Parse(same); var c = IPAddress.Parse(other);
        Assert.True(guard.TryAttempt(a, 1));
        Assert.False(guard.TryAttempt(b, 2));
        Assert.True(guard.TryAttempt(c, 3));
        using var one = guard.TryEnter(a); Assert.NotNull(one); one.Promoted();
        Assert.Null(guard.TryEnter(b));
        using var two = guard.TryEnter(c); Assert.NotNull(two); two.Promoted();
        Assert.Equal(2, guard.Snapshot.Live);
        one.Dispose();
        using var reclaimed = guard.TryEnter(b); Assert.NotNull(reclaimed);
        Assert.True(guard.TryAttempt(b, 60_001));
    }

    [Fact]
    public async Task Forwarded_IPv6_budget_does_not_replace_the_recorded_peer_address()
    {
        var settings = P1048WebSocketTests.Settings();
        settings.KnownProxies = ["127.0.0.1"]; settings.AllowLoopbackDevelopment = false;
        settings.AllowedOrigins = ["https://lobby.invalid"]; settings.MaxConnectionsPerIp = 1;
        var site = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(site, settings); host.Host(0, IPAddress.Loopback);
        using var first = await P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded("2001:db8:1:1::1234"));
        await Assert.ThrowsAsync<WebSocketException>(() => P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded("2001:db8:1:1::5678")));
        using var second = await P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded("2001:db8:1:2::9"));
        await P1048WebSocketTests.Until(() => site.Joins.Count == 2);
        Assert.Equal(new[] { IPAddress.Parse("2001:db8:1:1::1234"), IPAddress.Parse("2001:db8:1:2::9") }, site.Joins.Select(host.PeerAddress));
    }

    [Fact]
    public void Credential_lockout_uses_the_same_IPv6_prefix_key_for_all_transports()
    {
        using var server = new Allegiance.Server.Server();
        server.TestPeerAddressOverride = peer => IPAddress.Parse(peer.Value == 7 ? "2001:db8:1:2::1" : $"2001:db8:1:1::{peer.Value:x}");
        for (uint i = 1; i <= 5; i++)
        {
            var id = new ConnectionId(i);
            server.CredentialGuesses.RecordFailure(id, server.AddressKeyOf(id), server.SimNow);
        }
        Assert.True(server.CredentialGuessBlocked(new ConnectionId(6)));
        Assert.False(server.CredentialGuessBlocked(new ConnectionId(7)));
        Assert.Equal("2001:db8:1:1::", server.AddressKeyOf(new ConnectionId(6)));
        server.TestPeerAddressOverride = _ => IPAddress.Parse("::ffff:192.0.2.1");
        Assert.Equal("192.0.2.1", server.AddressKeyOf(new ConnectionId(8)));
    }

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    public async Task Initial_join_and_session_reconnect_refuse_every_handshake_redirect(int status)
    {
        var site = new P1048WebSocketTests.Site();
        using var target = new WebSocketServerTransport(site, P1048WebSocketTests.Settings()); target.Host(0, IPAddress.Loopback);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); app.UseWebSockets();
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        app.Map("/game", async context =>
        {
            // Only the first /game request may establish a session. Its resume
            // dials, and all initial /redirect dials, get a redirect instead.
            if (context.Request.Path != "/game" || Interlocked.Increment(ref requests) != 1)
            {
                context.Response.StatusCode = status;
                context.Response.Headers.Location = $"ws://127.0.0.1:{target.BoundPort}/game";
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync(WebSocketTransport.Subprotocol);
            try
            {
                await socket.ReceiveAsync(new byte[16384], context.RequestAborted);
                await socket.SendAsync(FedPacket.Pack(new LogonAck { FValidated = true, ShipID = 1 }.Encode()), WebSocketMessageType.Binary, true, context.RequestAborted);
                await drop.Task.WaitAsync(context.RequestAborted);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "resume", context.RequestAborted);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        });
        app.Map("/redirect", context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = $"ws://127.0.0.1:{target.BoundPort}/game";
            return Task.CompletedTask;
        });
        await app.StartAsync();
        try
        {
            int port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            await using (var client = new WebSocketTransport(new P1048WebSocketTests.Site(), WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{port}/redirect"), "http://localhost"))
            {
                await Assert.ThrowsAsync<WebSocketException>(() => client.JoinAsync("", 0, ""));
                Assert.False(client.IsConnected);
            }
            await using var session = new ConnectionSession
            { WebSocketEndpoint = WebSocketEndpoint.FromConfiguration($"ws://127.0.0.1:{port}/game"), WebSocketOrigin = "http://localhost" };
            session.EnableAutoReconnect(1);
            await session.ConnectAsync((ushort)port, "P1052-resume");
            session.SendLogonRequest(new LogonRequest { CharacterName = "P1052-resume", FedsrvVer = LogonRequest.MsgVer });
            await P1048WebSocketTests.Until(() => session.Ack?.FValidated == true);
            drop.SetResult();
            await P1048WebSocketTests.Until(() => session.ResumeAttempts > 0 && session.Lost && !session.Reconnecting);
            Assert.True(requests > 1);
            Assert.Equal(0, session.ResumeSuccesses);
            Assert.Empty(site.Joins);
        }
        finally { drop.TrySetResult(); await app.StopAsync(); }
    }
}

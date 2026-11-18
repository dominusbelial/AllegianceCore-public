using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Allegiance.Core.Tests.Transport;
using Allegiance.Core.Transport;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

/// <summary>RWS — adversarial review of the game-server WebSocket transport (P1048 / P1048b).
/// Each test states the behaviour the code SHOULD have. Every one was run unskipped and
/// failed before being marked pending; RWS_REPORT.md records how.</summary>
[Collection("LiveNetwork")]
public sealed class RwsReviewTests
{
    private const string F1 = "RWS finding F1 — pending fix";
    private const string F2 = "RWS finding F2 — pending fix";
    private const string F4 = "RWS finding F4 — pending fix";

    /// <summary>The documented production topology: one trusted TLS proxy on loopback,
    /// every other limit left at its default.</summary>
    private static WebSocketConfig Proxied()
    {
        var settings = P1048WebSocketTests.Settings();
        settings.AllowLoopbackDevelopment = false;
        settings.KnownProxies = ["127.0.0.1"];
        settings.AllowedOrigins = ["https://lobby.invalid"];
        return settings;
    }
    private static Dictionary<string, string> Forwarded(string source) => new()
    { ["X-Forwarded-For"] = source, ["X-Forwarded-Proto"] = "https" };

    [Fact(Skip = F1)]
    public async Task F1_one_remote_source_cannot_spend_the_proxys_handshake_budget_for_everyone()
    {
        var settings = Proxied();
        var site = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(site, settings); host.Host(0, IPAddress.Loopback);
        // One hostile remote address spends exactly its OWN per-source budget on
        // upgrades that are refused (wrong Origin). It never authenticates.
        for (int i = 0; i < settings.HandshakeAttemptsPerIpPerWindow; i++)
            await Assert.ThrowsAsync<WebSocketException>(() => P1048WebSocketTests.Connect(host.BoundPort, "https://evil.invalid", headers: Forwarded("198.51.100.1")));
        Assert.Empty(site.Joins);
        // A different, well-behaved remote address must still be admitted.
        using var victim = await P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded("198.51.100.2"));
        await P1048WebSocketTests.Until(() => site.Joins.Count == 1);
        Assert.Equal(IPAddress.Parse("198.51.100.2"), host.PeerAddress(site.Joins.Single()));
    }

    [Fact(Skip = F1)]
    public async Task F1_one_remote_source_cannot_hold_every_connection_slot_of_the_proxy()
    {
        var settings = Proxied();
        var site = new P1048WebSocketTests.Site();
        using var host = new WebSocketServerTransport(site, settings); host.Host(0, IPAddress.Loopback);
        var held = new List<ClientWebSocket>();
        try
        {
            // One remote address opens exactly its own per-source allowance of
            // idle, unauthenticated sockets.
            for (int i = 0; i < settings.MaxConnectionsPerIp; i++)
                held.Add(await P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded("198.51.100.1")));
            await P1048WebSocketTests.Until(() => site.Joins.Count == settings.MaxConnectionsPerIp);
            using var victim = await P1048WebSocketTests.Connect(host.BoundPort, "https://lobby.invalid", headers: Forwarded("198.51.100.2"));
            await P1048WebSocketTests.Until(() => site.Joins.Count == settings.MaxConnectionsPerIp + 1);
        }
        finally { foreach (var socket in held) socket.Dispose(); }
    }

    [Fact(Skip = F2)]
    public void F2_one_IPv6_subscriber_prefix_cannot_spend_the_global_handshake_budget()
    {
        // A single IPv6 subscriber controls a whole /64. Defaults unchanged.
        var settings = P1048WebSocketTests.Settings();
        var guard = new WebSocketAdmission(settings);
        int addresses = settings.HandshakeAttemptsPerWindow / settings.HandshakeAttemptsPerIpPerWindow;
        int admitted = 0;
        for (int a = 0; a < addresses; a++)
            for (int i = 0; i < settings.HandshakeAttemptsPerIpPerWindow; i++)
                if (guard.TryAttempt(IPAddress.Parse($"2001:db8:1:1::{a + 1:x}"), 1)) admitted++;
        // One subscriber's allowance should be one source's allowance, and an
        // unrelated client must still be able to start a handshake.
        Assert.True(admitted <= settings.HandshakeAttemptsPerIpPerWindow, $"one /64 ({addresses} addresses) was granted {admitted} attempts");
        Assert.True(guard.TryAttempt(IPAddress.Parse("203.0.113.9"), 2));
    }

    [Fact(Skip = F4)]
    public async Task F4_client_does_not_follow_a_handshake_redirect_away_from_the_validated_route()
    {
        var site = new P1048WebSocketTests.Site();
        using var elsewhere = new WebSocketServerTransport(site, P1048WebSocketTests.Settings()); elsewhere.Host(0, IPAddress.Loopback);
        using var advertised = new TcpListener(IPAddress.Loopback, 0); advertised.Start();
        int port = ((IPEndPoint)advertised.LocalEndpoint).Port;
        var redirect = Task.Run(async () =>
        {
            using var connection = await advertised.AcceptTcpClientAsync();
            var buffer = new byte[4096];
            int read = await connection.GetStream().ReadAsync(buffer);
            Assert.True(read > 0);
            await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 307 Temporary Redirect\r\nLocation: ws://127.0.0.1:{elsewhere.BoundPort}/game\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        // The only route that was validated is the advertised one.
        using var client = new WebSocketTransport(new P1048WebSocketTests.Site(),
            WebSocketEndpoint.FromServerListing($"ws://127.0.0.1:{port}/advertised"), "http://localhost");
        await Assert.ThrowsAnyAsync<Exception>(() => client.JoinAsync("", 0, ""));
        await redirect;
        Assert.False(client.IsConnected);
        Assert.Empty(site.Joins); // nothing may arrive at the never-validated destination
    }
}

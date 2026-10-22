using System.Net;
using System.Net.Sockets;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.Server;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P731 — discovery tests: pure target enumeration (LAN broadcast endpoints,
/// the subnet-scan cap, Tailscale peer parsing), plus end-to-end discovery
/// against a real loopback <see cref="DiscoveryResponder"/> (unicast + the
/// coordinator's merge/dedupe, with a mocked <c>tailscale status</c>).
/// </summary>
public class DiscoveryTests
{
    private static readonly InterfaceInfo Net192 = new("eth0", IPAddress.Parse("192.168.1.5"), 24, IsLoopback: false);
    private static readonly InterfaceInfo Lo = new("lo", IPAddress.Parse("127.0.0.1"), 8, IsLoopback: true);

    [Fact]
    public void Lan_endpoints_include_directed_limited_broadcast_and_multicast()
    {
        var eps = DiscoveryTargets.LanEndpoints(new[] { Net192 }, 6074, "239.255.7.31")
            .Select(t => t.Host).ToHashSet();
        Assert.Contains("192.168.1.255", eps);       // directed broadcast
        Assert.Contains("255.255.255.255", eps);     // limited broadcast
        Assert.Contains("239.255.7.31", eps);        // multicast group
        Assert.Equal(3, eps.Count);                  // deduped
    }

    [Fact]
    public void Subnet_scan_caps_large_subnets_and_excludes_own_address()
    {
        // /16 is far bigger than the cap → no hosts swept.
        var big = new InterfaceInfo("wg0", IPAddress.Parse("10.0.0.1"), 16, IsLoopback: false);
        Assert.Empty(DiscoveryTargets.SubnetHosts(new[] { big }, 6074));

        // /24 → 254 hosts minus self = 253, in range, excluding own address.
        var hosts = DiscoveryTargets.SubnetHosts(new[] { Net192 }, 6074);
        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain(hosts, t => t.Host == "192.168.1.5");
        Assert.DoesNotContain(hosts, t => t.Host == "192.168.1.0");    // network
        Assert.DoesNotContain(hosts, t => t.Host == "192.168.1.255");  // broadcast
        Assert.All(hosts, t => Assert.Equal(DiscoverySource.Subnet, t.Source));

        // Loopback is not swept.
        Assert.Empty(DiscoveryTargets.SubnetHosts(new[] { Lo }, 6074));
    }

    [Fact]
    public void Tailscale_peers_parse_only_online_peers_with_ip_and_magicdns()
    {
        const string json = """
        {
          "Peer": {
            "peer-a": { "Online": true, "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"], "DNSName": "alpha.tailnet.ts.net" },
            "peer-b": { "Online": false, "TailscaleIPs": ["100.64.0.2"], "DNSName": "beta.tailnet.ts.net" },
            "peer-c": { "Online": true, "TailscaleIPs": [], "DNSName": "gamma.tailnet.ts.net" }
          }
        }
        """;
        var peers = DiscoveryTargets.ParseTailscalePeers(json);
        var p = Assert.Single(peers);
        Assert.Equal(IPAddress.Parse("100.64.0.1"), p.Address);
        Assert.Equal("alpha.tailnet.ts.net", p.DisplayName);
    }

    [Fact]
    public void Empty_or_garbage_tailscale_json_yields_no_peers()
    {
        Assert.Empty(DiscoveryTargets.ParseTailscalePeers(""));
        Assert.Empty(DiscoveryTargets.ParseTailscalePeers("<<< not json"));
    }

    // ── end-to-end against a real loopback responder ──────────────────────

    private static DiscoveryResponder StartLoopbackResponder(
        string name = "Test Mission", int gamePort = 2326, int rate = 20)
    {
        var responder = new DiscoveryResponder(
            () => new DiscoverySnapshot(name, "XXV_simple.igc", 3, 100, "1.0", gamePort),
            port: 0, rateLimitPerSecond: rate, multicastGroup: "",   // no multicast join in tests
            udp: new UdpClient(new IPEndPoint(IPAddress.Any, 0)));
        responder.Start();
        return responder;
    }

    [Fact]
    public void Unicast_probe_finds_a_loopback_responder()
    {
        using var responder = StartLoopbackResponder();
        using var engine = new DiscoveryEngine(probeTimeoutMs: 30_000); // scheduling allowance, not a latency budget

        var found = engine.ProbeTargetAsync(
            new DiscoveryTarget("127.0.0.1", responder.BoundPort, DiscoverySource.Lan));

        Assert.NotNull(found);
        Assert.Equal("Test Mission", found!.Name);
        Assert.Equal(2326, found.Port);            // reply's gamePort is authoritative
        Assert.Equal(DiscoverySource.Lan, found.Source);
        Assert.Equal(3, found.Players);
        Assert.Equal(100, found.MaxPlayers);
        Assert.True(found.PingMs >= 0);
    }

    [Fact]
    public void Unicast_probe_returns_null_for_a_silent_port()
    {
        // Bind a socket to grab an ephemeral port, then dispose it so nothing
        // listens there.
        using (var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            int quietPort = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;
            using var engine = new DiscoveryEngine(probeTimeoutMs: 80);
            var found = engine.ProbeTargetAsync(
                new DiscoveryTarget("127.0.0.1", quietPort, DiscoverySource.Lan));
            Assert.Null(found);
        }
    }

    [Fact]
    public void Broadcast_discovery_collects_a_loopback_responder()
    {
        using var responder = StartLoopbackResponder(gamePort: 2500);
        using var engine = new DiscoveryEngine();

        // Fire at the loopback directed broadcast and the limited broadcast;
        // the 0.0.0.0-bound responder on the same host should hear one of them.
        var targets = DiscoveryTargets.LanEndpoints(
            new[] { new InterfaceInfo("lo", IPAddress.Parse("127.0.0.1"), 8, IsLoopback: true) },
            responder.BoundPort, "239.255.7.31");
        var found = engine.CollectLanAsync(targets, windowMs: 700);

        Assert.NotEmpty(found);
        var s = found[0];
        Assert.Equal(2500, s.Port);
        Assert.Equal(DiscoverySource.Lan, s.Source);
    }

    [Fact]
    public void Coordinator_merges_favorite_and_tailscale_and_dedupes()
    {
        using var responder = StartLoopbackResponder(gamePort: 2500);
        using var engine = new DiscoveryEngine(probeTimeoutMs: 30_000); // scheduling allowance, not a latency budget
        var coord = new DiscoveryCoordinator(engine);

        // Same host reachable as a favorite AND as a mocked Tailscale peer —
        // both probe 127.0.0.1, so they must collapse to one entry
        // (favorite wins by source order).
        const string tailJson = """
        { "Peer": { "self-peer": { "Online": true,
            "TailscaleIPs": ["127.0.0.1"], "DNSName": "testhost.tailnet.ts.net" } } }
        """;
        var opts = new DiscoveryScanOptions
        {
            EnableLan = false,
            EnableSubnet = false,
            EnableTailscale = true,
            EnableFavorites = true,
            DiscoveryPort = responder.BoundPort,   // probe the responder's port
            Favorites = new[] { "127.0.0.1" },
            TailscaleStatus = () => tailJson,
        };
        var results = coord.Scan(opts);

        var s = Assert.Single(results);
        Assert.Equal(2500, s.Port);
        Assert.Equal(DiscoverySource.Favorite, s.Source);   // favorites first-wins
    }

    [Fact]
    public void Coordinator_is_silent_when_tailscale_absent()
    {
        using var engine = new DiscoveryEngine();
        var coord = new DiscoveryCoordinator(engine);
        var opts = new DiscoveryScanOptions
        {
            EnableLan = false,
            EnableSubnet = false,
            EnableTailscale = true,
            TailscaleStatus = () => null,   // absent CLI → silent, no throw
        };
        Assert.Empty(coord.Scan(opts));
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Allegiance.ClientCore.MonoGame.Discovery;

/// <summary>Well-known defaults for the discovery protocol. The server's
/// responder defaults to <c>game port + 1</c>, which for the default game port
/// 6073 is 6074 — so the client's well-known probe port matches the server's
/// default. An admin who moves the game port must also pin
/// <c>server.json discovery.port</c> to this same well-known value (or tell
/// players the port) so clients can still find the server.</summary>
public static class DiscoveryDefaults
{
    /// <summary>UDP port the client always probes when seeking servers.</summary>
    public const int Port = 6074;

    /// <summary>IPv4 multicast group used for multicast "Find Server" queries.</summary>
    public const string MulticastGroup = "239.255.7.31";

    /// <summary>How long a single unicast probe waits for a reply before giving
    /// up (bounds a subnet/Tailscale sweep; native's poll had no such bound).</summary>
    public const int ProbeTimeoutMs = 350;

    /// <summary>Native's LAN poll cadence (introscreen.cpp:824,
    /// <c>AddEventTarget(&amp;…PollForServers, GetWindow(), 1.0f)</c>).</summary>
    public const double LanRefreshSeconds = 1.0;

    /// <summary>Maximum hosts a per-interface subnet sweep will probe (cap for
    /// subnets larger than /24; see <see cref="DiscoveryTargets.SubnetHosts"/>).</summary>
    public const int SubnetScanCap = 256;
}

/// <summary>A normalized description of one local network interface (avoiding a
/// hard dependency on <see cref="NetworkInterface"/> so the pure enumeration
/// logic is unit-testable with synthetic interfaces).</summary>
public sealed record InterfaceInfo(
    string Name,
    IPAddress IPv4,
    int PrefixLength,
    bool IsLoopback);

/// <summary>One candidate target to probe, tagged with its discovery source and
/// an optional display name (e.g. a Tailscale MagicDNS name). <see cref="Host"/>
/// is an IP literal or a hostname (MagicDNS / VPN DNS names work); the
/// <see cref="DiscoveryEngine"/> resolves non-IP hosts before probing, so every
/// source can yield hostnames uniformly.</summary>
public sealed record DiscoveryTarget(
    string Host,
    int Port,
    DiscoverySource Source,
    string DisplayName = "");

/// <summary>
/// P731 — pure target enumeration for the client's "Find Server" flow. Each
/// function takes normalized inputs (interface descriptors, tailscale JSON,
/// remembered entries) and returns the candidate endpoints to probe, so the
/// whole strategy is unit-testable without sockets.
/// </summary>
public static class DiscoveryTargets
{
    /// <summary>Read the machine's real non-loopback + loopback IPv4 interfaces
    /// into <see cref="InterfaceInfo"/> descriptors. Loopback is included (its
    /// broadcast address lets a local test server on the same machine be found
    /// by broadcast). Errors on a single interface are skipped.</summary>
    public static IReadOnlyList<InterfaceInfo> GetLocalInterfaces()
    {
        var list = new List<InterfaceInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                var props = ni.GetIPProperties();
                var uni = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                        && u.PrefixLength is >= 1 and <= 31);
                if (uni is null) continue;
                list.Add(new InterfaceInfo(
                    ni.Name,
                    uni.Address,
                    uni.PrefixLength,
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback));
            }
        }
        catch (Exception)
        {
            // Networking enumeration is best-effort; an empty list just yields
            // no LAN/subnet candidates.
        }
        return list;
    }

    /// <summary>
    /// LAN broadcast + multicast endpoints to probe. Per non-loopback interface
    /// the directed subnet broadcast address is added (native enumerated LAN
    /// games; modern NICs/virtual bridges vary, so targeting each subnet's
    /// directed broadcast is the reliable form), plus the 255.255.255.255
    /// limited broadcast and the IPv4 multicast group. Loopback contributes its
    /// directed broadcast so a same-machine server is findable over broadcast.
    /// Deduplicated by endpoint.
    /// </summary>
    public static IReadOnlyList<DiscoveryTarget> LanEndpoints(
        IReadOnlyList<InterfaceInfo> ifaces, int port, string multicastGroup)
    {
        var seen = new HashSet<string>();
        var result = new List<DiscoveryTarget>();

        void Add(IPAddress ip, DiscoverySource src)
        {
            var key = ip.ToString() + ":" + port;
            if (seen.Add(key))
                result.Add(new DiscoveryTarget(ip.ToString(), port, src));
        }

        foreach (var f in ifaces)
        {
            var bc = DirectedBroadcast(f.IPv4, f.PrefixLength);
            if (bc is not null)
                Add(bc, DiscoverySource.Lan);
        }
        Add(IPAddress.Broadcast, DiscoverySource.Lan);      // 255.255.255.255
        if (IPAddress.TryParse(multicastGroup, out var mc))
            Add(mc, DiscoverySource.Multicast);             // multicast
        return result;
    }

    /// <summary>Compute a subnet's directed broadcast address, or null when the
    /// prefix covers the whole /1 range (none sensibly broadcastable).</summary>
    public static IPAddress? DirectedBroadcast(IPAddress ipv4, int prefixLength)
    {
        if (prefixLength < 1 || prefixLength > 31)
            return null;
        uint ip = IpToUInt(ipv4);
        uint mask = prefixLength == 0 ? 0 : (uint.MaxValue << (32 - prefixLength));
        uint bc = (ip & mask) | ~mask;
        return UIntToIp(bc);
    }

    /// <summary>Compute a subnet's network address (for host-range iteration).</summary>
    public static IPAddress? NetworkAddress(IPAddress ipv4, int prefixLength)
    {
        if (prefixLength < 1 || prefixLength > 31)
            return null;
        uint ip = IpToUInt(ipv4);
        uint mask = prefixLength == 0 ? 0 : (uint.MaxValue << (32 - prefixLength));
        return UIntToIp(ip & mask);
    }

    private static uint IpToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress UIntToIp(uint v)
        => new IPAddress(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    /// <summary>
    /// Parse <c>tailscale status --json</c> and return the Tailscale IP + MagicDNS
    /// name of each ONLINE peer. Peers carry <c>Online: true</c> and a
    /// <c>TailscaleIPs</c> array (typically one 100.x IPv4, sometimes an fd7a::
    /// IPv6). We take the first IPv4 if present, else the first global IPv6, and
    /// the DNSName (e.g. <c>host.tailnet.ts.net</c>). Absent Tailscale → empty
    /// list (the caller treats absence as silent, per the task brief).
    /// The JSON is parsed defensively: any unexpected shape yields no peers.
    /// </summary>
    public static IReadOnlyList<(IPAddress Address, string DisplayName)> ParseTailscalePeers(string json)
    {
        var result = new List<(IPAddress, string)>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Peer", out var peers) ||
                peers.ValueKind != System.Text.Json.JsonValueKind.Object)
                return result;
            foreach (var prop in peers.EnumerateObject())
            {
                var p = prop.Value;
                bool online = p.TryGetProperty("Online", out var o) &&
                              o.ValueKind == System.Text.Json.JsonValueKind.True;
                if (!online) continue;
                if (!p.TryGetProperty("TailscaleIPs", out var ips) ||
                    ips.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                string? dns = null;
                if (p.TryGetProperty("DNSName", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String)
                    dns = dn.GetString();
                foreach (var ipEl in ips.EnumerateArray())
                {
                    if (ipEl.ValueKind != System.Text.Json.JsonValueKind.String)
                        continue;
                    var s = ipEl.GetString();
                    if (s is null) continue;
                    if (IPAddress.TryParse(s, out var ip))
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork ||
                            (ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal))
                        {
                            result.Add((ip, dns ?? ""));
                            break;
                        }
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparsable tailscale output → no peers (silent).
        }
        return result;
    }

    /// <summary>
    /// The bounded per-interface subnet sweep. Only subnets with prefix length
    /// &gt;= <paramref name="maxPrefixLength"/> (i.e. at most 2^(32-prefix)-2
    /// host addresses) are swept, capped at <paramref name="cap"/> hosts per
    /// interface, excluding the network and broadcast addresses and the
    /// interface's own address. Larger-than-/24 subnets are OFF unless widened
    /// (the task's cap). Deterministic order.
    /// </summary>
    public static IReadOnlyList<DiscoveryTarget> SubnetHosts(
        IReadOnlyList<InterfaceInfo> ifaces, int port,
        int maxPrefixLength = 24, int cap = DiscoveryDefaults.SubnetScanCap,
        IPAddress? excludeSelf = null)
    {
        var result = new List<DiscoveryTarget>();
        foreach (var f in ifaces)
        {
            if (f.IsLoopback || f.PrefixLength < maxPrefixLength)
                continue;
            uint net = IpToUInt(NetworkAddress(f.IPv4, f.PrefixLength) ?? f.IPv4);
            uint mask = f.PrefixLength == 0 ? 0 : (uint.MaxValue << (32 - f.PrefixLength));
            uint hostCount = (uint.MaxValue - mask);          // # addresses in range
            uint first = net + 1;                             // skip network address
            uint last = net + hostCount - 1;                  // skip broadcast
            if (hostCount - 2 > (uint)cap)
                continue;                                     // subnet too large for the cap
            uint own = IpToUInt(excludeSelf ?? f.IPv4);
            for (uint h = first; h <= last; h++)
            {
                if (h == own) continue;
                result.Add(new DiscoveryTarget(UIntToIp(h).ToString(), port, DiscoverySource.Subnet));
            }
        }
        return result;
    }

    /// <summary>Remembered favorites / recent entries become unicast probes
    /// against the discovery port (the live game port comes back in the reply).
    /// A trailing <c>:port</c> is stripped for the probe (the reply's gamePort
    /// is authoritative); the hostname itself is preserved as <see cref="Host"/>
    /// so the engine can resolve MagicDNS / VPN DNS names.</summary>
    public static IReadOnlyList<DiscoveryTarget> FavoriteEndpoints(
        IEnumerable<string> hosts, int discoveryPort)
    {
        var result = new List<DiscoveryTarget>();
        foreach (var h in hosts)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            var host = h.Trim();
            var idx = host.LastIndexOf(':');
            if (idx > 0 && ushort.TryParse(host[(idx + 1)..],
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var _))
            {
                host = host[..idx];
            }
            if (host.Length == 0) continue;
            result.Add(new DiscoveryTarget(host, discoveryPort, DiscoverySource.Favorite, DisplayName: host));
        }
        return result;
    }
}

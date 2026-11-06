using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace Allegiance.Core.Transport;

/// <summary>Direct-path orchestration, shared by the player host and joiner.
/// A later relay can implement establishment at this boundary. Never falls back
/// silently to TCP when a player-hosted internet listing cannot be reached.</summary>
public static class NatTraversalClient
{
    public static HttpClient CreateHttp(string lobbyUrl)
    {
        ValidateLobbyUrl(lobbyUrl);
        // HTTP and UDP must choose the same address family. Default HTTP Happy
        // Eyeballs can otherwise pick IPv6 while STUN deliberately picks IPv4.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var address = await ResolveAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler)
        { BaseAddress = new Uri(lobbyUrl), Timeout = TimeSpan.FromSeconds(5) };
    }
    public static void ValidateLobbyUrl(string lobbyUrl)
    {
        if (!Uri.TryCreate(lobbyUrl, UriKind.Absolute, out var url) || url.UserInfo.Length != 0
            || url.Scheme != "https" && !(url.Scheme == "http" && url.IsLoopback))
            throw new ArgumentException("Internet hosting requires an HTTPS lobby URL (HTTP is allowed only on loopback).");
    }
    public static async Task<(IPEndPoint Primary, IPEndPoint Secondary)> ConfigurationAsync(HttpClient http, CancellationToken ct = default)
    {
        var configuration = await http.GetFromJsonAsync("/api/nat/config", NatJsonContext.Default.NatConfiguration, ct).ConfigureAwait(false);
        if (configuration is not { PlayerHostedGames: true }) throw new IOException("This lobby has playerHostedGames disabled. Ask its administrator to enable player hosting.");
        if (configuration.PrimaryPort is < 1 or > 65535 || configuration.SecondaryPort is < 1 or > 65535
            || configuration.PrimaryPort == configuration.SecondaryPort) throw new IOException("Lobby UDP discovery is unavailable");
        var address = await ResolveAsync(http.BaseAddress!.DnsSafeHost, ct).ConfigureAwait(false);
        return (new(address, configuration.PrimaryPort), new(address, configuration.SecondaryPort));
    }
    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault(IsGlobalV6) ?? throw new IOException("No usable lobby IPv4/global IPv6 address");
    }
    private static bool IsGlobalV6(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6
        && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast && !ip.IsIPv6SiteLocal && !IPAddress.IsLoopback(ip)
        && (ip.GetAddressBytes()[0] & 0xe0) == 0x20; // global unicast 2000::/3

    public static async Task<StunObservation> DiscoverStableAsync(UdpTransport transport,
        (IPEndPoint Primary, IPEndPoint Secondary) servers, CancellationToken ct = default)
    {
        try
        {
            var primary = await transport.DiscoverAsync(servers.Primary, ct).ConfigureAwait(false);
            var secondary = await transport.DiscoverAsync(servers.Secondary, ct).ConfigureAwait(false);
            if (!primary.Endpoint.Equals(secondary.Endpoint)) throw new DirectConnectionException();
            if (primary.Receipt is null) throw new IOException("Lobby did not provide a UDP observation receipt");
            return primary;
        }
        catch (TimeoutException ex) { throw new DirectConnectionException(ex); }
    }
    public static async Task ConnectAsync(UdpTransport transport, InternetJoin target, string playerName, CancellationToken ct = default)
    {
        using var http = CreateHttp(target.LobbyUrl);
        await ConnectAsync(transport, http, target.ListingId, playerName, ct).ConfigureAwait(false);
    }
    public static async Task ConnectAsync(UdpTransport transport, HttpClient http, string listingId, string playerName, CancellationToken ct = default)
    {
        try
        {
            var servers = await ConfigurationAsync(http, ct).ConfigureAwait(false);
            transport.PrepareTraversal();
            var observation = await DiscoverStableAsync(transport, servers, ct).ConfigureAwait(false);
            using var response = await http.PostAsJsonAsync($"/api/servers/{Uri.EscapeDataString(listingId)}/punch",
                new PunchRequest(observation.Receipt!), NatJsonContext.Default.PunchRequest, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var introduction = await response.Content.ReadFromJsonAsync(NatJsonContext.Default.PunchIntroduction, ct).ConfigureAwait(false)
                ?? throw new IOException("Empty lobby introduction");
            // The same address family must work end to end. Private/link-local v6
            // never participates; IPv4 is selected whenever the lobby has it.
            var endpoint = introduction.Endpoint.ToIPEndPoint();
            if (endpoint.AddressFamily != observation.Endpoint.AddressFamily
                || endpoint.AddressFamily == AddressFamily.InterNetworkV6 && !IsGlobalV6(endpoint.Address)) throw new DirectConnectionException();
            await transport.PunchAsync(introduction, ct).ConfigureAwait(false);
            transport.Join(endpoint.Address.ToString(), (ushort)endpoint.Port, playerName);
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException)
        { transport.Shutdown(); throw new DirectConnectionException(ex); }
        catch { transport.Shutdown(); throw; }
    }
}

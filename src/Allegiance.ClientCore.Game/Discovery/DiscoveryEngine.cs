using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Discovery;

namespace Allegiance.ClientCore.MonoGame.Discovery;

/// <summary>
/// P731 — the client's discovery prober and coordinator for the intro screen's
/// "Find Server" flow. Owns one UDP socket bound to an ephemeral local port and
/// speaks the <see cref="DiscoveryProtocol"/> request/reply to the server's
/// responder. It merges every discovery source (LAN broadcast/multicast,
/// Tailscale unicast, subnet sweep, favorites, and — via the caller — the lobby)
/// into one de-duplicated list, each entry tagged with its source and ping.
/// <para>
/// <see cref="ProbeTargetAsync"/> does a bounded unicast probe of one host
/// (hostnames / MagicDNS names are resolved first); <see cref="CollectLanAsync"/>
/// fires the request to the broadcast + multicast endpoints and gathers every
/// reply within a short window (native's LAN enumeration, introscreen.cpp:905,
/// made asynchronous so the UI never hangs — the era constraint native itself
/// worked around at introscreen.cpp:897-901).</para>
/// <para>
/// Everything is bounded and cancellable so a subnet/Tailscale sweep can never
/// stall the intro screen.</para>
/// </summary>
public sealed class DiscoveryEngine : IDisposable
{
    private readonly UdpClient _udp;
    private readonly int _probeTimeoutMs;
    private bool _disposed;

    /// <summary>The local ephemeral port the engine's socket bound (diagnostic).</summary>
    public int LocalPort { get; }

    public DiscoveryEngine(int probeTimeoutMs = DiscoveryDefaults.ProbeTimeoutMs, UdpClient? udp = null)
    {
        _probeTimeoutMs = probeTimeoutMs;
        _udp = udp ?? new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _udp.Client.ReceiveTimeout = probeTimeoutMs;
    }

    /// <summary>Resolve a target host (IP literal or hostname/MagicDNS name) to
    /// probe addresses. Returns IP literals directly; resolves hostnames via
    /// DNS (IPv4 first, then IPv6), best-effort.</summary>
    public static IReadOnlyList<IPAddress> ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
            return new[] { ip };
        try
        {
            return Dns.GetHostAddresses(host)
                .OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToArray();
        }
        catch (SocketException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>
    /// Probe one target (unicast) and return the discovered server, or null on
    /// timeout / no reply. Tries each resolved address in turn with a bounded
    /// wait. The returned server's <c>Address</c> is the original
    /// <see cref="DiscoveryTarget.Host"/> (an IP literal or the MagicDNS /
    /// hostname that led here — the connectable identity); the reply's
    /// <c>gamePort</c> is authoritative for the connect port.
    /// </summary>
    public DiscoveredServer? ProbeTargetAsync(DiscoveryTarget target, CancellationToken ct = default)
    {
        var addrs = ResolveHost(target.Host);
        if (addrs.Count == 0) return null;

        string nonce = DiscoveryProtocol.NewNonce();
        foreach (var addr in addrs)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            if (!TrySend(new IPEndPoint(addr, target.Port), DiscoveryProtocol.EncodeRequest(nonce)))
                continue;
            var got = WaitForReply(nonce, _probeTimeoutMs);
            if (got is null) continue;
            return new DiscoveredServer(
                Name: got.Value.Reply.Name ?? "",
                Address: target.Host,
                Port: got.Value.Reply.GamePort > 0 ? got.Value.Reply.GamePort : target.Port,
                Source: target.Source,
                PingMs: (int)sw.ElapsedMilliseconds,
                Map: got.Value.Reply.Map ?? "",
                Players: got.Value.Reply.Players,
                MaxPlayers: got.Value.Reply.MaxPlayers,
                Version: got.Value.Reply.Version ?? "",
                DisplayName: target.DisplayName ?? "");
        }
        return null;
    }

    /// <summary>
    /// Fire the request to every broadcast + multicast endpoint and collect all
    /// replies within a short window (native's 1 s LAN poll cadence collects
    /// everyone on the subnet). Each reply becomes a <see cref="DiscoveredServer"/>
    /// addressed by the responder's actual source IP and tagged
    /// <see cref="DiscoverySource.Lan"/> (replies to multicast are
    /// indistinguishable from broadcast on the receive side, so LAN is the
    /// aggregate tag).
    /// </summary>
    public IReadOnlyList<DiscoveredServer> CollectLanAsync(
        IReadOnlyList<DiscoveryTarget> lanTargets, int windowMs = 700, CancellationToken ct = default)
    {
        var results = new List<DiscoveredServer>();
        if (lanTargets.Count == 0) return results;

        string nonce = DiscoveryProtocol.NewNonce();
        var req = DiscoveryProtocol.EncodeRequest(nonce);
        foreach (var t in lanTargets)
        {
            foreach (var addr in ResolveHost(t.Host))
            {
                TrySend(new IPEndPoint(addr, t.Port), req);
            }
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < windowMs && !ct.IsCancellationRequested)
        {
            int remain = windowMs - (int)deadline.ElapsedMilliseconds;
            if (remain <= 0) break;
            var got = WaitForReply(nonce, remain);
            if (got is null) break;                 // receive timeout ended the window
            results.Add(new DiscoveredServer(
                Name: got.Value.Reply.Name ?? "",
                Address: got.Value.Remote.Address.ToString(),
                Port: got.Value.Reply.GamePort > 0 ? got.Value.Reply.GamePort : DiscoveryDefaults.Port,
                Source: DiscoverySource.Lan,
                PingMs: 0,
                Map: got.Value.Reply.Map ?? "",
                Players: got.Value.Reply.Players,
                MaxPlayers: got.Value.Reply.MaxPlayers,
                Version: got.Value.Reply.Version ?? "",
                DisplayName: ""));
        }
        return results;
    }

    private bool TrySend(IPEndPoint ep, byte[] req)
    {
        try
        {
            _udp.Send(req, req.Length, ep);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private (DiscoveryProtocol.Reply Reply, IPEndPoint Remote)? WaitForReply(string nonce, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int remain = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (remain <= 0) return null;
            try
            {
                _udp.Client.ReceiveTimeout = Math.Max(1, remain);
                var remote = new IPEndPoint(IPAddress.Any, 0);
                var buf = _udp.Receive(ref remote);
                var reply = DiscoveryProtocol.TryParseReply(buf, buf.Length);
                if (reply is not null && (nonce.Length == 0 || reply.Nonce == nonce))
                    return (reply, remote);
                // Different nonce (a late reply to an older request): keep waiting.
            }
            catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _udp.Dispose(); } catch { /* already closed */ }
    }
}

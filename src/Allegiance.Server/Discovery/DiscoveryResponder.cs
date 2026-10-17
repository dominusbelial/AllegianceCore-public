using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Discovery;

namespace Allegiance.Server;

/// <summary>Current server facts the responder advertises. Snapshot is sampled
/// at reply time (not cached) so players/max stay live, but each field is read
/// once per reply and the whole struct is immutable.</summary>
public readonly record struct DiscoverySnapshot(
    string Name, string Map, int Players, int MaxPlayers, string Version, int GamePort);

/// <summary>
/// P731 — the server's UDP discovery responder. Answers the client's "Find
/// Server" queries (WinTrek/introscreen.cpp's <c>FindStandaloneServersByName</c>,
/// introscreen.cpp:905) with the server's public facts, so a client can find a
/// server over LAN broadcast/multicast, a Tailscale/VPN unicast, or a direct
/// unicast probe without any lobby directory.
/// <para>
/// Behaviour (all native-faithful intent; era constraints sized for today):</para>
/// <list type="bullet">
///   <item><b>Broadcast + unicast</b> queries are answered on one socket bound
///        to 0.0.0.0, replying to the <em>sender's</em> address (native's LAN
///        enumeration answered any local host, and unicast-over-VPN/Tailscale
///        needs exactly this — see the task brief's "must find servers over
///        VPNs and Tailscale too").</item>
///   <item><b>Multicast</b> queries are served by joining the configured
///        <see cref="DiscoveryConfig.MulticastGroup"/> on that same socket, so a
///        multicast seek also works. Join failures are non-fatal (some hosts /
///        networks do not carry multicast; broadcast+unicast still work).</item>
///   <item><b>Rate limited</b> per wall-clock second
///        (<see cref="DiscoveryConfig.RateLimitPerSecond"/>): the responder is
///        stateless and tiny, but a spoofed broadcast storm must not turn it
///        into a reflection amplifier. Excess requests are counted, not answered.
///        This is an era/hardening improvement over native's unfiltered
///        enumeration, not a gameplay rule.</item>
///   <item><b>No secrets</b>: the reply carries only the public server facts
///        (name, map, players/max, version, game port) — exactly what native's
///        LAN advertisement exposed, and nothing more.</item>
/// </list>
/// <para>
/// The <see cref="DiscoveryProtocol"/> request/reply JSON is declared once in
/// <c>Allegiance.Core</c> and shared by both the responder here and the client's
/// discovery scanners, so the two sides cannot drift.</para>
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly Func<DiscoverySnapshot> _snapshot;
    private readonly int _rateLimitPerSecond;
    private readonly string _multicastGroup;
    private readonly UdpClient _udp;
    private readonly object _gate = new();
    private DateTime _windowStartUtc = DateTime.UtcNow;
    private int _requestsInWindow;
    private long _requestsAnswered;
    private long _requestsDropped;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>The port the responder bound (0 before <see cref="Start"/>).</summary>
    public int BoundPort { get; private set; }

    /// <summary>Total requests answered since start (diagnostic).</summary>
    public long RequestsAnswered => Interlocked.Read(ref _requestsAnswered);

    /// <summary>Total requests dropped by the rate limiter since start.</summary>
    public long RequestsDropped => Interlocked.Read(ref _requestsDropped);

    /// <summary>
    /// Construct with an externally-owned socket (test seam: lets a test bind
    /// an ephemeral port / fake socket and drive <see cref="ProcessRequest"/>
    /// synchronously) or with <paramref name="port"/> 0 for an ephemeral bind.
    /// </summary>
    public DiscoveryResponder(
        Func<DiscoverySnapshot> snapshot,
        int port,
        int rateLimitPerSecond = 20,
        string multicastGroup = "239.255.7.31",
        UdpClient? udp = null)
    {
        _snapshot = snapshot;
        _rateLimitPerSecond = rateLimitPerSecond;
        _multicastGroup = multicastGroup;
        _udp = udp ?? new UdpClient(new IPEndPoint(IPAddress.Any, port));
        BoundPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>Bind is done in the constructor; Start begins the async receive
    /// loop that answers broadcast/unicast/multicast queries. Idempotent.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        try
        {
            if (!string.IsNullOrWhiteSpace(_multicastGroup)
                && IPAddress.TryParse(_multicastGroup, out var group))
            {
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.JoinMulticastGroup(group);
            }
        }
        catch (Exception ex)
        {
            // Some hosts/networks carry no multicast; broadcast+unicast still work.
            Console.WriteLine($"[discovery] multicast join '{_multicastGroup}' skipped: {ex.Message}");
        }
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try
                {
                    res = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Transient socket error; keep looping (the socket stays alive).
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                byte[]? reply = ProcessRequest(res.Buffer, res.Buffer.Length);
                if (reply is null) continue;
                try
                {
                    await _udp.SendAsync(reply, reply.Length, res.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Best-effort reply; ignore (a vanished receiver is normal on UDP).
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Handle one request datagram synchronously and return the reply bytes, or
    /// null when the request is malformed, unmatched, or dropped by the rate
    /// limiter. Public so tests can drive it without a real socket/thread.
    /// </summary>
    public byte[]? ProcessRequest(byte[] data, int length)
    {
        var req = DiscoveryProtocol.TryParseRequest(data, length);
        if (req is null) return null;
        if (!AllowRequest())
            return null;

        var snap = _snapshot();
        var reply = new DiscoveryProtocol.Reply
        {
            Nonce = req.Nonce,
            Name = snap.Name ?? "",
            Map = snap.Map ?? "",
            Players = snap.Players,
            MaxPlayers = snap.MaxPlayers,
            GamePort = snap.GamePort,
            ResponderPort = BoundPort,
            Version = snap.Version ?? "",
        };
        Interlocked.Increment(ref _requestsAnswered);
        return DiscoveryProtocol.EncodeReply(reply);
    }

    private bool AllowRequest()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if ((now - _windowStartUtc).TotalSeconds >= 1.0)
            {
                _windowStartUtc = now;
                _requestsInWindow = 0;
            }
            if (_requestsInWindow >= _rateLimitPerSecond)
            {
                Interlocked.Increment(ref _requestsDropped);
                return false;
            }
            _requestsInWindow++;
            return true;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _udp.Dispose(); } catch { /* already closed */ }
        try { _loop?.Wait(500); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}

namespace Allegiance.Core.Transport;

/// <summary>Default TCP; opt-in UDP with bounded TCP fallback, or an explicit validated WebSocket listing/config route.</summary>
public static class TransportFactory
{
    public static ITransport CreateClient(ITransportSite site, string? protocol = null, ushort? udpPort = null, WebSocketEndpoint? webSocketEndpoint = null, string? webSocketOrigin = null)
    {
        if (webSocketEndpoint is not null) return new WebSocketTransport(site, webSocketEndpoint, webSocketOrigin);
#if BROWSER
        throw new PlatformNotSupportedException("TCP/UDP is not available in the browser; select an advertised WebSocket route");
#else
        protocol ??= Environment.GetEnvironmentVariable("ALLEGIANCE_TRANSPORT_PROTOCOL");
        protocol = string.IsNullOrWhiteSpace(protocol) ? "tcp" : protocol.Trim().ToLowerInvariant();
        return protocol switch
        {
            "tcp" => new TcpTransport(site),
            "udp" => new FallbackTransport(site, udpPort),
            _ => throw new ArgumentException($"Unknown transport protocol '{protocol}' (expected tcp or udp)", nameof(protocol)),
        };
#endif
    }

    /// <summary>Resolved transport for diagnostics after Join (UDP or fallback TCP).</summary>
    public static ITransport ActiveTransport(ITransport transport)
#if BROWSER
        => transport;
#else
        => transport is FallbackTransport fallback && fallback.Active is { } active ? active : transport;
#endif

#if !BROWSER

    /// <summary>Callbacks are suppressed for a failed handshake. Once connected,
    /// session loss belongs to the ordinary reconnect policy, never a hidden switch.</summary>
    private sealed class FallbackTransport(ITransportSite site, ushort? udpPort) : ITransport
    {
        private ITransport? _active;
        internal ITransport? Active => _active;
        public bool IsConnected => _active?.IsConnected == true;
        public ConnectionId LocalId => ConnectionId.Me;
        public ushort BoundPort => 0;
        public void Host(ushort port = 0) => throw new NotSupportedException("client only");
        public void Join(string address, ushort port, string playerName)
            => JoinAsync(address, port, playerName).GetAwaiter().GetResult();
        public async Task JoinAsync(string address, ushort port, string playerName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_active is not null) throw new InvalidOperationException("transport already used");
            ushort selectedPort = udpPort ?? port;
            if (udpPort is null && Environment.GetEnvironmentVariable("ALLEGIANCE_TRANSPORT_UDP_PORT") is { Length: > 0 } value)
                selectedPort = ushort.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (selectedPort == 0) selectedPort = port;
            var udp = new UdpTransport(site);
            _active = udp;
            try { await udp.JoinAsync(address, selectedPort, playerName, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or System.Net.Sockets.SocketException)
            {
                udp.Shutdown();
                cancellationToken.ThrowIfCancellationRequested();
                _active = new TcpTransport(site);
                await _active.JoinAsync(address, port, playerName, cancellationToken).ConfigureAwait(false);
            }
        }
        public void Shutdown() => _active?.Shutdown();
        public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => _active?.Send(target, data, guaranteed);
        public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => _active?.SendCoalescing(target, data, coalesceKey);
        public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => _active?.SendGuaranteedCoalescing(target, data, coalesceKey);
    }
#endif
}

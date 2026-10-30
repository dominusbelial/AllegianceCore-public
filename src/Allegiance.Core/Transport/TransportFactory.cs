namespace Allegiance.Core.Transport;

/// <summary>Default TCP; opt-in UDP with a bounded handshake and TCP fallback.</summary>
public static class TransportFactory
{
    public static ITransport CreateClient(ITransportSite site, string? protocol = null, ushort? udpPort = null)
    {
        protocol ??= Environment.GetEnvironmentVariable("ALLEGIANCE_TRANSPORT_PROTOCOL");
        protocol = string.IsNullOrWhiteSpace(protocol) ? "tcp" : protocol.Trim().ToLowerInvariant();
        return protocol switch
        {
            "tcp" => new TcpTransport(site),
            "udp" => new FallbackTransport(site, udpPort),
            _ => throw new ArgumentException($"Unknown transport protocol '{protocol}' (expected tcp or udp)", nameof(protocol)),
        };
    }

    /// <summary>Resolved transport for diagnostics after Join (UDP or fallback TCP).</summary>
    public static ITransport ActiveTransport(ITransport transport) => transport is FallbackTransport fallback && fallback.Active is { } active ? active : transport;

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
        {
            if (_active is not null) throw new InvalidOperationException("transport already used");
            ushort selectedPort = udpPort ?? port;
            if (udpPort is null && Environment.GetEnvironmentVariable("ALLEGIANCE_TRANSPORT_UDP_PORT") is { Length: > 0 } value)
                selectedPort = ushort.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (selectedPort == 0) selectedPort = port;
            var udp = new UdpTransport(site);
            _active = udp;
            try { udp.Join(address, selectedPort, playerName); }
            catch (Exception ex) when (ex is TimeoutException or IOException or System.Net.Sockets.SocketException)
            {
                udp.Shutdown();
                _active = new TcpTransport(site);
                _active.Join(address, port, playerName);
            }
        }
        public void Shutdown() => _active?.Shutdown();
        public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => _active?.Send(target, data, guaranteed);
        public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => _active?.SendCoalescing(target, data, coalesceKey);
        public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => _active?.SendGuaranteedCoalescing(target, data, coalesceKey);
    }
}

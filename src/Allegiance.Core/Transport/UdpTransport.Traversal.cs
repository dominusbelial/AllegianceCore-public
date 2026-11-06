using System.Buffers.Binary;
using System.Net;

namespace Allegiance.Core.Transport;

public sealed partial class UdpTransport
{
    private const uint ProbeMagic = 0x36333950; // P936, distinct from ADU2/STUN
    public const int ProbeSize = 32;
    private BindingWait? _binding;
    private readonly SemaphoreSlim _bindingGate = new(1);
    private readonly Dictionary<IPEndPoint, Introduction> _introductions = new();
    private bool _traversalPrepared;
    /// <summary>Only internet player hosts opt in; local host/bot TCP remains available.</summary>
    public bool RequireIntroductions { get; set; }
    internal bool AllowUnintroducedLoopback { get; set; } = true;
    // In-process NAT shims route actual sockets without requiring privileged namespaces.
    internal Func<IPEndPoint, IPEndPoint>? OutboundRouteForTesting { get; set; }
    internal Func<IPEndPoint, IPEndPoint>? InboundRouteForTesting { get; set; }
    public ushort SocketPort => _boundPort;

    /// <summary>Reserve the client game socket before STUN. Join reuses it.</summary>
    public void PrepareTraversal()
    {
        lock (_gate)
        {
            EnsureStopped();
            _host = false;
            _traversalPrepared = true;
            Start(IPAddress.IPv6Any, 0);
        }
    }

    public async Task<StunObservation> DiscoverAsync(IPEndPoint server, CancellationToken cancellationToken = default)
    {
        await _bindingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BindingWait? pending = null;
        try
        {
            lock (_gate)
            {
                if (!_running) throw new IOException("UDP game socket is closed");
                pending = new(Normalize(server), StunBinding.CreateRequest());
                _binding = pending;
            }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                lock (_gate)
                {
                    if (!_running) throw new IOException("UDP game socket is closed");
                    SendTraversal(pending.Request, pending.Server);
                }
                try { return await pending.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) when (attempt < 3) { }
            }
            throw new TimeoutException("Lobby UDP discovery did not respond");
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_binding, pending)) _binding = null;
            _bindingGate.Release();
        }
    }

    public async Task PunchAsync(PunchIntroduction introduction, CancellationToken cancellationToken = default)
    {
        var endpoint = Normalize(introduction.Endpoint.ToIPEndPoint());
        double remaining = (introduction.ExpiresUtc - DateTimeOffset.UtcNow).TotalMilliseconds;
        if (remaining is <= 0 or > 11000 || !Guid.TryParseExact(introduction.Secret, "N", out var token))
            throw new IOException("Lobby introduction is invalid or expired");
        Introduction pending;
        lock (_gate)
        {
            if (!_running) throw new IOException("UDP game socket is closed");
            long now = Environment.TickCount64;
            foreach (var key in _introductions.Where(x => x.Value.Expires < now).Select(x => x.Key).ToArray()) _introductions.Remove(key);
            if (_introductions.Count >= 32) throw new IOException("Too many pending introductions");
            pending = new(token, now + (long)remaining);
            _introductions[endpoint] = pending;
        }
        // Both ends keep probing briefly, even after receipt, so a late host poll
        // can open both port-restricted filters. No unsolicited endpoint gets a reply.
        byte[] packet = new byte[ProbeSize];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, ProbeMagic);
        packet[4] = 1; packet[5] = 1; token.TryWriteBytes(packet.AsSpan(8, 16));
        for (int i = 0; i < 25 && Environment.TickCount64 < pending.Expires; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_running) throw new IOException("UDP game socket is closed");
                SendTraversal(packet, endpoint);
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if (pending.Received && i >= 4) return;
        }
        if (!pending.Received) throw new DirectConnectionException();
    }

    private bool ReceiveTraversal(ReadOnlySpan<byte> packet, IPEndPoint endpoint, long now)
    {
        if (StunBinding.HasCookie(packet))
        {
            var pending = _binding;
            if (pending is not null && pending.Server.Equals(endpoint)
                && StunBinding.TryReadSuccess(packet, pending.Request.AsSpan(8, 12), out var mapped, out var receipt))
                pending.Completion.TrySetResult(new(mapped!, receipt));
            return true;
        }
        if (packet.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(packet) != ProbeMagic) return false;
        if (packet.Length != ProbeSize || packet[4] != 1 || packet[5] is not (1 or 2)
            || packet[6] != 0 || packet[7] != 0 || BinaryPrimitives.ReadUInt64LittleEndian(packet[24..]) != 0
            || !_introductions.TryGetValue(endpoint, out var introduction) || introduction.Expires < now
            || new Guid(packet.Slice(8, 16)) != introduction.Token) return true;
        introduction.Received = true;
        if (packet[5] == 1 && introduction.Replies++ < 32)
        {
            Span<byte> reply = stackalloc byte[ProbeSize];
            packet.CopyTo(reply); reply[5] = 2;
            SendTraversal(reply, endpoint);
        }
        return true;
    }

    private bool CanIntroduce(IPEndPoint endpoint, long now) => !RequireIntroductions
        || (AllowUnintroducedLoopback && IPAddress.IsLoopback(endpoint.Address))
        || _endpoints.ContainsKey(endpoint)
        || (_introductions.TryGetValue(endpoint, out var introduction) && introduction.Expires >= now && introduction.Received);

    private void SendTraversal(ReadOnlySpan<byte> packet, IPEndPoint endpoint)
    {
        try
        {
            endpoint = OutboundRouteForTesting?.Invoke(endpoint) ?? endpoint;
            if (_socket!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                endpoint = new(endpoint.Address.MapToIPv4(), endpoint.Port);
            if (_socket!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                endpoint = new(endpoint.Address.MapToIPv6(), endpoint.Port);
            int sent = _socket.SendTo(packet, endpoint);
            Interlocked.Add(ref _written, sent);
        }
        catch (System.Net.Sockets.SocketException) { } // bounded retry/timeout owns failure
    }

    private sealed record BindingWait(IPEndPoint Server, byte[] Request)
    {
        public TaskCompletionSource<StunObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Introduction(Guid token, long expires)
    {
        public Guid Token { get; } = token;
        public long Expires { get; } = expires;
        public volatile bool Received;
        public int Replies;
    }
}

public sealed record StunObservation(IPEndPoint Endpoint, string? Receipt);

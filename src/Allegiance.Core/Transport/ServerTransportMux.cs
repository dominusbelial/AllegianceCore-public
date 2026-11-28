using System.Net;

namespace Allegiance.Core.Transport;

/// <summary>Host-only listener composition. IDs are allocated for live (listener, inner ID)
/// pairs, never encoded in bits: all 32 bits of an inner ID are valid on every listener.
/// Retired outer IDs are never reused, so queued events cannot address a new peer.</summary>
public class ServerTransportMux(ITransportSite site) : IServerTransport, ISendPoolDiagnostics, IUdpReceiveDiagnostics, IUdpListenerInfo
{
    private readonly object _gate = new();
    private readonly List<Listener> _listeners = [];
    private readonly Dictionary<ConnectionId, Route> _routes = [];
    private uint _nextId;
    private bool _running;
    private sealed record Route(Listener Listener, ConnectionId Inner);
    private sealed class Listener
    {
        public required IServerTransport Transport;
        public ushort? Port;
        public IPAddress? Bind;
        public readonly Dictionary<ConnectionId, ConnectionId> Outer = [];
    }

    /// <summary>Configure before Host. The first listener supplies BoundPort; additional
    /// listeners default to that port unless an explicit port (including 0) is supplied.</summary>
    public T Add<T>(Func<ITransportSite, T> factory, ushort? port = null, IPAddress? bindAddress = null) where T : IServerTransport
    {
        if (_running) throw new InvalidOperationException("Configure listeners before Host");
        var adapter = new ListenerSite(this, site);
        var transport = factory(adapter);
        var listener = new Listener { Transport = transport, Port = port, Bind = bindAddress };
        adapter.Listener = listener;
        _listeners.Add(listener);
        return transport;
    }

    public string ProtocolName => "multiplexed";
    public ushort BoundPort => _listeners.Count == 0 ? (ushort)0 : _listeners[0].Transport.BoundPort;
    public ushort UdpPort => _listeners.Select(l => l.Transport).OfType<IUdpListenerInfo>().Select(t => t.UdpPort).FirstOrDefault();
    public ConnectionId LocalId => ConnectionId.Me;
    public bool IsConnected => _running && _listeners.All(l => l.Transport.IsConnected);
    public EgressAccounting? Egress => _listeners.FirstOrDefault()?.Transport.Egress;
    public ReliableTelemetry? Reliable => _listeners.FirstOrDefault()?.Transport.Reliable;
    public long TotalWrittenBytes => _listeners.Sum(l => l.Transport.TotalWrittenBytes);
    public long TotalEnqueuedBytes => _listeners.Sum(l => l.Transport.TotalEnqueuedBytes);
    public void Host(ushort port = 0) => Host(port, null);
    public void Host(ushort port, IPAddress? bindAddress)
    {
        if (_running || _listeners.Count == 0) throw new InvalidOperationException("No listeners or already hosted");
        _running = true;
        try
        {
            for (int i = 0; i < _listeners.Count; i++)
            {
                var l = _listeners[i];
                l.Transport.Host(l.Port ?? (i == 0 ? port : BoundPort), l.Bind ?? bindAddress);
            }
        }
        catch { Shutdown(); throw; }
    }
    public void Join(string address, ushort port, string playerName) => throw new NotSupportedException("Host-only transport");
    private Route? Find(ConnectionId id) { lock (_gate) return _routes.GetValueOrDefault(id); }
    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed)
    { if (Find(target) is { } r) r.Listener.Transport.Send(r.Inner, data, guaranteed); }
    public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey)
    { if (Find(target) is { } r) r.Listener.Transport.SendCoalescing(r.Inner, data, coalesceKey); }
    public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey)
    { if (Find(target) is { } r) r.Listener.Transport.SendGuaranteedCoalescing(r.Inner, data, coalesceKey); }
    public IPAddress? PeerAddress(ConnectionId id) => Find(id) is { } r ? r.Listener.Transport.PeerAddress(r.Inner) : null;
    public string PeerProtocolName(ConnectionId id) => Find(id) is { } r ? r.Listener.Transport.PeerProtocolName(r.Inner) : "unknown";
    public bool IsPeerLoopback(ConnectionId id) => Find(id) is { } r && r.Listener.Transport.IsPeerLoopback(r.Inner);
    public bool IsPeerHuman(ConnectionId id) => Find(id) is { } r && r.Listener.Transport.IsPeerHuman(r.Inner);
    public bool IsPeerClosed(ConnectionId id) => Find(id) is not { } r || r.Listener.Transport.IsPeerClosed(r.Inner);
    public void SetPeerHuman(ConnectionId id, bool human) { if (Find(id) is { } r) r.Listener.Transport.SetPeerHuman(r.Inner, human); }
    public void MarkPeerAuthenticated(ConnectionId id) { if (Find(id) is { } r) r.Listener.Transport.MarkPeerAuthenticated(r.Inner); }
    public void ClosePeer(ConnectionId id, DisconnectReason reason = DisconnectReason.Shutdown)
    { if (Find(id) is { } r) r.Listener.Transport.ClosePeer(r.Inner, reason); }
    public void ClosePeerAfterFlush(ConnectionId id) { if (Find(id) is { } r) r.Listener.Transport.ClosePeerAfterFlush(r.Inner); }
    public IReadOnlyDictionary<DisconnectReason, int> DrainDisconnectCounts()
    {
        var result = new Dictionary<DisconnectReason, int>();
        foreach (var l in _listeners)
            foreach (var (reason, count) in l.Transport.DrainDisconnectCounts()) result[reason] = result.GetValueOrDefault(reason) + count;
        return result;
    }
    public (int Peers, int MaxOrderedPending, int MaxStreamPending, long MaxQueuedBytes, long TotalQueuedBytes, long MaxOldestGuaranteedMs) SamplePeerQueues()
    {
        int peers = 0, ordered = 0, stream = 0; long maxBytes = 0, bytes = 0, age = 0;
        foreach (var l in _listeners)
        {
            var q = l.Transport.SamplePeerQueues(); peers += q.Peers;
            ordered = Math.Max(ordered, q.MaxOrderedPending); stream = Math.Max(stream, q.MaxStreamPending);
            maxBytes = Math.Max(maxBytes, q.MaxQueuedBytes); bytes += q.TotalQueuedBytes; age = Math.Max(age, q.MaxOldestGuaranteedMs);
        }
        return (peers, ordered, stream, maxBytes, bytes, age);
    }
    public SendWindow DrainSendWindow(Span<PeerSendWindow> peers, Span<SendWorkerWindow> workers)
    {
        // TCP is the only send-worker-pool owner. Remap its diagnostics to public IDs too.
        foreach (var l in _listeners)
            if (l.Transport is ISendPoolDiagnostics d)
            {
                var result = d.DrainSendWindow(peers, workers);
                lock (_gate)
                    for (int i = 0; i < Math.Min(result.Peers, peers.Length); i++)
                        if (l.Outer.TryGetValue(peers[i].Peer, out var outer)) peers[i] = peers[i] with { Peer = outer };
                return result;
            }
        return default;
    }
    public UdpReceiveDelayWindow DrainReceiveDelay()
    {
        foreach (var l in _listeners) if (l.Transport is IUdpReceiveDiagnostics d) return d.DrainReceiveDelay();
        return default;
    }
    public void Shutdown()
    {
        _running = false;
        foreach (var l in _listeners.AsEnumerable().Reverse()) l.Transport.Shutdown();
    }
    private sealed class ListenerSite(ServerTransportMux owner, ITransportSite site) : ITransportSite
    {
        internal Listener Listener = null!;
        public void OnPeerJoined(ConnectionId id, string name)
        {
            ConnectionId outer;
            lock (owner._gate)
            {
                // Exhaustion fails closed rather than wrapping into a live/retired ID.
                if (owner._nextId == uint.MaxValue) { Listener.Transport.ClosePeer(id, DisconnectReason.ProtocolError); return; }
                outer = new(++owner._nextId);
                Listener.Outer.Add(id, outer);
                owner._routes.Add(outer, new Route(Listener, id));
            }
            site.OnPeerJoined(outer, name);
        }
        public void OnPeerLeft(ConnectionId id)
        {
            ConnectionId outer;
            lock (owner._gate)
            {
                if (!Listener.Outer.Remove(id, out outer)) return;
                owner._routes.Remove(outer);
            }
            site.OnPeerLeft(outer);
        }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            if (TryGetOuter(from, out var outer)) site.OnReceive(outer, data);
        }
        void ITransportSite.OnReceiveOwned(ConnectionId from, byte[] body)
        {
            if (TryGetOuter(from, out var outer)) site.OnReceiveOwned(outer, body);
        }
        private bool TryGetOuter(ConnectionId from, out ConnectionId outer)
        {
            lock (owner._gate) return Listener.Outer.TryGetValue(from, out outer);
        }
        public void OnSessionLost() => site.OnSessionLost();
    }
}

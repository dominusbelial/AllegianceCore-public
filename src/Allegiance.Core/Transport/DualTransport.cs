using System.Net;

namespace Allegiance.Core.Transport;

/// <summary>Optional UDP listener alongside the unchanged TCP pool. High-bit ids
/// separate UDP peers from TCP peers; the site never sees colliding ids.</summary>
public sealed class DualTransport : IServerTransport
{
    private const uint UdpBit = 0x80000000;
    public TcpTransport Tcp { get; }
    public UdpTransport Udp { get; }
    private readonly ushort _udpPort;
    public DualTransport(ITransportSite site, TcpTransport tcp, ushort udpPort,
        EgressAccounting? egress, ReliableTelemetry? reliable, ServerCounters counters, bool park)
    {
        Tcp = tcp;
        _udpPort = udpPort;
        Udp = new UdpTransport(new UdpSite(site))
        { Egress = egress, Reliable = reliable, ServerCounters = counters, ReliableOneShot = park,
            IdleTimeout = TcpTransport.DataLinkIdleTimeout, MaxBufferedBytes = (int)Math.Min(int.MaxValue, TcpTransport.HardCapBytes) };
    }
    public EgressAccounting? Egress => Tcp.Egress;
    public ReliableTelemetry? Reliable => Tcp.Reliable;
    public long TotalWrittenBytes => Tcp.TotalWrittenBytes + Udp.TotalWrittenBytes;
    public long TotalEnqueuedBytes => Tcp.TotalEnqueuedBytes + Udp.TotalEnqueuedBytes;
    public ushort BoundPort => Tcp.BoundPort;
    public ushort UdpPort => Udp.BoundPort;
    public ConnectionId LocalId => ConnectionId.Me;
    public bool IsConnected => Tcp.IsConnected;
    public void Host(ushort port = 0) => Host(port, null);
    public void Host(ushort port, IPAddress? bindAddress)
    {
        Tcp.Host(port, bindAddress);
        try { Udp.Host(_udpPort == 0 ? Tcp.BoundPort : _udpPort, bindAddress); }
        catch { Tcp.Shutdown(); throw; }
    }
    public void Join(string address, ushort port, string playerName) => throw new NotSupportedException("host-only multiplexing transport");
    private IServerTransport Route(ConnectionId id) => (id.Value & UdpBit) != 0 ? Udp : Tcp;
    private static ConnectionId Inner(ConnectionId id) => new(id.Value & ~UdpBit);
    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => Route(target).Send(Inner(target), data, guaranteed);
    public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Route(target).SendCoalescing(Inner(target), data, coalesceKey);
    public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Route(target).SendGuaranteedCoalescing(Inner(target), data, coalesceKey);
    public bool IsPeerLoopback(ConnectionId id) => Route(id).IsPeerLoopback(Inner(id));
    public void SetPeerHuman(ConnectionId id, bool human) => Route(id).SetPeerHuman(Inner(id), human);
    public bool IsPeerClosed(ConnectionId id) => Route(id).IsPeerClosed(Inner(id));
    public void ClosePeer(ConnectionId id, DisconnectReason reason = DisconnectReason.Shutdown) => Route(id).ClosePeer(Inner(id), reason);
    public void ClosePeerAfterFlush(ConnectionId id) => Route(id).ClosePeerAfterFlush(Inner(id));
    public IReadOnlyDictionary<DisconnectReason, int> DrainDisconnectCounts()
    {
        var result = new Dictionary<DisconnectReason, int>(Tcp.DrainDisconnectCounts());
        foreach (var entry in Udp.DrainDisconnectCounts()) result[entry.Key] = result.GetValueOrDefault(entry.Key) + entry.Value;
        return result;
    }
    public (int Peers, int MaxOrderedPending, int MaxStreamPending, long MaxQueuedBytes, long TotalQueuedBytes, long MaxOldestGuaranteedMs) SamplePeerQueues()
    {
        var t = Tcp.SamplePeerQueues(); var u = Udp.SamplePeerQueues();
        return (t.Peers + u.Peers, Math.Max(t.MaxOrderedPending, u.MaxOrderedPending), Math.Max(t.MaxStreamPending, u.MaxStreamPending),
            Math.Max(t.MaxQueuedBytes, u.MaxQueuedBytes), t.TotalQueuedBytes + u.TotalQueuedBytes, Math.Max(t.MaxOldestGuaranteedMs, u.MaxOldestGuaranteedMs));
    }
    public void Shutdown() { Udp.Shutdown(); Tcp.Shutdown(); }
    private sealed class UdpSite(ITransportSite site) : ITransportSite
    {
        private static ConnectionId Outer(ConnectionId id) => new(id.Value | UdpBit);
        public void OnPeerJoined(ConnectionId id, string name) => site.OnPeerJoined(Outer(id), name);
        public void OnPeerLeft(ConnectionId id) => site.OnPeerLeft(Outer(id));
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => site.OnReceive(Outer(from), data);
        public void OnSessionLost() => site.OnSessionLost();
    }
}

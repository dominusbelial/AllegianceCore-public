using System.Net;

namespace Allegiance.Core.Transport;

/// <summary>Host lifecycle, admission and accounting shared by TCP and UDP.</summary>
public interface IServerTransport : ITransport
{
    void Host(ushort port, IPAddress? bindAddress);
    bool IsPeerLoopback(ConnectionId id);
    void SetPeerHuman(ConnectionId id, bool human);
    bool IsPeerClosed(ConnectionId id);
    void ClosePeer(ConnectionId id, DisconnectReason reason = DisconnectReason.Shutdown);
    void ClosePeerAfterFlush(ConnectionId id);
    IReadOnlyDictionary<DisconnectReason, int> DrainDisconnectCounts();
    (int Peers, int MaxOrderedPending, int MaxStreamPending, long MaxQueuedBytes, long TotalQueuedBytes, long MaxOldestGuaranteedMs) SamplePeerQueues();
    long TotalWrittenBytes { get; }
    long TotalEnqueuedBytes { get; }
    EgressAccounting? Egress { get; }
    ReliableTelemetry? Reliable { get; }
}

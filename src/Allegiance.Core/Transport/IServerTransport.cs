using System.Net;

namespace Allegiance.Core.Transport;

/// <summary>Host lifecycle, admission and accounting shared by TCP and UDP.</summary>
public interface IServerTransport : ITransport
{
    void Host(ushort port, IPAddress? bindAddress);
    /// <summary>Transport-observed source address, or null if unavailable. Never use untrusted client metadata.</summary>
    IPAddress? PeerAddress(ConnectionId id);
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

/// <summary>Optional send-worker diagnostics, independent of listener composition.</summary>
public interface ISendPoolDiagnostics
{
    SendWindow DrainSendWindow(Span<PeerSendWindow> peers, Span<SendWorkerWindow> workers);
}

/// <summary>Optional UDP receive scheduling diagnostics.</summary>
public interface IUdpReceiveDiagnostics
{
    UdpReceiveDelayWindow DrainReceiveDelay();
}

/// <summary>Optional UDP listener advertised by a composed server transport.</summary>
public interface IUdpListenerInfo
{
    ushort UdpPort { get; }
}

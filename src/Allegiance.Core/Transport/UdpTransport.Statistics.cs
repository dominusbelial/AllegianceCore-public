namespace Allegiance.Core.Transport;

public sealed partial class UdpTransport
{
    private readonly long[] _packetCounts = new long[10], _packetBytes = new long[10];
    private long _dataRecords, _dataHeaders, _ackBytes, _retransmitBytes;
    private long _workerTurns, _retransmits, _sendWaitCount, _sendWaitMs, _sendWaitOver1Ms;

    private void RecordSendWait(long milliseconds)
    {
        _sendWaitCount++;
        _sendWaitMs += milliseconds;
        if (milliseconds > 1) _sendWaitOver1Ms++;
    }

    /// <summary>Successful UDP socket sends, excluding IP/UDP headers. Index is the wire packet kind (1..9).
    /// Cumulative snapshots allow proof tools to exclude handshake/warmup traffic.</summary>
    public UdpTransportStatistics SampleStatistics()
    {
        lock (_gate) return new((long[])_packetCounts.Clone(), (long[])_packetBytes.Clone(),
            _workerTurns, 0, _retransmits, _sendWaitCount, _sendWaitMs, _sendWaitOver1Ms, _receiveSignals, _enqueueSignals, _timerWakes, _dataRecords, _dataHeaders, _ackBytes, _retransmitBytes);
    }
}

public sealed record UdpTransportStatistics(long[] PacketCounts, long[] PacketBytes,
    long WorkerTurns, long EmptyReceives, long Retransmits, long SendWaitCount, long SendWaitMs, long SendWaitOver1Ms,
    long ReceiveSignals = 0, long EnqueueSignals = 0, long TimerWakes = 0, long DataRecords = 0, long DataHeaderBytes = 0, long AckIdBytes = 0, long RetransmitBytes = 0)
{
    public UdpTransportStatistics Since(UdpTransportStatistics before) => new(
        PacketCounts.Zip(before.PacketCounts, (a, b) => a - b).ToArray(),
        PacketBytes.Zip(before.PacketBytes, (a, b) => a - b).ToArray(),
        WorkerTurns - before.WorkerTurns, EmptyReceives - before.EmptyReceives, Retransmits - before.Retransmits,
        SendWaitCount - before.SendWaitCount, SendWaitMs - before.SendWaitMs, SendWaitOver1Ms - before.SendWaitOver1Ms,
        ReceiveSignals - before.ReceiveSignals, EnqueueSignals - before.EnqueueSignals, TimerWakes - before.TimerWakes, DataRecords - before.DataRecords, DataHeaderBytes - before.DataHeaderBytes,
        AckIdBytes - before.AckIdBytes, RetransmitBytes - before.RetransmitBytes);
}

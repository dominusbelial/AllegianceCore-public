namespace Allegiance.Core.Transport;

public sealed partial class EgressAccounting
{
    private readonly bool _transportMetrics;
    public EgressAccounting() : this(Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_METRICS") == "1") { }
    internal EgressAccounting(bool transportMetrics) => _transportMetrics = transportMetrics;
    private readonly TransportCounters[] _transports = [new(), new(), new()];
    private sealed class TransportCounters
    {
        internal long Sends, Bytes, Drops, DroppedBytes, QueueBytes, QueueEntries;
        internal readonly long[] Disconnects = new long[32];
    }
    private static int TransportIndex(string protocol) => protocol switch { "ws" or "wss" => 1, "udp" => 2, _ => 0 };
    private void RecordTransport(string protocol, EgressEvent ev, int bytes, bool complete)
    {
        if (!_transportMetrics) return;
        var c = _transports[TransportIndex(protocol)];
        if (ev == EgressEvent.Sent)
        { Interlocked.Add(ref c.Bytes, bytes); if (complete) Interlocked.Increment(ref c.Sends); }
        if (ev == EgressEvent.Dropped)
        { Interlocked.Add(ref c.DroppedBytes, bytes); Interlocked.Increment(ref c.Drops); }
    }
    /// <summary>Observe queued plus in-flight storage while the sender owns its gate.</summary>
    public void ObserveTransportQueue(string protocol, long bytes, long entries)
    {
        if (!_transportMetrics) return;
        var c = _transports[TransportIndex(protocol)];
        Max(ref c.QueueBytes, bytes); Max(ref c.QueueEntries, entries);
    }
    private static void Max(ref long target, long value)
    {
        long old = Volatile.Read(ref target);
        while (value > old)
        {
            long observed = Interlocked.CompareExchange(ref target, value, old);
            if (old == observed) return;
            old = observed;
        }
    }
    /// <summary>Independent of the existing disconnect-summary drain.</summary>
    public void RecordTransportDisconnect(string protocol, DisconnectReason reason, string? cause = null)
    {
        if (!_transportMetrics) return;
        int index = cause switch { "idle" => 30, "fragment" => 31, _ => (int)reason };
        Interlocked.Increment(ref _transports[TransportIndex(protocol)].Disconnects[index]);
    }
    public IReadOnlyList<TransportEgressWindow> DrainTransportWindows()
    {
        if (!_transportMetrics) return [];
        return _transports.Select((c, i) => new TransportEgressWindow(i == 0 ? "tcp" : i == 1 ? "ws" : "udp",
            Interlocked.Exchange(ref c.Sends, 0), Interlocked.Exchange(ref c.Bytes, 0),
            Interlocked.Exchange(ref c.Drops, 0), Interlocked.Exchange(ref c.DroppedBytes, 0),
            Interlocked.Exchange(ref c.QueueBytes, 0), Interlocked.Exchange(ref c.QueueEntries, 0),
            Enumerable.Range(0, 32).Select(k => (Key: k == 30 ? "IdleTimeout" : k == 31 ? "FragmentTimeout" : ((DisconnectReason)k).ToString(),
                Value: Interlocked.Exchange(ref c.Disconnects[k], 0))).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value))).ToArray();
    }
}

/// <summary>Sends/drops count carrier frames (not FEDMESSAGEs); bytes include carrier headers.
/// Queue maxima are per-peer observed window high-water marks, not fleet totals.</summary>
public sealed record TransportEgressWindow(string Protocol, long Sends, long Bytes, long Drops, long DroppedBytes,
    long QueueBytesHighWater, long QueueEntriesHighWater, IReadOnlyDictionary<string, long> Disconnects);

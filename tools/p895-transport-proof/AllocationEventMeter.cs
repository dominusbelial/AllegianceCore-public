using System.Diagnostics.Tracing;

namespace Allegiance.TransportProof;

/// <summary>Proof-only sampled runtime allocation events. This independent sum
/// never subtracts cumulative GC counters that decreased in local .NET 10 runs.
/// Both cost arms must enable it; its event-dispatch overhead is included.</summary>
internal sealed class AllocationEventMeter : EventListener
{
    private long _bytes, _events;
    internal long Bytes => Interlocked.Read(ref _bytes);
    internal long Samples => Interlocked.Read(ref _events);

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)1); // GCKeyword
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventId != 10 || data.PayloadNames is null || data.Payload is null) return; // GCAllocationTick
        int index = data.PayloadNames.IndexOf("AllocationAmount64");
        if (index < 0) index = data.PayloadNames.IndexOf("AllocationAmount");
        if (index < 0) return;
        long bytes = Convert.ToInt64(data.Payload[index], System.Globalization.CultureInfo.InvariantCulture);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Increment(ref _events);
    }
}

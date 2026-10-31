using Allegiance.Core.Profiling;

namespace Allegiance.Core.Transport;

public sealed partial class UdpTransport
{
    /// <summary>Host-only measurement, fixed storage allocated once at startup.
    /// No kernel-arrival timestamp is exposed by ReceiveFromAsync.</summary>
    public bool MeasureReceiveDelay { get; init; }
    private DelayHistogram? _receiveDelay;
    private long _receiveCompletedAt;
    private long _synchronousReceives;

    /// <summary>One reporting lock on the existing state gate, never a new per-packet lock.</summary>
    public UdpReceiveDelayWindow DrainReceiveDelay()
    {
        lock (_gate)
        {
            var window = new UdpReceiveDelayWindow(_receiveDelay?.Drain() ?? default, _synchronousReceives);
            _synchronousReceives = 0;
            return window;
        }
    }
}

public readonly record struct UdpReceiveDelayWindow(DelayWindow CompletionToWorker, long SynchronousReceives);

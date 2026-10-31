using System.Diagnostics;

namespace Allegiance.Core.Transport;

public enum SendWorkerState { Idle, Scanning, Writing, Backpressure }

/// <summary>Socket acceptance, including the five-byte outer frame header; not remote delivery.</summary>
public readonly record struct PeerSendWindow(ConnectionId Peer, long EnqueuedBytes, long WrittenBytes,
    long QueuedBytes, long NoProgressMs)
{
    /// <summary>P898: current application frame remainder, not kernel TCP flight.</summary>
    public long InflightBytes { get; init; }
    /// <summary>P898: lifetime counts for this peer, not window deltas.</summary>
    public long SupersededFrames { get; init; }
    public long PollBlocked { get; init; }
}

public readonly record struct SendWorkerWindow(int Worker, SendWorkerState State, long WriteCalls,
    double BusyWriteMs, double LongestWriteMs, double CurrentWriteMs);

public readonly record struct SendWindow(double Seconds, long EnqueuedBytes, long WrittenBytes,
    long QueuedBytes, long QueueGrowthBytes, int Peers, int Workers, bool DrainCollapsed);

/// <summary>Host lifetime counters survive peer departure. No storage grows on the send path.</summary>
internal sealed class SendCounters
{
    internal long Enqueued, Written;
}

/// <summary>One writer and one report consumer. The short lock protects timing bookkeeping only,
/// never socket I/O. Splits an in-progress write across windows without losing or double-counting time.</summary>
internal sealed class SendWorkerDiagnostics
{
    private readonly object _gate = new();
    private SendWorkerState _state;
    private long _started, _accountedThrough, _busyTicks, _longestTicks, _calls;

    internal void State(SendWorkerState state) { lock (_gate) _state = state; }
    internal void BeginWrite()
    {
        lock (_gate)
        {
            _state = SendWorkerState.Writing;
            _started = _accountedThrough = Stopwatch.GetTimestamp();
            _calls++;
        }
    }
    internal void EndWrite()
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            _busyTicks += now - _accountedThrough;
            _longestTicks = Math.Max(_longestTicks, now - _started);
            _started = 0;
            _state = SendWorkerState.Scanning;
        }
    }
    internal SendWorkerWindow Drain(int worker)
    {
        lock (_gate)
        {
            long current = 0;
            if (_started != 0)
            {
                long now = Stopwatch.GetTimestamp();
                current = now - _started;
                _busyTicks += now - _accountedThrough;
                _accountedThrough = now;
            }
            const double milliseconds = 1000d;
            double scale = milliseconds / Stopwatch.Frequency;
            var row = new SendWorkerWindow(worker, _state, _calls, _busyTicks * scale,
                Math.Max(_longestTicks, current) * scale, current * scale);
            _calls = _busyTicks = _longestTicks = 0;
            return row;
        }
    }
}

/// <summary>Diagnostic only: queues growing with less than 10% of enqueue rate draining,
/// or a fall below 25% of the preceding nonzero drain rate. One incident per bad window.</summary>
internal sealed class SendStallDetector
{
    private double _previousWrittenRate;
    internal bool Observe(double seconds, long enqueued, long written, long growth)
    {
        double rate = written / Math.Max(seconds, 1e-9);
        bool collapsed = growth > 0 && (enqueued > 0 && written < enqueued * 0.1
            || _previousWrittenRate > 0 && rate < _previousWrittenRate * 0.25);
        _previousWrittenRate = rate;
        return collapsed;
    }
}

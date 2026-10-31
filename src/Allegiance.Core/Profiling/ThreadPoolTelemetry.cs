using System.Diagnostics;

namespace Allegiance.Core.Profiling;

/// <summary>Process-wide counters and one reusable pool probe per host. Sample and
/// DrainWindow run on the simulation thread. No timer, pool tuning or captured context.</summary>
public sealed class ThreadPoolTelemetry : IThreadPoolWorkItem, IDisposable
{
    private readonly DelayHistogram _delay = new();
    private long _windowStart = Stopwatch.GetTimestamp();
    private long _completed = ThreadPool.CompletedWorkItemCount;
    private long _lastQueued, _queuedAt, _elapsed;
    private int _state; // 0 idle, 1 queued/executing, 2 completed (published last)
    private bool _disposed;

    public void Sample()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _state) == 2)
        {
            _delay.Record(_elapsed);
            Volatile.Write(ref _state, 0);
        }
        long now = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _state) != 0 || now - _lastQueued < Stopwatch.Frequency) return;
        _queuedAt = _lastQueued = now;
        Volatile.Write(ref _state, 1);
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    void IThreadPoolWorkItem.Execute()
    {
        _elapsed = Stopwatch.GetTimestamp() - _queuedAt;
        Volatile.Write(ref _state, 2);
    }

    public ThreadPoolWindow DrainWindow()
    {
        // Harvest a completed probe even if no new simulation tick has run.
        Sample();
        long now = Stopwatch.GetTimestamp(), completed = ThreadPool.CompletedWorkItemCount;
        double seconds = Stopwatch.GetElapsedTime(_windowStart, now).TotalSeconds;
        var result = new ThreadPoolWindow(seconds, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
            (completed - _completed) / Math.Max(seconds, 1e-9), _delay.Drain(),
            Volatile.Read(ref _state) == 1 ? Stopwatch.GetElapsedTime(_queuedAt, now).TotalMilliseconds : 0);
        _windowStart = now;
        _completed = completed;
        return result;
    }

    // An outstanding probe only owns this small telemetry object, never a server/socket.
    public void Dispose() => _disposed = true;
}

public readonly record struct ThreadPoolWindow(double Seconds, int Threads, long PendingItems,
    double CompletedPerSecond, DelayWindow Delay, double ProbePendingMs);

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Allegiance.Core.Transport;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Dedicated 100ms watchdog; no ThreadPool work or session/roster gates.</summary>
internal sealed class StallWatchdog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Func<double> _clock;
    private readonly Func<StallMetrics.RuntimeSample> _runtime;
    private readonly object _sampleGate = new();
    private readonly Action<string> _write;
    private readonly ConcurrentDictionary<ITransportSite, ObservedSession> _sessions = new();
    private readonly BlockingCollection<string> _logs = new(2048);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread? _watchdog, _logger;
    private int _disposed;
    private long _logDroppedLines;
    private double _expected;
    internal StallMetrics Metrics { get; }

    internal StallWatchdog(bool start = true, Func<double>? clock = null,
        Func<StallMetrics.RuntimeSample>? runtime = null, Action<string>? write = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        var sampler = new RuntimeSampler();
        _runtime = runtime ?? sampler.Sample;
        _write = write ?? Console.Error.WriteLine;
        Metrics = new StallMetrics(_clock, _runtime());
        _expected = _clock() + 100;
        if (!start) return;
        _logger = new Thread(LogLoop) { IsBackground = true, Name = "allegiance-mcp-stall-log" };
        _watchdog = new Thread(WatchLoop)
        {
            IsBackground = true, Name = "allegiance-mcp-stall-watchdog", Priority = ThreadPriority.Highest,
        };
        _logger.Start();
        _watchdog.Start();
    }

    internal ObservedSession Track(ITransportSite client, string name)
    {
        var session = new ObservedSession(client, name, _clock, (s, reason, detail) =>
        {
            // Include the losing session in the starvation snapshot before removing it.
            try
            {
                StallReport state;
                lock (_sampleGate) state = Metrics.SnapshotAtLoss(_runtime(), ReceiveSnapshot());
                Log($"[session-loss] utc={DateTime.UtcNow:O} name={JsonSerializer.Serialize(s.Name)} reason={reason} " +
                    $"detail={JsonSerializer.Serialize(detail)} readAgeMs={s.ReadAgeMs.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"snapshot={JsonSerializer.Serialize(WithLogCount(state), JsonOptions)}");
            }
            finally { _sessions.TryRemove(client, out _); }
        });
        session.Attach();
        if (!_sessions.TryAdd(client, session)) throw new InvalidOperationException("session already tracked");
        return session;
    }

    internal void Connect(ITransportSite client, string name, string host, ushort port)
    {
        var session = Track(client, name);
        try { session.Transport.Join(host, port, name); }
        catch (Exception ex)
        {
            session.End("connect-exception", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    internal void Close(IDisposable? client)
    {
        if (client is ITransportSite site && _sessions.TryGetValue(site, out var session))
            session.End("our-disposal", "bot manager disposed this session");
        client?.Dispose();
    }

    internal StallMetrics.Receives ReceiveSnapshot()
    {
        int count = 0, over2 = 0, over5 = 0;
        double max = 0;
        foreach (var session in _sessions.Values)
        {
            // Ended sessions are removed just after their loss snapshot.
            count++;
            double age = session.ReadAgeMs;
            if (age > 2000) over2++;
            if (age > 5000) over5++;
            max = Math.Max(max, age);
        }
        return new(count, over2, over5, max);
    }

    private (StallReport Report, StallWindow? Minute, bool Incident) Sample(double late)
    {
        lock (_sampleGate) return Metrics.Observe(late, _runtime(), ReceiveSnapshot());
    }

    internal static JsonElement JsonSnapshot(StallReport report) => JsonSerializer.SerializeToElement(report, JsonOptions);
    internal StallReport Snapshot() => WithLogCount(Metrics.Snapshot());
    private StallReport WithLogCount(StallReport report) => report with { LogDroppedLines = Interlocked.Read(ref _logDroppedLines) };

    internal void Tick()
    {
        double now = _clock();
        double late = Math.Max(0, now - _expected);
        _expected = now + 100; // Drop missed ticks; never run a catch-up burst.
        var sample = Sample(late);
        if (sample.Incident)
            Log($"[stall] utc={DateTime.UtcNow:O} trigger={(late > 1000 ? "watchdog-late" : "receive-starvation")} " +
                $"snapshot={JsonSerializer.Serialize(WithLogCount(sample.Report), JsonOptions)}");
        if (sample.Minute is { } minute)
            Log($"[stall-minute] utc={DateTime.UtcNow:O} window={JsonSerializer.Serialize(minute, JsonOptions)} " +
                $"snapshot={JsonSerializer.Serialize(WithLogCount(sample.Report), JsonOptions)}");
    }

    private void WatchLoop()
    {
        while (!_stop.WaitOne((int)Math.Clamp(_expected - _clock(), 1, 100)))
        {
            try { Tick(); }
            catch (Exception ex) { Log($"[stall-watchdog-error] {JsonSerializer.Serialize(ex.Message)}"); }
        }
    }
    private void Log(string line)
    {
        if (_logger is null) { Write(line); return; }
        // Keep the high-priority thread off stderr I/O. Bounded queue, no waiting.
        try { if (!_logs.TryAdd(line)) Interlocked.Increment(ref _logDroppedLines); }
        catch (InvalidOperationException) { Interlocked.Increment(ref _logDroppedLines); }
    }
    private void LogLoop()
    {
        foreach (var line in _logs.GetConsumingEnumerable())
            Write(line);
    }
    private void Write(string line)
    {
        try { _write(line); }
        catch (IOException) { Interlocked.Increment(ref _logDroppedLines); }
        catch (ObjectDisposedException) { Interlocked.Increment(ref _logDroppedLines); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        bool stopped = _watchdog?.Join(500) ?? true;
        foreach (var site in _sessions.Keys) Close((IDisposable)site);
        _logs.CompleteAdding();
        bool flushed = _logger?.Join(500) ?? true;
        if (stopped) _stop.Dispose();
        if (flushed) _logs.Dispose();
        // A blocked stderr writer may outlive disposal; it owns its queue until exit.
    }

    private sealed class RuntimeSampler
    {
        private readonly long[] _indices = new long[4];
        internal StallMetrics.RuntimeSample Sample()
        {
            double max = 0;
            for (int k = 0; k < _indices.Length; k++)
            {
                var kind = (GCKind)k;
                var info = GC.GetGCMemoryInfo(kind);
                if (info.Index <= _indices[(int)kind]) continue;
                _indices[(int)kind] = info.Index;
                foreach (var pause in info.PauseDurations) max = Math.Max(max, pause.TotalMilliseconds);
            }
            return new(GC.GetTotalPauseDuration().TotalMilliseconds, max,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount);
        }
    }
}

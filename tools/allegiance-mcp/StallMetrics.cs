namespace Allegiance.Tools.McpSpawner;

/// <summary>Monotonic, socket-free minute aggregation. Readers never reset a window.</summary>
internal sealed class StallMetrics
{
    internal readonly record struct RuntimeSample(double TotalGcPauseMs, double NewGcPauseMaxMs,
        long Gen0, long Gen1, long Gen2, int PoolThreads, long PoolPending, long PoolCompleted);
    internal readonly record struct Receives(int Sessions, int Over2Seconds, int Over5Seconds, double MaxAgeMs);
    private readonly object _gate = new();
    private readonly Func<double> _clock;
    private double _start, _lastWatchdog, _maxPass, _maxLate, _maxGcPause, _poolNoProgressSince;
    private double _passStart = -1;
    private RuntimeSample _baseline, _runtime;
    private Receives _receives;
    private int _peak2, _peak5, _peakThreads;
    private long _peakPending, _incidents;
    private double _maxPoolNoProgress, _lastIncident = double.NegativeInfinity;
    private bool _incidentActive;
    private double _lastLate;
    private StallWindow? _completed;

    internal StallMetrics(Func<double> clock, RuntimeSample baseline)
    {
        _clock = clock;
        _start = _lastWatchdog = clock();
        _baseline = _runtime = baseline;
        _poolNoProgressSince = -1;
    }

    internal void BeginPass() { lock (_gate) _passStart = _clock(); }
    internal void EndPass(double durationMs)
    {
        lock (_gate) { _maxPass = Math.Max(_maxPass, durationMs); _passStart = -1; }
    }

    // One incident per episode, with a ten-second reminder if starvation persists.
    internal static bool IsIncident(double lateMs, int starved2) => lateMs > 1000 || starved2 > 10;

    internal (StallReport Report, StallWindow? Minute, bool Incident) Observe(
        double lateMs, RuntimeSample runtime, Receives receives)
    {
        lock (_gate)
        {
            double now = _clock();
            Refresh(now, runtime, receives);
            _lastWatchdog = now;
            _lastLate = lateMs;
            _maxLate = Math.Max(_maxLate, Math.Max(0, lateMs));
            bool active = IsIncident(lateMs, receives.Over2Seconds);
            bool incident = active && (!_incidentActive || now - _lastIncident >= 10000);
            _incidentActive = active;
            if (incident) { _incidents++; _lastIncident = now; }
            var report = Report(now, lateMs);
            StallWindow? minute = null;
            if (now - _start >= 60000)
            {
                minute = _completed = Window(now);
                _start = now;
                _baseline = runtime;
                _maxPass = _maxLate = _maxGcPause = _maxPoolNoProgress = 0;
                _peak2 = _peak5 = _peakThreads = 0;
                _peakPending = 0;
            }
            return (report, minute, incident);
        }
    }

    internal StallReport Snapshot()
    {
        lock (_gate) return Report(_clock(), _lastLate);
    }

    // Loss callbacks take fresh runtime/receive samples but do not impersonate
    // watchdog wakeups, reset minutes, or change incident episode/cooldown state.
    internal StallReport SnapshotAtLoss(RuntimeSample runtime, Receives receives)
    {
        lock (_gate)
        {
            double now = _clock();
            Refresh(now, runtime, receives);
            return Report(now, _lastLate);
        }
    }

    private void Refresh(double now, RuntimeSample runtime, Receives receives)
    {
        if (runtime.PoolPending > 0)
        {
            if (_poolNoProgressSince < 0 || runtime.PoolCompleted != _runtime.PoolCompleted)
                _poolNoProgressSince = now;
        }
        else _poolNoProgressSince = -1;
        _runtime = runtime;
        _receives = receives;
        _maxGcPause = Math.Max(_maxGcPause, runtime.NewGcPauseMaxMs);
        _peak2 = Math.Max(_peak2, receives.Over2Seconds);
        _peak5 = Math.Max(_peak5, receives.Over5Seconds);
        _peakThreads = Math.Max(_peakThreads, runtime.PoolThreads);
        _peakPending = Math.Max(_peakPending, runtime.PoolPending);
        _maxPoolNoProgress = Math.Max(_maxPoolNoProgress, PoolNoProgress(now));
    }

    private double PoolNoProgress(double now) => _poolNoProgressSince < 0 ? 0 : Math.Max(0, now - _poolNoProgressSince);
    private StallWindow Window(double now) => new(_start, Math.Max(0, now - _start), _maxPass,
        _maxGcPause, Math.Max(0, _runtime.TotalGcPauseMs - _baseline.TotalGcPauseMs),
        _runtime.Gen0 - _baseline.Gen0, _runtime.Gen1 - _baseline.Gen1, _runtime.Gen2 - _baseline.Gen2,
        _peakThreads, _peakPending, _maxPoolNoProgress, _maxLate, _peak2, _peak5);
    private StallReport Report(double now, double late) => new(Window(now), _completed,
        _runtime.PoolThreads, _runtime.PoolPending, PoolNoProgress(now), PoolNoProgress(now) > 1000,
        late, Math.Max(0, now - _lastWatchdog), _receives.Sessions, _receives.Over2Seconds,
        _receives.Over5Seconds, _receives.MaxAgeMs, _passStart < 0 ? 0 : Math.Max(0, now - _passStart), _incidents);
}

public sealed record StallWindow(double StartMonotonicMs, double DurationMs, double DecisionPassMaxMs,
    double GcPauseMaxMs, double GcPauseTotalMs, long GcGen0, long GcGen1, long GcGen2,
    int ThreadPoolThreadsMax, long ThreadPoolPendingMax, double ThreadPoolNoProgressMaxMs,
    double WatchdogLateMaxMs, int SessionsReadOver2sMax, int SessionsReadOver5sMax);

public sealed record StallReport(StallWindow CurrentMinute, StallWindow? LastCompletedMinute,
    int ThreadPoolThreads, long ThreadPoolPending, double ThreadPoolNoProgressMs, bool ThreadPoolStarvationSuspected,
    double WatchdogLateMs, double WatchdogSampleAgeMs, int Sessions, int SessionsReadOver2s,
    int SessionsReadOver5s, double ReceiveAgeMaxMs, double DecisionPassInProgressMs, long IncidentCount,
    long LogDroppedLines = 0);

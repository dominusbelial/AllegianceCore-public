using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Allegiance.Core.Profiling;

/// <summary>
/// P600 — a lightweight, feature-flagged per-tick profiler for the server's
/// single simulation thread. OFF by default (the static <see cref="Instance"/>
/// is null), in which case every hot-path call collapses to a null check and a
/// defaulted no-op struct — so the disabled cost is negligible (verified by
/// <c>TickProfilerDisabledCostTests</c>). When enabled it records, per
/// simulation tick, the wall time spent in each named bucket (server step
/// stages, intra-sector subsystem passes, per-sector updates) plus an optional
/// per-sector object/ship population, then every
/// <see cref="ReportIntervalSeconds"/> it folds a window into
/// mean / p95 / max milliseconds per tick per bucket, prints it to the log and —
/// when a CSV path is configured — appends one row per bucket per window.
/// </summary>
/// <remarks>
/// <para><b>Thread model.</b> Everything the profiler measures runs on the
/// server's single simulation thread (<c>SimPass</c>, <c>Mission.Update</c>,
/// <c>Sector.Update</c>), so the profiler needs no locking: buckets accumulate
/// on the sim thread and are reported on the sim thread. Transport send threads
/// are deliberately NOT profiled here — their CPU/context-switch behaviour is a
/// separate concern (P600 5b) measured with OS counters, not in-tick sampling.</para>
/// <para><b>Per-tick folding.</b> Rather than store one sample per pass (there
/// can be thousands of <c>Sector</c> passes per tick at 300 players), each
/// bucket folds its elapsed time into a single per-tick accumulator
/// (<see cref="_cur"/>), which is promoted into the window histogram once at
/// <see cref="FinishTick"/>. Storage is therefore one double per bucket per
/// tick, independent of the number of passes.</para>
/// <para><b>Zero-cost disabled path.</b> <see cref="Enter"/> returns a
/// defaulted <see cref="ProfilerScope"/> (null interior) when disabled, whose
/// <c>Dispose</c> is a no-op; <see cref="EnterTick"/>/<see cref="EndTick"/> and
/// <see cref="RecordSectorCounts"/> each do a single null check and return.</para>
/// </remarks>
public sealed class TickProfiler
{
    /// <summary>The live profiler, or null when disabled. Null is the
    /// zero-cost path: every entry point checks this before doing any work.</summary>
    public static TickProfiler? Instance { get; private set; }

    private readonly int _reportIntervalSeconds;
    private readonly string? _csvPath;

    // P600 — the profiler's mutable state is guarded by a single lock. In
    // production everything runs on the server's one simulation thread and the
    // lock is uncontended; it exists so that a unit test that enables the global
    // profiler cannot corrupt a CONCURRENTLY-running live test's tick (the Core
    // test assembly runs many live-socket tests in parallel, all of which call
    // Mission/Sector.Update). Guarding the few mutations per tick (one dictionary
    // write per bucket per tick, not per pass) costs nothing at 1 thread.
    private readonly object _lock = new();

    /// <summary>The configured report-window length, in seconds (read by the boot
    /// banner so the operator sees what a measurement run will emit).</summary>
    public int ReportIntervalSeconds => _reportIntervalSeconds;

    /// <summary>The configured CSV output path, or null for log-only (read by the
    /// boot banner).</summary>
    public string? CsvPath => _csvPath;

    // Per-tick accumulator: bucket name -> elapsed ms accumulated THIS tick.
    private readonly Dictionary<string, double> _cur = new();

    // Window histogram: bucket name -> one duration (ms) per tick in the current window.
    private readonly Dictionary<string, List<double>> _hist = new();

    // Sector object/ship population (most recent sample per sector id).
    private readonly Dictionary<int, (int Objects, int Ships)> _sectorCounts = new();

    private readonly List<double> _tickWallList = new();   // whole-SimPass wall ms, one per tick
    private double _tickWallMax;
    private long _ticksInWindow;
    private long _windowStartUnix;
    private readonly Stopwatch _tickSw = new();
    private StreamWriter? _csv;

    private TickProfiler(int reportIntervalSeconds, string? csvPath)
    {
        _reportIntervalSeconds = Math.Max(1, reportIntervalSeconds);
        _csvPath = csvPath;
        _windowStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (csvPath is { Length: > 0 })
        {
            _csv = new StreamWriter(csvPath, append: true);
            _csv.AutoFlush = true;
            _csv.WriteLine("# P600 tick profiler — one row per bucket per report window.");
            _csv.WriteLine("window_start_unix,window_seconds,bucket,count,mean_ms,p95_ms,max_ms,total_ms");
        }
    }

    /// <summary>Enable (enabled = true) or disable (false) the profiler globally.
    /// A disabled profiler costs nothing thereafter (the Instance is null).</summary>
    public static void Configure(bool enabled, int reportIntervalSeconds, string? csvPath)
    {
        TickProfiler? prev;
        lock (StaticGate)
        {
            prev = Instance;
            Instance = enabled ? new TickProfiler(reportIntervalSeconds, csvPath) : null;
        }
        prev?.Close();
    }

    /// <summary>Flush and release the CSV handle (best-effort; called at shutdown).</summary>
    public static void Shutdown() => Configure(false, 5, null);

    // Guards the Instance pointer swap in Configure/Shutdown (only those two
    // mutate Instance; readers read the static field, which is an atomic reference).
    private static readonly object StaticGate = new();

    private void Close()
    {
        // Lock so a concurrent Report (another test thread in the enabled window)
        // can't be mid-write when we dispose the CSV writer.
        lock (_lock)
        {
            try { _csv?.Dispose(); } catch { /* best-effort */ }
            _csv = null;
        }
    }

    /// <summary>Mark the start of one simulation tick (called once per SimPass).</summary>
    public static void EnterTick()
    {
        var p = Instance;
        if (p is null) return;
        lock (p._lock) p._tickSw.Restart();
    }

    /// <summary>Close the current tick: fold per-bucket samples into the window,
    /// and roll the window over + report when the interval has elapsed.</summary>
    public static void EndTick()
    {
        var p = Instance;
        if (p is null) return;
        p.FinishTick();
    }

    private void FinishTick()
    {
        lock (_lock)
        {
            _tickSw.Stop();
            double wall = _tickSw.Elapsed.TotalMilliseconds;
            _tickWallList.Add(wall);
            if (wall > _tickWallMax) _tickWallMax = wall;
            _ticksInWindow++;

            foreach (var (bucket, ms) in _cur)
            {
                if (!_hist.TryGetValue(bucket, out var list))
                {
                    list = new List<double>();
                    _hist[bucket] = list;
                }
                list.Add(ms);
            }
            _cur.Clear();

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _windowStartUnix >= _reportIntervalSeconds)
                Report();
        }
    }

    /// <summary>
    /// Time one named bucket until the returned scope is disposed. Returns a
    /// defaulted no-op scope when the profiler is disabled (zero cost).
    /// </summary>
    public static ProfilerScope Enter(string bucket)
    {
        var p = Instance;
        if (p is null) return default;
        return new ProfilerScope(p, bucket, Stopwatch.GetTimestamp());
    }

    internal void Record(string bucket, long elapsedTicks)
    {
        double ms = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        lock (_lock)
        {
            _cur.TryGetValue(bucket, out var cur);
            _cur[bucket] = cur + ms;
        }
    }

    /// <summary>P600 test seam — force an immediate window report (no interval
    /// wait), so tests can verify aggregation deterministically. No-op when off.</summary>
    internal static void ForceReport()
    {
        var p = Instance;
        if (p is not null) p.Report();
    }

    /// <summary>Record a sector's object/ship population for the next report.
    /// No-op when disabled.</summary>
    public static void RecordSectorCounts(int sectorId, int objects, int ships)
    {
        var p = Instance;
        if (p is null) return;
        lock (p._lock) p._sectorCounts[sectorId] = (objects, ships);
    }

    private void Report()
    {
        lock (_lock)
        {
            long startUnix = _windowStartUnix;
            double seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startUnix;
            _windowStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            double totSum = _tickWallList.Sum();
            Console.WriteLine(
                $"[tickprof] window {seconds:0.##}s ticks={_ticksInWindow} " +
                $"tick-wall mean={MeanOf(_tickWallList):0.00}ms p95={P95Of(_tickWallList):0.00}ms max={_tickWallMax:0.00}ms");

            foreach (var bucket in _hist.Keys.OrderByDescending(b => MeanOf(_hist[b])))
            {
                var list = _hist[bucket];
                double mean = MeanOf(list), p95 = P95Of(list), max = MaxOf(list);
                double total = mean * list.Count;
                double pct = totSum > 0 ? 100.0 * total / totSum : 0.0;
                Console.WriteLine(
                    $"  {bucket,-34} mean {mean,7:0.00}  p95 {p95,7:0.00}  max {max,7:0.00}ms  ({pct,5:0.#}%)");
                _csv?.WriteLine(
                    $"{startUnix},{seconds:0.###},{bucket},{list.Count},{mean:0.####},{p95:0.####},{max:0.####},{total:0.####}");
            }

            if (_sectorCounts.Count > 0)
            {
                Console.WriteLine("  [sector populations]");
                foreach (var (id, (objects, ships)) in _sectorCounts)
                    Console.WriteLine($"    sector {id}: {ships} ships / {objects} objects");
            }

            _hist.Clear();
            _tickWallList.Clear();
            _tickWallMax = 0;
            _ticksInWindow = 0;
        }
    }

    private static double MeanOf(IReadOnlyCollection<double> xs) => xs.Count == 0 ? 0.0 : xs.Sum() / xs.Count;

    private static double MaxOf(IReadOnlyCollection<double> xs) => xs.Count == 0 ? 0.0 : xs.Max();

    private static double P95Of(List<double> xs)
    {
        if (xs.Count == 0) return 0.0;
        var sorted = new List<double>(xs);
        sorted.Sort();
        int idx = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }
}

/// <summary>
/// RAII scope returned by <see cref="TickProfiler.Enter"/>: records the elapsed
/// time since construction into its bucket when disposed. The default value (all
/// null fields) is the disabled no-op form and is what <see cref="TickProfiler.Enter"/>
/// returns when the profiler is off.
/// </summary>
public readonly struct ProfilerScope : IDisposable
{
    private readonly TickProfiler? _p;
    private readonly string _bucket;
    private readonly long _start;

    internal ProfilerScope(TickProfiler p, string bucket, long start)
    {
        _p = p;
        _bucket = bucket;
        _start = start;
    }

    public void Dispose() => _p?.Record(_bucket, Stopwatch.GetTimestamp() - _start);
}

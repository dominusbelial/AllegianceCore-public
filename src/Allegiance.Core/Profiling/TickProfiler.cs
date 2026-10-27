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

    /// <summary>
    /// P619 — true only while slow-tick capture is armed (profiler on AND
    /// <c>slowTickCapture</c> AND a positive threshold). Hot paths read this
    /// static (not <see cref="Instance"/> + field) once per call site and skip
    /// all capture machinery when false, so an uninstrumented run pays a single
    /// boolean check with no other cost.
    /// </summary>
    public static bool SlowTickCaptureActive { get; private set; }

    private readonly int _reportIntervalSeconds;
    private readonly string? _csvPath;

    // P619 — slow-tick capture. A per-tick wall time at or above _slowTickMs
    // (while _slowTickCapture) emits one [slowtick] line: the top sub-step
    // (bucket) timings inside that tick, the GC-collection deltas (before/after
    // EnterTick/EndTick), the GC pause-duration delta, and the max/concurent
    // worker duration from the parallel broadcast build. All zero-cost when off.
    private readonly double _slowTickMs;
    private readonly bool _slowTickCapture;
    private long _tickSeq;
    private long _gcStart0, _gcStart1, _gcStart2;
    private double _pauseStartMs;
    private double _workerMaxMs;
    private int _workerCount;

    // P838 — per-window GC accounting for the [gcprof] line. All counters are
    // baselined once per report window (not per tick) and differenced in
    // Report(); the only per-tick work is one GC.CollectionCount(0) read plus
    // (only on ticks where a collection actually happened, ~10/s on the rig)
    // one GC.GetGCMemoryInfo call to sample the last GC's pauses + heap. No
    // per-object cost anywhere.
    private long _gcWindowStartAlloc;        // GC.GetTotalAllocatedBytes(false) at window start
    private double _gcWindowStartPauseMs;    // GC.GetTotalPauseDuration() at window start
    private long _gcWindowStartGen0, _gcWindowStartGen1, _gcWindowStartGen2;
    private long _lastGen0Sample;            // gen0 count seen at the previous FinishTick (pause sampler)
    private double _gcWorstPauseMs;          // worst single pause observed in this window
    private long _heapAfterGcBytes;          // heap size as of the most recent GC (GCMemoryInfo.HeapSizeBytes)
    // P838 — per-window allocation attribution per `step:` bucket (bytes).
    // Filled by ProfilerScope.Dispose for top-level step scopes only; steps run
    // sequentially on the sim thread, so the GLOBAL counter delta is the honest
    // per-step number (it includes the parallel broadcast/sector workers that
    // run inside the step). Cleared at each Report().
    private readonly Dictionary<string, long> _allocByStep = new();

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

    /// <summary>
    /// P603 — an optional short label (e.g. the active broadcast build mode)
    /// stamped into every report header so a measurement run is identifiable.
    /// Set via <see cref="SetLabel"/>; null (the default) = no label, so the
    /// pre-P603 report line is unchanged when nothing labels the run.
    /// </summary>
    private static string? _label;

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

    private TickProfiler(int reportIntervalSeconds, string? csvPath, double slowTickMs = 0, bool slowTickCapture = false)
    {
        _reportIntervalSeconds = Math.Max(1, reportIntervalSeconds);
        _csvPath = csvPath;
        _slowTickMs = slowTickMs;
        _slowTickCapture = slowTickCapture;
        _windowStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // P838 — baseline the window GC counters so the first Report()'s deltas
        // cover exactly the profiler's own lifetime.
        _gcWindowStartAlloc = GC.GetTotalAllocatedBytes(precise: false);
        _gcWindowStartPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        _gcWindowStartGen0 = GC.CollectionCount(0);
        _gcWindowStartGen1 = GC.CollectionCount(1);
        _gcWindowStartGen2 = GC.CollectionCount(2);
        _lastGen0Sample = _gcWindowStartGen0;
        if (csvPath is { Length: > 0 })
        {
            _csv = new StreamWriter(csvPath, append: true);
            _csv.AutoFlush = true;
            _csv.WriteLine("# P600 tick profiler — one row per bucket per report window.");
            _csv.WriteLine("window_start_unix,window_seconds,bucket,count,mean_ms,p95_ms,max_ms,total_ms");
        }
    }

    /// <summary>Enable (enabled = true) or disable (false) the profiler globally.
    /// A disabled profiler costs nothing thereafter (the Instance is null).
    /// <paramref name="slowTickMs"/> (&gt;0) + <paramref name="slowTickCapture"/>
    /// arm the P619 slow-tick capture on the enabled instance.</summary>
    public static void Configure(bool enabled, int reportIntervalSeconds, string? csvPath,
        double slowTickMs = 0, bool slowTickCapture = false)
    {
        TickProfiler? prev;
        lock (StaticGate)
        {
            prev = Instance;
            Instance = enabled ? new TickProfiler(reportIntervalSeconds, csvPath, slowTickMs, slowTickCapture) : null;
            SlowTickCaptureActive = enabled && slowTickCapture && slowTickMs > 0;
        }
        prev?.Close();
    }

    /// <summary>Flush and release the CSV handle (best-effort; called at shutdown).</summary>
    public static void Shutdown() => Configure(false, 5, null);

    /// <summary>P603 — set the short label stamped into every report header
    /// (e.g. the active broadcast build mode).</summary>
    public static void SetLabel(string? label)
    {
        lock (StaticGate) _label = label;
    }

    /// <summary>P667 — read the current report-header label so a later resolver
    /// can APPEND to it instead of clobbering the earlier flags (broadcast mode,
    /// transport mode, ship-update model, sim time-scale). Strings are immutable
    /// so the unlocked read is safe.</summary>
    public static string? Label => _label;

    /// <summary>
    /// P618 — fired once per report window roll-over, immediately after the
    /// <c>[tickprof]</c> header (and bucket lines) are written. The server
    /// subscribes to emit its <c>[tidi] scale=… cost=…</c> line and to reprint
    /// the live <c>sim.timeScale</c> in the <c>[tickprof]</c> label. Subscribers
    /// must not block or re-enter the profiler.
    /// </summary>
    public static event Action? WindowReported;

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
        lock (p._lock)
        {
            p._tickSw.Restart();
            p._tickSeq++;
            // P619 — reset per-tick slow-tick capture state and, when armed,
            // baseline the GC counters so a slow tick's GC activity is attributable.
            p._workerMaxMs = 0;
            p._workerCount = 0;
            if (p._slowTickCapture && p._slowTickMs > 0)
            {
                p._gcStart0 = GC.CollectionCount(0);
                p._gcStart1 = GC.CollectionCount(1);
                p._gcStart2 = GC.CollectionCount(2);
                p._pauseStartMs = GC.GetTotalPauseDuration().TotalMilliseconds;
            }
        }
    }

    /// <summary>
    /// P619 — record one parallel-broadcast worker's build duration (ms) for
    /// this tick's slow-tick capture (the max + count appear on a [slowtick]
    /// line). Called by each <c>Parallel.For</c> worker in
    /// <c>BroadcastShipStatesParallel</c>. No-op (single null check + flag check)
    /// when the profiler or capture is off.
    /// </summary>
    public static void RegisterWorker(double ms)
    {
        var p = Instance;
        if (p is null || !p._slowTickCapture || p._slowTickMs <= 0) return;
        lock (p._lock)
        {
            if (ms > p._workerMaxMs) p._workerMaxMs = ms;
            p._workerCount++;
        }
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

            // P838 — sample the worst single GC pause cheaply: only when a
            // collection happened during this tick do we call
            // GC.GetGCMemoryInfo(GCKind.Any), whose PauseDurations hold the LAST
            // GC's pause durations (a blocking GC has one pause; a background
            // gen2 can have several) and whose HeapSizeBytes is the heap size
            // after that GC. On the rig that is ~10 info calls/s, zero otherwise
            // (no pause → nothing to sample). Verified working under NativeAOT
            // (the published server binary emits the sampled values).
            long gen0Now = GC.CollectionCount(0);
            if (gen0Now != _lastGen0Sample)
            {
                _lastGen0Sample = gen0Now;
                var info = GC.GetGCMemoryInfo(GCKind.Any);
                var pauses = info.PauseDurations;
                for (int i = 0; i < pauses.Length; i++)
                {
                    double p = pauses[i].TotalMilliseconds;
                    if (p > _gcWorstPauseMs) _gcWorstPauseMs = p;
                }
                if (info.HeapSizeBytes > 0) _heapAfterGcBytes = info.HeapSizeBytes;
            }

            // P619 — emit the slow-tick capture line the moment a tick exceeds
            // the threshold, from this tick's still-intact bucket accumulator
            // (_cur is folded + cleared just below) and the GC baselines taken at
            // EnterTick. Captures inside-parallel-build time (worker max) too.
            if (_slowTickCapture && _slowTickMs > 0 && wall >= _slowTickMs)
                EmitSlowTick(wall);

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

    /// <summary>
    /// P667 — snapshot the current window's per-bucket aggregates (count, mean,
    /// p95, max, total ms) WITHOUT rolling the window or touching the CSV. Lets a
    /// benchmark (or a test) read where a run's time went directly from the
    /// in-memory histograms. Returns an empty map when the profiler is off.
    /// </summary>
    public static Dictionary<string, (int Count, double MeanMs, double P95Ms, double MaxMs, double TotalMs)> Snapshot()
    {
        var p = Instance;
        if (p is null) return new Dictionary<string, (int, double, double, double, double)>();
        lock (p._lock)
        {
            var d = new Dictionary<string, (int, double, double, double, double)>();
            foreach (var kv in p._hist)
            {
                var l = kv.Value;
                if (l.Count == 0) continue;
                double mean = MeanOf(l);
                d[kv.Key] = (l.Count, mean, P95Of(l), MaxOf(l), mean * l.Count);
            }
            return d;
        }
    }

    /// <summary>
    /// P838 — accumulate one <c>step:</c> scope's allocated-bytes delta (global
    /// counter, all threads) into the window's per-step table, reported as the
    /// top allocating steps on the <c>[gcprof]</c> line. Called from
    /// <see cref="ProfilerScope.Dispose"/> for step scopes only.
    /// </summary>
    internal void RecordAlloc(string bucket, long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            _allocByStep.TryGetValue(bucket, out var cur);
            _allocByStep[bucket] = cur + bytes;
        }
    }

    /// <summary>P838 test/bench seam — snapshot the current window's per-step
    /// allocation table (bytes per <c>step:</c> bucket) without rolling the
    /// window. Empty when the profiler is off or no step scope has run. Public
    /// like <see cref="Snapshot"/> so the proof benches (outside the solution,
    /// no InternalsVisibleTo) can read it.</summary>
    public static Dictionary<string, long> SnapshotAlloc()
    {
        var p = Instance;
        if (p is null) return new Dictionary<string, long>();
        lock (p._lock) return new Dictionary<string, long>(p._allocByStep);
    }

    /// <summary>Record a sector's object/ship population for the next report.
    /// No-op when disabled.</summary>
    public static void RecordSectorCounts(int sectorId, int objects, int ships)
    {
        var p = Instance;
        if (p is null) return;
        lock (p._lock) p._sectorCounts[sectorId] = (objects, ships);
    }

    /// <summary>
    /// P619 — one slow-tick line. Called from <see cref="FinishTick"/> under the
    /// lock when this tick's wall time met the armed threshold. Reads this tick's
    /// bucket accumulator (<c>_cur</c>, folded+cleared right after), the GC
    /// baselines taken at <see cref="EnterTick"/>, and the parallel-build worker
    /// stats registered via <see cref="RegisterWorker"/>. The line carries the top
    /// sub-step timings INCLUDING inside the parallel build (the worker max), plus
    /// whether a GC happened during the tick, so an unattributable multi-second
    /// tick (all buckets summing to far less than the wall) is distinguished from
    /// one consumed by a single sub-step or a GC pause.
    /// </summary>
    private void EmitSlowTick(double wall)
    {
        var top = _cur
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .ToList();
        long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        long dg0 = g0 - _gcStart0, dg1 = g1 - _gcStart1, dg2 = g2 - _gcStart2;
        double dpause = pause - _pauseStartMs;
        string buckets = top.Count == 0
            ? "(no timed sub-steps)"
            : string.Join(", ", top.Select(kv => $"{kv.Key}={kv.Value:0}ms"));
        double bucketSum = top.Sum(kv => kv.Value);
        Console.WriteLine(
            $"[slowtick] #{_tickSeq} wall={wall:0}ms (≥{_slowTickMs:0}ms) top8[ {buckets} ] " +
            $"bucketSum={bucketSum:0}ms unaccounted={(wall - bucketSum):0}ms " +
            $"gc(gen0+{dg0},gen1+{dg1},gen2+{dg2},pause+{dpause:0}ms) " +
            $"parallel-worker max={_workerMaxMs:0}ms (workers={_workerCount})");
    }

    private void Report()
    {
        lock (_lock)
        {
            long startUnix = _windowStartUnix;
            double seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startUnix;
            _windowStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            double totSum = _tickWallList.Sum();
            // P603 — stamp the optional label (e.g. broadcast build mode) into the
            // report header so a measurement run is identifiable at a glance.
            string label = _label is { Length: > 0 } ? $" [{_label}]" : "";
            Console.WriteLine(
                $"[tickprof] window {seconds:0.##}s ticks={_ticksInWindow}{label} " +
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

            // P838 — the per-window GC line. Emitted AFTER the bucket lines so
            // the existing [tickprof] header + step: format stay byte-identical
            // (the rig's filter scripts parse those first). All values are
            // window deltas: pause total (and its share of the tick-wall sum),
            // collections per generation, bytes allocated per tick (global
            // counter — all threads), heap size after the most recent GC, and
            // the worst single pause sampled at collection boundaries.
            double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - _gcWindowStartPauseMs;
            long allocBytes = GC.GetTotalAllocatedBytes(precise: false) - _gcWindowStartAlloc;
            long dg0 = GC.CollectionCount(0) - _gcWindowStartGen0;
            long dg1 = GC.CollectionCount(1) - _gcWindowStartGen1;
            long dg2 = GC.CollectionCount(2) - _gcWindowStartGen2;
            int ticks = (int)_ticksInWindow;
            double pausePerTick = ticks > 0 ? pauseMs / ticks : 0;
            double pauseShare = totSum > 0 ? 100.0 * pauseMs / totSum : 0.0;
            double kbPerTick = ticks > 0 ? allocBytes / 1024.0 / ticks : 0;
            Console.WriteLine(
                $"[gcprof] window {seconds:0.##}s ticks={ticks} " +
                $"pause={pauseMs:0.0}ms ({pausePerTick:0.000}ms/tick, {pauseShare:0.0}% of tick-wall) " +
                $"gc gen0={dg0} gen1={dg1} gen2={dg2} " +
                $"alloc={allocBytes / 1048576.0:0.0}MB ({kbPerTick:0.0}KB/tick) " +
                $"heapAfterGc={(_heapAfterGcBytes > 0 ? _heapAfterGcBytes : GC.GetGCMemoryInfo(GCKind.Any).HeapSizeBytes) / 1048576.0:0.0}MB worstPause={_gcWorstPauseMs:0.00}ms");
            if (_allocByStep.Count > 0)
            {
                string top = string.Join(", ", _allocByStep.OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => $"{kv.Key}={kv.Value / 1048576.0:0.0}MB"));
                Console.WriteLine($"  [gcprof] top-alloc steps (global counter, incl. parallel workers): {top}");
            }

            // Re-baseline the window GC counters for the next window.
            _gcWindowStartAlloc = GC.GetTotalAllocatedBytes(precise: false);
            _gcWindowStartPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
            _gcWindowStartGen0 = GC.CollectionCount(0);
            _gcWindowStartGen1 = GC.CollectionCount(1);
            _gcWindowStartGen2 = GC.CollectionCount(2);
            _gcWorstPauseMs = 0;
            _allocByStep.Clear();

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

        // P618 — notify subscribers (server emits [tidi], refreshes the label)
        // AFTER the lock is released so a subscriber that prints/logs cannot
        // deadlock against a concurrent reader.
        WindowReported?.Invoke();
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
    private readonly long _allocStart;   // P838 — global allocated bytes at enter (step: buckets only)
    private readonly bool _trackAlloc;   // P838 — default false = the disabled no-op form

    internal ProfilerScope(TickProfiler p, string bucket, long start)
    {
        _p = p;
        _bucket = bucket;
        _start = start;
        // P838 — allocation attribution for the TOP-LEVEL step scopes only.
        // Steps run sequentially on the sim thread, so the GLOBAL counter delta
        // (GC.GetTotalAllocatedBytes — all threads, including the parallel
        // broadcast/sector workers that run inside a step) is the honest
        // per-step number. Inner buckets stay time-only: the per-thread counter
        // (GC.GetAllocatedBytesForCurrentThread) would double-count nested
        // scopes and miss the workers entirely. Cost: one counter read at
        // enter + one at dispose per step (~20 per tick), nothing per object.
        if (bucket.StartsWith("step:", StringComparison.Ordinal))
        {
            _trackAlloc = true;
            _allocStart = GC.GetTotalAllocatedBytes(precise: false);
        }
    }

    public void Dispose()
    {
        if (_p is null) return;
        _p.Record(_bucket, Stopwatch.GetTimestamp() - _start);
        if (_trackAlloc)
            _p.RecordAlloc(_bucket, GC.GetTotalAllocatedBytes(precise: false) - _allocStart);
    }
}

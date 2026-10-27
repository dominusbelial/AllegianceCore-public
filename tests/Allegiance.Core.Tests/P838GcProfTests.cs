using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Allegiance.Core.Profiling;
using Xunit;

namespace Allegiance.Core.Tests;

// P838 — tests for the per-window GC line ([gcprof]) and the per-step
// allocation attribution added to the tick profiler. Like TickProfilerTests,
// these flip the GLOBAL static profiler on, so they run in the serialized
// LiveNetwork collection (a concurrent Mission.Update elsewhere must not see a
// torn window).
[Collection("LiveNetwork")]
public class P838GcProfTests
{
    private static void Disable() => TickProfiler.Configure(false, 5, null);

    private static string CaptureReport(Action runTicks)
    {
        var sw = new StringWriter();
        var orig = Console.Out;
        try
        {
            TickProfiler.Configure(true, 3600, null);   // long interval → no auto-rollover
            Console.SetOut(sw);
            runTicks();
            TickProfiler.ForceReport();                 // deterministic, no interval wait
        }
        finally
        {
            Console.SetOut(orig);
            Disable();
        }
        return sw.ToString();
    }

    [Fact]
    public void Gcprof_line_emitted_with_sane_values_and_tickprof_format_unchanged()
    {
        string log = CaptureReport(() =>
        {
            // 20 ticks; each allocates ~64 KB inside a step scope and does ~1 ms
            // of timed work, so alloc/tick and the pause counters all have
            // non-trivial expected ranges.
            for (int t = 0; t < 20; t++)
            {
                TickProfiler.EnterTick();
                using (TickProfiler.Enter("step:allocwork"))
                {
                    _ = new byte[64 * 1024];
                    Thread.Sleep(1);
                    if (t % 10 == 0) GC.Collect();   // force real pauses so the worst-pause sampler is exercised
                }
                TickProfiler.EndTick();
            }
        });

        // The existing [tickprof] line format the rig scripts parse stays intact.
        var tickLine = log.Split('\n').First(l => l.StartsWith("[tickprof]"));
        Assert.Matches(
            new Regex(@"^\[tickprof\] window [\d.]+s ticks=\d+(\ \[[^\]]*\])? tick-wall mean=[\d.,]+ms p95=[\d.,]+ms max=[\d.,]+ms"),
            tickLine.TrimEnd('\r', '\n'));

        // The new [gcprof] line carries every P838 field with sane values.
        var gcLine = log.Split('\n').First(l => l.StartsWith("[gcprof]"));
        var m = Regex.Match(gcLine,
            @"^\[gcprof\] window (?<secs>[\d.]+)s ticks=(?<ticks>\d+) " +
            @"pause=(?<pause>[\d.,]+)ms \((?<ppt>[\d.,]+)ms/tick, (?<share>[\d.,]+)% of tick-wall\) " +
            @"gc gen0=(?<g0>\d+) gen1=(?<g1>\d+) gen2=(?<g2>\d+) " +
            @"alloc=(?<allocMb>[\d.,]+)MB \((?<kbt>[\d.,]+)KB/tick\) " +
            @"heapAfterGc=(?<heapMb>[\d.,]+)MB worstPause=(?<wp>[\d.,]+)ms");
        Assert.True(m.Success, $"[gcprof] line did not match the expected shape: {gcLine.TrimEnd()}");
        double secs = P(m, "secs"), pause = P(m, "pause"), ppt = P(m, "ppt"), share = P(m, "share");
        double allocMb = P(m, "allocMb"), kbt = P(m, "kbt"), heapMb = P(m, "heapMb"), wp = P(m, "wp");
        int ticks = int.Parse(m.Groups["ticks"].Value, CultureInfo.InvariantCulture);
        Assert.Equal(20, ticks);
        Assert.InRange(allocMb, 0.8, 16.0);                       // 20 × 64 KB ≈ 1.25 MB; the global counter also sees tests running in parallel (2.6 MB observed)
        Assert.InRange(kbt, 32.0, 4096.0);                        // ≥ 32 KB/tick allocated, sane upper bound
        Assert.InRange(pause, 0.0, 2000.0);                       // no wild negatives or multi-second pauses
        Assert.True(Math.Abs(pause / 20.0 - ppt) <= 0.05 / 20.0 + 0.001,   // per-tick consistent with the total (printed rounding: 0.0 format)
            $"pause/tick mismatch: total {pause}ms, per-tick {ppt}ms");
        Assert.InRange(share, 0.0, 100.0);                        // a percentage
        Assert.True(heapMb > 0.0, "heapAfterGc should reflect a real heap size");
        Assert.InRange(wp, 0.0, 1000.0);                          // worst single pause sane
        Assert.InRange(secs, 0.0, 5.0);
        Assert.Contains("top-alloc steps", log);
        Assert.Contains("step:allocwork=", log);                  // the allocating step is ranked
    }

    [Fact]
    public void Step_allocation_attribution_accumulates_and_ignores_non_step_buckets()
    {
        TickProfiler.Configure(true, 3600, null);
        try
        {
            // One tick with a step scope that allocates ~1 MB and an inner
            // (non-step) scope; only the step scope must be tracked.
            TickProfiler.EnterTick();
            using (TickProfiler.Enter("step:attribution"))
            {
                _ = new byte[1024 * 1024];
                using (TickProfiler.Enter("inner.bucket"))
                {
                    _ = new byte[256 * 1024];
                }
            }
            TickProfiler.EndTick();

            var alloc = TickProfiler.SnapshotAlloc();
            Assert.True(alloc.TryGetValue("step:attribution", out long bytes),
                "the step scope's allocation must be tracked");
            Assert.True(bytes >= 1024 * 1024, $"step delta should include the ~1 MB allocation, got {bytes}");
            Assert.True(bytes < 8 * 1024 * 1024, $"step delta should be bounded, got {bytes}");
            Assert.DoesNotContain(alloc.Keys, k => k.StartsWith("inner", StringComparison.Ordinal));
        }
        finally { Disable(); }
    }

    [Fact]
    public void Disabled_profiler_records_no_step_allocation()
    {
        Disable();   // explicit: Instance is null, scope is the default no-op
        Assert.Null(TickProfiler.Instance);
        using var scope = TickProfiler.Enter("step:offpath");
        Assert.Equal(default, scope);
        Assert.Empty(TickProfiler.SnapshotAlloc());
    }

    [Fact]
    public void Profiler_overhead_with_gc_tracking_is_bounded()
    {
        // Generous load-shape guard, not a benchmark: the same 400 ticks of
        // trivial work (4 scopes each, one small allocation) must not cost more
        // than ~5× with the profiler on than off + a fixed floor. The P838
        // additions are a handful of counter reads per tick and per step scope.
        RunTrivialTicks(profiler: false, ticks: 400);
        long offMs = MeasureTrivialTicks(profiler: false, ticks: 400);
        RunTrivialTicks(profiler: true, ticks: 400);
        long onMs = MeasureTrivialTicks(profiler: true, ticks: 400);
        Assert.True(onMs < offMs * 5 + 250,
            $"profiler-on ({onMs}ms) should be within 5×+250ms of profiler-off ({offMs}ms)");
    }

    private static void RunTrivialTicks(bool profiler, int ticks)
    {
        TickProfiler.Configure(profiler, 3600, null);
        try
        {
            for (int t = 0; t < ticks; t++)
            {
                TickProfiler.EnterTick();
                using (TickProfiler.Enter("step:overhead.a")) { _ = new byte[1024]; }
                using (TickProfiler.Enter("step:overhead.b")) { }
                using (TickProfiler.Enter("step:overhead.c")) { }
                using (TickProfiler.Enter("inner.d")) { }
                TickProfiler.EndTick();
            }
        }
        finally { Disable(); }
    }

    private static long MeasureTrivialTicks(bool profiler, int ticks)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RunTrivialTicks(profiler: profiler, ticks: ticks);
        return sw.ElapsedMilliseconds;
    }

    private static double P(Match m, string g)
        => double.Parse(m.Groups[g].Value.Replace(",", ""), CultureInfo.InvariantCulture);
}

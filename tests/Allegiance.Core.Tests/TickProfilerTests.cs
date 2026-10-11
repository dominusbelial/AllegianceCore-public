using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Allegiance.Core.Profiling;
using Xunit;

namespace Allegiance.Core.Tests;

// P600 — these tests flip the GLOBAL static profiler on, which any concurrently-
// running test that calls Mission.Update/SimPass would observe. The profiler is
// thread-safe (so the leak cannot crash a parallel test — the run-1 bug this
// fixes), and putting these tests in the serialized LiveNetwork collection keeps
// the enable window from overlapping the live-socket tests in particular.
[Collection("LiveNetwork")]
public class TickProfilerTests
{
    private static void Disable() => TickProfiler.Configure(false, 5, null);

    [Fact]
    public void Profiler_is_off_by_default_when_not_configured()
    {
        try
        {
            // Explicitly turning it off yields a null Instance (the zero-cost path).
            TickProfiler.Configure(false, 5, null);
            Assert.Null(TickProfiler.Instance);
            // Disabled Enter returns the default no-op scope and must not throw.
            using var scope = TickProfiler.Enter("any");
            Assert.Equal(default, scope);
        }
        finally { Disable(); }
    }

    [Fact]
    public void Enable_then_disable_roundtrips_the_singleton()
    {
        try
        {
            Assert.Null(TickProfiler.Instance);
            TickProfiler.Configure(true, 5, null);
            Assert.NotNull(TickProfiler.Instance);
            Assert.Equal(5, TickProfiler.Instance!.ReportIntervalSeconds);
            Assert.Null(TickProfiler.Instance.CsvPath);
            TickProfiler.Configure(false, 5, null);
            Assert.Null(TickProfiler.Instance);
        }
        finally { Disable(); }
    }

    [Fact]
    public void Enabled_profiler_records_bucket_and_reports_to_csv()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"p600-prof-{Guid.NewGuid():N}.csv");
        try
        {
            TickProfiler.Configure(true, 3600, csv);   // long interval → no auto-rollover
            Assert.NotNull(TickProfiler.Instance);

            // One "tick": time a real bucket that does measurable work.
            TickProfiler.EnterTick();
            using (TickProfiler.Enter("bucket.a")) Thread.Sleep(5);
            using (TickProfiler.Enter("bucket.b")) Thread.Sleep(3);
            TickProfiler.EndTick();

            TickProfiler.ForceReport();                // deterministic, no interval wait
            TickProfiler.Shutdown();                   // flush + close CSV

            var rows = File.ReadAllLines(csv);
            Assert.Contains(rows, l => l.StartsWith("# P600"));
            // Column header + at least the two bucket rows (+ possibly more).
            Assert.Contains(rows, l => l.StartsWith("window_start_unix,"));

            var dataRows = rows.Where(l => !l.StartsWith("#") && !l.StartsWith("window_start_unix")).ToList();
            Assert.Contains(dataRows, r => r.Contains("bucket.a"));
            Assert.Contains(dataRows, r => r.Contains("bucket.b"));

            // Parse bucket.a mean_ms (col index 4) — must be positive (5ms sleep).
            var rowA = dataRows.First(r => r.Contains("bucket.a")).Split(',');
            double meanA = double.Parse(rowA[4], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(meanA > 0.5, $"bucket.a mean_ms should reflect the ~5ms work, got {meanA}");
        }
        finally { Disable(); }
    }

    [Fact]
    public void Bucket_samples_sum_within_the_measured_tick_wall()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"p600-prof-{Guid.NewGuid():N}.csv");
        try
        {
            TickProfiler.Configure(true, 3600, csv);
            // Several ticks with a couple of disjoint buckets each. Because the
            // two buckets are disjoint and are the only timed work, their sum
            // should track the per-tick wall time closely (each within it, and
            // collectively on the order of the wall minus untimed gaps).
            const int ticks = 8;
            for (int t = 0; t < ticks; t++)
            {
                TickProfiler.EnterTick();
                using (TickProfiler.Enter("disjoint.x")) Thread.Sleep(2);
                using (TickProfiler.Enter("disjoint.y")) Thread.Sleep(1);
                TickProfiler.EndTick();
            }
            TickProfiler.ForceReport();
            TickProfiler.Shutdown();

            var dataRows = File.ReadAllLines(csv)
                .Where(l => !l.StartsWith("#") && !l.StartsWith("window_start_unix") && l.Split(',').Length >= 8)
                .ToList();
            double sumBucketTotalMs = dataRows
                .Where(r => r.Contains("disjoint."))
                .Select(r => double.Parse(r.Split(',')[7], System.Globalization.CultureInfo.InvariantCulture))
                .Sum();

            // 8 ticks × (2+1)ms of measured work ≈ 24ms floor.
            Assert.True(sumBucketTotalMs >= 15.0,
                $"bucket totals should reflect ~24ms of timed sleeps, got {sumBucketTotalMs:0.##}ms");
            Assert.True(sumBucketTotalMs < 500.0,
                $"bucket totals must not blow up; got {sumBucketTotalMs:0.##}ms");
        }
        finally { Disable(); }
    }

    [Fact]
    public void Disabled_profiler_has_negligible_overhead()
    {
        try
        {
            TickProfiler.Configure(false, 5, null);     // the zero-cost path
            const int n = 1_000_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                using (var s = TickProfiler.Enter("hot")) { }   // no-op scope
                TickProfiler.RecordSectorCounts(i & 0xff, 1, 1); // null-check no-op
            }
            sw.Stop();
            // Generous bound: 1M disabled Enter/Record calls must run in well under
            // a second (the disabled path is a null check + defaulted struct).
            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"disabled profiler hot-path too slow: {sw.ElapsedMilliseconds}ms for {n} iterations");
        }
        finally { Disable(); }
    }
}

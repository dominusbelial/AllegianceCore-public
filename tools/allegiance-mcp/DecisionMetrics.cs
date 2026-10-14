using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P633 — pure, unit-testable aggregation for the bot decision-loop metrics the
/// MCP tool reports live through <c>get_perf_stats</c>:
/// <list type="bullet">
///   <item><see cref="PassStat"/> — the decision-pass time distribution
///   (mean / p50 / p95 / max) over the last N completed passes;</item>
///   <item><see cref="ResourceSnapshot"/> — the bot process's live CPU %, RSS
///   and per-generation GC collection counts (sampled, not modeled).</item>
/// </list>
/// Both are pure maths / trivial reads so they can be unit-proven without a
/// server. The Loop records one <see cref="PassStat"/> per pass (see
/// <c>BotManager.RecordPass</c>) and <c>get_perf_stats</c> reads the latest.
/// </summary>
public static class DecisionMetrics
{
    /// <summary>P633 — the decision-pass time distribution over a bounded rolling
    /// window of completed passes (ms). <c>MaxMs</c> is the worst single pass;
    /// <c>P50Ms</c>/<c>P95Ms</c> are nearest-rank percentiles.</summary>
    public readonly record struct PassStat(double MeanMs, double P50Ms, double P95Ms, double MaxMs)
    {
        public static readonly PassStat Empty = new(0, 0, 0, 0);
    }

    /// <summary>P633 — a sampled snapshot of the bot process's resource usage:
    /// CPU utilisation (fraction of one core, 0..N, computed as a rolling
    /// interval average), resident set size (MB), and the cumulative gen-0/1/2
    /// GC collection counts. Read directly from the runtime; pure/trivial.</summary>
    public readonly record struct ResourceSnapshot(
        double CpuFraction, double RssMb, long Gen0, long Gen1, long Gen2);

    /// <summary>
    /// P633 — reduce a list of pass durations (ms) to mean / nearest-rank
    /// p50 / p95 / max. Empty input yields <see cref="PassStat.Empty"/> (no NaN,
    /// no divide-by-zero). The input is copied and sorted, so the caller's list
    /// is untouched.
    /// </summary>
    public static PassStat SummarizePasses(IReadOnlyList<double> ms)
    {
        if (ms.Count == 0) return PassStat.Empty;
        var sorted = new List<double>(ms);
        sorted.Sort();
        double mean = 0;
        foreach (var v in sorted) mean += v;
        mean /= sorted.Count;
        double p = (int)Math.Min(sorted.Count - 1, Math.Max(0, Math.Ceiling(0.5 * sorted.Count) - 1));
        double p50 = sorted[(int)p];
        double q = (int)Math.Min(sorted.Count - 1, Math.Max(0, Math.Ceiling(0.95 * sorted.Count) - 1));
        double p95 = sorted[(int)q];
        return new PassStat(mean, p50, p95, sorted[^1]);
    }

    /// <summary>
    /// P633 — the effective per-bot decision rate (Hz) implied by a pass of
    /// <paramref name="passMs"/> milliseconds that decided every bot exactly
    /// once: one decision per bot per pass, so the per-bot rate is 1 / pass
    /// duration in seconds. 0 when the pass took no time. (The aggregate fleet
    /// decision rate is <c>bots / passMs</c>; the per-bot figure is what the
    /// task's target "≥ 15 Hz effective in combat" refers to.)
    /// </summary>
    public static double DecisionRateHz(double passMs)
        => passMs > 0 ? 1000.0 / passMs : 0.0;

    /// <summary>
    /// P636b — a deadline-based FIXED decision cadence. Drives the bot decision
    /// loop so each pass starts ~<c>periodMs</c> after the previous one, and
    /// guarantees the loop NEVER busy-spins: when a pass overshoots its tick it
    /// drops the debt and waits a full period (at least <c>minYieldMs</c>), instead of looping with
    /// zero sleep (the P636 behaviour that spun at ~670 Hz once the lock-free
    /// snapshot made passes faster than the period, starving the receive threads
    /// and collapsing combat at scale). Pure + wall-clock-free so it is unit-
    /// provable with synthetic timestamps.
    /// </summary>
    public sealed class LoopCadence
    {
        private readonly double _periodMs;
        private readonly double _minYieldMs;
        private double _nextTickMs;
        public long OverrunCount { get; private set; }

        public LoopCadence(double periodMs, double minYieldMs = 1.0)
        {
            if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs), "period must be > 0");
            if (minYieldMs < 0) throw new ArgumentOutOfRangeException(nameof(minYieldMs), "min yield must be >= 0");
            _periodMs = periodMs;
            _minYieldMs = minYieldMs;
        }

        /// <summary>Anchor the first tick (call once, before the first pass).</summary>
        public void Begin(double nowMs) => _nextTickMs = nowMs + _periodMs;

        /// <summary>
        /// Called after finishing a pass at wall time <paramref name="nowMs"/>:
        /// returns the ms to sleep so the next pass starts ~one period after this
        /// one. On an overrun drops accumulated debt and schedules one period from now;
        /// fast subsequent passes cannot run catch-up bursts.
        /// </summary>
        public double SleepForRemainder(double nowMs)
        {
            double sleep = _nextTickMs - nowMs;
            if (sleep <= 0)
            {
                OverrunCount++;
                // Drop all debt and give the receive threads one full period.
                sleep = Math.Max(_periodMs, _minYieldMs);
                _nextTickMs = nowMs + sleep;
            }
            _nextTickMs += _periodMs;
            return sleep;
        }
    }

    /// <summary>P633 — read a live CPU/RSS/GC snapshot for <paramref name="proc"/>
    /// (the current bot process). CPU is the interval-average fraction of one
    /// core between the previous call (carried in <paramref name="prevCpuFrac"/>
    /// with its timestamp in <paramref name="prevStampMs"/> / TotalProcessorTime)
    /// and now; the first call returns the process's instantaneous
    /// <c>TotalProcessorTime</c>/uptime average. Pure/trivial runtime reads.
    /// </summary>
    public static ResourceSnapshot SampleResources(
        Process proc,
        double prevStampMs,
        TimeSpan prevTotalCpu,
        double prevCpuFrac,
        out double nextStampMs,
        out TimeSpan nextTotalCpu,
        out double nextCpuFrac)
    {
        double nowMs = Environment.TickCount64;
        TimeSpan cpu = proc.TotalProcessorTime;
        int cores = Math.Max(1, Environment.ProcessorCount);
        double frac;
        double dtSec = (nowMs - prevStampMs) / 1000.0;
        double dtCpu = (cpu - prevTotalCpu).TotalSeconds;
        if (dtSec > 0.01 && prevStampMs > 0)
            frac = dtCpu / dtSec;                       // interval average across all cores
        else
            frac = cpu.TotalSeconds / Math.Max(1e-3, nowMs / 1000.0);   // lifetime average
        nextStampMs = nowMs;
        nextTotalCpu = cpu;
        nextCpuFrac = frac;
        return new ResourceSnapshot(
            frac,
            proc.WorkingSet64 / (1024.0 * 1024.0),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }
}

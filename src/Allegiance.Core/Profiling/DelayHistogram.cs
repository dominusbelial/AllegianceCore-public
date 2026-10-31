using System.Diagnostics;
using System.Numerics;

namespace Allegiance.Core.Profiling;

/// <summary>Fixed storage, single-owner histogram. The owner supplies synchronization.
/// Mean/max are exact; p95 is the upper edge of a power-of-two microsecond bucket.</summary>
internal sealed class DelayHistogram
{
    private readonly long[] _bins = new long[64];
    private long _count, _sum, _max;

    internal void Record(long ticks)
    {
        ticks = Math.Max(0, ticks);
        ulong us = (ulong)Math.Ceiling(ticks * (1_000_000d / Stopwatch.Frequency));
        int bin = us <= 1 ? 0 : BitOperations.Log2(us - 1) + 1;
        _bins[bin]++;
        _count++;
        _sum += ticks;
        _max = Math.Max(_max, ticks);
    }

    internal DelayWindow Drain()
    {
        long rank = (long)Math.Ceiling(_count * .95), cumulative = 0;
        double p95 = 0, max = _max * (1000d / Stopwatch.Frequency);
        for (int i = 0; i < _bins.Length; i++)
        {
            cumulative += _bins[i];
            if (_count != 0 && cumulative >= rank) { p95 = Math.Min(max, Math.Pow(2, i) / 1000); break; }
        }
        var result = new DelayWindow(_count, _count == 0 ? 0 : _sum * (1000d / Stopwatch.Frequency) / _count, p95, max);
        Array.Clear(_bins);
        _count = _sum = _max = 0;
        return result;
    }
}

public readonly record struct DelayWindow(long Samples, double MeanMs, double P95UpperMs, double MaxMs);

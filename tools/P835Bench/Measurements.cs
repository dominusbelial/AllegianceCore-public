using Allegiance.Core.Profiling;

// One profiler window per phase. Contact pools tick samples across fresh scenes,
// but counts allocation, pauses and collections only inside each scene's run.
internal sealed class Measurements
{
    private readonly string _phase;
    public List<double> Updates { get; }
    public List<double> FixtureWalls { get; }
    private long _bytes;
    private double _pauseMs;
    private int _gen0, _gen1, _gen2;

    public Measurements(string phase, int ticks)
    {
        _phase = phase;
        Updates = new(ticks);
        FixtureWalls = new(ticks);
        TickProfiler.Configure(true, 3600, null);
    }

    internal readonly record struct Counters(long Bytes, double PauseMs, int Gen0, int Gen1, int Gen2)
    {
        public static Counters Read() => new(GC.GetTotalAllocatedBytes(precise: true),
            GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(0),
            GC.CollectionCount(1), GC.CollectionCount(2));
    }

    public void Accumulate(Counters start)
    {
        var end = Counters.Read();
        _bytes += end.Bytes - start.Bytes;
        _pauseMs += end.PauseMs - start.PauseMs;
        _gen0 += end.Gen0 - start.Gen0;
        _gen1 += end.Gen1 - start.Gen1;
        _gen2 += end.Gen2 - start.Gen2;
    }

    public void Report()
    {
        Console.WriteLine($"P835-BENCH phase={_phase}");
        // Keep the sustained summary readable by the historical P928 log parser.
        bool sustained = _phase == "sustained";
        string gc = sustained ? "P838-GC" : "first-contact-GC";
        string rowPrefix = sustained ? "  " : "first-contact  ";
        PrintStats(sustained ? "Mission.Update" : "Mission.Update (first-contact)", Updates);
        PrintStats("Update + profiler and salvo upkeep", FixtureWalls);
        double wall = FixtureWalls.Sum();
        double updateMs = Updates.Sum();
        int ticks = Updates.Count;
        Console.WriteLine(
            $"{gc}: alloc={_bytes / 1048576.0:0.0}MB ({_bytes / 1024.0 / ticks:0.0}KB/tick) " +
            $"pause={_pauseMs:0.0}ms ({_pauseMs / ticks:0.000}ms/tick, {100 * _pauseMs / wall:0.0}% of measured wall) " +
            $"gc gen0={_gen0} gen1={_gen1} gen2={_gen2} ticks={ticks}");
        foreach (var (bucket, bytes) in TickProfiler.SnapshotAlloc().OrderByDescending(kv => kv.Value).Take(8))
            Console.WriteLine($"{gc}: step-alloc {bucket,-34} {bytes / 1048576.0,8:0.0}MB total  {bytes / 1024.0 / ticks,8:0.0}KB/tick");
        Console.WriteLine("Buckets in ms (amortised per tick, then mean and p95 on due ticks):");
        foreach (var (bucket, v) in TickProfiler.Snapshot().OrderByDescending(kv => kv.Value.TotalMs))
            Console.WriteLine($"{rowPrefix}{bucket,-34} amort={v.TotalMs / ticks,8:0.00}  mean={v.MeanMs,8:0.00} (n={v.Count,4})  p95={v.P95Ms,8:0.00}  {100 * v.TotalMs / updateMs,5:0.#}%");
        TickProfiler.Shutdown();
    }

    private static void PrintStats(string label, List<double> samples)
    {
        samples.Sort();
        double Percentile(double p) => samples[(int)Math.Ceiling(p * samples.Count) - 1];
        Console.WriteLine($"P835-BENCH {label} mean={samples.Average(),6:0.00} ms  median={samples[samples.Count / 2],6:0.00} ms  p95={Percentile(0.95),6:0.00} ms  p99={Percentile(0.99),6:0.00} ms  max={samples[^1],6:0.00} ms  ({samples.Count} ticks)");
    }
}

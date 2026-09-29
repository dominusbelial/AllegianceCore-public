using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P430 — prove <see cref="BotManager.ComputePerf"/>'s aggregation math (mean,
/// nearest-rank p95, worst offender) against a small SYNTHETIC sample set. This
/// is the same pure function a live <c>get_perf_stats</c> call is built from, so
/// the numbers a caller trusts ("bot X is the one actually teleporting") are
/// proven correct here with no server involved.
/// </summary>
public class PerfStatsAggregationTests
{
    private static BotCadence Bot(string name, params (double Interval, double Distance)[] samples) =>
        new(name, samples.Select(s => new CadenceSample(s.Interval, s.Distance)).ToList());

    [Fact]
    public void EmptyInput_YieldsACleanZeroSummary()
    {
        var ps = BotManager.ComputePerf([]);
        Assert.Equal(0, ps.BotCount);
        Assert.Equal(0, ps.Interval.Mean);
        Assert.Equal(0, ps.Interval.P95);
        Assert.Equal("", ps.Interval.WorstBot);
        Assert.Equal(0, ps.Interval.WorstValue);
        Assert.Equal(0, ps.Jump.WorstValue);
    }

    [Fact]
    public void BotsWithoutSamplesDoNotContribute()
    {
        var ps = BotManager.ComputePerf([
            Bot("Moving", (1, 10), (2, 20)),
            Bot("Stationary"),          // no samples → excluded from the aggregate
        ]);
        Assert.Equal(1, ps.BotCount);
        Assert.Equal(1.5, ps.Interval.Mean, 6);
        Assert.Equal("Moving", ps.Interval.WorstBot);
    }

    [Fact]
    public void MeanIsAveragedOverAllBotsPooledSamples()
    {
        // pooled intervals: 1,2,3,3 → mean 2.25 ; pooled jumps: 10,20,30,10 → mean 17.5
        var ps = BotManager.ComputePerf([
            Bot("A", (1, 10), (2, 20), (3, 30)),
            Bot("B", (3, 10)),
        ]);
        Assert.Equal(2, ps.BotCount);
        Assert.Equal(2.25, ps.Interval.Mean, 6);
        Assert.Equal(17.5, ps.Jump.Mean, 6);
    }

    [Fact]
    public void P95IsTheNearestRankPercentile()
    {
        // 20 equally-spaced intervals 0..19 → nearest-rank p95 = ceil(.95*20)-1 = 18 → value 18
        var samples = Enumerable.Range(0, 20)
            .Select(i => new CadenceSample((double)i, 100.0)).ToArray();
        var ps = BotManager.ComputePerf([new BotCadence("A", samples)]);
        Assert.Equal(18.0, ps.Interval.P95, 6);
        Assert.Equal(9.5, ps.Interval.Mean, 6);          // mean of 0..19
        Assert.Equal(19.0, ps.Interval.WorstValue, 6);   // max
    }

    [Fact]
    public void WorstOffenderIsTheBotWithTheLargestSingleSample()
    {
        var ps = BotManager.ComputePerf([
            Bot("Slow", (1, 10), (2, 20), (1.5, 15)),
            Bot("Bad", (9, 500), (1, 10)),          // the teleporter
            Bot("Calm", (0.5, 5)),
        ]);
        Assert.Equal("Bad", ps.Interval.WorstBot);
        Assert.Equal(9.0, ps.Interval.WorstValue, 6);
        Assert.Equal("Bad", ps.Jump.WorstBot);
        Assert.Equal(500.0, ps.Jump.WorstValue, 6);
    }

    [Fact]
    public void SingleBotAggregateMatchesItsOwnSamples()
    {
        var ps = BotManager.ComputePerf([Bot("Solo", (0.1, 2), (0.2, 4), (0.3, 6))]);
        Assert.Equal(1, ps.BotCount);
        Assert.Equal(0.2, ps.Interval.Mean, 6);
        Assert.Equal(0.3, ps.Interval.P95, 6);       // ceil(.95*3)-1 = 2 → 3rd sorted = 0.3
        Assert.Equal("Solo", ps.Jump.WorstBot);
        Assert.Equal(6.0, ps.Jump.WorstValue, 6);
    }
}

using System.Text.Json;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P700IdleTargetMetricsTests
{
    [Fact]
    public void RollingMinuteCountsDistinctBots_ExpiresRecoveredAndRemovedBots_AndFilters()
    {
        var metrics = new IdleTargetMetrics();
        Assert.Null(metrics.Snapshot(0).SampleAgeSeconds);
        metrics.Observe(0, ["alpha", "alpha", "bravo"]);
        Assert.Equal(new IdleTargetReport(2, 2, 0), metrics.Snapshot(0));
        Assert.False(metrics.Due(.9)); Assert.True(metrics.Due(1));
        metrics.Observe(1, ["alpha"]);
        metrics.Observe(2, []);
        Assert.Equal(new IdleTargetReport(0, 2, 1), metrics.Snapshot(3));
        Assert.Equal(new IdleTargetReport(0, 1, 1), metrics.Snapshot(3, "ALP"));
        Assert.Equal(1, metrics.Snapshot(60).BotsLastMinute);
        Assert.Equal(0, metrics.Snapshot(61).BotsLastMinute);
        metrics.Observe(61, ["charlie"]);
        Assert.Equal(new IdleTargetReport(1, 1, 0), metrics.Snapshot(61));
        Assert.Equal(metrics.Snapshot(61), metrics.Snapshot(61));
    }

    [Fact]
    public void SupervisorSumsIdleCounts_AndReportsOldestSampleAge()
    {
        WorkerReply Reply(int current, int minute, double age) => new(false, JsonSerializer.SerializeToElement(new
        {
            Report = new { idleWithTarget = new { currentCount = current, botsLastMinute = minute, sampleAgeSeconds = age } },
            Cadence = Array.Empty<BotCadence>(),
        }));
        var report = SupervisorResults.MergePerf([Reply(1, 2, .2), Reply(3, 4, .8)], [new { id = 1 }, new { id = 2 }], []);
        var metric = report.GetProperty("idleWithTarget");
        Assert.Equal(4, metric.GetProperty("currentCount").GetDouble());
        Assert.Equal(6, metric.GetProperty("botsLastMinute").GetDouble());
        Assert.Equal(.8, metric.GetProperty("sampleAgeSeconds").GetDouble());
    }
}

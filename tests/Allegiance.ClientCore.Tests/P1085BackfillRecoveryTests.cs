using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Trait("Tier", "Fast")]
public sealed class P1085BackfillRecoveryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.5)]
    [InlineData(1)]
    public void Retry_schedule_is_exponential_jittered_capped_and_resets_after_seating(double jitter)
    {
        var retry = new BackfillRetry();
        double now = 100;
        Assert.True(retry.Due(now));
        foreach (double basis in new double[] { 1, 2, 4, 8, 16, 32, 60, 60 })
        {
            double delay = retry.Failed(now, jitter);
            Assert.InRange(delay, basis, Math.Min(60, basis * 1.2));
            Assert.Equal(Math.Min(60, basis * (1 + jitter * .2)), delay);
            Assert.False(retry.Due(now + delay - .001));
            Assert.True(retry.Due(now + delay));
            now += delay;
        }
        for (int i = 0; i < 10000; i++) now += retry.Failed(now, jitter);
        Assert.Equal(60, retry.Failed(now, jitter));
        retry.Succeeded();
        Assert.True(retry.Due(now));
        Assert.InRange(retry.Failed(now, jitter), 1, 1.2);
    }

    [Theory]
    [InlineData("probe lost")]
    [InlineData("world not ready")]
    [InlineData("stage over")]
    public void Stall_is_once_per_not_ready_episode_and_rearms_only_after_ready(string reason)
    {
        var health = new BackfillLiveness();
        Assert.Null(health.Observe(0, null));
        Assert.Null(health.Observe(20, reason));
        Assert.Null(health.Observe(619.999, reason));
        Assert.Equal($"[backfill] stalled seconds=600 reason={reason}", health.Observe(620, reason));
        Assert.Null(health.Observe(1000, "another reason"));
        Assert.Null(health.Observe(4000, reason));
        Assert.Null(health.Observe(4001, null));
        Assert.Null(health.Observe(4010, reason));
        Assert.NotNull(health.Observe(4610, reason));
    }
}

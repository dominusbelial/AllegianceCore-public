using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P967 (review finding P963-09) — the P119 bucket-completion diagnostic history
/// is bounded and per-mission. The old list appended one id per completed bucket
/// for the life of the process (ids repeat across worlds, so the history was not
/// identity), and every <c>BucketCompletions</c> read copied it in full.
///
/// This is a pure seam test: no artwork, no transport, no sim ticks.
/// </summary>
public sealed class P967BucketCompletionBoundTests
{
    [Fact]
    public void Completion_history_stays_at_its_bound_and_keeps_the_newest_ids()
    {
        using var server = new Srv();
        int limit = Srv.BucketCompletionHistoryLimit;
        Assert.True(limit > 0 && limit <= 1024, $"unexpected completion-history bound {limit}");

        // Simulate a long-lived match completing far more buckets than the bound.
        for (short i = 0; i < limit * 8; i++) server.RecordBucketCompletionForTesting(i);

        Assert.Equal(limit, server.BucketCompletionStorageCount);
        Assert.Equal(limit, server.BucketCompletions.Count);
        // Order preserved, oldest dropped: the newest id is last, the first kept
        // id is limit*8 - limit.
        var kept = server.BucketCompletions;
        Assert.Equal((short)(limit * 8 - 1), kept[^1]);
        Assert.Equal((short)(limit * 8 - limit), kept[0]);
        Assert.Equal(kept.Count, kept.Distinct().Count());
    }

    [Fact]
    public void Completion_history_starts_empty_and_grows_only_to_the_bound()
    {
        using var server = new Srv();
        Assert.Empty(server.BucketCompletions);
        Assert.Equal(0, server.BucketCompletionStorageCount);

        server.RecordBucketCompletionForTesting(7);
        Assert.Single(server.BucketCompletions);
        Assert.Equal(7, server.BucketCompletions[0]);
    }
}

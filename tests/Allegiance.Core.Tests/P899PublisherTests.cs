using System.Collections.Concurrent;
using System.Reflection;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P899PublisherTests
{
    private sealed class Lease(bool fail = false) : BroadcastPacket(new byte[] { 42 })
    {
        internal int Releases;
        internal override void Release()
        {
            Interlocked.Increment(ref Releases);
            if (fail) throw new OverflowException("injected packet return failure");
        }
    }

    private static void Flush(BroadcastPublisher publisher)
    {
        var task = Task.Run(publisher.Flush);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "publication must keep draining after a failure");
    }

    [Fact]
    public void Return_failure_releases_every_lease_once_reports_errors_and_keeps_publishing()
    {
        var errors = new ConcurrentQueue<Exception>();
        int sent = 0;
        using var publisher = new BroadcastPublisher((_, _) => Interlocked.Increment(ref sent), 2);
        publisher.OnError = e => { errors.Enqueue(e); throw new Exception("broken error logger"); };
        var broken = new Lease(true);
        var healthy = new Lease();
        var snapshot = publisher.Rent(1);
        snapshot.Deliveries.Add(new(new(1), broken));
        snapshot.Deliveries.Add(new(new(1), healthy));
        publisher.Submit(snapshot);
        Flush(publisher);
        Assert.Equal(1, broken.Releases);
        Assert.Equal(1, healthy.Releases);
        Assert.Single(errors);
        var next = publisher.Rent(2);
        var final = new Lease();
        next.Deliveries.Add(new(new(1), final));
        publisher.Submit(next);
        Flush(publisher);
        Assert.Equal(1, final.Releases);
        Assert.Equal(3, sent);
        Assert.Equal(2, publisher.PublishedSnapshots);
        Assert.Equal(3, publisher.SnapshotContainersCreated);
    }

    [Fact]
    public void Container_pool_return_failure_does_not_strand_flush_or_retain_packets()
    {
        var errors = new ConcurrentQueue<Exception>();
        using var publisher = new BroadcastPublisher((_, _) => { }, 1);
        publisher.OnError = errors.Enqueue;
        publisher.BeforePoolReturn = () => throw new OverflowException("injected container return failure");
        var lease = new Lease();
        var snapshot = publisher.Rent(1);
        snapshot.Deliveries.Add(new(new(1), lease));
        snapshot.AfterPublish.Add(() => { });
        publisher.Submit(snapshot);
        Flush(publisher);
        Assert.Equal(1, lease.Releases);
        Assert.Empty(snapshot.Deliveries);
        Assert.Empty(snapshot.AfterPublish);
        Assert.Single(errors);
        publisher.BeforePoolReturn = null;
        publisher.Submit(publisher.Rent(2));
        Flush(publisher);
        Assert.Equal(2, publisher.PublishedSnapshots);
    }

    [Fact]
    public void Superseded_packet_return_failure_preserves_pending_traffic_and_releases_remaining_packets()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var publisher = new BroadcastPublisher((_, _) => { }, 1);
        publisher.BeforeEncode = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var broken = new Lease(true);
        var reliable = new Lease();
        var replacement = new Lease();
        try
        {
            publisher.Submit(publisher.Rent(0));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var pending = publisher.Rent(1);
            pending.Deliveries.Add(new(new(1), broken, 2, false));
            pending.Deliveries.Add(new(new(1), reliable));
            publisher.Submit(pending);
            var next = publisher.Rent(2);
            next.Deliveries.Add(new(new(1), replacement, 2, false));
            publisher.Submit(next);
        }
        finally { release.Set(); }
        Flush(publisher);
        Assert.Equal(1, broken.Releases);
        Assert.Equal(1, reliable.Releases);
        Assert.Equal(1, replacement.Releases);
        Assert.Equal(1, publisher.EncodeFailures);
        Assert.Equal(3, publisher.SnapshotContainersCreated);
    }

    [Fact]
    public void Waiting_thread_exception_is_reported_and_publication_recovers()
    {
        using var reported = new ManualResetEventSlim();
        using var publisher = new BroadcastPublisher((_, _) => { }, 1);
        publisher.OnError = _ => reported.Set();
        var thread = (Thread)typeof(BroadcastPublisher).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(publisher)!;
        thread.Interrupt(); // delivered on the coordinator's next blocking wait
        Assert.True(reported.Wait(TimeSpan.FromSeconds(10)));
        publisher.Submit(publisher.Rent(1));
        Flush(publisher);
        Assert.Equal(1, publisher.PublishedSnapshots);
        Assert.Equal(1, publisher.EncodeFailures);
    }

    [Fact]
    public void Encode_send_and_after_publish_failures_do_not_skip_packet_cleanup()
    {
        using var publisher = new BroadcastPublisher((_, _) => throw new IOException("send"), 2);
        var lease = new Lease();
        var snapshot = publisher.Rent(1);
        snapshot.Deliveries.Add(new(new(1), lease));
        snapshot.AfterPublish.Add(() => throw new IOException("after"));
        publisher.Submit(snapshot);
        Flush(publisher);
        Assert.Equal(1, lease.Releases);
        Assert.Equal(2, publisher.EncodeFailures);
        publisher.BeforeEncode = () => throw new IOException("encode");
        var next = publisher.Rent(2);
        var skipped = new Lease();
        next.Deliveries.Add(new(new(1), skipped));
        publisher.Submit(next);
        Flush(publisher);
        Assert.Equal(1, skipped.Releases);
        Assert.Equal(3, publisher.EncodeFailures);
    }
}

namespace Allegiance.Core.Collision;

/// <summary>
/// The time-ordered collision queue — the port of the C++ <c>CollisionQueue</c>
/// (<c>src/_Utility/CollisionQueue.cpp</c>). Entries are inserted as detected and
/// sorted ascending by <see cref="CollisionEvent.TCollision"/> before resolution
/// walks them in time order (<c>clusterIGC.cpp:290-318</c>). A stable sort with a
/// secondary key (collider identity) keeps the order deterministic regardless of
/// insertion order.
/// </summary>
public sealed class CollisionQueue
{
    private readonly List<CollisionEvent> _events = [];

    public int Count => _events.Count;

    /// <summary>Append a detected event (not yet ordered).</summary>
    public void Add(CollisionEvent e) => _events.Add(e);

    /// <summary>All detected events, in detection order.</summary>
    public IReadOnlyList<CollisionEvent> Items => _events;

    /// <summary>
    /// Sort ascending by collision time (stable, with a collider-identity
    /// secondary key for determinism). Equivalent to the C++ <c>sort(0)</c>.
    /// </summary>
    public void Sort()
    {
        _events.Sort((x, y) =>
        {
            int c = x.TCollision.CompareTo(y.TCollision);
            if (c != 0) return c;
            c = x.A.Ordinal.CompareTo(y.A.Ordinal);
            if (c != 0) return c;
            c = x.B.Ordinal.CompareTo(y.B.Ordinal);
            if (c != 0) return c;
            return (x.HtsA * 8 + x.HtsB).CompareTo(y.HtsA * 8 + y.HtsB);
        });
    }

    /// <summary>
    /// Detection emits at most one event per pair in (A ordinal, B ordinal)
    /// order. Bucket those already ordered ties by time instead of comparing
    /// every tie O(log N) times in a mass overlap. The public unordered queue
    /// entry point above retains its full comparator.
    /// </summary>
    internal void SortOrdinalEvents()
    {
        // Dense stationary overlaps often already have one common time. Keep
        // the ordinal stream as-is whenever it is also ordered by time, rather
        // than copying every event through a bucket and back.
        int i = 1;
        while (i < _events.Count && _events[i - 1].TCollision.CompareTo(_events[i].TCollision) <= 0) i++;
        if (i == _events.Count || _events.Count == 0) return;
        if (_events.Count < 256) { Sort(); return; }
        var buckets = new Dictionary<float, List<CollisionEvent>>();
        foreach (var ev in _events)
        {
            if (!buckets.TryGetValue(ev.TCollision, out var bucket))
                buckets.Add(ev.TCollision, bucket = []);
            bucket.Add(ev);
        }
        var times = buckets.Keys.ToArray();
        Array.Sort(times);
        _events.Clear();
        foreach (float time in times) _events.AddRange(buckets[time]);
    }

    /// <summary>Clear the queue (the C++ <c>purge</c>, <c>CollisionQueue.cpp:124-136</c>).</summary>
    public void Clear() => _events.Clear();

    /// <summary>The events in time order (after <see cref="Sort"/>).</summary>
    public IReadOnlyList<CollisionEvent> Ordered() => _events;
}

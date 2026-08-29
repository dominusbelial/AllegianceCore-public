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

    /// <summary>Clear the queue (the C++ <c>purge</c>, <c>CollisionQueue.cpp:124-136</c>).</summary>
    public void Clear() => _events.Clear();

    /// <summary>The events in time order (after <see cref="Sort"/>).</summary>
    public IReadOnlyList<CollisionEvent> Ordered() => _events;
}

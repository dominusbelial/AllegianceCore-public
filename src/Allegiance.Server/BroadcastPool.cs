using System.Diagnostics.CodeAnalysis;

namespace Allegiance.Server;

// Cross-thread reuse needs only mutual exclusion: these pools do not need
// per-thread work-stealing queues or counters that accumulate historical adds.
internal sealed class BroadcastPool<T>(int capacity = 4)
{
    private readonly object _gate = new();
    private readonly Stack<T> _items = new(capacity);

    internal bool TryTake([MaybeNullWhen(false)] out T item)
    {
        lock (_gate) return _items.TryPop(out item);
    }

    internal void Add(T item)
    {
        lock (_gate) _items.Push(item);
    }
}

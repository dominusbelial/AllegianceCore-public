using System.Collections;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>HUD-11 — a caller-owned, frame-to-frame reused list view. The HUD
/// snapshot needs a stable <see cref="IReadOnlyList{T}"/> for the renderer, but
/// at 107v107 (~214 contacts) copying <c>context.Contacts.ToArray()</c> every
/// frame allocates a fresh array per frame. A <see cref="SnapshotList{T}"/>
/// keeps its backing array and only grows when the source gets bigger; its
/// contents are overwritten in place. Ownership is the caller's, exactly like
/// HUD-4's <c>_drawRadarContactsBuffer</c> attempt: a model is valid for the
/// frame that produced it and must not be retained across a later snapshot.
/// </summary>
public sealed class SnapshotList<T> : IReadOnlyList<T>
{
    private T[] _items = Array.Empty<T>();

    public int Count { get; private set; }
    public T this[int index] => _items[index];

    /// <summary>The live backing array (reused; length is capacity, not count).</summary>
    public T[] Buffer => _items;

    /// <summary>Copy <paramref name="source"/> into the reused buffer and return
    /// this view. No allocation once the buffer is large enough.</summary>
    public SnapshotList<T> Set(IReadOnlyList<T> source)
    {
        int count = source.Count;
        if (_items.Length < count)
        {
            int capacity = Math.Max(8, _items.Length * 2);
            while (capacity < count) capacity *= 2;
            _items = new T[capacity];
        }
        for (int i = 0; i < count; i++) _items[i] = source[i];
        Count = count;
        return this;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return _items[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// HUD-11 — long-lived per-client snapshot scratch: the radar-contact list, the
/// inventory-pane row list and the HUD-5b radar label cache. Passing one of
/// these to <see cref="HudModel.Create(HudFrame?, HudContext?, HudSnapshotBuffers?)"/>
/// makes <c>HudModel.Create</c> copy into reused buffers instead of allocating
/// arrays every frame, and gives <see cref="HudElements"/> the same per-contact
/// label cache the renderer already uses.
/// </summary>
public sealed class HudSnapshotBuffers
{
    public SnapshotList<HudContact> Contacts { get; } = new();
    public SnapshotList<HudInventoryRow> InventoryRows { get; } = new();
    public RadarLabelCache RadarLabels { get; } = new();
}

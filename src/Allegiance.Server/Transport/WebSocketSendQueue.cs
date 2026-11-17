using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>One socket writer, bounded queued + in-flight storage. Key-zero is FIFO;
/// keyed entries replace in place. Replacing guaranteed data never resets its age.</summary>
internal delegate void WebSocketQueueEvent(ReadOnlySpan<byte> data, int key, bool guaranteed, EgressEvent ev);

internal sealed class WebSocketSendQueue(WebSocketConfig settings, WebSocketQueueEvent? record = null)
{
    internal sealed record Frame(byte[] Data, int Key, bool Guaranteed, long Since);
    private readonly LinkedList<Frame> _pending = [];
    private readonly Dictionary<int, LinkedListNode<Frame>> _keys = [];
    private Frame? _inflight;
    private long _bytes;
    private bool _closed;
    internal readonly SemaphoreSlim Ready = new(0, 1);
    // The peer's gate protects all operations; never held across socket I/O.
    internal DisconnectReason Enqueue(ReadOnlySpan<byte> data, int key, bool guaranteed, long now)
    {
        if (_closed) return DisconnectReason.None;
        guaranteed |= key == 0;
        _keys.TryGetValue(key, out var replace);
        guaranteed |= replace?.Value.Guaranteed == true;
        long nextBytes = _bytes - (replace?.Value.Data.Length ?? 0) + data.Length;
        int nextCount = _pending.Count + (_inflight is null ? 0 : 1) + (replace is null ? 1 : 0);
        // Shed only non-guaranteed, unsent entries. Guaranteed traffic either
        // survives or retires the peer; it is never silently discarded.
        while (nextBytes > settings.MaxQueuedBytes || nextCount > settings.MaxQueuedEntries)
        {
            var drop = _pending.First;
            while (drop is not null && (drop == replace || drop.Value.Guaranteed)) drop = drop.Next;
            if (drop is null)
            {
                if (!guaranteed) record?.Invoke(data, key, false, EgressEvent.Dropped);
                return guaranteed ? DisconnectReason.QueueHardCap : DisconnectReason.None;
            }
            record?.Invoke(drop.Value.Data, drop.Value.Key, drop.Value.Guaranteed, EgressEvent.Dropped);
            _pending.Remove(drop); if (drop.Value.Key != 0) _keys.Remove(drop.Value.Key);
            _bytes -= drop.Value.Data.Length; nextBytes -= drop.Value.Data.Length; nextCount--;
        }
        var frame = new Frame(data.ToArray(), key, guaranteed || replace?.Value.Guaranteed == true,
            replace?.Value.Since ?? now);
        if (replace is not null) { record?.Invoke(replace.Value.Data, replace.Value.Key, replace.Value.Guaranteed, EgressEvent.Coalesced); replace.Value = frame; }
        else
        {
            var node = _pending.AddLast(frame);
            if (key != 0) _keys.Add(key, node);
        }
        _bytes = nextBytes;
        if (Ready.CurrentCount == 0) Ready.Release();
        return DisconnectReason.None;
    }
    internal Frame? Take()
    {
        if (_closed || _pending.First is not { } first) return null;
        _pending.RemoveFirst(); if (first.Value.Key != 0) _keys.Remove(first.Value.Key);
        _inflight = first.Value; return _inflight;
    }
    internal void Complete() { if (_inflight is { } f) _bytes -= f.Data.Length; _inflight = null; }
    internal (int Ordered, int Stream, long Bytes, long Age) Sample(long now)
    {
        int ordered = 0, stream = 0; long age = 0;
        foreach (var f in _pending) Observe(f);
        if (_inflight is { } inflight) Observe(inflight);
        return (ordered, stream, _bytes, age);
        void Observe(Frame frame)
        {
            if (frame.Key == 0) ordered++; else stream++;
            if (frame.Guaranteed) age = Math.Max(age, now - frame.Since);
        }
    }
    internal void Close() { _closed = true; _pending.Clear(); _keys.Clear(); _inflight = null; _bytes = 0; }
}

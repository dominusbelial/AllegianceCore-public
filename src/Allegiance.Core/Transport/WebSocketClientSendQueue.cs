namespace Allegiance.Core.Transport;

/// <summary>Bounded queued + in-flight bytes; one writer owns Take/Complete.
/// Key-zero is FIFO; nonzero keys replace pending entries in place as TCP does.</summary>
internal sealed class WebSocketClientSendQueue(int maxBytes = 1024 * 1024, int maxEntries = 256)
{
    internal sealed record Frame(byte[] Data, int Key, bool Guaranteed, long Since);
    private readonly LinkedList<Frame> _pending = [];
    private readonly Dictionary<int, LinkedListNode<Frame>> _keys = [];
    private Frame? _inflight;
    private int _bytes;
    internal int Bytes => _bytes;
    internal int Count => _pending.Count + (_inflight is null ? 0 : 1);
    // Caller holds the transport gate.
    internal bool Enqueue(ReadOnlySpan<byte> data, int key, bool guaranteed, long now)
    {
        guaranteed |= key == 0;
        _keys.TryGetValue(key, out var replace);
        guaranteed |= replace?.Value.Guaranteed == true;
        int bytes = _bytes - (replace?.Value.Data.Length ?? 0) + data.Length;
        int count = Count + (replace is null ? 1 : 0);
        while (bytes > maxBytes || count > maxEntries)
        {
            var drop = _pending.First;
            while (drop is not null && (drop == replace || drop.Value.Guaranteed)) drop = drop.Next;
            if (drop is null) return !guaranteed; // shed stream; reliable overflow loses session
            _pending.Remove(drop); _keys.Remove(drop.Value.Key);
            _bytes -= drop.Value.Data.Length; bytes -= drop.Value.Data.Length; count--;
        }
        var frame = new Frame(data.ToArray(), key, guaranteed, replace?.Value.Since ?? now);
        if (replace is not null) replace.Value = frame;
        else
        {
            var node = _pending.AddLast(frame);
            if (key != 0) _keys.Add(key, node);
        }
        _bytes = bytes;
        return true;
    }
    internal Frame? Take()
    {
        if (_pending.First is not { } first) return null;
        _pending.RemoveFirst(); if (first.Value.Key != 0) _keys.Remove(first.Value.Key);
        return _inflight = first.Value;
    }
    internal void Complete() { if (_inflight is { } frame) _bytes -= frame.Data.Length; _inflight = null; }
    internal void Clear() { _pending.Clear(); _keys.Clear(); _inflight = null; _bytes = 0; }
}

using System.Globalization;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>Opt-in, sim-thread-only applied-input attribution. The 100-tick
/// window retains every tick (including empty ones), rather than percentiles
/// that hide cadence. Bytes are message bytes, excluding CRC/transport framing.</summary>
internal sealed class InboundTrace
{
    private readonly Dictionary<ushort, (int Count, long Bytes)> _fmids = [];
    private readonly Dictionary<(uint Peer, ushort Fmid), (int Count, long Bytes)> _peers = [];
    private readonly List<string> _window = [];
    private int _count;
    private long _bytes;
    internal Action<string> Write { get; init; } = Console.WriteLine;

    internal void Message(ConnectionId peer, ushort fmid, int bytes)
    {
        var old = _fmids.GetValueOrDefault(fmid);
        _fmids[fmid] = (old.Count + 1, old.Bytes + bytes);
        var peerKey = (peer.Value, fmid);
        var previous = _peers.GetValueOrDefault(peerKey);
        _peers[peerKey] = (previous.Count + 1, previous.Bytes + bytes);
        _count++;
        _bytes += bytes;
    }

    internal void EndTick(long tick, double drainMs)
    {
        string ms = drainMs.ToString("F3", CultureInfo.InvariantCulture);
        // fmid=count/bytes; peer:fmid=count/bytes. Ordered keys make traces comparable.
        string fmids = string.Join(',', _fmids.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value.Count}/{p.Value.Bytes}"));
        string peers = string.Join(',', _peers.OrderBy(p => p.Key.Peer).ThenBy(p => p.Key.Fmid).Select(p => $"{p.Key.Peer}:{p.Key.Fmid}={p.Value.Count}/{p.Value.Bytes}"));
        Write($"[inbound] tick={tick} messages={_count} bytes={_bytes} drainMs={ms} fmids[{fmids}] peers[{peers}]");
        _window.Add($"{tick}:{_count}/{_bytes}/{ms}");
        if (_window.Count == 100)
        {
            Write($"[inbound-window] tick:messages/bytes/drainMs[{string.Join(',', _window)}]");
            _window.Clear();
        }
        _fmids.Clear();
        _peers.Clear();
        _count = 0;
        _bytes = 0;
    }
}

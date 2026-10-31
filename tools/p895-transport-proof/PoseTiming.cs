using System.Collections.Concurrent;
using System.Diagnostics;

namespace Allegiance.TransportProof;

/// <summary>Single receive-thread pose observer. Tick-log lookup may lag socket
/// delivery on loopback; deferred correlation preserves the real arrival time.</summary>
public sealed class PoseTiming(ConcurrentDictionary<uint, long> ticks)
{
    public readonly ConcurrentQueue<double> Ages = new(), Gaps = new();
    private readonly ConcurrentQueue<(uint Tick, long Received)> _pending = new();
    private readonly Dictionary<short, (uint Tick, long Received)> _last = new();

    public void Observe(short ship, uint tick, long received)
    {
        if (_last.TryGetValue(ship, out var previous))
        {
            if (tick <= previous.Tick) return;
            Gaps.Enqueue(Stopwatch.GetElapsedTime(previous.Received, received).TotalMilliseconds);
        }
        _last[ship] = (tick, received);
        if (ticks.TryGetValue(tick, out long generated))
            Ages.Enqueue(Stopwatch.GetElapsedTime(generated, received).TotalMilliseconds);
        else _pending.Enqueue((tick, received));
    }

    public bool PendingTicksReady() => _pending.All(p => ticks.ContainsKey(p.Tick));
    public void ResolvePendingTicks()
    {
        while (_pending.TryDequeue(out var pose))
            Ages.Enqueue(Stopwatch.GetElapsedTime(ticks[pose.Tick], pose.Received).TotalMilliseconds);
    }
}

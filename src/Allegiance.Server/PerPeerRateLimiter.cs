using System.Collections.Concurrent;

namespace Allegiance.Server;

/// <summary>
/// P315 (stability review finding H3) — bounds how often a per-key (normally
/// per-connection) diagnostic is emitted to the console, so that an attacker
/// scaling a message stream cannot force unbounded synchronous console I/O
/// inside one simulation tick. A single game tick must not spend seconds
/// inside <c>Console.WriteLine</c> because a peer shipped it ten thousand
/// unsupported/garbage messages.
/// <para>
/// Semantics: at most <see cref="MaxPerWindow"/> emissions per key per
/// <see cref="WindowMs"/> window; excess is counted as suppressed and
/// re-surfaced as a single summary line every <see cref="SummaryEvery"/>
/// suppressions, so an operator still sees that a peer is flooding without
/// the flood being echoed line-for-line. The window is wall-clock
/// (<c>Environment.TickCount64</c>), which is adequate for a log gate.
/// </para>
/// <para>
/// The table is keyed by the caller's <paramref name="key"/> (a
/// <c>ConnectionId.Value</c>). The owner must call <see cref="Remove"/> on
/// peer leave so the table stays bounded by the live peer set rather than by
/// every historical connection id (a connection-churn attacker must not be
/// able to grow this unboundedly either).
/// </para>
/// </summary>
internal sealed class PerPeerRateLimiter
{
    /// <summary>Wall-clock length of one gate window, in milliseconds.</summary>
    public long WindowMs { get; init; } = 5_000;

    /// <summary>Maximum emissions per key within one window before suppression.</summary>
    public int MaxPerWindow { get; init; } = 5;

    /// <summary>Emit a one-line summary every this many suppressions (per key).</summary>
    public int SummaryEvery { get; init; } = 100;

    private readonly ConcurrentDictionary<long, (long WindowStart, int Emitted, long Suppressed)> _m = new();

    /// <summary>
    /// Claim a ticket for emitting one diagnostic line for <paramref name="key"/>.
    /// <c>Emit</c> true means the caller should print its line; false means it
    /// should stay quiet. When <c>Emit</c> is false and <c>Summarize</c> is
    /// true the caller should print a summary line noting
    /// <c>Suppressed</c> suppressed repeats.
    /// </summary>
    public (bool Emit, long Suppressed, bool Summarize) Ticket(long key)
    {
        long now = Environment.TickCount64;
        // Ensure the key exists (insert is a no-op if another thread beat us).
        _m.TryAdd(key, (WindowStart: now, Emitted: 0, Suppressed: 0L));
        // Read-modify-write loop (CAS) so the disposition of THIS call is
        // decided against the pre-update counters — unambiguous even under
        // concurrent callers.
        while (true)
        {
            var cur = _m[key];

            long ws = cur.WindowStart;
            int emitted = cur.Emitted;
            long suppressed = cur.Suppressed;
            if (now - ws >= WindowMs) { ws = now; emitted = 0; suppressed = 0; }  // window rolled

            bool emit;
            if (emitted < MaxPerWindow) { emitted++; emit = true; }
            else { suppressed++; emit = false; }

            var next = (WindowStart: ws, Emitted: emitted, Suppressed: suppressed);
            if (_m.TryUpdate(key, next, cur))
            {
                bool summarize = !emit && suppressed > 0 && suppressed % SummaryEvery == 0;
                return (emit, suppressed, summarize);
            }
            // Lost the CAS race to another thread — re-read and retry.
        }
    }

    /// <summary>Drop all state for <paramref name="key"/> (call on peer leave).</summary>
    public void Remove(long key) => _m.TryRemove(key, out _);
}

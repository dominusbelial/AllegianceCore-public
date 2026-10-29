using Allegiance.Core.Transport;

namespace Allegiance.Server;

// Removal invalidates derived sweep work; additions only advance authoritative
// one-shot latches and need no invalidation. All callers still use the same set.
internal sealed class SweepLatchSet<T>
{
    private readonly HashSet<T> _entries = new();
    internal int Count => _entries.Count;
    /// <summary>P884b — the latch's retained backing-array capacity (test seam:
    /// proves the rotation releases a finished match's peak, not just its entries).</summary>
    internal int Capacity => _entries.Capacity;
    internal bool Add(T entry) => _entries.Add(entry);
    internal long RemovalVersion { get; private set; }
    internal int RemoveWhere(Predicate<T> match)
    {
        int removed = _entries.RemoveWhere(match);
        if (removed != 0) RemovalVersion++;
        return removed;
    }
    internal void Clear() { _entries.Clear(); RemovalVersion++; }

    /// <summary>P884b — release the PEAK capacity this latch ever needed. A match
    /// populates one entry per (recipient, subject) the recipient has ever seen,
    /// so the set's backing arrays grow to the match's widest moment and were then
    /// never released: the rotation pruned the entries but kept the arrays, and
    /// every match whose peak exceeded the last one re-grew them (the rig's
    /// per-match step). Trimming at the rotation boundary makes the retained size
    /// follow the live population again. Safe here because the sweep has just
    /// removed this world's entries, so the surviving entries are another
    /// mission's (P883b) or none.</summary>
    internal void TrimExcess() => _entries.TrimExcess();

    /// <summary>P884b — trim only when the set's peak is meaningfully larger than
    /// its live population (see <c>Server.TrimRetiredLatchCapacity</c>).</summary>
    internal void TrimExcessIfWorthwhile()
    {
        int count = _entries.Count;
        if (_entries.EnsureCapacity(0) > 2 * count + 64) _entries.TrimExcess();
    }
}

// P863: retain the completed (subject, side, audience) work, then visit only
// recipients whose original refresh phase is due. Audience changes, removals,
// object replacement and visibility edges re-enter the original full loop.
// This is scheduling only: the caller owns every visibility gate/latch/message.
internal sealed class CrossSideSweepCache : IDisposable
{
    private sealed class Audience
    {
        internal ConnectionId[] Connections = [];
        internal List<ConnectionId>[] Phases = [];
        internal uint MaxConnection;
        internal long Version;
    }
    private readonly Dictionary<(uint Scope, short Side), Audience> _audiences = new();
    private readonly Dictionary<(object Subject, short Side), (long Audience, long Generation)> _done = new();
    private long _removalVersion = -1, _generation;
    internal CrossSideSweepCache Begin(long removalVersion)
    {
        if (_removalVersion != removalVersion) { _done.Clear(); _removalVersion = removalVersion; }
        _generation++;
        return this;
    }
    internal void SetAudience(short side, List<ConnectionId> connections, int period, uint scope = 0)
    {
        if (!_audiences.TryGetValue((scope, side), out var audience))
            _audiences.Add((scope, side), audience = new());
        if (connections.SequenceEqual(audience.Connections) && audience.Phases.Length == period) return;
        audience.Connections = connections.ToArray();
        audience.MaxConnection = connections.Count == 0 ? 0 : connections.Max(c => c.Value);
        audience.Version++;
        audience.Phases = new List<ConnectionId>[period];
        for (int i = 0; i < period; i++) audience.Phases[i] = new();
        foreach (var conn in connections)
            if (period > 0) audience.Phases[conn.Value % (uint)period].Add(conn);
    }
    internal IReadOnlyList<ConnectionId> Recipients(object subject, short side, short id, long tick,
        bool force = false, uint scope = 0)
    {
        var audience = _audiences[(scope, side)];
        var key = (subject, side);
        bool complete = _done.TryGetValue(key, out var done) && done.Audience == audience.Version;
        _done[key] = (audience.Version, _generation);
        if (!complete || force) return audience.Connections;
        int period = audience.Phases.Length;
        if (period == 0) return Array.Empty<ConnectionId>();
        // The original phase adds uint IDs with wrap. Fall back to its exact
        // full loop for unusual IDs whose addition can wrap (including negatives).
        if (id < 0 || audience.MaxConnection > uint.MaxValue - (uint)id) return audience.Connections;
        int phase = (int)((period - (tick + id) % period) % period);
        return audience.Phases[phase];
    }
    public void Dispose()
    {
        foreach (var entry in _done)
            if (entry.Value.Generation != _generation) _done.Remove(entry.Key);
    }
    internal void ForgetScope(uint scope)
    {
        foreach (var key in _audiences.Keys.Where(key => key.Scope == scope).ToArray())
            _audiences.Remove(key);
        _done.Clear(); // derived work only; authoritative send latches are unchanged
    }
    internal void Clear() { _done.Clear(); _audiences.Clear(); }
}

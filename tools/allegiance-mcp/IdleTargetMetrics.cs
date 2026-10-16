namespace Allegiance.Tools.McpSpawner;

/// <summary>Decision-loop samples, independent of MCP polling. Counts distinct
/// bots seen idle with a target in the rolling minute; repeated ticks count once.</summary>
internal sealed class IdleTargetMetrics
{
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _lastIdle = new();
    private readonly HashSet<string> _current = new();
    private double _sampledAt = double.NegativeInfinity;

    internal bool Due(double now) { lock (_gate) return now - _sampledAt >= 1; }

    internal void Observe(double now, IEnumerable<string> idleNames)
    {
        lock (_gate)
        {
            _current.Clear();
            foreach (string name in idleNames) { _current.Add(name); _lastIdle[name] = now; }
            foreach (var name in _lastIdle.Where(kv => now - kv.Value >= 60).Select(kv => kv.Key).ToArray())
                _lastIdle.Remove(name);
            _sampledAt = now;
        }
    }

    internal IdleTargetReport Snapshot(double now, string? filter = null)
    {
        bool Matches(string name) => string.IsNullOrEmpty(filter) || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        lock (_gate)
            return new(_current.Count(Matches), _lastIdle.Count(kv => now - kv.Value < 60 && Matches(kv.Key)),
                double.IsFinite(_sampledAt) ? Math.Max(0, now - _sampledAt) : null);
    }
}

public readonly record struct IdleTargetReport(int CurrentCount, int BotsLastMinute, double? SampleAgeSeconds);

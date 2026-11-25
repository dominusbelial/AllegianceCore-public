namespace Allegiance.Tools.McpSpawner;

/// <summary>Driver-only admission pacing, measured in monotonic wall seconds.
/// Callers serialize access; successful seating resets the failure sequence.</summary>
internal sealed class BackfillRetry
{
    private int _failures;
    internal double NextAttemptAt { get; private set; }
    internal bool Due(double now) => now >= NextAttemptAt;

    internal double Failed(double now, double jitter)
    {
        double basis = Math.Min(60, Math.Pow(2, Math.Min(_failures, 6)));
        _failures = Math.Min(_failures + 1, 7);
        double delay = Math.Min(60, basis * (1 + .2 * Math.Clamp(jitter, 0, 1)));
        NextAttemptAt = now + delay;
        return delay;
    }

    internal void Succeeded() { _failures = 0; NextAttemptAt = 0; }
}

/// <summary>One signal per continuous not-ready episode, even if its reason changes.</summary>
internal sealed class BackfillLiveness
{
    internal const double StallSeconds = 600;
    private double? _since;
    private bool _reported;

    internal string? Observe(double now, string? reason)
    {
        if (reason is null) { _since = null; _reported = false; return null; }
        _since ??= now;
        if (_reported || now - _since.Value < StallSeconds) return null;
        _reported = true;
        return $"[backfill] stalled seconds={now - _since.Value:F0} reason={reason}";
    }
}

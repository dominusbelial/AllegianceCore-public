using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P464 — a client-side estimate of the server's simulation clock, derived
/// from the server timestamps already carried on the peer's own pose updates.
/// The gunner's <c>ACTIVE_TURRET_UPDATE(99)</c> carries a <c>time</c> the server
/// compares against the gunner's own <c>LastUpdate</c> and <b>drops the whole
/// message</b> when it lies more than ±1.5 s away (<c>shipIGC.cpp:3353-3354</c>,
/// ported at <c>Server.ShipCommands.cs:212-213</c>). The MonoGame client had no
/// server-clock notion at all, so it could never produce that timestamp. This
/// estimator reconstructs it from the server <c>Time</c> slot (the mission
/// clock, in seconds) that every <see cref="Allegiance.Core.Wire.ServerShipUpdate"/>
/// on the local ship carries, mapped onto the local monotonic clock.
/// </summary>
/// <remarks>
/// Model: the server's simulation clock advances ~1:1 with real time (20 sim
/// updates/s, each ~0.05 s), so <c>serverTime ≈ slope·local + intercept</c> over
/// a short window. A least-squares fit over a sliding window of
/// <c>(serverTime, localRecvTime)</c> samples both absorbs per-packet latency
/// jitter and extrapolates the current instant from the most recent sample —
/// precisely the robustness a single-phase "last sample + elapsed" tracker
/// lacks when a single late/early packet would skew it by the full jitter. The
/// <c>within-1.5 s gate</c> unit test feeds the estimator samples arriving under
/// simulated latency and asserts the estimate never leaves the server's
/// acceptance window at extrapolation points between samples.
/// </remarks>
public sealed class ServerClockEstimator
{
    /// <summary>Sliding-window size. Large enough to average jitter, small
    /// enough to track any real drift in the sim rate.</summary>
    private const int MaxSamples = 32;

    private readonly List<(double Server, double Local)> _samples = new();

    /// <summary>True once at least one server timestamp has been observed.</summary>
    public bool HasEstimate => _samples.Count > 0;

    /// <summary>The local monotonic time of the most recent accepted sample,
    /// in seconds (for diagnostics).</summary>
    public double LastSampleLocal => _samples.Count > 0 ? _samples[^1].Local : 0.0;

    /// <summary>
    /// Record an observed (server timestamp, local monotonic receive time) pair.
    /// The server <c>Time</c> is the mission sim clock and is monotonic, so a
    /// sample whose server time is not strictly greater than the last accepted
    /// one is ignored (guards clock wraps / duplicate deliveries).
    /// </summary>
    /// <param name="serverTime">The server's sim-clock <c>Time</c> slot, in
    /// seconds (uint seconds — the same units the server compares against
    /// <c>Ship.LastUpdate</c>).</param>
    /// <param name="localMonotonicMs"><c>Environment.TickCount64</c> at receive
    /// (milliseconds, monotonic).</param>
    public void AddSample(uint serverTime, long localMonotonicMs)
    {
        double server = serverTime;
        double local = localMonotonicMs / 1000.0;
        if (_samples.Count > 0 && server <= _samples[^1].Server)
            return;                                   // non-monotonic server clock — ignore
        _samples.Add((server, local));
        if (_samples.Count > MaxSamples)
            _samples.RemoveAt(0);
    }

    /// <summary>
    /// The estimated server sim time (seconds) at the given local monotonic
    /// instant. With no samples yet this returns 0 (callers gate ACTIVE sends
    /// on <see cref="HasEstimate"/> so a timestamp of 0 — certain to be dropped
    /// as stale — is never sent). With one sample it extrapolates at slope 1;
    /// with more it uses the least-squares fit.
    /// </summary>
    public double Estimate(long localMonotonicMs)
    {
        double local = localMonotonicMs / 1000.0;
        if (_samples.Count == 0)
            return 0.0;
        if (_samples.Count == 1)
            return _samples[0].Server + (local - _samples[0].Local);

        double n = _samples.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        foreach (var (s, l) in _samples)
        {
            sumX += l; sumY += s; sumXY += l * s; sumXX += l * l;
        }
        double denom = n * sumXX - sumX * sumX;
        if (Math.Abs(denom) < 1e-9)
            return _samples[^1].Server + (local - _samples[^1].Local);   // degenerate (constant local) — fall back to slope 1

        double slope = (n * sumXY - sumX * sumY) / denom;
        double intercept = (sumY - slope * sumX) / n;
        return slope * local + intercept;
    }
}

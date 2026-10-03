using System.Collections.Generic;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P508 — a client-side estimate of the server's simulation clock in <b>milliseconds</b>,
/// used to stamp the <c>timeUpdate</c> of the native <c>FM_C_SHIP_UPDATE(24)</c>. The
/// server drops a native SHIP_UPDATE whose <c>timeUpdate/1000</c> is outside a ±1.5 s
/// window of its own <c>SimNow</c> (<c>shipIGC.cpp:3142,3199-3200</c>; ported at
/// <c>Server.Dispatch.cs</c>), so the client must convert its local clock to
/// (approximate) server sim time.
///
/// <para><b>Source of server time.</b> The clock is fed from <c>FM_CS_PING(35)</c>
/// echoes: the server stamps <c>timeServer = (uint)(SimNow * 1000.0)</c>
/// (<c>Server.Dispatch.cs</c>, <c>FedSrv.CPP:4897-4911</c>) and returns the whole
/// message to the sender. So a PING echo directly observes server sim-
/// <b>milliseconds</b>, independent of the server's tick rate — no conversion from the
/// tick-index pose slot (which is loop iterations, not seconds; the P464 estimator's
/// 1:1-seconds assumption breaks on that slot — see <c>ShipDeadReckoner.cs:128</c>) is
/// needed, and there is no hardcoded update rate.</para>
///
/// <para><b>Model.</b> A least-squares fit of <c>serverMs = a·local + c</c> over a
/// sliding window of (serverMs, local-receive-time) samples (the same jitter-robust
/// fit the P464 estimator uses). The server sim clock advances ~1:1 with real time, so
/// <c>a ≈ 1</c> and <c>c</c> is a constant local↔server offset that absorbs both the
/// local/sim epoch difference and the (roughly constant) network RTT. <see
/// cref="ServerTimeMs"/> evaluates the fitted line at the current local instant.</para>
/// </summary>
public sealed class OwnShipServerClock
{
    /// <summary>Sliding-window size — enough to average jitter, small enough to track
    /// any real drift in the sim rate (same as the P464 estimator).</summary>
    private const int MaxSamples = 32;

    private readonly List<(double ServerMs, double LocalSec)> _samples = new();
    private bool _hasAnchor;
    private double _anchorServerMs;
    private double _anchorLocalSec;

    /// <summary>True once at least one PING echo has been observed (a time may be
    /// stamped).</summary>
    public bool Ready => _hasAnchor;

    /// <summary>
    /// Observe a (server sim-ms, local monotonic receive time) pair from a PING echo.
    /// Call under the world gate or on the main thread, once per echo. Server times
    /// are monotonic; a non-increasing one is ignored (guard against wraps/dups).
    /// </summary>
    /// <param name="serverSimMs">The echo's <c>timeServer</c> (server SimNow, ms).</param>
    /// <param name="localRecvMs"><c>Environment.TickCount64</c> when the echo was
    /// received (ms).</param>
    public void AddSample(uint serverSimMs, long localRecvMs)
    {
        double s = serverSimMs;
        double l = localRecvMs / 1000.0;
        if (!_hasAnchor)
        {
            _anchorServerMs = s;
            _anchorLocalSec = l;
            _hasAnchor = true;
        }
        if (_samples.Count > 0 && s <= _samples[^1].ServerMs)
            return;                                   // non-monotonic — ignore
        _samples.Add((s, l));
        if (_samples.Count > MaxSamples)
            _samples.RemoveAt(0);
    }

    /// <summary>
    /// The estimated server sim time in <b>milliseconds</b> (uint) at the local
    /// monotonic instant <paramref name="localMs"/> — the value to stamp into
    /// <c>ShipUpdate.Time</c> so the server's <c>SimNow − timeUpdate/1000</c> stays
    /// inside the ±1.5 s gate. Returns 0 until the first echo (callers gate native
    /// sends on <see cref="Ready"/>).
    /// </summary>
    public uint ServerTimeMs(long localMs)
    {
        double local = localMs / 1000.0;
        if (!_hasAnchor) return 0;
        if (_samples.Count == 0) return 0;

        double est;
        if (_samples.Count == 1)
        {
            // Single echo: extrapolate the (server − local) offset at slope 1 — the
            // sim clock advances ~1:1 with real time, so this is already accurate.
            est = _anchorServerMs + (local - _anchorLocalSec) * 1000.0;
        }
        else
        {
            double n = _samples.Count, sx = 0, sy = 0, sxy = 0, sxx = 0;
            foreach (var (s, l) in _samples) { sx += l; sy += s; sxy += l * s; sxx += l * l; }
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9)
            {
                est = _anchorServerMs + (local - _anchorLocalSec) * 1000.0;
            }
            else
            {
                double a = (n * sxy - sx * sy) / denom;   // serverMs per local-second (~1)
                double c = (sy - a * sx) / n;             // serverMs at local-second 0
                est = a * local + c;
            }
        }

        return est <= 0 ? 0u : (uint)est;
    }
}

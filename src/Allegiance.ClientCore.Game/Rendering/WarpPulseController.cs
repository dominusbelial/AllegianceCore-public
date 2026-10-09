using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P585 — the pure envelope behind the opt-in aleph <b>TRANSIT PULSE</b> (a
/// NON-native flourish, active only under <c>--aleph-glimpse on</c>): whenever
/// any ship crosses an aleph in the viewer's current sector, that aleph's
/// funnel briefly brightens, its destination glimpse opens wide, and a
/// lens-flare burst flashes from the funnel centre.
///
/// This class is the pure, GPU-free state machine: it accepts transit
/// triggers (<see cref="Trigger"/>) keyed by warp id, and reports the current
/// pulse strength (0..1 envelope) for any warp so renderers can drive the
/// funnel brightness, the glimpse radius/opacity and the flare. Kept free of
/// MonoGame/GraphicsDevice so the envelope, the stacking cap and the
/// coalescing rule are all unit-testable headless.
///
/// Native has NO equivalent effect — <c>WinTrekClient::ShipWarped</c>
/// (<c>WinTrek/trekigc.cpp:2976-2991</c>) is an empty, commented-out body, so
/// there is nothing to port; this is a deliberately opt-in addition.
/// </summary>
public sealed class WarpPulseController
{
    /// <summary>Quick attack to peak, in seconds (the pulse must read as a
    /// sharp "flare", not a slow ramp).</summary>
    public const float AttackSeconds = 0.12f;

    /// <summary>Ease-out fall to rest, in seconds. Total pulse =
    /// <see cref="AttackSeconds"/> + <see cref="FallSeconds"/> ≈ 0.9 s, inside
    /// the task's "~0.6–1.0 s, ease-out" window.</summary>
    public const float FallSeconds = 0.78f;

    /// <summary>Full pulse duration (attack + fall).</summary>
    public const float TotalSeconds = AttackSeconds + FallSeconds;

    /// <summary>Upper bound on simultaneously-pulsing warps so a busy scrum of
    /// transits stacks sensibly instead of fanning every aleph on screen
    /// (eviction picks the OLDEST pulse when the cap is hit).</summary>
    public const int MaxSimultaneous = 8;

    /// <summary>Coalescing window: a second transit of the SAME warp within
    /// this many seconds restarts the pulse rather than letting a dense
    /// convoy chop the flare into flicker.</summary>
    public const float MinRepeatGapSeconds = 0.15f;

    private readonly struct Pulse
    {
        public readonly double Start;
        public readonly long Seq;          // monotonic insertion order (deterministic eviction)
        public Pulse(double start, long seq) { Start = start; Seq = seq; }
    }

    private readonly Dictionary<short, Pulse> _active = new();
    private long _seqCounter;

    /// <summary>Number of warps currently tracked by a pulse (peak ≤
    /// <see cref="MaxSimultaneous"/>).</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// Begin (or restart) a transit pulse for <paramref name="warpId"/>, using
    /// <paramref name="now"/> as the pulse clock (monotonic seconds). A
    /// re-trigger within <see cref="MinRepeatGapSeconds"/> coalesces; when the
    /// <see cref="MaxSimultaneous"/> cap is reached the OLDEST pulse is evicted
    /// so the newest transit is never dropped.
    /// </summary>
    public void Trigger(short warpId, double now)
    {
        if (_active.TryGetValue(warpId, out var existing))
        {
            double age = now - existing.Start;
            if (age >= 0.0 && age < MinRepeatGapSeconds)
                return;                          // same-warp burst: coalesce, don't flicker
            _active[warpId] = new Pulse(now, ++_seqCounter);    // restart the timeline
            return;
        }

        if (_active.Count >= MaxSimultaneous)
            EvictOldest();

        _active[warpId] = new Pulse(now, ++_seqCounter);
    }

    /// <summary>Current 0..1 envelope strength for <paramref name="warpId"/>
    /// at clock <paramref name="now"/> (0 when idle or expired).</summary>
    public float Strength(short warpId, double now)
        => _active.TryGetValue(warpId, out var p) ? Envelope((float)(now - p.Start)) : 0f;

    /// <summary>Whether <paramref name="warpId"/> is currently pulsing.</summary>
    public bool IsActive(short warpId, double now) => Strength(warpId, now) > 0f;

    /// <summary>Prune expired pulses (age ≥ <see cref="TotalSeconds"/>). Call
    /// once per frame so the map never grows with stale warps.</summary>
    public void Update(double now)
    {
        if (_active.Count == 0) return;
        List<short>? remove = null;
        foreach (var kv in _active)
            if (now - kv.Value.Start >= TotalSeconds)
                (remove ??= new()).Add(kv.Key);
        if (remove is null) return;
        foreach (var w in remove) _active.Remove(w);
    }

    /// <summary>
    /// The pulse envelope as a pure function of age: 0 at the instant of
    /// trigger, a linear attack to 1 across <see cref="AttackSeconds"/>, then a
    /// quadratic ease-out (decelerating) to 0 at <see cref="TotalSeconds"/> —
    /// the "fast flare, gentle settle" the task asks for.
    /// </summary>
    public static float Envelope(float ageSeconds)
    {
        if (ageSeconds <= 0f) return 0f;
        if (ageSeconds < AttackSeconds)
            return ageSeconds / AttackSeconds;                    // linear attack
        float t = (ageSeconds - AttackSeconds) / FallSeconds;     // 0..1 over the fall
        if (t >= 1f) return 0f;
        float eased = 1f - t;
        return eased * eased;                                     // quadratic ease-out
    }

    private void EvictOldest()
    {
        short oldest = default;
        long oldestSeq = long.MaxValue;
        foreach (var kv in _active)
        {
            if (kv.Value.Seq < oldestSeq) { oldestSeq = kv.Value.Seq; oldest = kv.Key; }
        }
        _active.Remove(oldest);
    }
}

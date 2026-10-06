using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P546 — the smoke density budget: a capacity-limited set of admitted puffs
/// that, when full, <b>evicts the oldest first</b> to make room for newer ones
/// (the P543 "MaxSmokePerExplosion" idea generalised to on-screen counts).
/// Smoke is the dominant particle cost and the main readability offender, so
/// it is capped both per emitter (<see cref="EnhancedExplosionRenderer.MaxSmokePerExplosion"/>)
/// and across the whole screen (<see cref="EnhancedSmokeField.MaxSmokeOnScreen"/>).
/// Reused (Reset per frame) so it allocates nothing steady-state.
/// Pure and unit-testable headless.
/// </summary>
public sealed class ParticleDensityBudget
{
    private readonly float[] _admissionKey;   // oldest-first comparator key per slot
    private int _count;

    /// <param name="capacity">Max admitted puffs. 0 disables admission.</param>
    public ParticleDensityBudget(int capacity)
    {
        Capacity = Math.Max(0, capacity);
        _admissionKey = new float[Capacity > 0 ? Capacity : 1];
    }

    /// <summary>The maximum number of simultaneously-admitted puffs.</summary>
    public int Capacity { get; }

    /// <summary>How many puffs are currently admitted.</summary>
    public int Count => _count;

    /// <summary>Clear all admissions (call once per frame before drawing).</summary>
    public void Reset() => _count = 0;

    /// <summary>
    /// Attempt to admit a puff whose <paramref name="key"/> orders admissions by
    /// age (for smoke: the puff's remaining-life fraction — lower = younger =
    /// more visible). Admits while under <see cref="Capacity"/>; at capacity it
    /// <b>evicts the admitted puff with the smallest key (the oldest) and admits
    /// the new one</b>, so under load the freshest/most-visible puffs survive and
    /// the oldest are thinned first. Returns true iff the caller's puff may be
    /// drawn.
    /// </summary>
    public bool TryAdmit(float key)
    {
        if (Capacity <= 0) return false;
        if (_count < Capacity)
        {
            _admissionKey[_count++] = key;
            return true;
        }
        // At capacity: find the oldest admitted (smallest key) and replace it.
        int oldest = 0;
        for (int i = 1; i < _count; i++)
            if (_admissionKey[i] < _admissionKey[oldest])
                oldest = i;
        _admissionKey[oldest] = key;
        return true;
    }

    /// <summary>True when the budget is full (no more admissions without an eviction).</summary>
    public bool IsFull => _count >= Capacity;
}

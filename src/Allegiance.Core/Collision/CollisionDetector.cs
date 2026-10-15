using System.Collections.Concurrent;

namespace Allegiance.Core.Collision;

/// <summary>
/// The collision-detection orchestrator — the C++ <c>CclusterIGC::Update</c>
/// collision block (<c>src/Igc/clusterIGC.cpp:255-321</c>) reduced to the parts
/// this slice owns: sweep the step once (<b>per sector update</b>, not per chunk),
/// gather candidates with the broad phase, test them with the narrow phase into a
/// queue, and sort by collision time. <b>Reports only</b> — no damage, no
/// destruction, no explosion.
/// </summary>
public sealed class CollisionDetector
{
    private readonly List<Collider> _colliders = [];
    private readonly CollisionQueue _queue = new();

    public IReadOnlyList<Collider> Colliders => _colliders;
    public int CandidatePairs => BroadPhase.OverlappingPairs(_colliders).Count();

    /// <summary>Register a collider. Its <see cref="Collider.Ordinal"/> is its registration index, giving deterministic tie-breaks.</summary>
    public void Add(Collider c)
    {
        ArgumentNullException.ThrowIfNull(c);
        c.Ordinal = _colliders.Count;
        _colliders.Add(c);
    }

    /// <summary>
    /// Detect all collisions over a step of <paramref name="dt"/> seconds
    /// (from the colliders' current positions). Returns the events sorted
    /// ascending by collision time. Calling again with the same colliders and dt
    /// yields the same list (determinism).
    /// </summary>
    public IReadOnlyList<CollisionEvent> Detect(float dt) => Detect(dt, parallel: false, threads: 1);

    /// <summary>
    /// P667 — Detect over a step, optionally parallelising the detection phase.
    /// <paramref name="parallel"/> + <paramref name="threads"/> &gt; 1 runs the
    /// per-collider swept-AABB step and the broad/narrow pair testing on
    /// <paramref name="threads"/> workers; <paramref name="threads"/> == 1 runs
    /// the identical work single-threaded. <b>Either way the returned list is
    /// byte-for-byte identical</b>: detection reads only per-collider state that
    /// is fixed before the phase (<see cref="Collider.SetStep"/> writes only that
    /// collider's own swept bounds; <see cref="NarrowPhase.TestPair"/> is a pure
    /// function), and <see cref="CollisionQueue.Sort"/> imposes a deterministic
    /// total order (<c>TCollision</c>, then collider ordinals) independent of the
    /// order events were inserted — so any thread schedule and any
    /// <paramref name="threads"/> value yields the same ordered events as the
    /// serial path.
    /// </summary>
    public IReadOnlyList<CollisionEvent> Detect(float dt, bool parallel, int threads)
    {
        _queue.Clear();
        if (_colliders.Count < 2) return _queue.Ordered();

        if (parallel && threads > 1)
        {
            // SetStep writes only the owning collider's swept bounds — safe in
            // parallel, and identical to the serial loop.
            Parallel.For(0, _colliders.Count, NewOpts(threads), i => _colliders[i].SetStep(0f, dt));
        }
        else
        {
            foreach (var c in _colliders)
                c.SetStep(0f, dt);              // swept AABB for the step (SetBB)
        }

        // Broad phase: deterministic (index-ordered) candidate pairs.
        var pairs = BroadPhase.OverlappingPairs(_colliders).ToList();

        if (parallel && threads > 1)
        {
            // Narrow phase is a pure function of the (fixed) collider state; the
            // final deterministic Sort repairs any insertion-order variation.
            var found = new ConcurrentBag<CollisionEvent>();
            Parallel.For(0, pairs.Count, NewOpts(threads), k =>
            {
                var (i, j) = pairs[k];
                var ev = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
                if (ev.Found)
                    found.Add(new CollisionEvent(_colliders[i], _colliders[j], ev.TCollision, ev.HtsA, ev.HtsB));
            });
            foreach (var e in found) _queue.Add(e);
        }
        else
        {
            foreach (var (i, j) in pairs)
            {
                var ev = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
                if (ev.Found)
                    _queue.Add(new CollisionEvent(_colliders[i], _colliders[j], ev.TCollision, ev.HtsA, ev.HtsB));
            }
        }

        _queue.Sort();
        return _queue.Ordered();
    }

    private static ParallelOptions NewOpts(int threads) => new() { MaxDegreeOfParallelism = Math.Max(1, threads) };
}

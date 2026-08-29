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
    public IReadOnlyList<CollisionEvent> Detect(float dt)
    {
        _queue.Clear();
        if (_colliders.Count < 2) return _queue.Ordered();

        foreach (var c in _colliders)
            c.SetStep(0f, dt);              // swept AABB for the step (SetBB)

        foreach (var (i, j) in BroadPhase.OverlappingPairs(_colliders))
        {
            var ev = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
            if (ev.Found)
                _queue.Add(new CollisionEvent(_colliders[i], _colliders[j], ev.TCollision, ev.HtsA, ev.HtsB));
        }

        _queue.Sort();
        return _queue.Ordered();
    }
}

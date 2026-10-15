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
    /// yields the same list (determinism). A pure pair filter may exclude
    /// categories the caller does not handle; retained events keep their original
    /// ordinals, collision times, sub-shapes, and tie order.
    /// </summary>
    public IReadOnlyList<CollisionEvent> Detect(float dt, Func<Collider, Collider, bool>? pairFilter = null)
        => Detect(dt, parallel: false, threads: 1, pairFilter);

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
    public IReadOnlyList<CollisionEvent> Detect(float dt, bool parallel, int threads, Func<Collider, Collider, bool>? pairFilter = null)
    {
        _queue.Clear();
        GeometricPairTests = 0;
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

        // Identical launch/ordnance bodies can contribute thousands of pairs
        // with exactly the same geometric inputs. Solve each ordered geometry
        // pair once per Detect call, then expand onto the ORIGINAL colliders.
        // No persistent cache, approximation, or gameplay-state filtering.
        var groups = new Dictionary<Geometry, int>();
        var groupIds = new int[_colliders.Count];
        for (int i = 0; i < _colliders.Count; i++)
        {
            var key = Geometry.Of(_colliders[i]);
            if (!groups.TryGetValue(key, out int group))
                groups.Add(key, group = groups.Count);
            groupIds[i] = group;
        }
        if (!parallel || threads <= 1)
        {
            // The serial path can solve/expand in candidate order without
            // materialising a pair array or a per-pair expansion map.
            Dictionary<long, CollisionTest>? solved = groups.Count < _colliders.Count ? [] : null;
            foreach (var (i, j) in BroadPhase.OverlappingPairs(_colliders, pairFilter))
            {
                long key = ((long)groupIds[i] << 32) | (uint)groupIds[j];
                if (solved is null || !solved.TryGetValue(key, out var ev))
                {
                    ev = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
                    GeometricPairTests++;
                    solved?.Add(key, ev);
                }
                if (ev.Found)
                    _queue.Add(new CollisionEvent(_colliders[i], _colliders[j], ev.TCollision, ev.HtsA, ev.HtsB));
            }
            _queue.SortOrdinalEvents();
            return _queue.Ordered();
        }

        var pairs = BroadPhase.OverlappingPairs(_colliders, pairFilter).ToList();
        var work = pairs;
        int[]? workIds = null;
        if (groups.Count < _colliders.Count)
        {
            work = [];
            workIds = new int[pairs.Count];
            var unique = new Dictionary<long, int>();
            for (int k = 0; k < pairs.Count; k++)
            {
                var (i, j) = pairs[k];
                long key = ((long)groupIds[i] << 32) | (uint)groupIds[j]; // preserve A/B order
                if (!unique.TryGetValue(key, out int index))
                {
                    unique.Add(key, index = work.Count);
                    work.Add((i, j));
                }
                workIds[k] = index;
            }
        }
        GeometricPairTests = work.Count;
        var results = new CollisionTest[work.Count];
        if (parallel && threads > 1)
            Parallel.For(0, work.Count, NewOpts(threads), k =>
            {
                var (i, j) = work[k];
                results[k] = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
            });
        else
            for (int k = 0; k < work.Count; k++)
            {
                var (i, j) = work[k];
                results[k] = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
            }

        for (int k = 0; k < pairs.Count; k++)
        {
            var ev = results[workIds is null ? k : workIds[k]];
            if (!ev.Found) continue;
            var (i, j) = pairs[k];
            _queue.Add(new CollisionEvent(_colliders[i], _colliders[j], ev.TCollision, ev.HtsA, ev.HtsB));
        }

        _queue.SortOrdinalEvents();
        return _queue.Ordered();
    }

    /// <summary>Number of distinct ordered geometric pairs solved by the last detection.</summary>
    public int GeometricPairTests { get; private set; }

    // Bitwise keys deliberately distinguish signed zero and NaN payloads. A
    // collider's Data/ordinal never enters TestPair; every other input does.
    private readonly record struct Bits(int X, int Y, int Z)
    {
        public static Bits Of(Vector3 v) => new(BitConverter.SingleToInt32Bits(v.X),
            BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
    }
    private readonly record struct Geometry(CvhFile? Hull, int Shape, int Radius, int Scale,
        Bits Position, Bits Velocity, Bits Right, Bits Up, Bits Backward)
    {
        public static Geometry Of(Collider c) => new(c.Hull, c.Shape,
            BitConverter.SingleToInt32Bits(c.EffectiveRadius), BitConverter.SingleToInt32Bits(c.Scale),
            Bits.Of(c.Position), Bits.Of(c.Velocity), Bits.Of(c.Orientation.GetRight()),
            Bits.Of(c.Orientation.GetUp()), Bits.Of(c.Orientation.GetBackward()));
    }

    private static ParallelOptions NewOpts(int threads) => new() { MaxDegreeOfParallelism = Math.Max(1, threads) };
}

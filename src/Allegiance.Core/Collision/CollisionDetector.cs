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
    private readonly Dictionary<Geometry, int> _groups = new();
    private int[] _groupIds = [];
    private CollisionTest[] _directResults = [];
    private bool[] _directTested = [];
    private readonly Dictionary<long, CollisionTest> _solved = new();
    private readonly List<(int I, int J)> _pairs = [];
    private readonly List<(int I, int J)> _work = [];
    private int[] _workIds = [];
    private CollisionTest[] _results = [];
    private readonly Dictionary<long, int> _unique = new();

    private static T[] Buffer<T>(ref T[] buffer, int count)
    {
        if (buffer.Length < count) Array.Resize(ref buffer, Math.Max(count, buffer.Length * 2));
        return buffer;
    }

    // P928: sector-local frame storage. Never shared between sectors or held
    // by broadcast workers. A nested callback gets independent storage.
    private readonly List<Collider> _pool = [];
    private bool _frameActive;
    private int _poolCursor;

    internal Frame BeginFrame()
    {
        if (_frameActive) return new CollisionDetector().BeginFrame();
        _frameActive = true;
        _poolCursor = 0;
        GeometricPairTests = 0;
        _colliders.Clear();
        _queue.Clear();
        return new(this);
    }

    internal readonly struct Frame(CollisionDetector detector) : IDisposable
    {
        public CollisionDetector Detector => detector;
        public void Dispose()
        {
            for (int i = 0; i < detector._poolCursor; i++) detector._pool[i].Release();
            detector._colliders.Clear();
            detector._queue.Clear();
            detector._groups.Clear(); // geometry keys contain hull references
            detector._frameActive = false;
        }
    }

    internal Collider CreateCollider(object? data, Vector3 position, Vector3 velocity,
        Sim.Orientation? orientation, CvhFile? hull, float desiredRadius, bool isStatic,
        int fallbackShape = HitShape.Sphere)
    {
        Collider collider;
        if (_poolCursor == _pool.Count)
        {
            collider = new(data, position, velocity, orientation ?? Sim.Orientation.Identity, hull, desiredRadius, isStatic, fallbackShape);
            _pool.Add(collider);
            _poolCursor++;
        }
        else
        {
            // Count the borrowed slot before Reset: a failing hull-support
            // build must still release its newly assigned model/hull references.
            collider = _pool[_poolCursor++];
            collider.Reset(data, position, velocity, orientation, hull, desiredRadius, isStatic, fallbackShape);
        }
        return collider;
    }

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
        var groups = _groups;
        // No removes: group numbers still come from first encounter in collider
        // registration order. Dictionary enumeration is never used here.
        groups.Clear();
        groups.EnsureCapacity(_colliders.Count);
        var groupIds = Buffer(ref _groupIds, _colliders.Count);
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
            // Small repeated-geometry sets need no hash lookup for every
            // expanded identity pair. Bound the direct table to 65,536 cells;
            // larger worlds retain the sparse dictionary and unique geometry
            // still needs no result cache at all.
            bool direct = groups.Count * 2 <= _colliders.Count && groups.Count <= 256;
            int cells = direct ? groups.Count * groups.Count : 0;
            var resultsByGroup = direct ? Buffer(ref _directResults, cells) : null;
            var tested = direct ? Buffer(ref _directTested, cells) : null;
            if (direct) Array.Clear(tested!, 0, cells);
            _solved.Clear();
            Dictionary<long, CollisionTest>? solved = !direct && groups.Count < _colliders.Count ? _solved : null;
            foreach (var (i, j) in BroadPhase.OverlappingPairs(_colliders, pairFilter))
            {
                CollisionTest ev;
                if (direct)
                {
                    int index = groupIds[i] * groups.Count + groupIds[j];
                    if (!tested![index])
                    {
                        resultsByGroup![index] = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
                        tested[index] = true;
                        GeometricPairTests++;
                    }
                    ev = resultsByGroup![index];
                }
                else
                {
                    long key = ((long)groupIds[i] << 32) | (uint)groupIds[j];
                    if (solved is null || !solved.TryGetValue(key, out ev))
                    {
                        ev = NarrowPhase.TestPair(_colliders[i], _colliders[j], dt);
                        GeometricPairTests++;
                        solved?.Add(key, ev);
                    }
                }
                if (ev.Found)
                    _queue.Add(new CollisionEvent(_colliders[i], _colliders[j], ev.TCollision, ev.HtsA, ev.HtsB));
            }
            _queue.SortOrdinalEvents();
            return _queue.Ordered();
        }

        var pairs = _pairs;
        pairs.Clear();
        foreach (var pair in BroadPhase.OverlappingPairs(_colliders, pairFilter)) pairs.Add(pair);
        var work = pairs;
        int[]? workIds = null;
        if (groups.Count < _colliders.Count)
        {
            work = _work;
            work.Clear();
            workIds = Buffer(ref _workIds, pairs.Count);
            var unique = _unique;
            unique.Clear();
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
        var results = Buffer(ref _results, work.Count);
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

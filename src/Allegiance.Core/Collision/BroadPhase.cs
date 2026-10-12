namespace Allegiance.Core.Collision;

/// <summary>
/// The broad phase — deterministic swept-AABB candidate-pair generation.
///
/// Native Allegiance's spatial broad phase is a KD tree of bins built over
/// per-axis <b>sorted endpoint lists</b> (<c>src/_Utility/KDroot.cpp</c> /
/// <c>KDnode.cpp</c>): <c>KDroot::update</c> (<c>KDroot.cpp:166-205</c>) resorts
/// the hit tests' endpoints on each axis and <c>KDnode::pivot</c>
/// (<c>KDnode.cpp:238-298</c>) splits them on a cost-minimising plane, so
/// <c>KDnode::test</c> (<c>KDnode.cpp:300-331</c>) only ever hands the narrow
/// phase pairs whose swept AABBs actually overlap. That is precisely a
/// <b>sweep-and-prune</b>: sort intervals on one axis, sweep the active set, and
/// emit the pairs that pass the full 3-D AABB overlap predicate.
///
/// This port implements that intent directly as a sweep-and-prune. It yields
/// <b>exactly the same candidate set</b> as the O(n²) pairwise AABB sweep — the
/// predicate it gates on is byte-for-byte the same overlap test — but reaches it
/// in O(n log n + P) instead of O(n²), so the (now hull-shaped) narrow phase is
/// only fed local neighbourhoods. Q1 of COLLISION.md stands: the broad phase
/// only accelerates reaching overlapping pairs, it never changes <i>which</i>
/// pairs are tested.
///
/// Determinism: the sweep sorts endpoints by (coordinate, index) and the emitted
/// pairs are re-sorted by <c>(I, J)</c>, so the pair stream (and therefore every
/// tie broken downstream) is bit-identical to the O(n²) sweep it replaces.
/// </summary>
public static class BroadPhase
{
    /// <summary>
    /// All unordered pairs of <paramref name="colliders"/> whose swept AABBs
    /// overlap, in deterministic (index-ordered) order — identical to the
    /// O(n²) sweep, computed with a sweep-and-prune spatial broad phase.
    /// </summary>
    public static IEnumerable<(int I, int J)> OverlappingPairs(IReadOnlyList<Collider> colliders)
    {
        int n = colliders.Count;
        if (n < 2) yield break;

        // Sweep axis: the axis of widest bounding-box spread (cheapest active
        // set — native rotates pivots across axes for the same reason,
        // KDnode.cpp:244-246). Deterministic: ties fall through to the next.
        int axis = WidestAxis(colliders, n);

        // Endpoints sorted by (min, index): stable interval ordering.
        Span<(int Index, float Min, float Max)> sweep = n <= 1024
            ? stackalloc (int, float, float)[n]
            : new (int, float, float)[n];
        for (int i = 0; i < n; i++)
        {
            ref var e = ref sweep[i];
            e.Index = i;
            e.Min = AxisMin(colliders[i], axis);
            e.Max = AxisMax(colliders[i], axis);
        }
        sweep[..n].Sort(static (a, b) =>
        {
            int c = a.Min.CompareTo(b.Min);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });

        // Active set: colliders whose [min,max] still overlaps the sweep
        // position on the sweep axis (their max > current min). Every active
        // interval already overlaps the current one on the sweep axis, so only
        // the other two axes need testing.
        var active = new List<(int Index, float SweepMax)>(64);
        var pairs = new List<(int I, int J)>();
        for (int si = 0; si < n; si++)
        {
            float curMin = sweep[si].Min;
            int curIdx = sweep[si].Index;
            var cur = colliders[curIdx];

            // Drop expired actives (their sweep-maximum passed the sweep pos).
            active.RemoveAll(a => a.SweepMax <= curMin);

            // Test every still-active box on the two remaining axes.
            foreach (var (aj, _) in active)
            {
                var b = colliders[aj];
                if (OverlapRemaining(cur, b, axis))
                    pairs.Add(curIdx < aj ? (curIdx, aj) : (aj, curIdx));
            }

            active.Add((curIdx, sweep[si].Max));
        }

        // Bit-identical to the O(n²) index-ordered stream (stable ties).
        pairs.Sort(static (a, b) =>
        {
            int c = a.I.CompareTo(b.I);
            return c != 0 ? c : a.J.CompareTo(b.J);
        });
        foreach (var p in pairs)
            yield return p;
    }

    /// <summary>
    /// The O(n²) pairwise swept-AABB sweep this class used to be (and that
    /// native's tree produces the same set as). Kept as the reference oracle
    /// for the candidate-set equivalence test; not used by the hot path.
    /// </summary>
    public static IEnumerable<(int I, int J)> OverlappingPairsBruteForce(IReadOnlyList<Collider> colliders)
    {
        for (int i = 0; i < colliders.Count; i++)
        {
            var a = colliders[i];
            for (int j = i + 1; j < colliders.Count; j++)
            {
                var b = colliders[j];
                if (Overlap(a, b))
                    yield return (i, j);
            }
        }
    }

    private static int WidestAxis(IReadOnlyList<Collider> colliders, int n)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var c = colliders[i];
            if (c.BoundsMin.X < minX) minX = c.BoundsMin.X;
            if (c.BoundsMin.Y < minY) minY = c.BoundsMin.Y;
            if (c.BoundsMin.Z < minZ) minZ = c.BoundsMin.Z;
            if (c.BoundsMax.X > maxX) maxX = c.BoundsMax.X;
            if (c.BoundsMax.Y > maxY) maxY = c.BoundsMax.Y;
            if (c.BoundsMax.Z > maxZ) maxZ = c.BoundsMax.Z;
        }
        float sx = maxX - minX, sy = maxY - minY, sz = maxZ - minZ;
        if (sx >= sy && sx >= sz) return 0;
        if (sy >= sz) return 1;
        return 2;
    }

    private static float AxisMin(Collider c, int axis) => axis switch { 0 => c.BoundsMin.X, 1 => c.BoundsMin.Y, _ => c.BoundsMin.Z };
    private static float AxisMax(Collider c, int axis) => axis switch { 0 => c.BoundsMax.X, 1 => c.BoundsMax.Y, _ => c.BoundsMax.Z };

    /// <summary>Full 3-D swept-AABB overlap (the exact gate the O(n²) used).</summary>
    private static bool Overlap(Collider a, Collider b) =>
        a.BoundsMax.X > b.BoundsMin.X && a.BoundsMin.X < b.BoundsMax.X &&
        a.BoundsMax.Y > b.BoundsMin.Y && a.BoundsMin.Y < b.BoundsMax.Y &&
        a.BoundsMax.Z > b.BoundsMin.Z && a.BoundsMin.Z < b.BoundsMax.Z;

    /// <summary>3-D overlap excluding the already-checked sweep axis.</summary>
    private static bool OverlapRemaining(Collider a, Collider b, int axis)
    {
        return axis switch
        {
            0 => a.BoundsMax.Y > b.BoundsMin.Y && a.BoundsMin.Y < b.BoundsMax.Y &&
                 a.BoundsMax.Z > b.BoundsMin.Z && a.BoundsMin.Z < b.BoundsMax.Z,
            1 => a.BoundsMax.X > b.BoundsMin.X && a.BoundsMin.X < b.BoundsMax.X &&
                 a.BoundsMax.Z > b.BoundsMin.Z && a.BoundsMin.Z < b.BoundsMax.Z,
            _ => a.BoundsMax.X > b.BoundsMin.X && a.BoundsMin.X < b.BoundsMax.X &&
                 a.BoundsMax.Y > b.BoundsMin.Y && a.BoundsMin.Y < b.BoundsMax.Y,
        };
    }
}

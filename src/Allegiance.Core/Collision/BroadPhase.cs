namespace Allegiance.Core.Collision;

/// <summary>
/// The broad phase — deterministic swept-AABB candidate-pair generation. Yields
/// exactly the set of pairs whose swept bounding boxes overlap, which is the
/// same candidate set the C++ KD tree presents to the narrow phase (Q1 of
/// COLLISION.md: the tree only accelerates reaching overlapping pairs, it never
/// changes <i>which</i> AABB-overlapping pairs are tested; the pairwise overlap
/// test is what gates both).
/// </summary>
public static class BroadPhase
{
    /// <summary>
    /// All unordered pairs of <paramref name="colliders"/> whose swept AABBs
    /// overlap, in deterministic (index-ordered) order.
    /// </summary>
    public static IEnumerable<(int I, int J)> OverlappingPairs(IReadOnlyList<Collider> colliders)
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

    private static bool Overlap(Collider a, Collider b) =>
        a.BoundsMax.X > b.BoundsMin.X && a.BoundsMin.X < b.BoundsMax.X &&
        a.BoundsMax.Y > b.BoundsMin.Y && a.BoundsMin.Y < b.BoundsMax.Y &&
        a.BoundsMax.Z > b.BoundsMin.Z && a.BoundsMin.Z < b.BoundsMax.Z;
}

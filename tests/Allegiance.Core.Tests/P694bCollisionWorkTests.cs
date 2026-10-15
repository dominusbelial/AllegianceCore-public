using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P694bCollisionWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dense_and_sparse_candidates_keep_filtered_brute_force_order(bool dense)
    {
        var random = new Random(694);
        var colliders = Enumerable.Range(0, 320).Select(i => new Collider(i,
            new Vector3(i % 17 == 0 ? i * 100 : dense ? (float)random.NextDouble() : i * 30,
                (float)random.NextDouble(), (float)random.NextDouble()),
            new Vector3(i % 7, 0, 0), new Orientation(), null, 5, i % 3 == 0)).ToArray();
        foreach (var collider in colliders) collider.SetStep(0, .05f);
        // Asymmetric predicates must always receive original registration order.
        static bool Filter(Collider a, Collider b) => (int)a.Data! % 3 == 0 && (int)b.Data! % 2 == 0;
        var expected = BroadPhase.OverlappingPairsBruteForce(colliders)
            .Where(p => Filter(colliders[p.I], colliders[p.J])).ToArray();
        var actual = BroadPhase.OverlappingPairs(colliders, Filter).ToArray();
        Assert.Equal(expected, actual);
        if (dense) Assert.True(actual.Length > 5000);
        else Assert.Empty(actual);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(257)]
    public void Repeated_geometry_keeps_all_identities_with_direct_and_sparse_result_caches(int groups)
    {
        var detector = new CollisionDetector();
        for (int i = 0; i < groups * 2; i++)
            detector.Add(new Collider(i, new Vector3(i % groups * .001f, 0, 0),
                Vector3.Zero, new Orientation(), null, 5, false));
        const float dt = .05f;
        foreach (var collider in detector.Colliders) collider.SetStep(0, dt);
        static bool Filter(Collider a, Collider b) => a.Ordinal % 3 == 0 || b.Ordinal % 2 == 0;
        var expected = new CollisionQueue();
        int pairs = 0;
        foreach (var (i, j) in BroadPhase.OverlappingPairsBruteForce(detector.Colliders))
        {
            var a = detector.Colliders[i]; var b = detector.Colliders[j];
            if (!Filter(a, b)) continue;
            pairs++;
            var hit = NarrowPhase.TestPair(a, b, dt);
            if (hit.Found) expected.Add(new CollisionEvent(a, b, hit.TCollision, hit.HtsA, hit.HtsB));
        }
        expected.Sort();
        foreach (int threads in new[] { 0, 1, 2, 4, 8 })
        {
            var actual = detector.Detect(dt, threads != 0, Math.Max(1, threads), Filter);
            Assert.Equal(expected.Ordered(), actual);
            Assert.True(detector.GeometricPairTests < pairs);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ordinal_event_sort_matches_general_comparator_for_common_and_mixed_times(bool commonTime)
    {
        var general = new CollisionQueue();
        var ordinal = new CollisionQueue();
        var a = new Collider(0, Vector3.Zero, Vector3.Zero, new Orientation(), null, 1, false);
        float[] times = [float.NaN, -0f, 0f, .01f, .02f, float.PositiveInfinity];
        for (int i = 1; i < 600; i++)
        {
            var b = new Collider(i, Vector3.Zero, Vector3.Zero, new Orientation(), null, 1, false) { Ordinal = i };
            var ev = new CollisionEvent(a, b, commonTime ? .01f : times[i % times.Length], -1, -1);
            general.Add(ev); ordinal.Add(ev);
        }
        general.Sort(); ordinal.SortOrdinalEvents();
        Assert.Equal(general.Ordered(), ordinal.Ordered());
    }
}

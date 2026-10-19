using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P694CollisionGeometryTests
{
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Perf")] // Expensive corpus/scenario or collision-work budget; docs/testing/TIERS.md.
    [SkippableFact]
    public void Shared_geometry_matches_uncached_narrow_phase_in_both_paths_and_after_mutation()
    {
        Require.Artwork();
        var hulls = new[] { "fig07", "faohshbmb", "ss24a" }.Select(name =>
            CvhReader.Parse(File.ReadAllText(Path.Combine(TestArtRoot.Value, name + ".cvh")))).ToArray();
        var detector = new CollisionDetector();
        var rng = new Random(694);
        for (int i = 0; i < 100; i++)
        {
            // Repeated geometry has DIFFERENT identity, mixed registration order,
            // true multi-hulls, sphere ordnance, static bodies, ties and misses.
            int group = i % 10;
            var orientation = new Orientation();
            if (group % 3 == 0) orientation.Set(new Vector3(1, 1, 2));
            detector.Add(new Collider(i, new Vector3(group * 3, group % 4, 0),
                new Vector3(group % 2 * 40, 0, -10), orientation,
                group < 3 ? null : hulls[group % 3], group < 3 ? 1 : 30, group == 9));
        }
        Check(expectReuse: true);
        // Cache lifetime is one call, not tied to mutable Collider/Orientation
        // identities. Shape/radius and time-step changes must not reuse old work.
        foreach (var c in detector.Colliders)
        {
            c.Position += new Vector3((float)rng.NextDouble() * 50, 0, 0);
            c.Velocity += new Vector3(0, (float)rng.NextDouble() * 20, 0);
            c.Orientation.Set(new Vector3(1, (float)rng.NextDouble(), 1));
            if (c.Ordinal % 4 == 0) c.Shape = HitShape.Ellipse;
        }
        Check(expectReuse: false);

        void Check(bool expectReuse)
        {
            const float dt = .049f;
            var expected = new CollisionQueue();
            foreach (var c in detector.Colliders) c.SetStep(0, dt);
            static bool Filter(Collider a, Collider b) => a.Ordinal % 3 != 1 || b.Ordinal % 2 == 0;
            var pairs = BroadPhase.OverlappingPairsBruteForce(detector.Colliders)
                .Where(p => Filter(detector.Colliders[p.I], detector.Colliders[p.J])).ToArray();
            foreach (var (i, j) in pairs)
            {
                var a = detector.Colliders[i]; var b = detector.Colliders[j];
                var ev = NarrowPhase.TestPair(a, b, dt);
                if (ev.Found) expected.Add(new CollisionEvent(a, b, ev.TCollision, ev.HtsA, ev.HtsB));
            }
            expected.Sort();
            Assert.NotEmpty(expected.Ordered());
            foreach (int threads in new[] { 0, 1, 2, 4, 8 })
            {
                var actual = detector.Detect(dt, threads != 0, Math.Max(1, threads), Filter);
                Assert.Equal(expected.Ordered(), actual);
                Assert.Equal(expectReuse, detector.GeometricPairTests < pairs.Length);
            }
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cached_separation_probes_are_bit_identical_to_original_solver_including_cache_saturation()
    {
        Require.Artwork();
        var hulls = new[] { "fig07", "faohshbmb", "ss24a" }.Select(name =>
            CvhReader.Parse(File.ReadAllText(Path.Combine(TestArtRoot.Value, name + ".cvh")))).ToArray();
        var rng = new Random(694);
        var cache = new Separate.ProbeCache();
        for (int i = 0; i < 1500; i++)
        {
            var orientation = new Orientation();
            // Repeated attitude pairs first, then enough unique pairs to fill
            // the bounded cache and exercise uncached fallback.
            orientation.Set(new Vector3(1, i < 500 ? 1 : (float)rng.NextDouble(), 2));
            var a = new Collider(i, new Vector3((float)rng.NextDouble() * 20, 3, 2),
                new Vector3(1, 2, 3), orientation, hulls[i % 3], 20 + i % 4, false);
            var b = new Collider(-i, new Vector3(0, 1, -2), new Vector3(4, 0, 1),
                new Orientation(), hulls[(i + 1) % 3], 30, false);
            int htsA = i % 5 == 0 ? HitShape.Sphere : i % 5 == 1 ? HitShape.Ellipse : 0;
            int htsB = i % 7 == 0 ? HitShape.Point : 0;
            var expected = Separate.SeparateColliders(a, b, .01f, true, htsA, htsB);
            var actual = Separate.SeparateColliders(a, b, .01f, true, htsA, htsB, cache);
            static int[] Bits(Separate.Result r) => new[] { r.Position1, r.Position2, r.Normal }
                .SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(BitConverter.SingleToInt32Bits).ToArray();
            Assert.Equal(Bits(expected), Bits(actual));
        }
    }

    [Fact]
    public void Signed_zero_is_not_merged_and_default_detection_keeps_every_original_pair()
    {
        var detector = new CollisionDetector();
        foreach (float x in new[] { 0f, -0f, 0f, -0f })
            detector.Add(new Collider(new object(), new Vector3(x, 0, 0), Vector3.Zero,
                new Orientation(), null, 5, false));
        var events = detector.Detect(.05f);
        Assert.Equal(6, events.Count);
        Assert.Equal(4, detector.GeometricPairTests); // (+,+), (+,-), (-,+), (-,-)
        Assert.Equal(6, events.Select(e => (e.A.Ordinal, e.B.Ordinal)).Distinct().Count());
    }
}

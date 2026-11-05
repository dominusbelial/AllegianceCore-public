using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P928CollisionScratchTests
{
    [Fact]
    public void Reused_frames_match_fresh_detector_through_size_and_geometry_changes()
    {
        var reused = new CollisionDetector();
        foreach (int count in new[] { 48, 3, 0, 1, 90, 24, 2, 70 })
        foreach (bool repeated in new[] { true, false })
        {
            using var frame = reused.BeginFrame();
            var fresh = new CollisionDetector();
            for (int i = 0; i < count; i++)
            {
                var p = new Vector3((repeated ? i % 3 : i) * .2f, i % 2, 0);
                var v = new Vector3(i % 2, 0, 0);
                var c = frame.Detector.CreateCollider(i, p, v, null, null, 2, false);
                frame.Detector.Add(c);
                fresh.Add(new Collider(i, p, v, Orientation.Identity, null, 2, false));
            }
            foreach (int threads in new[] { 1, 2, 1, 4 })
            {
                static bool Filter(Collider a, Collider b) => a.Ordinal % 3 == 0 || b.Ordinal % 2 == 0;
                var expected = fresh.Detect(.053f, threads > 1, threads, Filter).Select(Key).ToArray();
                var actual = frame.Detector.Detect(.053f, threads > 1, threads, Filter).Select(Key).ToArray();
                Assert.Equal(expected, actual);
                Assert.Equal(fresh.GeometricPairTests, frame.Detector.GeometricPairTests);
            }
        }
        Assert.Empty(reused.Colliders);
    }

    [SkippableFact]
    public void Reused_body_resets_hull_shape_basis_bounds_and_releases_references()
    {
        Require.Artwork();
        var hull = CvhReader.Parse(File.ReadAllText(Path.Combine(
            Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork), "fig01.cvh")));
        var detector = new CollisionDetector();
        Collider original;
        using (var frame = detector.BeginFrame())
        {
            original = detector.CreateCollider(new object(), new(1, 2, 3), new(4, 5, 6),
                Orientation.Identity, hull, 7, false);
            detector.Add(original);
            original.Shape = HitShape.Ellipse;
            original.SetStep(0, 9);
        }
        Assert.Null(original.Data);
        Assert.Null(original.Hull);
        using (var frame = detector.BeginFrame())
        {
            var current = detector.CreateCollider(42, new(20, 0, 0), Vector3.Zero, null, null, 3, true, HitShape.Point);
            detector.Add(current);
            Assert.Same(original, current);
            var expected = new Collider(42, new(20, 0, 0), Vector3.Zero, Orientation.Identity, null, 3, true, HitShape.Point);
            Assert.Equal(expected.Data, current.Data);
            Assert.Equal(expected.Ordinal, current.Ordinal);
            Assert.Equal(expected.Position, current.Position);
            Assert.Equal(expected.Velocity, current.Velocity);
            Assert.Equal(expected.Orientation.GetRight(), current.Orientation.GetRight());
            Assert.Equal(expected.Orientation.GetUp(), current.Orientation.GetUp());
            Assert.Equal(expected.Orientation.GetBackward(), current.Orientation.GetBackward());
            Assert.Equal(expected.IsStatic, current.IsStatic);
            Assert.Equal(expected.OriginalRadius, current.OriginalRadius);
            Assert.Equal(expected.DesiredRadius, current.DesiredRadius);
            Assert.Equal(expected.EffectiveRadius, current.EffectiveRadius);
            Assert.Equal(expected.Scale, current.Scale);
            Assert.Equal(expected.EllipseEquation, current.EllipseEquation);
            Assert.Equal(expected.TrueShape, current.TrueShape);
            Assert.Equal(expected.Shape, current.Shape);
            Assert.Equal(expected.BoundsMin, current.BoundsMin);
            Assert.Equal(expected.BoundsMax, current.BoundsMax);
            Assert.Equal(expected.StepStart, current.StepStart);
            Assert.Equal(expected.StepStop, current.StepStop);
            current.SetStep(0, .1f); expected.SetStep(0, .1f);
            Assert.Equal(expected.BoundsMin, current.BoundsMin);
            Assert.Equal(expected.BoundsMax, current.BoundsMax);
        }
    }

    [Fact]
    public void Nested_frames_and_independent_parallel_sectors_do_not_share_bodies()
    {
        var detector = new CollisionDetector();
        using var outer = detector.BeginFrame();
        var a = outer.Detector.CreateCollider("outer", Vector3.Zero, Vector3.Zero, null, null, 1, false);
        outer.Detector.Add(a);
        using (var inner = detector.BeginFrame())
        {
            Assert.NotSame(outer.Detector, inner.Detector);
            var b = inner.Detector.CreateCollider("inner", Vector3.Zero, Vector3.Zero, null, null, 1, true);
            inner.Detector.Add(b);
            Assert.NotSame(a, b);
        }
        Assert.Equal("outer", a.Data);
        Parallel.For(0, 4, worker =>
        {
            var local = new CollisionDetector();
            for (int tick = 0; tick < 20; tick++)
            {
                using var frame = local.BeginFrame();
                for (int i = 0; i < 10; i++)
                    local.Add(local.CreateCollider(worker, new(i, 0, 0), Vector3.Zero, null, null, 2, false));
                Assert.All(local.Detect(.053f), e => { Assert.Equal(worker, e.A.Data); Assert.Equal(worker, e.B.Data); });
            }
        });
    }

    [Fact]
    public void Failed_body_reset_releases_the_borrowed_slot_and_next_frame_is_clean()
    {
        var detector = new CollisionDetector();
        Collider first;
        using (var frame = detector.BeginFrame())
        {
            first = detector.CreateCollider("initial", Vector3.Zero, Vector3.Zero, null, null, 2, false);
            detector.Add(first);
            detector.Add(detector.CreateCollider("other", Vector3.Zero, Vector3.Zero, null, null, 2, false));
            Assert.NotEmpty(detector.Detect(.053f));
            Assert.True(detector.GeometricPairTests > 0);
        }
        var broken = new CvhFile
        {
            Radius = 1, EllipseEquation = new(1, 1, 1), EllipseRadiusMultiplier = 1, Frames = [],
            Hulls = [new CvhHull { Center = Vector3.Zero, VertexCount = 0, AdjacencyCount = 0, Vertices = [] }]
        };
        using (var frame = detector.BeginFrame())
        {
            Assert.Equal(0, detector.GeometricPairTests);
            Assert.Throws<IndexOutOfRangeException>(() => detector.CreateCollider("failed-reset",
                Vector3.Zero, Vector3.Zero, null, broken, 1, false));
        }
        Assert.Null(first.Data);
        Assert.Null(first.Hull);
        using (var frame = detector.BeginFrame())
        {
            var next = detector.CreateCollider("recovered", Vector3.Zero, Vector3.Zero, null, null, 3, true);
            detector.Add(next);
            Assert.Same(first, next);
            Assert.Equal("recovered", next.Data);
            Assert.Equal(3, next.EffectiveRadius);
            Assert.Empty(detector.Detect(.053f));
        }
    }

    private static (int, int, int, int, int) Key(Allegiance.Core.Collision.CollisionEvent e) =>
        (e.A.Ordinal, e.B.Ordinal, BitConverter.SingleToInt32Bits(e.TCollision), e.HtsA, e.HtsB);
}

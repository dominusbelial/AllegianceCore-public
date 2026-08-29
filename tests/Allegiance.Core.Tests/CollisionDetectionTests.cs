using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Collision detection against the reference C++ algorithm
/// (<c>src/_Utility/HitTest.cpp</c> etc.), exercised with <b>real corpus hulls</b>
/// from <c>CvhCorpus</c> — not synthetic boxes. The four questions of
/// COLLISION.md are answered there; these tests verify the port behaves.
/// </summary>
public class CollisionDetectionTests
{
    private static readonly Lazy<CvhFile> Fig01 = new(() => Load("fig01.cvh"));
    private static readonly Lazy<CvhFile> Dgsup = new(() => Load("dgsup.cvh"));   // big single-hull "station/superheavy"
    private static readonly Lazy<CvhFile> P1Sup = new(() => Load("p1_exp.cvh")); // 6 sub-hulls (duplicate of p1_sup/p1_tac); exercises sub-hull iteration

    private static CvhFile Load(string name)
    {
        var path = CvhCorpus.Files.FirstOrDefault(f => Path.GetFileName(f) == name)
            ?? throw new InvalidOperationException($"corpus lacks {name}");
        return CvhReader.Parse(File.ReadAllText(path));
    }

    private static Collider Sphere(object? tag, Vector3 pos, Vector3 vel, float radius, bool isStatic = false) =>
        new(tag, pos, vel, Orientation.Identity, null, radius, isStatic, HitShape.Sphere);

    private static Collider Hull(object? tag, CvhFile hull, Vector3 pos, Vector3 vel, Orientation? orient = null, bool isStatic = false) =>
        new(tag, pos, vel, orient ?? Orientation.Identity, hull, hull.Radius, isStatic, HitShape.Sphere);

    // ── head-on timing (analytic) ───────────────────────────────────────────

    [Fact]
    public void HeadOn_Spheres_CollideAtAnalyticTime()
    {
        // Two spheres at (±100,0,0), each closing at 10 units/s along x.
        // Surface gap = 2·100 − (5+5) = 190; closing speed = 20 → t = 9.5 s.
        var a = Sphere("A", new Vector3(-100f, 0f, 0f), new Vector3(10f, 0f, 0f), 5f);
        var b = Sphere("B", new Vector3(100f, 0f, 0f), new Vector3(-10f, 0f, 0f), 5f);

        var d = new CollisionDetector();
        d.Add(a); d.Add(b);
        var ev = d.Detect(dt: 20f);

        Assert.Single(ev);
        Assert.Equal(9.5f, ev[0].TCollision, 2);   // sphere-sweep is analytic; tolerance 1e-2
    }

    [Fact]
    public void HeadOn_RealHulls_CollideAtPredictableTime()
    {
        // Two identical fig01 fighters head-on. fig01's leading vertex along the
        // approach axis is |x|=1.936683 (fig01.cvh). Centers start at ±20; closing
        // speed = 2·10 = 20. First contact when center separation = 2·1.936683,
        // i.e. t = (40 − 3.873366)/20 = 1.80633 s. The hull refines the sphere
        // seed by bisection to within maxDeltaT = 0.25/20 = 0.0125 s, so 0.02 tol.
        var a = Hull("A", Fig01.Value, new Vector3(-20f, 0f, 0f), new Vector3(10f, 0f, 0f));
        var b = Hull("B", Fig01.Value, new Vector3(20f, 0f, 0f), new Vector3(-10f, 0f, 0f));

        var d = new CollisionDetector();
        d.Add(a); d.Add(b);
        var ev = d.Detect(dt: 10f);

        Assert.Single(ev);
        Assert.Equal(1.80633f, ev[0].TCollision, 0.025f);
        Assert.Equal(HitShape.ConvexHullMin, ev[0].HtsA);
        Assert.Equal(HitShape.ConvexHullMin, ev[0].HtsB);
    }

    // ── non-colliding courses ──────────────────────────────────────────────

    [Fact]
    public void ParallelCourses_NeverCollide()
    {
        // Identical fig01 fighters flying parallel +x, separated in z by 8 >
        // hull z-extent sum (2·2.644). No contact ever.
        var a = Hull("A", Fig01.Value, new Vector3(0f, 0f, -4f), new Vector3(20f, 0f, 0f));
        var b = Hull("B", Fig01.Value, new Vector3(0f, 0f, 4f), new Vector3(20f, 0f, 0f));

        var d = new CollisionDetector();
        d.Add(a); d.Add(b);
        Assert.Empty(d.Detect(dt: 10f));
    }

    [Fact]
    public void CrossingSpheres_NearMiss_NeverCollide()
    {
        // Two spheres cross perpendicularly; the closest approach of their
        // rectilinear paths is 12.7 > r1+r2 = 10 (dP0×v_rel/|v_rel| with
        // dP0=(-41,59,0), v_rel=(50,-50,0) → 50·18/70.7). Their swept AABBs DO
        // overlap (broad yields a candidate), but the narrow-phase closest
        // approach has no real root (halfB<0, c>0 → not approaching/not touching),
        // so it is rejected — proving the narrow phase discriminates.
        var a = Sphere("A", new Vector3(-50f, 9f, 0f), new Vector3(50f, 0f, 0f), 5f);
        var b = Sphere("B", new Vector3(-9f, -50f, 0f), new Vector3(0f, 50f, 0f), 5f);

        var d = new CollisionDetector();
        d.Add(a); d.Add(b);
        var ev = d.Detect(dt: 10f);

        Assert.Empty(ev);
        Assert.True(d.CandidatePairs > 0, "broad phase must yield a candidate for the narrow phase to reject");
    }

    // ── real hull geometry ─────────────────────────────────────────────────

    [Fact]
    public void ShipIntoStationHull_Collides()
    {
        // fig01 fighter flies +x into a static dgsup hull. Sphere seed predicts
        // contact at ~7.96 s (radii 48.95+2.88), hull refinement locates the true
        // surface contact; both inside the 10 s step.
        var station = Hull("station", Dgsup.Value, Vector3.Zero, Vector3.Zero, isStatic: true);
        var ship = Hull("ship", Fig01.Value, new Vector3(-450f, 0f, 0f), new Vector3(50f, 0f, 0f));

        var d = new CollisionDetector();
        d.Add(ship); d.Add(station);
        var ev = d.Detect(dt: 10f);

        var hit = Assert.Single(ev);
        Assert.InRange(hit.TCollision, 0f, 10f);
        Assert.Same(ship, hit.A);
        Assert.Same(station, hit.B);
    }

    [Fact]
    public void MultiHullCollider_ExercisesSubHulls()
    {
        // p1_sup has 6 convex sub-hulls; a fig01 fighter flies into it. The
        // narrow phase iterates sub-hull combinations (HullCollide). Found.
        var station = Hull("p1_sup", P1Sup.Value, Vector3.Zero, Vector3.Zero, isStatic: true);
        var ship = Hull("ship", Fig01.Value, new Vector3(-300f, 0f, 0f), new Vector3(50f, 0f, 0f));

        var d = new CollisionDetector();
        d.Add(ship); d.Add(station);
        var ev = d.Detect(dt: 10f);

        var hit = Assert.Single(ev);
        Assert.InRange(hit.TCollision, 0f, 10f);
        Assert.InRange(hit.HtsA, HitShape.ConvexHullMin, P1Sup.Value.Hulls.Count - 1);
    }

    // ── determinism & ordering ─────────────────────────────────────────────

    [Fact]
    public void Determinism_IdenticalSetup_YieldsIdenticalListTwice()
    {
        CollisionEvent[] Run()
        {
            var d = new CollisionDetector();
            d.Add(Sphere("A", new Vector3(-80f, 0f, 5f), new Vector3(20f, 0f, 0f), 5f));
            d.Add(Sphere("B", new Vector3(80f, 0f, -5f), new Vector3(-20f, 0f, 0f), 5f));
            d.Add(Sphere("C", new Vector3(0f, 0f, 200f), new Vector3(0f, 0f, -10f), 5f));
            return d.Detect(dt: 10f).ToArray();
        }

        var first = Run();
        var second = Run();
        Assert.Equal(first.Length, second.Length);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i].TCollision, second[i].TCollision);
            Assert.Same(first[i].A, second[i].A);
            Assert.Same(first[i].B, second[i].B);
        }
    }

    [Fact]
    public void Queue_IsSortedAscendingByCollisionTime()
    {
        // Multiple pairs colliding at clearly distinct times.
        var d = new CollisionDetector();
        d.Add(Sphere("a", new Vector3(-50f, 0f, 0f), new Vector3(20f, 0f, 0f), 5f));   // early
        d.Add(Sphere("b", new Vector3(50f, 0f, 0f), new Vector3(-20f, 0f, 0f), 5f));
        d.Add(Sphere("c", new Vector3(-500f, 0f, 0f), new Vector3(40f, 0f, 0f), 5f));  // later
        d.Add(Sphere("e", new Vector3(500f, 0f, 0f), new Vector3(-40f, 0f, 0f), 5f));

        var ev = d.Detect(dt: 20f);

        Assert.True(ev.Count >= 2);
        for (int i = 1; i < ev.Count; i++)
            Assert.True(ev[i].TCollision >= ev[i - 1].TCollision);
        Assert.True(ev[0].TCollision < ev[^1].TCollision);   // genuinely different times
    }

    // ── Model integration ─────────────────────────────────────────────────

    private sealed class TestModel : Sim.Model
    {
        public TestModel(short id, Vector3 pos) : base(id, pos) { }
        protected override void OnUpdate(double now, double dt) { }
    }

    [Fact]
    public void Model_GainsOptionalHull_AndCreatesCollider()
    {
        var m = new TestModel(1, new Vector3(0f, 0f, 0f)) { Hull = Fig01.Value };
        Assert.Null(new TestModel(2, Vector3.Zero).CreateCollider(Fig01.Value.Radius));   // no hull → null

        var c = m.CreateCollider(Fig01.Value.Radius);
        Assert.NotNull(c);
        Assert.Equal(Fig01.Value.Radius, c!.OriginalRadius);
        Assert.Equal(1f, c.Scale, 3);
        Assert.Equal(1, c.TrueShape);          // 1 sub-hull → convex-hull index range
    }
}

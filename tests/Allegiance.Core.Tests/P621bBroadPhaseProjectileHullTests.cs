using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P621b — spatial broad phase + projectile/ordnance-vs-hull.
///
/// Guards for the two fixes this stage lands:
/// 1. <see cref="BroadPhase.OverlappingPairs"/> is now a sweep-and-prune
///    spatial broad phase (the port of native's per-axis-endpoint KD root,
///    <c>KDroot.cpp</c>/<c>KDnode.cpp</c>). It must yield <b>exactly the same
///    candidate set</b> — and therefore the same collision events — as the
///    O(n²) pairwise AABB sweep it replaces, deterministically.
/// 2. <see cref="Sector.ResolveCombatCollisions"/> now builds the ship /
///    asteroid / station colliders with their real <c>.cvh</c> hulls (via
///    <c>EnsureShipHull</c> + <see cref="Sector.CollisionHulls"/>), so ordnance
///    resolves against the target's real hull like native
///    (<c>modelIGC.cpp:105-118</c>, <c>projectileIGC.cpp:166-190</c>) — a shot
///    grazing a ship's empty bounding-sphere corner misses, one on the hull hits.
/// </summary>
public sealed class P621bBroadPhaseProjectileHullTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static CvhFile LoadCvh(string name)
        => CvhReader.Parse(File.ReadAllText(Path.Combine(ArtRoot, name + ".cvh")));

    /// <summary>The hull's maximum absolute extent along <paramref name="axis"/>
    /// (0=x,1=y,2=z) over every sub-hull vertex, times
    /// <c>scale = radius/originalRadius</c> — the game-space half-width.</summary>
    private static float MaxAbsExtent(CvhFile cvh, float scale, int axis)
    {
        float m = 0f;
        foreach (var hull in cvh.Hulls)
            foreach (var v in hull.Vertices)
            {
                float c = axis switch { 0 => v.Position.X, 1 => v.Position.Y, _ => v.Position.Z };
                m = MathF.Max(m, MathF.Abs(c));
            }
        return m * scale;
    }

    private static Collider Sphere(object? tag, Vector3 pos, Vector3 vel, float radius, bool isStatic = false)
        => new(tag, pos, vel, Orientation.Identity, null, radius, isStatic, HitShape.Sphere);

    private static Collider Hull(object? tag, CvhFile hull, Vector3 pos, Vector3 vel, Orientation? orient = null, bool isStatic = false, float? radius = null)
        => new(tag, pos, vel, orient ?? Orientation.Identity, hull, radius ?? hull.Radius, isStatic, HitShape.Sphere);

    // ══ 1. BROAD-PHASE CANDIDATE-SET EQUIVALENCE ═══════════════════════════
    /// <summary>
    /// The spatial broad phase (<see cref="BroadPhase.OverlappingPairs"/>) must
    /// produce the <b>same candidate pair set</b> as the O(n²) pairwise AABB
    /// sweep, for random populations (fixed seeds) of mixed sphere + hull
    /// colliders. Because the candidate set is what feeds the narrow phase, the
    /// collision EVENT lists must also be identical. Deterministic by
    /// construction — the test compares the full sorted pair streams.
    /// </summary>
    [Theory]
    [InlineData(12345)]
    [InlineData(54321)]
    [InlineData(99991)]
    [InlineData(20261012)]
    public void BroadPhase_candidate_set_equals_O2n_sweep(int seed)
    {
        var rng = new Random(seed);
        var cvh = TryLoad("fig07");
        foreach (int count in new[] { 2, 5, 50, 200 })
        {
            for (int trial = 0; trial < 5; trial++)
            {
                var det = new CollisionDetector();
                for (int i = 0; i < count; i++)
                {
                    bool hullBody = cvh is not null && rng.Next(2) == 0;
                    det.Add(new Collider(
                        i,
                        Rand(rng, 2000f),
                        Rand(rng, 60f),
                        Orientation.Identity,
                        hullBody ? cvh : null,
                        desiredRadius: 5f + 30f * (float)rng.NextDouble(),
                        isStatic: rng.Next(2) == 0,
                        HitShape.Sphere));
                }
                det.Detect(dt: 0.033f);   // populates swept AABBs

                var sap = BroadPhase.OverlappingPairs(det.Colliders).ToList();
                var brute = BroadPhase.OverlappingPairsBruteForce(det.Colliders).ToList();

                Assert.Equal(brute.Count, sap.Count);
                for (int k = 0; k < sap.Count; k++)
                {
                    Assert.Equal(brute[k].I, sap[k].I);
                    Assert.Equal(brute[k].J, sap[k].J);
                }
            }
        }
    }

    private static CvhFile? TryLoad(string name)
    {
        var p = Path.Combine(ArtRoot, name + ".cvh");
        return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
    }

    private static Vector3 Rand(Random rng, float span)
        => new((float)(rng.NextDouble() * 2 - 1) * span, (float)(rng.NextDouble() * 2 - 1) * span, (float)(rng.NextDouble() * 2 - 1) * span);

    // ══ 2. PROJECTILE-vs-SHIP-HULL (detector level, failing-first) ═══════════
    /// <summary>
    /// An ordnance round (a small, point-like bounding sphere) resolves against
    /// the target's REAL hull: a shot travelling through the empty part of the
    /// ship's bounding sphere (outside the hull's thin lateral extent) does NOT
    /// collide, while one aimed into the hull does. With the pre-P621b sphere
    /// collider the empty-corner shot DID collide — the failing-first contrast.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Projectile_hits_real_hull_but_passes_through_empty_bounding_sphere()
    {
        Require.Artwork();
        var cvh = LoadCvh("fig07");
        const float shipRadius = 50f;                      // 0.5 × fighter Length
        float scale = shipRadius / cvh.Radius;
        float xHalf = MaxAbsExtent(cvh, scale, 0);
        float yHalf = MaxAbsExtent(cvh, scale, 1);
        float zHalf = MaxAbsExtent(cvh, scale, 2);

        // The empty-corner window: lateral altitude h between the hull's thin
        // extent and the bounding sphere radius (travel along X, thin axis = Y or Z).
        int lateralAxis = yHalf <= zHalf ? 1 : 2;
        float latHalf = MathF.Min(yHalf, zHalf);
        float h = (latHalf + shipRadius) / 2f;
        Assert.True(h + 2f < shipRadius, $"empty-corner altitude {h} must stay inside the sphere (radius {shipRadius})");
        Assert.True(h - 2f > latHalf, $"empty-corner altitude {h} must clear the hull's thin extent {latHalf:0.#}");

        // Ordnance = tiny sphere (round radius 2), travelling along X.
        var ordEmpty = Sphere("round", OffsetStart(lateralAxis, h, xHalf), new Vector3(80f, 0f, 0f), 2f);
        var shipHull = Hull("ship-hull", cvh, Vector3.Zero, Vector3.Zero, isStatic: true, radius: shipRadius);
        var shipSphere = Sphere("ship-sphere", Vector3.Zero, Vector3.Zero, shipRadius, isStatic: true);

        var dHull = new CollisionDetector(); dHull.Add(ordEmpty); dHull.Add(shipHull);
        var dSphere = new CollisionDetector(); dSphere.Add(ordEmpty); dSphere.Add(shipSphere);

        // FAILING-FIRST: with the ship as a sphere the empty-corner round hits;
        // with the real hull it passes through (no collision).
        Assert.True(dSphere.Detect(dt: 10f).Count > 0, "pre-condition: the empty-corner round hits a spherical ship");
        Assert.Empty(dHull.Detect(dt: 10f));

        // Into the hull: the same round aimed at Y=0 (hull) DOES collide.
        var ordInto = Sphere("round-into", new Vector3(-(xHalf + 200f), 0f, 0f), new Vector3(80f, 0f, 0f), 2f);
        var dInto = new CollisionDetector(); dInto.Add(ordInto); dInto.Add(shipHull);
        Assert.NotEmpty(dInto.Detect(dt: 10f));

        Console.WriteLine($"P621b-PROJ-HULL[fig07]: radius {shipRadius}, xHalf {xHalf:0.#}, yHalf {yHalf:0.#}, zHalf {zHalf:0.#} — "
                          + $"empty corner at {lateralAxis}-axis altitude {h:0.#} (extent {latHalf:0.#}): hull=MISS, sphere=HIT; into-hull=HIT.");
    }

    private static Vector3 OffsetStart(int axis, float h, float xHalf)
        => axis == 1 ? new Vector3(-(xHalf + 200f), h, 0f) : new Vector3(-(xHalf + 200f), 0f, h);

    // ══ 3. COMBAT PASS POPULATES THE SHIP HULL HOOK (resolver level) ════════
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static Mission NewStartedMission(IIgcSite site)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> specs = [];
        for (int i = 0; i < civs.Count; i++)
            specs = specs.Append(new SideSpec((short)i, $"Team {i + 1}", civs[i].CivilizationId)).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, site);
        m.Stage = MissionStage.Started;
        return m;
    }

    private static CollisionHullCache ArtHulls() => new(name =>
    {
        var p = Path.Combine(ArtRoot, name + ".cvh");
        return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
    });

    private static (Vector3 Spot, Sector Sector) FarSpot(Mission m, float margin = 4500f)
    {
        var dirs = new[] { new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0),
                           new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1) };
        foreach (var sector in m.Sectors)
        {
            var occupied = new List<Vector3>();
            foreach (var a in sector.Mineable)
                if (a.Data.Position is { Length: 3 } p) occupied.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var w in sector.Warps)
                if (w.Warp.Position is { Length: 3 } p) occupied.Add(new Vector3(p[0], p[1], p[2]));
            occupied.Add(Vector3.Zero);
            Vector3 anchor = occupied.Count == 0 ? Vector3.Zero
                : new Vector3(occupied.Average(p => p.X), occupied.Average(p => p.Y), occupied.Average(p => p.Z));
            foreach (var d in dirs)
                foreach (var dist in new[] { 8500f, 12000f, 16000f })
                {
                    var cand = anchor + d * dist;
                    if (occupied.All(p => (p - cand).Length() > margin)) return (cand, sector);
                }
        }
        throw new InvalidOperationException("corpus map too crowded to place a clean synthetic ship");
    }

    private static void Pump(Mission m, int n, double step = 0.03125)
    {
        for (int i = 0; i < n; i++) m.Update(m.LastUpdate + step);
    }

    /// <summary>
    /// The combat pass (<see cref="Sector.ResolveCombatCollisions"/>) must
    /// resolve the ship's real hull from the sector <see cref="CollisionHulls"/>
    /// cache and populate the <see cref="Model.Hull"/> hook (<c>EnsureShipHull</c>).
    /// Before P621b the hook stayed null and the ship's ordnance collider was a
    /// bounding sphere; the failing-first guard is that the hook is populated
    /// once a combat pass runs against a cached ship model.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Combat_pass_populates_the_ship_hull_hook()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        foreach (var sec in mission.Sectors) sec.CollisionHulls = ArtHulls();

        var (spot, sector) = FarSpot(mission);
        var hullType = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9400, hullType, spot, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        // A parked ordnance in the sector forces ResolveCombatCollisions to run.
        var pt = mission.Core.ProjectileTypes.Values.First();
        var round = new Projectile(9500, sector, pt, spot + new Vector3(3000f, 0, 0), new Vector3(-200f, 0, 0), new Vector3(-1, 0, 0), mission.LastUpdate, ship);
        sector.Add(round);

        Pump(mission, 3);
        Assert.NotNull(ship.Hull);   // EnsureShipHull populated the hook from CollisionHulls

        // The resolved hull must be a real parse (a sub-hull count > 0), not a
        // sentinel — i.e. collidable, exactly as the physics passes use it.
        Assert.True(ship.Hull!.Hulls.Count > 0, $"model {hullType.ModelName} resolved a hull with sub-hulls");
        Console.WriteLine($"P621b-COMBAT-HOOK: ship model {hullType.ModelName} → {ship.Hull.Hulls.Count} sub-hull(s) populated.");
    }
}

using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P621 — convex-hull collisions. These are the failing-first guards for the
/// fix that makes the ship↔ship / ship↔asteroid / ship↔station collision passes
/// collide against each body's real <c>.cvh</c> hull (<c>modelIGC.cpp:LoadCVH</c>
/// → <c>HitTest::Create</c>) instead of a bounding sphere, and bounce off the
/// convex-hull contact normal (<c>shipIGC.cpp:Separate</c>) instead of a radial
/// centre line.
/// </summary>
/// <remarks>
/// <para><b>Why the tests fail before P621.</b> Before P621 the resolver passes
/// built every collider as <c>HitShape.Sphere</c> (<c>hull: null</c>); the
/// <c>CollisionHulls</c> cache did not exist, so even when this test attaches
/// one the pass ignored it and a ship near an elongated rock bounced off the
/// rock's empty bounding-sphere corner. Each test therefore expects the HULL
/// outcome (no collision through empty sphere space; a real bounce on hull
/// contact) and fails on the old sphere-only code.</para>
/// <para><b>Geometry is computed from the real <c>.cvh</c></b> (the artwork
/// corpus, e.g. <c>bgrnd03.cvh</c>): the test derives the hull's actual per-axis
/// extent from its parsed vertices scaled to the game radius, then places the
/// ship just outside the hull on its thinnest axis yet inside the enclosing
/// sphere — the "empty corner" a sphere wrongly collides with. This is the
/// native-fidelity golden case (<c>docs/P621_*</c>).</para>
/// </remarks>
public sealed class P621ConvexHullCollisionTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    // ── same harness as ShipAsteroidCollisionTests (real static core + map) ──
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

    /// <summary>A <see cref="CollisionHullCache"/> whose loader reads
    /// <c>&lt;artwork&gt;/&lt;name&gt;.cvh</c> — the production loader shape
    /// (Server.Lifecycle.cs). Each distinct model name is parsed once.</summary>
    private static CollisionHullCache ArtHulls() => new(name =>
    {
        var p = Path.Combine(ArtRoot, name + ".cvh");
        return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
    });

    private static CvhFile LoadCvh(string name)
        => CvhReader.Parse(File.ReadAllText(Path.Combine(ArtRoot, name + ".cvh")));

    /// <summary>The hull's maximum absolute extent along <paramref name="axis"/>
    /// (0=x,1=y,2=z) over every vertex of every sub-hull, times
    /// <c>scale = radius/originalRadius</c> — the game-space half-width the
    /// collider actually presents.</summary>
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

    private static DataHullTypeIGC FirstHull(Mission m)
        => m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

    // ══ 1. COLLISION-HULL CACHE (unit) ═══════════════════════════════════════
    /// <summary>The <see cref="CollisionHullCache"/> resolves a model once,
    /// caches the parsed file, and returns null for a model with no .cvh
    /// (native's HitTest::Create sphere fallback).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cache_resolves_once_and_falls_back_null()
    {
        Require.Artwork();
        int loads = 0;
        var cache = new CollisionHullCache(name =>
        {
            loads++;
            var p = Path.Combine(ArtRoot, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });

        var first = cache.Resolve("bgrnd03");
        var second = cache.Resolve("bgrnd03");
        Assert.NotNull(first);
        Assert.Same(first, second);          // cached (same instance)
        Assert.Equal(1, loads);              // parsed exactly once
        Assert.Equal(1, cache.Count);

        var missing = cache.Resolve("no_such_model");
        Assert.Null(missing);                // sphere fallback
        Assert.Equal(2, loads);              // the miss is cached too (native caches failures)
    }

    // ══ 2. GOLDEN GEOMETRY — elongated asteroid hulls vs their spheres ═══════
    /// <summary>⭐ Golden cases computed from real asteroid <c>.cvh</c> models
    /// (multiple <c>bgrnd*</c> rocks). For each rock, a small body travelling
    /// through its enclosing bounding sphere on the THIN (lateral) axis — the
    /// empty corner the sphere covers — collides when the body is a sphere but
    /// does NOT collide when it is the real hull. This is the geometry behind
    /// Domingo's "ejecting because of asteroids out of nowhere".</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Elongated_asteroid_hulls_let_a_ship_pass_through_empty_sphere_corners()
    {
        Require.Artwork();
        const float shipRadius = 5f;
        const float radius = 150f;
        string[] models = ["bgrnd03", "bgrnd51", "nbgrnd03", "drgrock"];
        int passed = 0;

        foreach (var name in models)
        {
            var cvh = LoadCvh(name);
            float scale = radius / cvh.Radius;
            float xHalf = MaxAbsExtent(cvh, scale, 0);
            float yHalf = MaxAbsExtent(cvh, scale, 1);
            float zHalf = MaxAbsExtent(cvh, scale, 2);

            // Travel along X; the empty corner is the thinner lateral axis (Y or Z).
            int lateralAxis = yHalf <= zHalf ? 1 : 2;
            float latHalf = MathF.Min(yHalf, zHalf);
            float h = (latHalf + radius) / 2f;
            if (!(h + shipRadius < radius && h - shipRadius > latHalf))
            {
                Console.WriteLine($"P621-GOLDEN-ASTEROID[{name}]: skip — no empty-corner window (xHalf {xHalf:0.#}, yHalf {yHalf:0.#}, zHalf {zHalf:0.#})");
                continue;
            }

            var ship = Sphere("ship", new Vector3(-400f, 0f, 0f) + withLateral(lateralAxis, h), new Vector3(100f, 0f, 0f), shipRadius);
            var rockHull = Hull(name, cvh, Vector3.Zero, Vector3.Zero, isStatic: true, radius: radius);
            var rockSphere = Sphere($"{name}-sphere", Vector3.Zero, Vector3.Zero, radius, isStatic: true);

            var dHull = new CollisionDetector(); dHull.Add(ship); dHull.Add(rockHull);
            var dSphere = new CollisionDetector(); dSphere.Add(ship); dSphere.Add(rockSphere);

            if (dHull.Detect(dt: 10f).Count == 0 && dSphere.Detect(dt: 10f).Count > 0)
                passed++;

            Console.WriteLine($"P621-GOLDEN-ASTEROID[{name}]: radius {radius}, xHalf {xHalf:0.#}, yHalf {yHalf:0.#}, zHalf {zHalf:0.#} — "
                              + $"altitude {h:0.#} on {lateralAxis}-axis (extent {latHalf:0.#}): hull=NO collision, sphere=collides.");
        }

        Assert.True(passed >= 2, $"at least 2 real asteroid models must show the empty-corner hull-vs-sphere distinction (got {passed})");
    }

    private static Vector3 withLateral(int axis, float h) => axis == 1 ? new Vector3(0f, h, 0f) : new Vector3(0f, 0f, h);

    // ══ 3. GOLDEN GEOMETRY — station hull vs its sphere ══════════════════════
    /// <summary>⭐ Golden case for a station model (<c>dgsup.cvh</c>): the same
    /// empty-corner geometry — a body inside the station's bounding sphere but
    /// outside its actual hull does not collide with the hull, but does with the
    /// sphere.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Station_hull_lets_a_ship_pass_through_empty_sphere_corner()
    {
        Require.Artwork();
        var cvh = LoadCvh("dgsup");
        float radius = cvh.Radius;           // at file scale (dgsup is already "big")
        float scale = radius / cvh.Radius;   // 1.0

        // Pick the thinner LATERAL axis (Y or Z) as the empty corner; travel along X.
        float[] lateralHalves = [MaxAbsExtent(cvh, scale, 1), MaxAbsExtent(cvh, scale, 2)];
        int thinAxis = 1 + Array.IndexOf(lateralHalves, lateralHalves.Min());

        const float shipRadius = 5f;
        float h = (lateralHalves.Min() + radius) / 2f;
        Assert.True(h + shipRadius < radius, $"altitude {h} must stay inside the sphere (radius {radius})");
        Assert.True(h - shipRadius > lateralHalves.Min(), $"altitude {h} must clear the hull's {thinAxis}-axis extent ({lateralHalves.Min():0.#})");

        // Travel parallel to X at lateral altitude h.
        var ship = Sphere("ship", AltPosX(-900f, thinAxis, h), new Vector3(120f, 0f, 0f), shipRadius);
        var stationHull = Hull("dgsup", cvh, Vector3.Zero, Vector3.Zero, isStatic: true, radius: radius);
        var stationSphere = Sphere("dgsup-sphere", Vector3.Zero, Vector3.Zero, radius, isStatic: true);

        var dHull = new CollisionDetector(); dHull.Add(ship); dHull.Add(stationHull);
        var dSphere = new CollisionDetector(); dSphere.Add(ship); dSphere.Add(stationSphere);

        Assert.Empty(dHull.Detect(dt: 10f));
        Assert.NotEmpty(dSphere.Detect(dt: 10f));

        Console.WriteLine($"P621-GOLDEN-STATION: dgsup (radius {radius:0.#}) — ship at {thinAxis}-axis altitude {h:0.#} (extent {lateralHalves.Min():0.#}) "
                          + $"passes through the empty corner: hull=NO collision, sphere=collides.");
    }

    /// <summary>A travel-along-X start position with lateral altitude <paramref name="h"/>
    /// applied on the chosen axis (1 = Y, 2 = Z).</summary>
    private static Vector3 AltPosX(float xTravel, int axis, float h) => axis switch
    {
        1 => new Vector3(xTravel, h, 0f),
        _ => new Vector3(xTravel, 0f, h),
    };

    // ══ 4. RESOLVER INTEGRATION — ship↔asteroid empty corner vs hull touch ══
    private static DataAsteroidIGC MkBgrndRoid(short id, Vector3 pos, float oreMax, float hitPoints, short radius, short sectorId, string model)
    {
        return new DataAsteroidIGC(
            Signature: 1.0f, Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: oreMax, OreMax: oreMax, AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: id, HitPoints: hitPoints, Radius: radius, ModelName: model,
            TextureName: "", IconName: "", ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);
    }

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
        throw new InvalidOperationException("corpus map too crowded to place a clean synthetic asteroid");
    }

    private static void Pump(Mission m, int n, double step = 0.0625)
    {
        for (int i = 0; i < n; i++) m.Update(m.LastUpdate + step);
    }

    /// <summary>⭐ The real ship↔asteroid resolver (Sector.ResolveShipAsteroidCollisions),
    /// driven through <see cref="Sector.Update"/> with a real
    /// <see cref="CollisionHulls"/> cache attached. Flying an elongated bgrnd03
    /// rock on its THIN axis inside the bounding sphere passes through with no
    /// bounce (hull); the old sphere-only code bounced it (the failing-first
    /// guard). A second run flying INTO the hull at the same speed does bounce,
    /// proving touching the hull still collides.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Resolver_bounces_off_asteroid_hull_but_not_through_empty_sphere_corner()
    {
        Require.Artwork();
        var cvh = LoadCvh("bgrnd03");
        float radius = 150f;
        float scale = radius / cvh.Radius;
        float yHalf = MaxAbsExtent(cvh, scale, 1);
        float xHalf = MaxAbsExtent(cvh, scale, 0);

        // Run the resolver twice: once threading the empty Y corner, once hitting the hull.
        (bool Bounced, bool Damaged, bool Transited) Run(float yOff, Vector3 velocity)
        {
            var recording = new RecordingIgcSite();
            var mission = NewStartedMission(recording);
            foreach (var sec in mission.Sectors) sec.CollisionHulls = ArtHulls();
            var (spot, sector) = FarSpot(mission);
            var rock = new Asteroid(MkBgrndRoid(8300, spot, oreMax: 99999f, hitPoints: 5000f, radius: (short)radius, sectorId: sector.Id, model: "bgrnd03"));
            mission.PlaceAsteroid(rock, sector);
            var hull = FirstHull(mission);
            var ship = new Ship(9300, hull, spot + new Vector3(-280f, yOff, 0f), velocity, Orientation.Identity);
            ship.StateM = ShipControlState.CoastButton;   // ballistic
            mission.PlaceShip(ship, sector);
            float preFrac = ship.Fraction;
            Vector3 preVel = ship.Velocity;
            // Enough ticks to transit the rock's X extent at |vx|.
            int ticks = (int)((2 * xHalf + 300f) / MathF.Abs(velocity.X) / 0.015625f) + 80;
            Pump(mission, ticks, 0.015625);
            bool bounced = ship.Velocity.X > preVel.X + 0.001f;   // reflected outward past inbound
            bool damaged = ship.Fraction < preFrac - 0.002f;
            bool transited = ship.Position.X > spot.X + xHalf;    // reached the far side of the rock
            return (bounced, damaged, transited);
        }

        // Empty corner: altitude midway between the hull's Y extent and the sphere radius.
        float h = (yHalf + radius) / 2f;
        var corner = Run(h, new Vector3(120f, 0f, 0f));
        Assert.False(corner.Bounced, $"ship threading the empty Y corner ({h:0.#} > yHalf {yHalf:0.#}) must NOT bounce (convex hull)");
        Assert.False(corner.Damaged, $"ship threading the empty Y corner must take NO collision damage (hull)");
        Assert.True(corner.Transited, $"ship threading the empty Y corner must fly THROUGH the rock to its far side (final X {corner.Bounced} past rock far edge {xHalf:0.#})");

        // Into the hull: same speed, straight through the elongated X at Y=0.
        var into = Run(0f, new Vector3(120f, 0f, 0f));
        Assert.True(into.Bounced || into.Damaged, $"a ship flying INTO the hull at Y=0 must collide (bounce or damage)");

        Console.WriteLine($"P621-RESOLVER-ASTEROID: bgrnd03 (radius {radius}, yHalf {yHalf:0.#}) — altitude {h:0.#} "
                          + $"corner: bounced={corner.Bounced}, damaged={corner.Damaged}, transited={corner.Transited}; "
                          + $"Y=0 into-hull: bounced={into.Bounced}, damaged={into.Damaged}.");
    }

    // ══ helpers (detector-level) ════════════════════════════════════════════
    private static Collider Sphere(object? tag, Vector3 pos, Vector3 vel, float radius, bool isStatic = false)
        => new(tag, pos, vel, Orientation.Identity, null, radius, isStatic, HitShape.Sphere);

    private static Collider Hull(object? tag, CvhFile hull, Vector3 pos, Vector3 vel, Orientation? orient = null, bool isStatic = false, float? radius = null)
        => new(tag, pos, vel, orient ?? Orientation.Identity, hull, radius ?? hull.Radius, isStatic, HitShape.Sphere);
}

using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P777b — missiles vs the aleph cone. Native rule 1: a missile is a sphere of
/// <c>dataMissileType-&gt;radius</c> (<c>Igc/missileigc.cpp:50-51,64-66</c>) and it
/// hits the <b>same cone as a ship</b> (<c>Igc/warpIGC.cpp:33-35</c> with
/// <c>Igc/clusterIGC.cpp:266-285,526-530</c>, <c>Igc/modelIGC.cpp:127-130</c>,
/// <c>_Utility/HitTest.cpp:197-201</c>). Native rule 2: a
/// <c>c_eabmWarpBomb</c> missile arming the warp
/// (<c>missileigc.cpp:459-467</c>). Native rule 3: <b>any other armed missile
/// that touches an aleph dies there</b> and explodes at the contact point
/// (<c>missileigc.cpp:452,468-498,512,516-530</c>;
/// <c>FedSrv.CPP:12512-12529</c>).
/// </summary>
/// <remarks>
/// <para>Two halves. The <b>pure</b> half pins the new exact swept test
/// (<see cref="AlephConeHitTest.SweptFirstContact"/>) — hits, misses, the
/// first-contact time, and a 20,000-case differential run against a 4,000-sample
/// brute force plus the P776 sampler (which must remain a subset). Those tests
/// use the new P777b API and therefore cannot run on the unfixed base.</para>
/// <para>The <b>domain</b> half runs a real started mission (the
/// <see cref="WarpBombTests"/> fixtures) and pins the native rules end to end:
/// the trigger is the swept cone (a warp bomb in front of the disc does not arm,
/// one crossing the disc between two ticks does), and a plain missile reaching
/// the aleph is consumed, explodes at the contact point and is broadcast through
/// <see cref="IIgcSite.MissileDestroyedEvent"/>.</para>
/// </remarks>
[Collection("LiveNetwork")]
public sealed class P777bMissileAlephConeTests
{
    // ── pure: the exact swept sphere-vs-cone test ───────────────────────────

    private static readonly Vector3 Forward = new(0f, 0f, -1f);   // backward = (0,0,1)

    /// <summary>The cone used by the pure tests: centre at the origin, radius 100.</summary>
    private static AlephConeHitTest.Geometry Cone(float radius = 100f)
        => AlephConeHitTest.Geometry.From(Vector3.Zero, Forward, radius);

    /// <summary>A world point from the native local frame: <paramref name="z"/>
    /// along <b>backward</b> (the cone's axis of symmetry, apex at
    /// <c>z = R</c>), <paramref name="rho"/> perpendicular to it. The disc plane
    /// is <c>z = 0</c>; "in front of the disc" is <c>z &lt; 0</c>.</summary>
    private static Vector3 Local(float rho, float z) => new(rho, 0f, z);

    /// <summary>
    /// ⭐ P777b test 1 — the exact swept test with <c>R = 100</c>, <c>r = 2</c>.
    /// Each case is the native answer: nothing of the solid is in front of the
    /// disc, the solid is the triangle <c>(0,0),(R,0),(0,R)</c>, and contact is
    /// "distance to that triangle ≤ r" along the whole segment.
    /// New API: this test cannot run on the unfixed base.
    /// </summary>
    [Fact]
    public void Swept_first_contact_matches_the_cone_geometry()
    {
        var g = Cone();
        const float r = 2f;

        // (a) 90 u in front of the disc → 30 u in front, on the axis: never
        // touches (the front of the disc is empty space).
        Assert.False(AlephConeHitTest.SweptFirstContact(g, Local(0f, -90f), Local(0f, -30f), r, out _));

        // (b) crossing the disc at rho = 0.95R with a 40 u step. The sphere
        // reaches the disc's front face — the solid's nearest point for a point
        // in front of the disc inside the rim is the rim point itself, so the
        // first contact is where z = -r, i.e. t = 0.5 − r/40 (the disc plane is
        // crossed at t = 0.5; the sphere is r ahead of its centre).
        {
            var start = Local(0.95f * 100f, -20f);
            var end = Local(0.95f * 100f, 20f);
            Assert.True(AlephConeHitTest.SweptFirstContact(g, start, end, r, out float t));
            Assert.Equal(0.5f - r / 40f, t, 1e-3f);
        }

        // (c) from behind the apex (z = 150) on the axis: to z = 120 is a miss
        // (nearest point is the apex at z = 100, 20 u away > r); to z = 90 hits
        // at the apex, i.e. z = R + r.
        Assert.False(AlephConeHitTest.SweptFirstContact(g, Local(0f, 150f), Local(0f, 120f), r, out _));
        {
            var start = Local(0f, 150f);
            var end = Local(0f, 90f);
            Assert.True(AlephConeHitTest.SweptFirstContact(g, start, end, r, out float t));
            Assert.Equal((150f - (100f + r)) / 60f, t, 1e-3f);          // first contact at z = R + r
            var contact = start + (end - start) * t;
            Assert.Equal(100f + r, contact.Z, 1e-2f);                     // the apex, plus the sphere radius
        }

        // (d) parallel to the disc, 0.01 outside the sphere radius in front of
        // it: a miss; 0.01 inside: a hit at t = 0 (the front face is the solid's
        // boundary, so the sphere already overlaps at the start of the tick).
        Assert.False(AlephConeHitTest.SweptFirstContact(g, Local(0f, -r - 0.01f), Local(50f, -r - 0.01f), r, out _));
        Assert.True(AlephConeHitTest.SweptFirstContact(g, Local(0f, -r + 0.01f), Local(50f, -r + 0.01f), r, out float t0));
        Assert.Equal(0f, t0);

        // (e) the broad-phase reject boundary: a segment that stays more than
        // R + r from the centre never touches, whatever its direction.
        Assert.False(AlephConeHitTest.SweptFirstContact(g, Local(102.5f, -102.5f), Local(102.5f, 102.5f), r, out _));
    }

    /// <summary>
    /// ⭐ P777b test 1 (last case) — a zero-length segment is the point test, i.e.
    /// exactly <see cref="AlephConeHitTest.Touches"/>; <c>t</c> is 0.
    /// New API: cannot run on the unfixed base.
    /// </summary>
    [Fact]
    public void Zero_length_segment_equals_touches()
    {
        var g = Cone();
        var rng = new Random(777);
        int hits = 0, misses = 0;
        for (int i = 0; i < 2000; i++)
        {
            float r = 0.5f + (float)rng.NextDouble() * 29.5f;
            var p = Local((float)(rng.NextDouble() * 2.4 - 0.2) * 100f,
                          (float)(rng.NextDouble() * 2.4 - 0.6) * 100f);
            bool swept = AlephConeHitTest.SweptFirstContact(g, p, p, r, out float t);
            bool touches = AlephConeHitTest.Touches(Vector3.Zero, Forward, 100f, p, r);
            Assert.Equal(touches, swept);
            if (swept) { Assert.Equal(0f, t); hits++; } else misses++;
        }
        Assert.True(hits > 100 && misses > 100, $"the sample must cover both outcomes (hits={hits}, misses={misses})");
        Console.WriteLine($"P777b-PURE: zero-length sweep == Touches on 2000 points ({hits} hit, {misses} miss)");
    }

    /// <summary>
    /// ⭐ P777b test 2 — differential: 20,000 seeded random segments around the
    /// cone (mixed lengths 0-400 u, <c>r ∈ [0.5, 30]</c>, both a unit and a
    /// non-unit forward). The exact test must agree with a 4,000-sample brute
    /// force except where <c>|min dist − r| &lt; 1e-3</c> (the tolerance band —
    /// counted and printed), and it must be a <b>superset</b> of what the P776
    /// sampler found (that sampler capped at 16 samples with spacing
    /// <c>max(r, 1)</c>, so it can tunnel through the cone).
    /// New API: cannot run on the unfixed base.
    /// </summary>
    [Trait("Tier", "Perf")] // 20,000 × 4,000-sample differential: ~10 s in Debug, docs/testing/TIERS.md.
    [Fact]
    public void Exact_sweep_agrees_with_brute_force_and_dominates_the_p776_sampler()
    {
        var rng = new Random(777_002);
        // A non-unit forward is included: the wire vectors are not unit length.
        Vector3[] forwards = [new(0f, 0f, -1f), new(1f, -0.811f, 0.088f)];
        const int cases = 20_000;
        const int samples = 4_000;
        int hits = 0, boundary = 0, mismatches = 0, samplerHits = 0, samplerMisses = 0;

        for (int i = 0; i < cases; i++)
        {
            var forward = forwards[i % forwards.Length];
            var g = AlephConeHitTest.Geometry.From(Vector3.Zero, forward, 100f);
            var backward = AlephConeHitTest.Backward(forward);
            var side = Vector3.CrossProduct(backward, new Vector3(0f, 1f, 0f));
            if (side.LengthSquared() < 1e-6f) side = Vector3.CrossProduct(backward, new Vector3(1f, 0f, 0f));
            side = side.Normalize();

            float r = 0.5f + (float)rng.NextDouble() * 29.5f;
            var start = backward * ((float)(rng.NextDouble() * 3.0 - 1.5) * 150f)
                      + side * ((float)(rng.NextDouble() * 3.0 - 1.5) * 150f);
            var dir = RandomUnit(rng);
            float length = (float)rng.NextDouble() * 400f;
            var end = start + dir * length;

            // Brute force: the smallest exact distance over 4,000 samples,
            // spaced for the LOWEST r of the population (0.5), so the sampling
            // error cannot be blamed on the step size.
            float minDist = float.PositiveInfinity;
            for (int s = 0; s <= samples; s++)
                minDist = MathF.Min(minDist, AlephConeHitTest.Distance(g, start + (end - start) * (s / (float)samples)));

            bool exact = AlephConeHitTest.SweptFirstContact(g, start, end, r, out float t);
            if (exact)
            {
                hits++;
                Assert.InRange(t, 0f, 1f);
                // The reported first contact really is a contact, and nothing
                // before it is (sample the decreasing prefix).
                Assert.True(AlephConeHitTest.Distance(g, start + (end - start) * t) <= r + 1e-3f,
                    $"case {i}: reported contact t={t} is not within r");
            }

            bool brute = minDist <= r;
            if (exact != brute)
            {
                if (MathF.Abs(minDist - r) < 1e-3f) boundary++;   // the documented tolerance band
                else mismatches++;
            }

            // The P776 sampler, exactly as it ran for ships (16-sample cap,
            // spacing max(r, 1)), applied to the same segment and radius.
            int steps = Math.Clamp((int)MathF.Ceiling(length / MathF.Max(r, 1f)), 1, 16);
            bool sampled = false;
            for (int s = 0; s <= steps; s++)
                if (AlephConeHitTest.Touches(Vector3.Zero, forward, 100f,
                        start + (end - start) * (s / (float)steps), r)) { sampled = true; break; }
            if (sampled)
            {
                samplerHits++;
                Assert.True(exact, $"case {i}: the P776 sampler found a hit the exact sweep missed " +
                                   $"(r={r:F2}, length={length:F1}, start={start}, end={end})");
            }
            else if (exact) samplerMisses++;
        }

        Assert.Equal(0, mismatches);
        Assert.True(hits > 1000, $"the population must produce plenty of hits (got {hits})");
        Console.WriteLine($"P777b-DIFF: cases={cases} hits={hits} boundary-band={boundary} mismatches={mismatches} " +
                          $"p776-sampler-hits={samplerHits} sampler-missed-tunnelling={samplerMisses}");
    }

    private static Vector3 RandomUnit(Random rng)
    {
        while (true)
        {
            var v = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1),
                                (float)(rng.NextDouble() * 2 - 1));
            float len2 = v.LengthSquared();
            if (len2 > 1e-4f && len2 <= 1f) return v.Normalize();
        }
    }

    // ── domain harness (the WarpBombTests fixtures) ─────────────────────────

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private sealed class CountingSite : IIgcSite
    {
        public int WarpBombs { get; private set; }
        public int MissileDestroys { get; private set; }
        public void WarpBombEvent(IWarpIgc warp, IMissileIgc missile) => WarpBombs++;
        public void MissileDestroyedEvent(Missile m) => MissileDestroys++;
    }

    private static (Mission Mission, Sector Sector, CountingSite Site) Domain()
    {
        var core = RepresentativeCore();
        var site = new CountingSite();
        var mission = Mission.Assemble(core, SomeMap(),
            [new SideSpec(0, "Team", core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        return (mission, mission.Sectors[0], site);
    }

    private static Ship PlaceShip(Mission mission, Sector sector, DataHullTypeIGC hull, Vector3 pos, short side, ref short id)
    {
        var ship = new Ship(id++, hull, pos, Vector3.Zero, Orientation.Identity);
        if (side != 0) ship.SideId = side;
        mission.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>A warp whose cone is at <paramref name="pos"/> facing
    /// <c>(0,0,-1)</c> — so <b>backward is +Z</b>: the base disc is the
    /// <c>z = pos.Z</c> plane, the body spans <c>z ∈ [0, R]</c> behind it and
    /// nothing of the solid is at <c>z &lt; pos.Z</c> ("in front"). Its partner
    /// (like <see cref="WarpBombTests"/>' helper) sits 10,000 u away in +Z.</summary>
    private static ResolvedWarp AttachWarp(Sector sector, short warpId, Vector3 pos, short radius)
    {
        var warp = new ResolvedWarp(
            new DataWarpIGC(
                WarpId: warpId, DestinationId: (short)(warpId + 1), Radius: radius,
                TextureName: "", IconName: "", Name: "",
                Position: [pos.X, pos.Y, pos.Z],
                Forward: [0f, 0f, -1f],
                Rotation: [0f, 0f, 0f, 1f],
                Signature: 0f, ClusterId: -1),
            destination: sector);
        var partner = new ResolvedWarp(warp.Warp with
        {
            WarpId = (short)(warpId + 1), DestinationId = warpId,
            Position = [pos.X, pos.Y, pos.Z + 10000f]
        }, sector);
        sector.AttachWarp(warp);
        sector.AttachWarp(partner);
        return warp;
    }

    /// <summary>A warp-bomb-capable missile type with real Power and a wide blast.</summary>
    private static readonly Lazy<DataMissileTypeIGC> WarpBombMissile = new(() =>
    {
        var core = RepresentativeCore();
        var t = core.MissileTypes.Values
            .FirstOrDefault(m => m.HasWarpBombCapability && m.Power > 0f && m.BlastRadius >= 150f);
        if (t.ExpendableTypeId == 0)
            throw new InvalidOperationException("no warp-bomb-capable missile in representative core");
        return t with { EabmCapabilities = (short)(t.EabmCapabilities | 0x02) };
    });

    /// <summary>
    /// Native rule 3's <b>exploding</b> case (<c>missileigc.cpp:516-530</c>): a
    /// non-warp-bomb missile with <c>BlastPower ≠ 0</c>, plus a hull whose single
    /// full-power hit is survivable and measurable. NOTE: <c>PCore001.igc</c>
    /// ships <b>no</b> missile with a non-zero <c>blastPower</c> (all 40+ types are
    /// 0; only the warp-bomb types carry a blastRadius, used as a <c>Power</c>
    /// blast by <c>warpIGC.h:65-67</c>). The blast numbers here are assembled from
    /// real data — the missile's own <c>Power</c> as <c>BlastPower</c> (native's
    /// impact blast is the type's <c>blastPower</c>; the port reads the same field,
    /// Sector.ResolveAlephMissile) and the narrowest real blastRadius in the core
    /// — so the rule's <b>code path</b> is what is pinned, not invented damage.
    /// </summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, DataHullTypeIGC Hull)> PlainBlast = new(() =>
    {
        var core = RepresentativeCore();
        float radius = core.MissileTypes.Values.Where(m => m.BlastRadius > 0f).Min(m => m.BlastRadius);
        foreach (var m in core.MissileTypes.Values
            .Where(m => !m.HasWarpBombCapability && m.Power > 0f && m.Lifespan >= 2f)
            .OrderBy(m => m.Power))
        {
            var type = m with { BlastPower = m.Power, BlastRadius = radius };
            foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
            {
                float dtm = core.Constants.DamageConstants[type.DamageType][hull.DefenseType];
                if (dtm <= 0f) continue;
                float fullDrop = type.Power * dtm / hull.HitPoints;    // d ≤ 0 → full Amount
                if (fullDrop >= 0.08f && fullDrop <= 0.600f) return (type, hull);
            }
        }
        throw new InvalidOperationException("no plain blast missile / hull combo in representative core");
    });

    private static DataHullTypeIGC SmallHull()
        => RepresentativeCore().HullTypes.Values.OrderBy(h => h.HitPoints).First();
    private static void Tick(Mission m, int n, double dt = 0.05)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < n; i++) { t += dt; m.Update(t); }
    }

    // ── domain: the warp-bomb trigger is the swept cone ──────────────────────

    /// <summary>
    /// ⭐ P777b test 3 — a warp-bomb missile flying <b>parallel to the disc</b>,
    /// 90 u in front of it (inside the old <c>R + r</c> sphere, which reached
    /// ~100 u on BOTH sides of the disc), must arm <b>nothing</b> and survive:
    /// native's solid has no points in front of the disc
    /// (<c>_Utility/HitTest.cpp:254-283</c>; <c>Igc/modelIGC.cpp:127-130</c>), so
    /// the missile never collides with the warp. <b>Fails on the unfixed code</b>
    /// (the end-of-tick point-in-sphere test arms the bomb).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Warp_bomb_passing_90u_in_front_of_the_disc_does_not_trigger()
    {
        Require.Artwork();
        var missileType = WarpBombMissile.Value;
        var hull = SmallHull();
        var (mission, sector, site) = Domain();
        short id = 7100;
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 6000f), side: 1, ref id);

        short warpRad = 100;
        AttachWarp(sector, 7102, Vector3.Zero, warpRad);          // backward = +Z

        float r = missileType.Radius;
        float zFront = -90f;                                      // 90 u in front of the disc
        Assert.True(-zFront <= warpRad + r, "precondition: the probe sits inside the old R + r sphere");
        double now = mission.LastUpdate;
        var missile = new Missile(7103, sector, missileType, new Vector3(0f, 0f, zFront),
            new Vector3(10f, 0f, 0f), new Vector3(1f, 0f, 0f), now, launcher);
        sector.Add(missile);

        Tick(mission, 6);                                         // ±3 u of lateral drift

        Assert.Equal(0, site.WarpBombs);
        Assert.Contains(missile, sector.Models);
        Console.WriteLine($"P777b-FRONT: warp bomb at z={zFront} (R={warpRad}, r={r:F2}) armed " +
                          $"{site.WarpBombs} bomb(s) and survived={sector.Models.Contains(missile)}");
    }

    /// <summary>
    /// ⭐ P777b test 4 — a fast warp bomb crosses the disc at <c>rho = 0.9R</c>
    /// in ONE tick with both end-of-tick samples more than <c>R + r</c> from the
    /// centre, so only a continuous test can see it: native's
    /// <c>HullCollide</c> is continuous over the tick
    /// (<c>_Utility/HitTest.cpp:114-207</c>). It must arm <b>exactly one</b>
    /// bomb. <b>Fails on the unfixed code</b> (no sweep: neither sample is inside
    /// the sphere).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Warp_bomb_crossing_the_disc_triggers_in_one_tick()
    {
        Require.Artwork();
        var missileType = WarpBombMissile.Value;
        var hull = SmallHull();
        var (mission, sector, site) = Domain();
        short id = 7110;
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 6000f), side: 1, ref id);

        short warpRad = 100;
        AttachWarp(sector, 7112, Vector3.Zero, warpRad);          // backward = +Z, disc at z = 0

        float r = missileType.Radius;
        float rho = 0.9f * warpRad;
        float zSpan = 70f;                                        // 140 u of travel in one 0.05 s tick
        var start = new Vector3(rho, 0f, -zSpan);
        var end = new Vector3(rho, 0f, zSpan);
        Assert.True(start.Length() > warpRad + r, $"precondition: start {start} outside the old sphere");
        Assert.True(end.Length() > warpRad + r, $"precondition: end {end} outside the old sphere");

        double now = mission.LastUpdate;
        var missile = new Missile(7113, sector, missileType, start,
            new Vector3(0f, 0f, 2f * zSpan / 0.05f), new Vector3(0f, 0f, 1f), now, launcher);
        sector.Add(missile);

        mission.Update(now + 0.05);                               // one tick: start → end

        Assert.Equal(1, site.WarpBombs);
        Assert.Equal(1, site.MissileDestroys);
        Assert.DoesNotContain(missile, sector.Models);
        Console.WriteLine($"P777b-CROSS: rho={rho} start={start} end={end} R={warpRad} r={r:F2} → " +
                          $"{site.WarpBombs} bomb(s)");
    }

    /// <summary>
    /// ⭐ P777b test 5 (guard) — approaching from BEHIND along the backward axis
    /// from <c>z = 3R</c> arms the bomb when the sphere reaches the apex region.
    /// Passes on both the fixed and the unfixed code (the old sphere also fires),
    /// so it pins that the cone change did not lose the native approach.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Warp_bomb_from_behind_hits_the_cone()
    {
        Require.Artwork();
        var missileType = WarpBombMissile.Value;
        var hull = SmallHull();
        var (mission, sector, site) = Domain();
        short id = 7120;
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 6000f), side: 1, ref id);

        short warpRad = 100;
        AttachWarp(sector, 7122, Vector3.Zero, warpRad);

        double now = mission.LastUpdate;
        var missile = new Missile(7123, sector, missileType, new Vector3(0f, 0f, 3f * warpRad),
            new Vector3(0f, 0f, -600f), new Vector3(0f, 0f, -1f), now, launcher);
        sector.Add(missile);

        Tick(mission, 15);                                        // 300 → 102 u in 30 u steps
        Assert.Equal(1, site.WarpBombs);
        Assert.DoesNotContain(missile, sector.Models);
        Console.WriteLine($"P777b-BEHIND: armed {site.WarpBombs} bomb(s) approaching along the axis from z=3R");
    }

    // ── domain: native rule 3 — a plain missile dies on the aleph ────────────

    /// <summary>
    /// ⭐ P777b test 6 — a PLAIN (non-warp-bomb) missile with a real blast flying
    /// into the disc at <c>rho = 0.5R</c>:
    /// <list type="bullet">
    /// <item>it is removed and <see cref="IIgcSite.MissileDestroyedEvent"/> fires once;</item>
    /// <item>its last position is the <b>contact point</b> — on the disc's front
    /// face, <c>r</c> in front of the disc plane (native
    /// <c>position1 = position + velocity·tCollision</c>, <c>missileigc.cpp:452</c>);</item>
    /// <item>a cross-side ship near the contact point takes the blast damage,
    /// attributed to the launcher (a same-side hull at the same distance is
    /// spared by the friendly-fire gate, which needs the launcher to resolve);</item>
    /// <item>no warp bomb is armed.</item>
    /// </list>
    /// <b>Fails on the unfixed code</b> (the missile flew through untouched).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Plain_missile_reaching_the_aleph_dies_and_explodes_at_the_contact_point()
    {
        Require.Artwork();
        var (missileType, hull) = PlainBlast.Value;
        var (mission, sector, site) = Domain();
        short id = 7130;
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 9000f), side: 1, ref id);

        short warpRad = 100;
        AttachWarp(sector, 7132, Vector3.Zero, warpRad);          // backward = +Z, disc at z = 0

        float r = missileType.Radius;
        float rho = 0.5f * warpRad;
        float contactZ = -r;                                      // the sphere touches the disc's front face
        float shipRadius = 0.5f * hull.Length;
        // 32 u from the contact point, on the side the missile came from (in
        // front of the disc) and laterally clear of the missile's flight line.
        // 20 u straight "behind" the contact (into the cone) would be INSIDE the
        // solid for rho = 0.5R (rho + z = 50 + 20 < 100) — such a hull would warp
        // instead of taking the blast, natively too.
        var victimPos = new Vector3(rho + 25f, 0f, contactZ - 20f);
        float blastDistance = (victimPos - new Vector3(rho, 0f, contactZ)).Length();
        Assert.True(blastDistance < missileType.BlastRadius - shipRadius,
            $"precondition: victim {blastDistance:F1} u away must be inside the {missileType.BlastRadius} blast");
        Assert.False(AlephConeHitTest.Touches(Vector3.Zero, Forward, warpRad, victimPos, shipRadius),
            "precondition: the victim hull must be clear of the cone (or it would warp, natively too)");
        Assert.True(25f > missileType.Radius + shipRadius,
            "precondition: the victim is laterally clear of the missile, so the combat pass never eats it");
        var victim = PlaceShip(mission, sector, hull, victimPos, side: 2, ref id);
        var friendly = PlaceShip(mission, sector, hull, victimPos, side: 1, ref id);   // same side as the launcher

        double now = mission.LastUpdate;
        var missile = new Missile(7133, sector, missileType, new Vector3(rho, 0f, -200f),
            new Vector3(0f, 0f, 600f), new Vector3(0f, 0f, 1f), now, launcher);
        sector.Add(missile);
        Assert.Contains(missile, sector.Models);

        Tick(mission, 20);                                        // reaches z = 0 and dies at the contact

        Assert.DoesNotContain(missile, sector.Models);            // consumed
        Assert.Equal(1, site.MissileDestroys);                    // destroy broadcast, once
        Assert.Equal(0, site.WarpBombs);                          // a plain missile never arms a bomb
        Assert.Equal(rho, missile.Position.X, 1f);                // the contact point ...
        Assert.Equal(contactZ, missile.Position.Z, 1f);           // ... on the disc's front face

        float f = 1f - (blastDistance - shipRadius) / missileType.BlastRadius;
        float amount = missileType.BlastPower * f * f;
        float dtm = mission.Core.Constants.DamageConstants[missileType.DamageType][hull.DefenseType];
        float drop = amount * dtm / hull.HitPoints;
        Assert.Equal(1f - drop, victim.Fraction, 3);              // the blast landed, at the contact point
        Assert.Equal(1f, friendly.Fraction, 3);                   // same side as the launcher: gated out
        Console.WriteLine($"P777b-PLAIN: missile died at {missile.Position} (disc face z={contactZ:F2}, r={r:F2}); " +
                          $"victim {victim.Fraction:F3} (expect {1f - drop:F3}, d={blastDistance:F1}); " +
                          $"friendly {friendly.Fraction:F3}; bombs={site.WarpBombs}");
    }

    /// <summary>
    /// ⭐ P777b test 7 — <c>BlastPower = 0</c>: the same kill, but no explosion
    /// and no damage (native <c>Explode</c> only creates an explosion when
    /// <c>blastPower ≠ 0</c>, <c>missileigc.cpp:516-530</c>; the visual-only
    /// case). <b>Fails on the unfixed code</b> (the missile flew through).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Plain_missile_without_blast_dies_without_damage()
    {
        Require.Artwork();
        var (missileType, hull) = PlainBlast.Value;
        missileType = missileType with { BlastPower = 0f };
        var (mission, sector, site) = Domain();
        short id = 7140;
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 9000f), side: 1, ref id);

        short warpRad = 100;
        AttachWarp(sector, 7142, Vector3.Zero, warpRad);

        float r = missileType.Radius;
        float rho = 0.5f * warpRad;
        var victim = PlaceShip(mission, sector, hull, new Vector3(rho + 25f, 0f, -r - 20f), side: 2, ref id);
        float before = victim.Fraction;

        double now = mission.LastUpdate;
        var missile = new Missile(7143, sector, missileType, new Vector3(rho, 0f, -200f),
            new Vector3(0f, 0f, 600f), new Vector3(0f, 0f, 1f), now, launcher);
        sector.Add(missile);

        Tick(mission, 20);

        Assert.DoesNotContain(missile, sector.Models);            // still consumed
        Assert.Equal(1, site.MissileDestroys);
        Assert.Equal(0, site.WarpBombs);
        Assert.Equal(before, victim.Fraction, 5);                 // no blast, no damage
        Console.WriteLine($"P777b-NOBLAST: missile died with BlastPower = 0; victim {before:F3}→{victim.Fraction:F3}");
    }

    /// <summary>
    /// ⭐ P777b test 8 (guard) — a DUD missile ignores the aleph: native
    /// <c>if (!m_bDisarmed)</c> wraps the whole collision handler
    /// (<c>missileigc.cpp:427-428</c>; port <see cref="Missile.IsDud"/>), so a
    /// dud flown at the centre flies through. Passes on both.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Dud_missile_ignores_the_aleph()
    {
        Require.Artwork();
        var missileType = WarpBombMissile.Value;
        var hull = SmallHull();
        var (mission, sector, site) = Domain();
        short id = 7150;
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 9000f), side: 1, ref id);

        AttachWarp(sector, 7152, Vector3.Zero, 100);

        double now = mission.LastUpdate;
        var missile = new Missile(7153, sector, missileType, Vector3.Zero,
            new Vector3(0f, 0f, 600f), new Vector3(0f, 0f, 1f), now, launcher, dud: true);
        sector.Add(missile);

        Tick(mission, 8);                                         // 0 → 240 u, straight through the disc

        Assert.Contains(missile, sector.Models);
        Assert.Equal(0, site.WarpBombs);
        Assert.Equal(0, site.MissileDestroys);
        Console.WriteLine($"P777b-DUD: dud missile flew through the aleph to {missile.Position}, {site.WarpBombs} bomb(s)");
    }
}

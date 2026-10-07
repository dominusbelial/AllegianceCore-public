using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P463b/P569 shield-hit flare pool (<see cref="ShieldFlareParticles"/>)
/// and its curved-cap placement (<see cref="ShieldFlareCapMath"/>). The cap math
/// tests are the P569 failing-first proof: they assert the cap's WORLD position
/// for a hit direction + a (non-spherical) shield ellipse against an independent
/// transcription of native's <c>ThingGeo::AddFlare</c> math
/// (<c>effect/thinggeo.cpp:1243-1305</c>, <c>orientation.cpp:314-390</c>,
/// <c>matrix.cpp:57-72</c>) — the exact values produced by a faithful C++
/// transcription of those routines. The pool tests exercise the snapshot model,
/// the native 0.75&#8209;s lifetime/fade, and the at-most-four-per-ship cap
/// (<c>trekigc.cpp:1626 GetFlareCount() &lt; 4</c>). Never touches a graphics device.
/// </summary>
public class ShieldFlareParticlesTests
{
    private const short TARGET = 707;
    private static readonly SimVec ShipCenter = new(1000f, 500f, -200f);
    private const float RADIUS = 50f;

    // ── ShieldFlareCapMath — pure native cap placement (P569 failing-first) ──

    /// <summary>Independent transcription of the native AddFlare placement for a
    /// single unit-cap vertex — the reference the implementation is checked
    /// against (thinggeo.cpp:1252-1289 + orientation.cpp + matrix.cpp).</summary>
    private static SimVec NativePlacement(SimVec capLocal, Orientation shipOrient,
        SimVec center, SimVec hitWorld, SimVec ellipse, float R, float mr)
    {
        var vecLocal = shipOrient.TimesInverse(hitWorld - center);
        var vector = new SimVec(
            vecLocal.X / ellipse.X, vecLocal.Y / ellipse.Y, vecLocal.Z / ellipse.Z);
        var co = new Orientation();
        co.Set(vector);
        co.Scale(new SimVec(ellipse.X / R, ellipse.Y / R, ellipse.Z / R));
        var r = co.GetRight();
        var u = co.GetUp();
        var b = co.GetBackward();
        var m = new SimVec(
            mr * (capLocal.X * r.X + capLocal.Y * u.X + capLocal.Z * b.X),
            mr * (capLocal.X * r.Y + capLocal.Y * u.Y + capLocal.Z * b.Y),
            mr * (capLocal.X * r.Z + capLocal.Y * u.Z + capLocal.Z * b.Z));
        var d = shipOrient.Times(m);
        return new SimVec(center.X + d.X, center.Y + d.Y, center.Z + d.Z);
    }

    private static void AssertNear(SimVec actual, SimVec expected, float tol)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
        Assert.Equal(expected.Z, actual.Z, tol);
    }

    /// <summary>Identity ship, hit along +X on an oblate (10,6,12) ellipse: the
    /// cap apex lands 14 (= meshRadius) units opposite the hit (thinggeo.cpp:1289).</summary>
    [Fact]
    public void CapMath_front_hit_apex_lands_opposite_the_hit()
    {
        var orient = new Orientation();
        var apex = ShieldFlareCapMath.TransformCapPosition(
            new SimVec(0, 0, 1), orient, new SimVec(100, 50, -20), new SimVec(110, 50, -20),
            new SimVec(10, 6, 12), 10f, 14f);
        AssertNear(apex, new SimVec(86.0000000f, 50.0000000f, -20.0000000f), 1e-3f);
        // The dome points opposite the outward hit normal (cap +Z → orient
        // backward under Matrix(orient,0,mr), matrix.cpp:68-70).
        var dirToApex = apex - new SimVec(100, 50, -20);
        Assert.True(dirToApex.X * 1f < 0f, "dome should oppose the +X hit direction");
    }

    /// <summary>Identity ship, diagonal hit (normalized (1,1,0) scaled by the
    /// ellipse) — proves the cap follows the ELLIPSOID, not a sphere.</summary>
    [Fact]
    public void CapMath_oblique_hit_follows_the_ellipsoid()
    {
        var orient = new Orientation();
        float sq = MathF.Sqrt(2f);
        var hit = new SimVec(100f + 10f / sq, 50f + 6f / sq, -20f);
        var apex = ShieldFlareCapMath.TransformCapPosition(
            new SimVec(0, 0, 1), orient, new SimVec(100, 50, -20), hit,
            new SimVec(10, 6, 12), 10f, 14f);
        AssertNear(apex, new SimVec(90.1005051f, 44.0603030f, -20.0000000f), 1e-3f);
    }

    /// <summary>Identity ship, hit along the Z axis (ellipse z extent 12) — the
    /// apex distance reflects the mesh radius scaled by E_z/R.</summary>
    [Fact]
    public void CapMath_z_hit_scales_by_the_ellipse_axis()
    {
        var orient = new Orientation();
        var apex = ShieldFlareCapMath.TransformCapPosition(
            new SimVec(0, 0, 1), orient, new SimVec(100, 50, -20), new SimVec(100, 50, -8),
            new SimVec(10, 6, 12), 10f, 14f);
        AssertNear(apex, new SimVec(100.0000000f, 50.0000000f, -36.8000000f), 1e-3f);
    }

    /// <summary>Rotated ship (forward = +X): the cap is placed in the SHIP's
    /// local frame (vecLocal = orientation.TimesInverse(hit), thinggeo.cpp:1256),
    /// so a rotated hull bends the cap with it.</summary>
    [Fact]
    public void CapMath_rotated_ship_places_in_local_frame()
    {
        var orient = new Orientation();
        orient.Set(new SimVec(1, 0, 0));          // forward = +X (like native Set)
        var apex = ShieldFlareCapMath.TransformCapPosition(
            new SimVec(0, 0, 1), orient, new SimVec(100, 50, -20), new SimVec(110, 50, -20),
            new SimVec(10, 6, 12), 10f, 14f);
        AssertNear(apex, new SimVec(83.2000000f, 50.0000000f, -20.0000000f), 1e-3f);

        var ring = ShieldFlareCapMath.TransformCapPosition(
            new SimVec(0.5f, 0f, 0.8660254037844386f), orient, new SimVec(100, 50, -20), new SimVec(110, 50, -20),
            new SimVec(10, 6, 12), 10f, 14f);
        AssertNear(ring, new SimVec(85.4507732f, 57.0000000f, -20.0000000f), 1e-3f);
    }

    /// <summary>Spherical shield (ellipse = radius): the cap degenerates to a
    /// sphere cap, apex exactly one mesh radius out — the P545 approximation
    /// preserved under the native formula.</summary>
    [Fact]
    public void CapMath_sphere_ellipse_places_apex_at_mesh_radius()
    {
        var orient = new Orientation();
        var apex = ShieldFlareCapMath.TransformCapPosition(
            new SimVec(0, 0, 1), orient, new SimVec(100, 50, -20), new SimVec(110, 50, -20),
            new SimVec(10, 10, 10), 10f, 10f);
        AssertNear(apex, new SimVec(90.0000000f, 50.0000000f, -20.0000000f), 1e-3f);
    }

    /// <summary>Whole-mesh transform reproduces the per-vertex transform
    /// (the renderer's bake path uses the array method).</summary>
    [Fact]
    public void CapMath_array_transform_matches_per_vertex()
    {
        var inP = new float[] { 0f, 0f, 1f, 0.5f, 0f, 0.8660254037844386f };
        var outP = new float[6];
        float sq = MathF.Sqrt(2f);
        var hit = new SimVec(100f + 10f / sq, 50f + 6f / sq, -20f);
        ShieldFlareCapMath.TransformCapMesh(inP, outP, new Orientation(),
            new SimVec(100, 50, -20), hit, new SimVec(10, 6, 12), 10f, 14f);
        for (int i = 0; i < 3; i++)
            AssertNear(new SimVec(outP[0], outP[1], outP[2]),
                ShieldFlareCapMath.TransformCapPosition(
                    new SimVec(inP[0], inP[1], inP[2]), new Orientation(),
                    new SimVec(100, 50, -20), hit, new SimVec(10, 6, 12), 10f, 14f), 1e-4f);
    }

    /// <summary>Cross-check a handful of placements against the independent
    /// native transcription (the strongest form of the failing-first proof:
    /// implementation == native formula, not a fixed snapshot).</summary>
    [Theory]
    [InlineData(0f, 0f, 1f, 100f, 50f, -20f, 110f, 50f, -20f, 10f, 6f, 12f, 10f, 14f)]
    [InlineData(0f, 1f, 0f, 100f, 50f, -20f, 100f, 56f, -20f, 10f, 6f, 12f, 10f, 14f)]
    [InlineData(0.083044f, 0f, 0.996545f, 100f, 50f, -20f, 100f + 10f / 1.41421356f, 50f + 6f / 1.41421356f, -20f, 10f, 6f, 12f, 10f, 14f)]
    public void CapMath_matches_independent_native_transcription(
        float cx, float cy, float cz, float sx, float sy, float sz,
        float hx, float hy, float hz, float ex, float ey, float ez, float R, float mr)
    {
        var cap = new SimVec(cx, cy, cz);
        var center = new SimVec(sx, sy, sz);
        var hit = new SimVec(hx, hy, hz);
        var ell = new SimVec(ex, ey, ez);
        var orient = new Orientation();
        var actual = ShieldFlareCapMath.TransformCapPosition(cap, orient, center, hit, ell, R, mr);
        var expected = NativePlacement(cap, orient, center, hit, ell, R, mr);
        AssertNear(actual, expected, 1e-3f);
    }

    // ── Pool mechanics ─────────────────────────────────────────────────────

    private static ShieldFlareView EmitOne(ShieldFlareParticles pool, SimVec dir,
        Orientation? orient = null, float radius = RADIUS, SimVec? ellipse = null,
        float meshRadius = 0f)
    {
        var o = orient ?? new Orientation();
        var e = ellipse ?? new SimVec(radius, radius, radius);
        var mr = meshRadius > 0f ? meshRadius : radius;
        pool.Emit(TARGET, ShipCenter, o, ShipCenter + dir.Normalize() * radius, e, radius, mr);
        // Enumeration is oldest-first; .Last() = the newest (the one just emitted).
        return pool.Enumerate().Last();
    }

    [Fact]
    public void Emit_snapshots_the_native_inputs_and_outward_direction()
    {
        var pool = new ShieldFlareParticles();
        var orient = new Orientation();
        orient.Yaw(0.4f);
        var v = EmitOne(pool, new SimVec(1, 0, 0), orient, 50f, new SimVec(40, 30, 45), 60f);

        Assert.Equal(ShipCenter, v.Center);
        Assert.Same(orient, v.ShipOrientation);
        Assert.Equal(ShipCenter + new SimVec(50, 0, 0), v.HitWorldPos);
        Assert.Equal(new SimVec(40, 30, 45), v.Ellipse);
        Assert.Equal(50f, v.Radius, 4);
        Assert.Equal(60f, v.MeshRadius, 4);
        Assert.Equal(1f, v.Direction.X, 4);   // normalized outward
        Assert.Equal(0f, v.Direction.Y, 4);
        Assert.Equal(0f, v.Direction.Z, 4);
        Assert.Equal(1f, v.RemainingFrac, 4);
    }

    [Fact]
    public void Emit_normalizes_a_non_unit_hit_direction()
    {
        var pool = new ShieldFlareParticles();
        var v = EmitOne(pool, new SimVec(3f, 0f, 0f));
        Assert.Equal(1f, v.Direction.X, 4);
        Assert.Equal(0f, v.Direction.Y, 4);
        Assert.Equal(0f, v.Direction.Z, 4);
    }

    [Fact]
    public void Emit_handles_a_zero_hit_direction_with_positive_X_fallback()
    {
        var pool = new ShieldFlareParticles();
        var v = EmitOne(pool, SimVec.Zero);
        Assert.Equal(1f, v.Direction.X, 4);
        Assert.Equal(0f, v.Direction.Y, 4);
        Assert.Equal(0f, v.Direction.Z, 4);
    }

    [Fact]
    public void Emit_sanitizes_invalid_ellipse_axes_to_the_radius()
    {
        var pool = new ShieldFlareParticles();
        var v = EmitOne(pool, new SimVec(0, 1, 0), ellipse: new SimVec(0f, 30f, -5f));
        // zero / negative axes degrade to the sphere radius so the cap math
        // never divides by zero and stays usable.
        Assert.Equal(RADIUS, v.Ellipse.X, 4);
        Assert.Equal(30f, v.Ellipse.Y, 4);
        Assert.Equal(RADIUS, v.Ellipse.Z, 4);
    }

    [Fact]
    public void Flare_fades_linearly_over_its_native_lifetime()
    {
        var pool = new ShieldFlareParticles();
        EmitOne(pool, new SimVec(0, 1, 0));
        pool.Update(ShieldFlareParticles.LifetimeSeconds * 0.5f);
        Assert.Equal(0.5f, Assert.Single(pool.Enumerate()).RemainingFrac, 4);
    }

    [Fact]
    public void Flares_expire_after_the_native_point_seven_five_second_lifetime()
    {
        var pool = new ShieldFlareParticles();
        EmitOne(pool, new SimVec(0, 1, 0));
        Assert.Equal(1, pool.ActiveCount);
        for (int i = 0; i < 100 && pool.ActiveCount > 0; i++)
            pool.Update(0.052f);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void At_most_four_flares_per_ship_matches_native_cap()
    {
        var pool = new ShieldFlareParticles();
        for (int i = 0; i < 6; i++)
            EmitOne(pool, new SimVec(0, 1, 0), radius: 10f + 10f * i);
        Assert.Equal(ShieldFlareParticles.MaxFlaresPerShip, pool.ActiveCount);
        // Retained are the four newest (radii 30,40,50,60).
        var radii = pool.Enumerate().Select(v => v.Radius).OrderBy(r => r).ToArray();
        Assert.Equal(new[] { 30f, 40f, 50f, 60f }, radii);
    }

    [Fact]
    public void Distinct_targets_share_the_pool()
    {
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, new Orientation(), ShipCenter + new SimVec(RADIUS, 0, 0), new SimVec(RADIUS, RADIUS, RADIUS), RADIUS, RADIUS);
        pool.Emit((short)(TARGET + 1), new SimVec(0, 0, 0), new Orientation(), new SimVec(-RADIUS, 0, 0), new SimVec(RADIUS, RADIUS, RADIUS), RADIUS, RADIUS);
        Assert.Equal(2, pool.ActiveCount);
    }

    [Fact]
    public void Reset_clears_every_flare()
    {
        var pool = new ShieldFlareParticles();
        EmitOne(pool, new SimVec(1, 0, 0));
        Assert.Equal(1, pool.ActiveCount);
        pool.Reset();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Empty(pool.Enumerate());
    }
}

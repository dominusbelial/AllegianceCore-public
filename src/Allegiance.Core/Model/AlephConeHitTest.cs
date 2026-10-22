using Allegiance.Core.Collision;

namespace Allegiance.Core.Model;

/// <summary>
/// P776 — the aleph (warp) trigger shape: a <b>cone</b>, not a sphere.
/// </summary>
/// <remarks>
/// <para><b>Native shape.</b> <c>CwarpIGC::Initialize</c> loads the warp with
/// <c>LoadWarp</c> (<c>Igc/warpIGC.cpp:33</c>), radius <c>warpDef.radius</c>
/// (<c>Igc/warpIGC.cpp:39</c>) and an orientation built from
/// <c>dataWarp-&gt;forward</c> (<c>Igc/warpIGC.cpp:45</c>).
/// <c>CmodelIGC::LoadWarp</c> creates the hit test with <c>c_htsCone</c>
/// (<c>Igc/modelIGC.cpp:127-130</c>), <em>not</em> <c>c_htsSphere</c>.
/// <c>BoundingCone::GetExtreme</c> (<c>_Utility/HitTest.cpp:254-283</c>) is the
/// support function of that solid: in the warp's local frame the extreme point
/// is the tip <c>(0,0,R)</c> when <c>direction.z &gt;= sqrt(2)/2</c>, otherwise
/// a rim point of the radius-<c>R</c> circle at <c>z = 0</c>.</para>
/// <para><b>World frame.</b> <c>HitTest::GetExtreme</c> maps the local extreme
/// to the world as <c>local * orientation</c> (<c>_Utility/HitTest.cpp:878-879</c>).
/// With the native row-vector convention local <c>+z</c> is the orientation's
/// <b>backward</b> row (<c>zlib/orientation.h:83-92</c>:
/// <c>GetForward() = -row2</c>, <c>GetBackward() = row2</c>). The solid is
/// therefore: <b>a base disc of radius R through the aleph centre, perpendicular
/// to forward, plus a 45&deg; cone behind it with its apex at
/// centre + R&middot;backward</b>. Nothing of it lies on the forward side of the
/// disc. Its cross-section in the <c>(rho, z)</c> half-plane — <c>z</c> along
/// backward, <c>rho</c> the perpendicular distance — is the triangle
/// <c>(0,0), (R,0), (0,R)</c>.</para>
/// <para><b>Ship shape.</b> The ship is approximated by its bounding sphere
/// <c>0.5 &middot; HullType.Length</c> — the same sphere the combat pass builds
/// (<see cref="Sector"/>.ResolveCombatCollisions, <c>0.5f * HullType.Length</c>;
/// native <c>Igc/shipIGC.cpp:1419</c> <c>SetRadius(0.5f * length)</c>) — where
/// native's <c>HitTest::Collide</c> runs <c>HullCollide</c> over the ship's
/// convex hull (<c>_Utility/HitTest.cpp:199-200,921-1015</c>). Contact is true
/// when the 2-D distance from <c>(rho, z)</c> to the triangle is at most the
/// ship radius.</para>
/// <para><b>No closing-velocity gate.</b> Native's <c>HitTest::Collide</c> only
/// uses the bounding spheres as a broad phase and produces a candidate even when
/// the objects are separating (<c>_Utility/HitTest.cpp:166-185</c>); the
/// "not closing: ignore / hit the back: bounce" logic in
/// <c>CshipIGC::HandleCollision</c> is commented out
/// (<c>Igc/shipIGC.cpp:638-715</c>) and <c>HitWarpEvent</c> is called
/// unconditionally (<c>Igc/shipIGC.cpp:629-636</c>). So a ship parked at the
/// centre warps, and this helper applies no velocity test.</para>
/// </remarks>
internal static class AlephConeHitTest
{
    /// <summary>The unit backward axis of a warp whose (possibly non-unit)
    /// <paramref name="forward"/> is given. A zero forward falls back to the
    /// identity orientation's forward <c>(0,0,-1)</c> — what
    /// <c>ServerHost.HitWarpEvent</c> / <c>Orientation.Set</c> do
    /// (<c>Igc/warpIGC.cpp:45</c>; <c>src/Allegiance.Core/Sim/Orientation.cs</c>
    /// <c>Set</c> resets to identity for a zero axis), whose backward is
    /// <c>(0,0,1)</c>.</summary>
    internal static Vector3 Backward(Vector3 forward)
    {
        var f = forward.Normalize();
        if (f.LengthSquared() == 0f) f = new Vector3(0f, 0f, -1f);
        return -f;
    }

    /// <summary>
    /// True iff a sphere of radius <paramref name="shipRadius"/> centred at
    /// <paramref name="shipCentre"/> touches the aleph cone: centre
    /// <paramref name="centre"/>, radius <paramref name="radius"/>, facing
    /// <paramref name="forward"/>. See the type remarks for the native cites.
    /// </summary>
    internal static bool Touches(Vector3 centre, Vector3 forward, float radius,
                                 Vector3 shipCentre, float shipRadius)
    {
        Vector3 b = Backward(forward);
        Vector3 d = shipCentre - centre;
        float z = d * b;                        // component along backward
        float rho = (d - b * z).Length();       // perpendicular distance from the axis
        return DistanceToConeSection(rho, z, radius) <= shipRadius;
    }

    /// <summary>
    /// P777b — a warp's cone, pre-computed once per tick: its centre, its
    /// <b>unit</b> backward axis and its radius. <see cref="Touches"/> normalises
    /// the forward vector on every call (<see cref="Backward"/>); this struct
    /// does it once, so the per-model × per-warp hot loop pays neither the
    /// normalise nor the <c>float[]</c> parse. Same solid, same numbers.
    /// </summary>
    internal readonly struct Geometry
    {
        /// <summary>The aleph centre (the centre of the base disc, the origin of
        /// the native <c>c_htsCone</c> in its local frame).</summary>
        internal readonly Vector3 Centre;

        /// <summary>The unit <b>backward</b> axis — the cone's axis of symmetry,
        /// pointing from the base disc to the apex.</summary>
        internal readonly Vector3 UnitBackward;

        /// <summary>The warp's raw radius <c>R</c> (the base-disc radius and the
        /// distance from the disc to the apex).</summary>
        internal readonly float Radius;

        internal Geometry(Vector3 centre, Vector3 unitBackward, float radius)
        {
            Centre = centre;
            UnitBackward = unitBackward;
            Radius = radius;
        }

        /// <summary>The geometry of a warp record. The forward axis is
        /// normalised exactly once, here; a zero forward falls back to the same
        /// identity-orientation backward <see cref="Backward"/> returns,
        /// <c>(0,0,1)</c>.</summary>
        internal static Geometry From(DataWarpIGC w)
            => From(Vector(w.Position), Vector(w.Forward), w.Radius);

        /// <summary>The geometry of a warp at <paramref name="centre"/> facing
        /// <paramref name="forward"/>. See <see cref="Backward"/> for the zero-axis
        /// fallback.</summary>
        internal static Geometry From(Vector3 centre, Vector3 forward, float radius)
            => new(centre, Backward(forward), radius);

        private static Vector3 Vector(float[]? v) => v is { Length: 3 }
            ? new Vector3(v[0], v[1], v[2])
            : new Vector3(0f, 0f, -1f);
    }

    /// <summary>
    /// The exact distance from <paramref name="p"/> to the cone — the same
    /// number <see cref="Touches"/> compares against a radius, but with the
    /// geometry already normalised. Zero when <paramref name="p"/> is inside the
    /// solid, so <c>Distance(g, p) &lt;= r</c> is exactly
    /// <c>Touches(g.Centre, forward, g.Radius, p, r)</c>.
    /// </summary>
    internal static float Distance(in Geometry g, Vector3 p)
    {
        Vector3 d = p - g.Centre;
        float z = d * g.UnitBackward;                       // component along backward
        float rho = (d - g.UnitBackward * z).Length();      // perpendicular distance from the axis
        return DistanceToConeSection(rho, z, g.Radius);
    }

    /// <summary>
    /// P777b — the <b>first</b> contact, within one tick, of a sphere of radius
    /// <paramref name="r"/> travelling along the segment
    /// <paramref name="start"/> → <paramref name="end"/> with the aleph cone.
    /// <paramref name="t"/> is the contact time on <c>p(t) = start + t·(end − start)</c>,
    /// <c>t ∈ [0, 1]</c>. This is the continuous test native runs: missiles are
    /// <c>c_mtCastRay | c_mtHitable</c> spheres of <c>dataMissileType-&gt;radius</c>
    /// (<c>Igc/missileigc.cpp:50-51,64-66</c>), they are tested against the static
    /// KD tree (<c>Igc/clusterIGC.cpp:266-285</c>) holding the warps
    /// (<c>Igc/warpIGC.cpp:33-35</c>, <c>Igc/clusterIGC.cpp:526-530</c>), and
    /// sphere-vs-cone goes to <c>HullCollide</c>
    /// (<c>_Utility/HitTest.cpp:197-201</c>; <c>c_htsCone = −1 &gt; c_htsSphere = −3</c>,
    /// <c>_Utility/Utility.h:691-696</c>), which is continuous over the tick and
    /// still collides while separating (<c>_Utility/HitTest.cpp:166-185</c>).
    /// Ships use the same call with <c>r = 0.5·HullType.Length</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an exact search works — convexity.</b> The cone solid is
    /// convex (it is the convex hull of the base disc and the apex), the distance
    /// to a convex set is a convex function of the point, and <c>p(t)</c> is
    /// affine in <c>t</c>; so <c>f(t) = dist(p(t), cone)</c> is <b>convex</b> in
    /// <c>t</c>. A convex <c>f</c> with <c>f(0) &gt; r</c> whose sublevel set
    /// <c>{f ≤ r}</c> is non-empty is non-increasing on <c>[0, t_first]</c>, so:
    /// (a) a golden-section search finds <c>min f</c> on <c>[0, 1]</c>;
    /// (b) if <c>min f &gt; r</c> there is no contact;
    /// (c) otherwise bisecting <c>f(t) = r</c> on <c>[0, t_min]</c> yields the
    /// <b>first</b> contact. <see cref="DistanceToConeSection"/> is the exact 3-D
    /// distance: the solid is a body of revolution, so the nearest point lies in
    /// the same meridian half-plane and the 2-D triangle distance is the 3-D one.</para>
    /// <para><b>Correctness of the rejects.</b> Every point of the solid is within
    /// <c>R</c> of the centre (the solid is the hull of a radius-<c>R</c> disc and
    /// the apex at distance <c>R</c>), so a segment whose closest approach to the
    /// centre exceeds <c>R + r</c> cannot touch. Every point of the solid has
    /// <c>z ∈ [0, R]</c> along backward, so a segment entirely at <c>z &lt; −r</c>
    /// (natively: in front of the disc) or entirely at <c>z &gt; R + r</c>
    /// (behind the apex) cannot touch either. The golden section also exits early
    /// once the Lipschitz bound proves a miss: <c>f</c> changes by at most
    /// <c>|end − start|</c> per unit <c>t</c>, so no unsampled point in the
    /// bracket can be below <c>best − travel·(hi − lo)</c>.</para>
    /// <para><b>Tolerance.</b> The bracket is driven to <c>1e-3</c> world units of
    /// travel, then bisection to <c>1e-4</c> of the travel, both capped (40 and 30
    /// iterations). A zero-length segment reduces to <see cref="Distance"/> ≤ r,
    /// i.e. to <see cref="Touches"/>.</para>
    /// </remarks>
    internal static bool SweptFirstContact(in Geometry g, Vector3 start, Vector3 end,
                                           float r, out float t)
    {
        t = 0f;
        if (r < 0f) r = 0f;
        Vector3 delta = end - start;
        float travel = delta.Length();
        if (travel <= 0f) return Distance(g, start) <= r;   // parked: the point test

        // (a) cheap rejects, in world space.
        float z0 = (start - g.Centre) * g.UnitBackward;
        float z1 = (end - g.Centre) * g.UnitBackward;
        if ((z0 < -r && z1 < -r) || (z0 > g.Radius + r && z1 > g.Radius + r)) return false;
        float reach = g.Radius + r;
        if (SegmentPointDistanceSquared(start, end, g.Centre) > reach * reach) return false;

        // (b) already touching at the start of the tick.
        if (Distance(g, start) <= r) { t = 0f; return true; }

        // (c) golden-section search for min f on [0, 1] (convex f).
        const float InvPhi = 0.6180339887f;
        float lo = 0f, hi = 1f;
        float x1 = hi - InvPhi * (hi - lo);
        float x2 = lo + InvPhi * (hi - lo);
        float f1 = Distance(g, start + delta * x1);
        float f2 = Distance(g, start + delta * x2);
        for (int i = 0; i < 40; i++)
        {
            float best = MathF.Min(f1, f2);
            if (best <= r) break;                                  // (c) found a contact
            if (best - travel * (hi - lo) > r) return false;        // (a) Lipschitz: no contact
            if (travel * (hi - lo) <= 1e-3f) break;                 // converged
            if (f1 < f2)
            {
                hi = x2; x2 = x1; f2 = f1;
                x1 = hi - InvPhi * (hi - lo);
                f1 = Distance(g, start + delta * x1);
            }
            else
            {
                lo = x1; x1 = x2; f1 = f2;
                x2 = lo + InvPhi * (hi - lo);
                f2 = Distance(g, start + delta * x2);
            }
        }

        // The bracket's best sample is a real point of the segment: if it is
        // not within r, no sampled point is, and the search above proved (or
        // converged on) the minimum — no contact. Written as a negated `<=` so a
        // non-finite position (NaN, a numeric fault) reports no contact instead
        // of a phantom hit.
        float bestT = f1 <= f2 ? x1 : x2;
        if (!(MathF.Min(f1, f2) <= r)) return false;

        // (d) bisect f(t) = r on [0, bestT]: f is convex, f(0) > r, f(bestT) ≤ r.
        float a = 0f, b = bestT;
        for (int i = 0; i < 30 && (b - a) * travel > 1e-4f; i++)
        {
            float mid = 0.5f * (a + b);
            if (Distance(g, start + delta * mid) <= r) b = mid;
            else a = mid;
        }
        t = b;
        return true;
    }

    /// <summary>Squared distance from <paramref name="p"/> to the segment
    /// <paramref name="a"/>-<paramref name="b"/> (the broad-phase reject).</summary>
    private static float SegmentPointDistanceSquared(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a, ap = p - a;
        float len2 = ab.LengthSquared();
        float u = len2 == 0f ? 0f : Math.Clamp((ap * ab) / len2, 0f, 1f);
        Vector3 d = ap - ab * u;
        return d.LengthSquared();
    }

    /// <summary>
    /// Distance in the <c>(rho, z)</c> half-plane from a point to the triangle
    /// <c>(0,0), (R,0), (0,R)</c>; zero when inside. The triangle is the cone's
    /// cross-section: the edge on <c>z = 0</c> is the base disc, the hypotenuse
    /// <c>rho + z = R</c> is the 45&deg; slant to the apex at <c>z = R</c>.
    /// </summary>
    internal static float DistanceToConeSection(float rho, float z, float radius)
    {
        // Inside (including the boundary): the triangle is the region
        // rho >= 0, z >= 0, rho + z <= R. rho is a distance, so never negative.
        if (z >= 0f && rho >= 0f && rho + z <= radius) return 0f;

        float dAB = DistanceToSegment(rho, z, 0f, 0f, radius, 0f);   // base disc edge
        float dBC = DistanceToSegment(rho, z, radius, 0f, 0f, radius); // slant to the apex
        float dCA = DistanceToSegment(rho, z, 0f, radius, 0f, 0f);   // the axis (rim to apex)
        return MathF.Min(dAB, MathF.Min(dBC, dCA));
    }

    /// <summary>Distance from <c>(px, py)</c> to the segment <c>(ax, ay)-(bx, by)</c>.</summary>
    private static float DistanceToSegment(float px, float py,
                                           float ax, float ay, float bx, float by)
    {
        float ex = bx - ax, ey = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = ex * ex + ey * ey;
        float t = len2 == 0f ? 0f : Math.Clamp((wx * ex + wy * ey) / len2, 0f, 1f);
        float dx = wx - ex * t, dy = wy - ey * t;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

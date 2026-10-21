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

using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P569 — the pure, MonoGame-free placement of a shield-flare CAP: the exact
/// port of native <c>ThingGeo::AddFlare</c>'s orientation/scale math
/// (<c>src/effect/thinggeo.cpp:1243-1305</c>), producing the WORLD-space
/// position of each unit-cap vertex given the struck ship's pose and shield
/// ellipsoid.
/// </summary>
/// <remarks>
/// <para><b>Native recipe (thinggeo.cpp).</b> For a shield-absorbed hit native
/// calls <c>AddFlare(time, position, id, ellipse)</c> from
/// <c>CshieldIGC::ApplyDamage</c> (<c>src/Igc/shieldIGC.cpp:171</c>), which
/// reaches <c>ThingGeo::AddFlare</c>. There:
/// <list type="number">
/// <item><c>vecLocal = m_orientation.TimesInverse(-vecDirection)</c> (:1256)
/// transforms the (negated) hit direction into ship-local space;</item>
/// <item><c>vector = vecLocal ÷ ellipse</c> component-wise (:1264-1269) — the
/// hit direction re-normalised by the ellipsoid's per-axis extents;</item>
/// <item><c>orient.Set(vector)</c> orients the cap along that direction
/// (:1271, <c>orientation.cpp:314-390</c>);</item>
/// <item><c>orient.Scale(ellipse / m_radius)</c> (:1272,
/// <c>orientation.h:174-179</c>) squashes the unit sphere onto the shield
/// ellipsoid;</item>
/// <item><c>Matrix mat(orient, 0, m_radiusMesh)</c> (:1289,
/// <c>matrix.cpp:57-72</c>) scales the cap by <c>m_radiusMesh</c> (= the ship's
/// mesh-bounding radius, <c>thinggeo.cpp:1053</c>) — the cap hugs the shield
/// surface at the hit.</item>
/// </list>
/// The net world transform of a cap-local vertex <c>p</c> (the cap is a unit
/// radius spherical cap, apex at <c>(0,0,1)</c>) is
/// <c>center + O · (mr · (p.x·R + p.y·U + p.z·B))</c>, where <c>{R,U,B}</c> are
/// the cap's right/up/backward axes after <c>Set</c>+<c>Scale</c> and <c>O</c>
/// is the ship's world orientation — see <see cref="TransformCapPosition"/>.
/// This is derived directly from <c>matrix.cpp:57-72</c> (the <c>mat</c> layout)
/// and <c>Matrix::Transform</c> (<c>matrix.cpp:782-790</c>, column convention).
/// </para>
/// <para><b>Data source.</b> The ellipse is the struck ship's native mesh
/// per-axis half-extents (<see cref="NativeShipResolver.MergedExtents"/>,
/// exposed via <see cref="NativeShipRenderer.ShieldEllipse"/>) — the client's
/// faithful proxy for native's hull hit-test <c>GetEllipseEquation()</c>
/// (<c>shieldIGC.cpp:171</c>), since the client has no collision hull for
/// arbitrary targets. <c>meshRadius</c> is the same mesh's merged-bounding
/// radius (native <c>m_radiusMesh</c>). When the native mesh is unavailable
/// both degrade to the ship's <c>0.5·Length</c> sphere (the P545 approximation),
/// preserving the formula with <c>ellipse = radius</c>.</para>
/// </remarks>
public static class ShieldFlareCapMath
{
    /// <summary>
    /// Compute the world-space position of one unit-cap local vertex
    /// <paramref name="capLocal"/> (the <c>.x</c> caps are unit-radius, apex
    /// <c>(0,0,1)</c>). Direct port of <c>ThingGeo::AddFlare</c>
    /// (thinggeo.cpp:1252-1305). All vector arguments are in sim/world units.
    /// </summary>
    /// <param name="capLocal">The cap-local vertex (unit cap).</param>
    /// <param name="shipOrientation">The struck ship's world orientation.</param>
    /// <param name="center">The struck ship's world centre.</param>
    /// <param name="hitWorldPos">The world hit position (relative vector
    /// <c>hitWorldPos - center</c> drives <c>vecLocal</c>, thinggeo.cpp:1256).</param>
    /// <param name="ellipse">The shield ellipsoid per-axis extents.</param>
    /// <param name="shipRadius">Native <c>m_radius</c> = <c>0.5 · HullLength</c>
    /// (shipIGC.cpp:1419).</param>
    /// <param name="meshRadius">Native <c>m_radiusMesh</c> — the struck ship's
    /// mesh-bounding radius (thinggeo.cpp:1053).</param>
    public static Vector3 TransformCapPosition(
        Vector3 capLocal,
        Orientation shipOrientation,
        Vector3 center,
        Vector3 hitWorldPos,
        Vector3 ellipse,
        float shipRadius,
        float meshRadius)
    {
        // (1) Hit position into ship-local space.
        var vecLocal = shipOrientation.TimesInverse(hitWorldPos - center);

        // (2) Re-normalise by the ellipsoid axes (thinggeo.cpp:1264-1269).
        var vector = new Vector3(
            ellipse.X != 0f ? vecLocal.X / ellipse.X : 0f,
            ellipse.Y != 0f ? vecLocal.Y / ellipse.Y : 0f,
            ellipse.Z != 0f ? vecLocal.Z / ellipse.Z : 0f);

        // (3) Orient the cap along `vector` (orientation.cpp:314-390).
        var capOrient = new Orientation();
        capOrient.Set(vector);

        // (4) Squash the unit cap onto the shield ellipsoid (thinggeo.cpp:1272,
        // orientation.h:174-179). Guard zero radius.
        float invR = shipRadius != 0f ? 1f / shipRadius : 0f;
        capOrient.Scale(new Vector3(ellipse.X * invR, ellipse.Y * invR, ellipse.Z * invR));

        // (5) Matrix(orient, 0, m_radiusMesh) · p (matrix.cpp:57-72, 782-790):
        // m = mr · (p.x·R + p.y·U + p.z·B).
        var r = capOrient.GetRight();
        var u = capOrient.GetUp();
        var b = capOrient.GetBackward();
        var m = new Vector3(
            meshRadius * (capLocal.X * r.X + capLocal.Y * u.X + capLocal.Z * b.X),
            meshRadius * (capLocal.X * r.Y + capLocal.Y * u.Y + capLocal.Z * b.Y),
            meshRadius * (capLocal.X * r.Z + capLocal.Y * u.Z + capLocal.Z * b.Z));

        // The thing's world transform maps ship-local → world (O · m).
        var disp = shipOrientation.Times(m);
        return new Vector3(center.X + disp.X, center.Y + disp.Y, center.Z + disp.Z);
    }

    /// <summary>
    /// Transform a whole unit-cap mesh (positions only) to world space with the
    /// same <see cref="TransformCapPosition"/> rules, writing into
    /// <paramref name="outPositions"/> (length = mesh vertex count × 3).
    /// </summary>
    public static void TransformCapMesh(
        float[] inPositions,
        float[] outPositions,
        Orientation shipOrientation,
        Vector3 center,
        Vector3 hitWorldPos,
        Vector3 ellipse,
        float shipRadius,
        float meshRadius)
    {
        int n = inPositions.Length / 3;
        for (int v = 0; v < n; v++)
        {
            var w = TransformCapPosition(
                new Vector3(inPositions[3 * v], inPositions[3 * v + 1], inPositions[3 * v + 2]),
                shipOrientation, center, hitWorldPos, ellipse, shipRadius, meshRadius);
            outPositions[3 * v] = w.X;
            outPositions[3 * v + 1] = w.Y;
            outPositions[3 * v + 2] = w.Z;
        }
    }
}

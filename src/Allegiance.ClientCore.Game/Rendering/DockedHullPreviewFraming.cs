using System;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P525 — the docked hull preview's camera framing, lifted verbatim from
/// native's loadout geo pane (WinTrek/load.cpp:860-934). Pure and MonoGame-
/// free (just scalars/angles) so the numbers are unit-testable next to
/// <see cref="DockedHullOrbit"/>.
///
/// <para><b>Native reference.</b> <c>LoadoutImpl::CreateGeoPane</c>
/// (load.cpp:860-911) gives the geo its own z-buffered viewport and a camera
/// with <c>SetFOV(RadiansFromDegrees(50))</c> (load.cpp:864) and
/// <c>SetZClip(1, 10000)</c> (load.cpp:863). <c>LoadoutImpl::SetGeo</c>
/// (load.cpp:919-934) then positions the camera at
/// <c>Vector(0, 0, 2.4f * radius)</c> where <c>radius</c> is the geo's own
/// radius (load.cpp:923,927-933) — i.e. the eye sits at <b>2.4 × the model
/// radius</b>, just outside the hull's bounding sphere with the whole sphere
/// inside the 50° frustum (arcsin(1/2.4) ≈ 24.6° &lt; 25°).</para>
///
/// <para><b>The bug this fixes.</b> The port's preview (Game1.Rendering.cs:
/// 2226-2227) normalised the model by <c>1 / HullLength</c>, but the compiled
/// Meshy meshes are ~190 model units long (merged-sphere render radius
/// ~95-160, <see cref="ShipRenderScale.RenderRadius"/>). A fighter (real
/// length ~17-21) therefore drew at a ~7-9 unit radius while the eye sat at
/// z=2.4 — <em>inside the hull</em> ("looking from the insides", and the mesh
/// wrapped the camera). Normalising to <b>radius 1</b> instead makes every
/// hull frame identically at the native eye distance.</para>
///
/// <para>This helper is framing only. The preview's <em>motion</em> matches
/// native's loadout geo — a level spin about Y at 1 rad/s with a fixed π/8 X
/// tilt (load.cpp:877-887), via <see cref="DockedHullOrbit"/> (P526) — and the
/// eye has no Y offset (P526), exactly as native.</para>
/// </summary>
public static class DockedHullPreviewFraming
{
    /// <summary>Native eye distance in units of the model radius:
    /// <c>SetGeo</c> puts the camera at <c>2.4f * radius</c> (load.cpp:927-933).</summary>
    public const float EyeDistanceFactor = 2.4f;

    /// <summary>Native loadout FOV, <c>SetFOV(RadiansFromDegrees(50))</c> (load.cpp:864).</summary>
    public const float FieldOfViewDegrees = 50f;

    /// <summary>The 50° loadout FOV in radians (load.cpp:864).</summary>
    public static float FovRadians => (float)(FieldOfViewDegrees * Math.PI / 180.0);

    /// <summary>Half the loadout FOV — the half-angle the whole hull sphere must
    /// fit inside (a sphere fully visible from the eye).</summary>
    public static float HalfFovDegrees => FieldOfViewDegrees / 2f;

    /// <summary>
    /// Normalise a model's TRUE rendered radius to 1 so the fixed native eye
    /// (2.4 × radius) frames every hull identically, independent of the raw
    /// mesh size. Guarded: a non-positive radius → 1 (no scaling).
    /// </summary>
    public static float NormaliseScale(float renderRadius) =>
        renderRadius > 0f ? 1f / renderRadius : 1f;

    /// <summary>
    /// The eye's distance from the hull centre for a hull of the given radius:
    /// <c>2.4 × radius</c> (load.cpp:927-933). With the model normalised to
    /// radius 1 this is the constant 2.4.
    /// </summary>
    public static float EyeDistance(float radius) => EyeDistanceFactor * radius;

    /// <summary>
    /// True iff a camera at <paramref name="cameraDistance"/> from a sphere of
    /// radius <paramref name="sphereRadius"/> sits OUTSIDE the sphere (the eye
    /// is not embedded in the hull). Pure geometry.
    /// </summary>
    public static bool EyeOutsideSphere(float cameraDistance, float sphereRadius) =>
        sphereRadius > 0f && cameraDistance > sphereRadius;

    /// <summary>
    /// The angular radius (half-angle, degrees) of a sphere of radius
    /// <paramref name="sphereRadius"/> as seen from <paramref name="cameraDistance"/>
    /// away — <c>asin(sphereRadius / cameraDistance)</c>. The whole sphere is
    /// inside the frustum iff this is less than the half-FOV. Pure geometry.
    /// </summary>
    public static float AngularRadiusDegrees(float cameraDistance, float sphereRadius)
    {
        if (sphereRadius <= 0f || cameraDistance <= sphereRadius) return 90f;
        return (float)(Math.Asin(sphereRadius / cameraDistance) * 180.0 / Math.PI);
    }
}

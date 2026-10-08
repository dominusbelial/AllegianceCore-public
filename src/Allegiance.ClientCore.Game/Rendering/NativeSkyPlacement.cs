using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P576b — camera-relative <b>placement</b> of the native per-sector sky.
///
/// The flight view is camera-relative (<c>ChaseCamera.cs:205</c>,
/// <c>CockpitCamera.cs:78</c>: <c>CreateLookAt(Zero,…)</c>), i.e. the camera sits
/// at the <b>origin</b> of view space and every renderer subtracts
/// <c>cameraEye</c> from world positions (cf.
/// <c>EnhancedExplosionRenderer.cs:120-122</c>). The sky is a backdrop centred
/// on the camera at infinity, so every element must be placed around the
/// <b>origin</b> — <c>World = Identity</c> + direction × radius — and must
/// <b>never</b> be offset by <c>cameraEye</c>: adding <c>cameraEye</c> in view
/// space pushes the dome/stars/planet thousands of units away (past the scene
/// far plane, producing the P576b hard-edged slabs and black wedges).
///
/// Each placement takes the camera eye explicitly and deliberately <b>ignores</b>
/// it — that "takes but ignores" shape is the fix, and it is exactly what makes
/// the invariant unit-testable (two eyes 5000 u apart yield identical sky).
///
/// Source rules ported (cited in docs/reports/P576_SECTOR_SKY_AND_ALEPH_GLIMPSE.md):
/// dome globe1-9 radius 7500 centred on the camera (efgeo.cpp:1029-1052), stars
/// drawn at infinity (starimage.cpp:32-53), planet poster billboard at the planet
/// direction (efimage.cpp:515-563).
/// </summary>
public static class NativeSkyPlacement
{
    /// <summary>A star point on the sky shell (radius = <paramref name="radius"/>
    /// from the origin). The camera eye is intentionally ignored — the star field
    /// is at infinity, camera-centred.</summary>
    public static Vector3 StarShellPosition(NativeStar star, float radius, Vector3 cameraEye)
        => new Vector3(star.DirX, star.DirY, star.DirZ) * radius;

    /// <summary>The planet poster's billboard centre on the sky shell at
    /// distance <paramref name="dist"/> along <paramref name="planetDir"/>.
    /// Camera eye intentionally ignored.</summary>
    public static Vector3 PlanetShellCenter(Vector3 planetDir, float dist, Vector3 cameraEye)
        => planetDir * dist;

    /// <summary>The dome's world matrix: always <see cref="Matrix.Identity"/> —
    /// the globe surrounds the camera at the origin, so it carries no
    /// translation. Camera eye intentionally ignored.</summary>
    public static Matrix DomeWorld(Vector3 cameraEye) => Matrix.Identity;

    /// <summary>World-space right axis of a camera-facing billboard at
    /// <paramref name="dir"/> (orthonormalised against world up).</summary>
    public static Vector3 BillboardRight(Vector3 dir)
        => Vector3.Normalize(Vector3.Cross(Vector3.Up, dir));

    /// <summary>World-space up axis of the billboard (orthogonal to both the
    /// facing direction and the right axis).</summary>
    public static Vector3 BillboardUp(Vector3 dir, Vector3 right)
        => Vector3.Cross(dir, right);

    /// <summary>
    /// The sky's OWN projection. The scene far plane (~6000,
    /// ExternalCameras.cs:45) is nearer than the native dome's 7500 shell, so
    /// using it would clip the sky (P576b: black wedges where faces fall past
    /// the far plane). The sky has no parallax, so it is re-projected with a
    /// large far (80000) and near = 1, keeping the dome/stars inside
    /// [near, far]; FOV/aspect are recovered from the scene projection so the
    /// sky still aligns with the world (angular appearance unchanged).
    /// </summary>
    public static Matrix SkyProjection(Matrix sceneProjection)
    {
        float focus = sceneProjection.M22;                 // cot(vFOV/2)
        float vFov = 2f * MathF.Atan(1f / focus);
        float aspect = sceneProjection.M22 / sceneProjection.M11;
        return Matrix.CreatePerspectiveFieldOfView(vFov, aspect, 1f, 80000f);
    }
}

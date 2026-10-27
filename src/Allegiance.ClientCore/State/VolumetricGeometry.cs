namespace Allegiance.ClientCore.State;

using Allegiance.Core.Collision;

/// <summary>
/// P579 — pure geometry/fade helpers shared by the volumetric renderer and the
/// failing-first unit tests.
///
/// <b>Camera-relative placement.</b> The game's view matrix keeps the EYE at the
/// origin; every renderer subtracts <c>cameraEye</c> before the view transform
/// (<c>EnhancedExplosionRenderer</c> centre − cameraEye; native
/// <c>ExplosionGeo</c> likewise). The P573 spike's volumetric renderers did NOT
/// subtract it, so every explosion was offset by the whole camera position —
/// the "no explosions" bug Domingo saw live. This helper is the single place
/// the offset is applied so a unit test can pin it.
///
/// <b>Depth fade.</b> The volume melts onto the opaque scene by fading where the
/// scene's linear depth is closer than the volume's (<c>Volumetric.fx
/// DepthFade</c>). The P573 slope was <c>* (1/8)</c> — ~2000× too weak — and with
/// <c>--soft off</c> the unbound depth sampler read 0 → fade 0, erasing every
/// volume. The P579 slope is <c>SoftnessInv = 250</c> (the same
/// <c>SoftParticle.fx SoftnessNormalized</c> slope the Enhanced smoke uses), and
/// a 1×1 white "far" depth texture makes the no-depth case decode to scene=1 →
/// fade 1.
/// </summary>
public static class VolumetricGeometry
{
    /// <summary>The world-space volume centre, expressed camera-relative (subtract
    /// the camera eye, matching the view matrix's eye-at-origin convention).</summary>
    public static Vector3 CameraRelative(Vector3 world, Vector3 eye)
        => new(world.X - eye.X, world.Y - eye.Y, world.Z - eye.Z);

    /// <summary>Depth-fade softness inverse — the P579 slope (2000× the P573
    /// 1/8): a 1-unit normalized-depth gap saturates the fade to full.</summary>
    public const float SoftnessInv = 250f;

    /// <summary>Normalized linear depths: <paramref name="scene"/> = the opaque
    /// scene's encoded linear depth (0..1), <paramref name="volume"/> = the
    /// volume fragment's linear depth (0..1). Fade = 1 when the volume is nearer
    /// than the scene by more than <c>1/SoftnessInv</c>; 0 when the scene is
    /// deeper-hidden (fully occluded). A "far" scene (1) yields fade 1 — the
    /// no-depth (--soft off) guarantee.</summary>
    public static float DepthFade(float scene, float volume, float softnessInv = SoftnessInv)
    {
        var d = (scene - volume) * softnessInv;
        return d < 0f ? 0f : d > 1f ? 1f : d;
    }

    /// <summary>
    /// P834 — clamp a raymarch interval to the <b>visible</b> depth range: never
    /// before the near plane, never past the opaque scene (both distances
    /// measured along the view ray). The <c>Volumetric.fx</c> pixel shaders do
    /// exactly this before marching, which is the "camera inside / at the volume"
    /// fix: when the eye is inside the bounding sphere <c>RaySphere</c> returns
    /// t0 = 0, so P579 marched the whole chord in front of the lens (and even
    /// behind hulls); here the march starts at the near plane and stops where the
    /// scene occludes it. <see cref="Visible"/> is false when the range collapses
    /// (the volume is entirely behind geometry or inside the near plane) and the
    /// shader can <c>clip()</c>.
    ///
    /// <para>P834b — the arguments are distances ALONG THE RAY; turning the
    /// view-axis near/scene depths into ray distances needs the ray's view-axis
    /// cosine, which is what <see cref="ClampVisibleRange"/> adds (and what P834
    /// got wrong by assuming the camera looks down world −Z).</para>
    /// </summary>
    public static MarchRange ClampMarchRange(float t0, float t1, float nearPlaneDistance, float sceneDistance)
    {
        float start = MathF.Max(t0, nearPlaneDistance);
        float end = MathF.Min(t1, sceneDistance);
        return new MarchRange(start, end);
    }

    /// <summary>
    /// P834b — the view-axis cosine denominator the shader uses:
    /// <c>1 / max(1e-4, dot(rayDir, viewForward))</c>. The near plane and the
    /// scene depth are measured ALONG THE VIEW AXIS, so a ray's distance to them is
    /// that depth divided by this cosine (<c>tNear = Near / cos</c>).
    /// </summary>
    public static float ViewAxisInvCos(Vector3 rayDir, Vector3 viewForward)
        => 1f / MathF.Max(1e-4f, rayDir * viewForward);

    /// <summary>
    /// P834b — <b>the full mirror of <c>Volumetric.fx</c>'s <c>ClampVisible</c></b>:
    /// turn the view-axis depths (near plane, normalized scene depth) into ray
    /// distances with the ray's view-axis cosine, then clamp the ray–sphere
    /// interval to them. <paramref name="rayDir"/> is a WORLD-space unit direction
    /// (the renderer passes camera-relative world positions and an eye at the
    /// origin), so <paramref name="viewForward"/> must be the camera's forward
    /// axis in that same space — <see cref="ViewForward(float,float,float)"/> derives
    /// it from the view matrix. P834 used <c>-rayDir.Z</c> instead, which is only
    /// the cosine for a camera looking down world −Z: at any other heading the
    /// interval collapsed and the volume was clipped away entirely (P834b).
    /// </summary>
    public static MarchRange ClampVisibleRange(
        float t0, float t1, float nearPlaneDistance, float sceneDepthNormalized,
        float invRange, Vector3 rayDir, Vector3 viewForward)
    {
        float invCos = ViewAxisInvCos(rayDir, viewForward);
        float nearRayDist = nearPlaneDistance * invCos;
        // scene is (viewDist - Near) * InvRange, so viewDist = scene / InvRange + Near.
        float sceneRayDist = (sceneDepthNormalized / MathF.Max(1e-6f, invRange) + nearPlaneDistance) * invCos;
        return ClampMarchRange(t0, t1, nearRayDist, sceneRayDist);
    }

    /// <summary>
    /// P834b — the camera's forward axis (unit, normalized) from a view matrix's
    /// <c>zaxis</c> components. XNA's <c>Matrix.CreateLookAt</c> stores
    /// <c>zaxis = normalize(eye − target)</c> in column 3, so forward is its
    /// negation. The renderer feeds this to the shader's <c>ViewForward</c>
    /// uniform, and the tests use it to prove the clamp does not depend on where
    /// the camera is pointing.
    /// </summary>
    public static Vector3 ViewForward(float m13, float m23, float m33)
        => new Vector3(-m13, -m23, -m33).Normalize();

    /// <summary>P834 — the clamped raymarch interval along a view ray (see
    /// <see cref="ClampMarchRange"/>).</summary>
    public readonly record struct MarchRange(float Start, float End)
    {
        /// <summary>True when any of the interval survives the clamp.</summary>
        public bool Visible => End > Start;

        /// <summary>Length of the surviving interval.</summary>
        public float Span => MathF.Max(0f, End - Start);
    }
}

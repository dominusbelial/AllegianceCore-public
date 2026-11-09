using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P979 — <c>visuals.radialVisibility</c>, the pure no-pop-in rule.
///
/// <para><b>The defect it removes.</b> Every flight camera's far clip is a
/// PLANE in view space: `Matrix.CreatePerspectiveFieldOfView` maps view-space
/// depth z, so an object dead ahead at 8000 m is clipped by a 6000 m far plane
/// while the same object 40° off axis has z = 8000·cos40° ≈ 6128 m and can be
/// inside it — the object pops in when the ship turns (and out when it turns
/// back). Native had exactly the same shape: `Camera::SetZClip` negates near/far
/// (`engine/camera.cpp:135-139`) and the perspective row is
/// `z_screen = (a·z + b)/−z`, a = zfar/(znear−zfar) (`engine/camera.cpp:89-96`),
/// i.e. a planar clip at `SetZClip(5.0f, 10000.0f)` (`WinTrek/WinTrek.cpp:323`).
/// The port's 6000 m far clip was the parity bug (fixed: native's 10000 m); the
/// PLANE is the era constraint and this flag replaces it, opt-in.</para>
///
/// <para><b>The rule.</b> With the flag ON an object is drawn by its true
/// distance from the camera eye — a sphere, not a plane: inside
/// <see cref="VisualOptions.ViewRange"/> it draws wherever it sits on screen;
/// outside it does not, whatever the angle. The object's own radius counts
/// (its nearest surface is what "distance" means), so a large asteroid whose
/// centre is just beyond the range but whose bulk is inside it does not vanish
/// at the boundary.</para>
///
/// <para><b>The projection.</b> The far plane is pushed past the range
/// (<see cref="RadialFarScale"/>) so the plane can never clip an object the
/// sphere rule keeps: an object at the range limit whose bulk extends outward
/// still has its far surface inside the frustum. The backdrop/skybox is
/// untouched (it has its own infinite-range placement).</para>
///
/// <para>Pure (no GraphicsDevice): the rule, the projection rewrite and the
/// depth-precision arithmetic are unit-testable headless.</para>
/// </summary>
public static class RadialVisibility
{
    /// <summary>
    /// How far past the range the RADIAL far plane sits (11000 m for the default
    /// 10000 m range). It exists only to cover an object whose centre is inside
    /// the range but whose bulk extends past it (a station/asteroid of a few
    /// hundred to ~1000 m radius); it is not a second visibility rule.
    /// </summary>
    public const float RadialFarScale = 1.1f;

    /// <summary>
    /// The radial far plane for <paramref name="viewRange"/> metres: the range
    /// plus the extent margin, but never below native's shipped 10000 m plane
    /// (so a small custom range does not needlessly crush depth precision).
    /// </summary>
    public static float RadialFarPlane(float viewRange)
        => MathF.Max(ExternalCameras.FarPlane, MathF.Max(0f, viewRange) * RadialFarScale);

    /// <summary>
    /// Is an object <paramref name="distance"/> metres from the camera eye drawn
    /// under the radial rule? Its nearest surface (distance − radius) must lie
    /// inside <paramref name="range"/>.
    /// </summary>
    public static bool RadialDrawn(float distance, float radius, float range)
        => distance - MathF.Max(0f, radius) <= MathF.Max(0f, range);

    /// <summary>
    /// Native's planar answer: drawn when the view-space depth is inside
    /// [<paramref name="near"/>, <paramref name="far"/>] — the rule the GPU's
    /// far plane has always applied, pinned here so the OFF path stays native.
    /// </summary>
    public static bool PlanarDrawn(float viewDepth, float near, float far)
        => viewDepth >= near && viewDepth <= far;

    /// <summary>
    /// The flag's decision for one object. <paramref name="radial"/> selects the
    /// sphere rule; OFF is native's planar clip.
    /// </summary>
    public static bool IsDrawn(bool radial, float range, float distance, float radius,
        float viewDepth, float near, float far)
        => radial ? RadialDrawn(distance, radius, range) : PlanarDrawn(viewDepth, near, far);

    /// <summary>
    /// Rewrite a perspective projection's far plane, keeping its near plane and
    /// field of view (the same matrix surgery P385's benchmark view distance
    /// uses). MonoGame stores <c>M33 = far/(near−far)</c> and
    /// <c>M43 = near·far/(near−far)</c>; a orthographic/other projection is
    /// returned unchanged.
    /// </summary>
    public static Matrix WithFarPlane(Matrix projection, float far)
    {
        if (MathF.Abs(projection.M33) < 1e-10f || MathF.Abs(projection.M34) <= 0.5f) return projection;
        float near = CameraPose.NearFromProjection(projection);
        far = MathF.Max(near + 1f, far);
        projection.M33 = far / (near - far);
        projection.M43 = near * far / (near - far);
        return projection;
    }

    /// <summary>
    /// Depth-buffer quantization at <paramref name="viewDepth"/> for a standard
    /// perspective projection with 2^<paramref name="depthBits"/> levels (MonoGame
    /// DesktopGL uses <c>DepthFormat.Depth24</c>): z_screen = (f/(f−n))(1 − n/z),
    /// so dz = dw·(f−n)·z²/(f·n). The unit is metres of view depth between
    /// adjacent representable depths — the measure of z-fighting risk at range.
    /// </summary>
    public static float DepthQuantization(float viewDepth, float near, float far, int depthBits = 24)
    {
        if (viewDepth <= 0f || near <= 0f || far <= near) return float.PositiveInfinity;
        double levels = (1 << depthBits) - 1;
        return (float)((far - near) * (double)viewDepth * viewDepth / ((double)far * near * levels));
    }
}

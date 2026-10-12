using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P612 — critically-damped easing for smooth camera transitions. When a camera
/// subject or framing changes (target switch, missile-cam start/end, mode change),
/// the view previously CUT instantly. This type supplies the easing that slides
/// position, orientation and FOV from the old framing to the new one over a short
/// named duration, instead of snapping.
/// <para>
/// It implements a critically-damped harmonic oscillator toward the target — the
/// classic "camera spring". Critical damping (ζ = 1) gives the fastest
/// non-overshooting response: the blended value is monotonic (never overshoots the
/// target) and converges to the target. <see cref="Blend"/> returns the eased value
/// at elapsed <c>t</c> over <paramref name="duration"/> seconds. A duration ≤ 0 is
/// the "instant" mode (native purists): the value is the target immediately, no
/// easing — matching the pre-P612 cut.
/// </para>
/// <para>Pure (no <see cref="GraphicsDevice"/>), so it is unit-testable headless.</para>
/// </summary>
public static class CameraEasing
{
    /// <summary>
    /// The target blend factor at which a transition is considered settled
    /// (used to pick <see cref="SpringNaturalFrequency"/> and by tests).
    /// </summary>
    public const double SettleFactor = 0.995;

    /// <summary>
    /// Natural frequency Ω (rad/s) chosen so a transition settles to
    /// <see cref="SettleFactor"/> by its nominal duration: the critical-damped
    /// residual is <c>(1 + Ωt)·e^(−Ωt)</c>; setting that to
    /// 1 − SettleFactor at t = duration gives Ω ≈ 6.6 / duration.
    /// </summary>
    public static double SpringNaturalFrequency(double duration)
        => duration > 0 ? 6.6 / duration : 0.0;

    /// <summary>
    /// The residual multiplier <c>g(t) = (1 + Ωt)·e^(−Ωt)</c> of the
    /// critically-damped spring (the fraction of the initial distance still
    /// remaining at elapsed <c>t</c>). Monotonic decreasing from 1 to 0; a
    /// transition blends <c>value = to + (from − to)·g(t)</c>.
    /// </summary>
    public static double Residual(double t, double duration)
    {
        if (duration <= 0) return 0.0;          // instant
        if (t >= duration) return 0.0;          // settled
        double om = SpringNaturalFrequency(duration);
        double x = om * t;
        return (1.0 + x) * Math.Exp(-x);
    }

    /// <summary>
    /// Blend a scalar from <paramref name="from"/> to <paramref name="to"/> at
    /// elapsed <c>t</c> over <paramref name="duration"/>. Instant (duration ≤ 0)
    /// returns <paramref name="to"/>. Monotonic, no overshoot, converges to
    /// <paramref name="to"/>.
    /// </summary>
    public static float Blend(float from, float to, double t, double duration)
        => (float)(to + (from - to) * Residual(t, duration));

    /// <summary>
    /// Blend a position vector from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public static Vector3 Blend(Vector3 from, Vector3 to, double t, double duration)
    {
        double g = Residual(t, duration);
        return to + (from - to) * (float)g;
    }

    /// <summary>
    /// Blend an orientation from <paramref name="from"/> to <paramref name="to"/>
    /// with spherical linear interpolation weighted by the spring residual (so it
    /// eases toward the target). Instant returns <paramref name="to"/>.
    /// </summary>
    public static Quaternion Blend(Quaternion from, Quaternion to, double t, double duration)
    {
        if (duration <= 0) return to;
        double g = Residual(t, duration);
        return Quaternion.Slerp(from, to, (float)(1.0 - g));
    }
}

/// <summary>
/// P612 — a complete camera pose: world-space <see cref="Eye"/>, world-space
/// orientation <see cref="Orient"/> (camera → world, so Forward =
/// <c>Vector3.Transform(Vector3.Forward, Orient)</c> is where the camera looks)
/// and a vertical <see cref="FovDeg"/> in degrees. Captured from an existing
/// view/projection pair so any mode can be expressed as a pose, and rebuilt into
/// a fresh view/projection after easing. Pure and unit-testable.
/// </summary>
public readonly record struct CameraPose(Vector3 Eye, Quaternion Orient, float FovDeg)
{
    /// <summary>Vertical FOV of a perspective projection (degrees) recovered from
    /// its matrix's <c>M22 = 1/tan(fov/2)</c> component (XNA
    /// <c>CreatePerspectiveFieldOfView</c>).</summary>
    public static float FovFromProjection(Matrix proj)
        => 2f * MathF.Atan2(1f, proj.M22) * (180f / MathF.PI);

    /// <summary>Near-plane of an XNA perspective projection. XNA's
    /// <c>CreatePerspectiveFieldOfView</c> stores <c>M33 = far/(near−far)</c> and
    /// <c>M34 = near·far/(near−far)</c>, so <c>near = M34/M33</c>.</summary>
    public static float NearFromProjection(Matrix proj) => proj.M34 / proj.M33;

    /// <summary>Far-plane of an XNA perspective projection: <c>far = M34/(1+M33)</c>
    /// (derived from the same two cells as <see cref="NearFromProjection"/>).</summary>
    public static float FarFromProjection(Matrix proj) => proj.M34 / (1f + proj.M33);

    /// <summary>
    /// Capture a pose from a finished view/projection/eye triple (the shape
    /// <c>ComputeFlightCamera</c> returns). The eye is used verbatim; the world
    /// orientation is recovered from the inverse view (so roll/up are preserved)
    /// and the FOV from the projection.
    /// </summary>
    public static CameraPose FromViewProjection(Matrix view, Matrix proj, Vector3 eye)
    {
        var inv = Matrix.Invert(view);
        var orient = Quaternion.CreateFromRotationMatrix(inv);
        return new CameraPose(eye, orient, FovFromProjection(proj));
    }

    /// <summary>Recover the eye-equivalent world position (inverse view translation).</summary>
    public static Vector3 EyeFromView(Matrix view) => Matrix.Invert(view).Translation;

    /// <summary>The camera's world-space forward (where it looks).</summary>
    public Vector3 Forward =>
        Vector3.Transform(Vector3.Forward, Orient);

    /// <summary>
    /// Rebuild a view matrix from this pose (orientation composed with translation
    /// at the eye, then inverted to view space) — the inverse of
    /// <see cref="FromViewProjection"/>.
    /// </summary>
    public Matrix View()
    {
        var world = Matrix.CreateFromQuaternion(Orient);
        world.Translation = Eye;
        return Matrix.Invert(world);
    }

    /// <summary>
    /// Rebuild a perspective projection from <see cref="FovDeg"/> using the given
    /// <paramref name="aspect"/> and the reference <paramref name="near"/> /
    /// <paramref name="far"/> planes (passed through unchanged).
    /// </summary>
    public Matrix Projection(float aspect, float near, float far)
        => Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(FovDeg), aspect, near, far);
}

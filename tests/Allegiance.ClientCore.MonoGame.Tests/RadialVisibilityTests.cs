using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P979 — the pure visibility rule behind <c>visuals.radialVisibility</c>.
/// Native's clip is a PLANE: `Camera::SetZClip` negates near/far
/// (<c>engine/camera.cpp:135-139</c>) and the perspective row is
/// `(a·z+b)/−z` with a = zfar/(znear−zfar) (<c>engine/camera.cpp:89-96</c>), the
/// same shape MonoGame's `CreatePerspectiveFieldOfView` has. An object 10.5 km
/// dead ahead is therefore hidden while the same object near the screen edge is
/// drawn, because its view-space depth is distance×cos(off-axis angle) — the
/// pop-in. With the flag ON the decision is the object's true distance from the
/// eye (a sphere), so the angle cannot change it.
/// </summary>
public sealed class RadialVisibilityTests
{
    private const float Aspect = 16f / 9f;
    private static readonly float OffAxisRadians = MathHelper.ToRadians(35f);   // still on screen at 16:9/50°

    /// <summary>Camera-relative projection of a point at <paramref name="distance"/>
    /// along the camera's forward, rotated <paramref name="offAxis"/> about its up
    /// (0 = dead ahead). Returns the view-space depth and the clip-space z/w.</summary>
    private static (float ViewDepth, float NdcZ) Project(Matrix view, Matrix projection,
        Vector3 eye, float distance, float offAxis)
    {
        Vector3 direction = Vector3.Normalize(Vector3.Transform(Vector3.Forward, Matrix.CreateRotationY(offAxis)));
        var clip = Vector4.Transform(new Vector4(eye + direction * distance, 1f), view * projection);
        return (Vector3.Dot(direction * distance, Vector3.Forward), clip.Z / clip.W);
    }

    [Theory]
    [InlineData(7000f, true)]
    [InlineData(9500f, true)]
    [InlineData(10500f, false)]
    public void Radial_rule_draws_by_true_distance(float distance, bool expected)
        => Assert.Equal(expected, RadialVisibility.RadialDrawn(distance, 0f, 10000f));

    [Fact]
    public void Radial_rule_counts_the_object_radius_so_a_giant_does_not_vanish_at_the_boundary()
    {
        Assert.False(RadialVisibility.RadialDrawn(10500f, 0f, 10000f));
        // Nearest surface at 9700 m: still in range, so the rock is drawn even
        // though its centre is past 10 km (it would otherwise pop out of view).
        Assert.True(RadialVisibility.RadialDrawn(10500f, 800f, 10000f));
        Assert.False(RadialVisibility.RadialDrawn(11200f, 800f, 10000f));
    }

    [Fact]
    public void Same_object_centre_and_edge_give_the_same_answer_with_the_flag_on()
    {
        var camera = new CockpitCamera(nearPlane: 1f, farPlane: ExternalCameras.FarPlane);
        var (view, proj) = camera.Matrices(Vector3.Zero, Matrix.Identity, Aspect, out var eye);
        var projection = RadialVisibility.WithFarPlane(proj, RadialVisibility.RadialFarPlane(10000f));
        foreach (float offAxis in new[] { 0f, OffAxisRadians })
        {
            var (viewDepth, ndcZ) = Project(view, projection, eye, 9000f, offAxis);
            Assert.True(ndcZ is >= 0f and <= 1f);           // inside the frustum
            Assert.True(RadialVisibility.RadialDrawn(9000f, 0f, 10000f));
            Assert.True(viewDepth < 9000f || offAxis == 0f); // the plane is angle-dependent
        }
    }

    [Fact]
    public void Flag_off_pins_native_planar_clip_the_10500m_pop_in()
    {
        var camera = new CockpitCamera(nearPlane: 1f, farPlane: ExternalCameras.FarPlane);
        var (view, proj) = camera.Matrices(Vector3.Zero, Matrix.Identity, Aspect, out var eye);

        // Dead ahead: 10.5 km is past native's 10000 m plane, so the GPU clips it.
        var (centreDepth, centreNdc) = Project(view, proj, eye, 10500f, 0f);
        Assert.False(RadialVisibility.PlanarDrawn(centreDepth, 1f, ExternalCameras.FarPlane));
        Assert.True(centreNdc > 1f);

        // The same object 35° off axis: view depth = 10500·cos35° ≈ 8600 m, inside
        // the plane — native DREW it there. Turning the ship made it appear.
        var (edgeDepth, edgeNdc) = Project(view, proj, eye, 10500f, OffAxisRadians);
        Assert.True(edgeDepth < ExternalCameras.FarPlane);
        Assert.True(RadialVisibility.PlanarDrawn(edgeDepth, 1f, ExternalCameras.FarPlane));
        Assert.True(edgeNdc is >= 0f and <= 1f);

        // The flag does not change the OFF answer: the native planar rule is pinned.
        Assert.False(RadialVisibility.IsDrawn(false, 10000f, 10500f, 0f, centreDepth, 1f, ExternalCameras.FarPlane));
        Assert.True(RadialVisibility.IsDrawn(false, 10000f, 10500f, 0f, edgeDepth, 1f, ExternalCameras.FarPlane));
    }

    [Fact]
    public void Flag_on_hides_the_10500m_object_at_both_angles_and_keeps_the_9000m_one()
    {
        var camera = new CockpitCamera(nearPlane: 1f, farPlane: ExternalCameras.FarPlane);
        var (view, proj) = camera.Matrices(Vector3.Zero, Matrix.Identity, Aspect, out var eye);
        float far = RadialVisibility.RadialFarPlane(10000f);
        var projection = RadialVisibility.WithFarPlane(proj, far);

        // The sphere rule is angle-independent.
        foreach (float offAxis in new[] { 0f, OffAxisRadians })
        {
            var (nearDepth, _) = Project(view, projection, eye, 9000f, offAxis);
            Assert.True(RadialVisibility.IsDrawn(true, 10000f, 9000f, 0f, nearDepth, 1f, far));
            var (farDepth, _) = Project(view, projection, eye, 10500f, offAxis);
            Assert.False(RadialVisibility.IsDrawn(true, 10000f, 10500f, 0f, farDepth, 1f, far));
        }
    }

    [Fact]
    public void The_radial_projection_never_clips_an_object_the_sphere_rule_keeps()
    {
        var camera = new CockpitCamera(nearPlane: 1f, farPlane: ExternalCameras.FarPlane);
        var (view, proj) = camera.Matrices(Vector3.Zero, Matrix.Identity, Aspect, out var eye);
        float far = RadialVisibility.RadialFarPlane(10000f);
        var projection = RadialVisibility.WithFarPlane(proj, far);

        // At the range limit dead ahead the object is at view depth == range; the
        // plane must still be beyond it (the margin covers its outward bulk).
        var (depth, ndcZ) = Project(view, projection, eye, 10000f, 0f);
        Assert.True(depth <= far);
        Assert.True(ndcZ <= 1f);
        // A station at the limit with a 700 m radius has its far surface at
        // 10700 m — inside the 11000 m radial plane, so no sliced hull.
        Assert.True(10700f < far);
    }

    [Fact]
    public void Far_plane_rewrite_keeps_near_and_fov()
    {
        var camera = new CockpitCamera(nearPlane: 1f, farPlane: ExternalCameras.FarPlane);
        var (_, proj) = camera.Matrices(Vector3.Zero, Matrix.Identity, Aspect, out _);
        float originalNear = CameraPose.NearFromProjection(proj);
        float originalFov = CameraPose.FovFromProjection(proj);
        float originalFar = CameraPose.FarFromProjection(proj);
        Assert.InRange(originalNear, 0.999f, 1.001f);
        // Recovering a float far from a single-precision matrix lands a couple of
        // metres wide (9998.34 for 10000) — compare with the same tolerance XNA's
        // own matrix gives, not exactly.
        Assert.InRange(originalFar, 9990f, 10010f);

        var widened = RadialVisibility.WithFarPlane(proj, RadialVisibility.RadialFarPlane(12000f));
        Assert.InRange(CameraPose.NearFromProjection(widened), 0.999f, 1.001f);
        Assert.Equal(originalFov, CameraPose.FovFromProjection(widened), 3);
        Assert.InRange(CameraPose.FarFromProjection(widened), 13180f, 13210f);
    }

    [Theory]
    [InlineData(1000f, 10000f)]    // small custom range keeps native's plane (precision)
    [InlineData(5000f, 10000f)]
    [InlineData(10000f, 11000f)]
    [InlineData(20000f, 22000f)]
    public void Radial_far_plane_sits_past_the_range_but_never_below_native(float range, float expected)
        => Assert.Equal(expected, RadialVisibility.RadialFarPlane(range), 0);

    [Fact]
    public void Depth_precision_at_range_is_measured_not_assumed()
    {
        // Cockpit/missile near = 1, Depth24: dz = dw·(f−n)·z²/(f·n).
        float defaultFar = ExternalCameras.FarPlane;
        float at9500Default = RadialVisibility.DepthQuantization(9500f, 1f, defaultFar);
        float at9500Radial = RadialVisibility.DepthQuantization(9500f, 1f, RadialVisibility.RadialFarPlane(10000f));
        float at10000Radial = RadialVisibility.DepthQuantization(10000f, 1f, RadialVisibility.RadialFarPlane(10000f));

        // The radial margin widens the range by 10%, which moves quantization by
        // well under 1% (q ∝ (f−n)/f and near dominates): ~5.4 m at 9.5 km and
        // ~6.0 m at 10 km either way. Coarser than a fighter's hull detail but far
        // smaller than a station feature; the capture in docs/reports/P979_assets/
        // is the visual check.
        Assert.InRange(at9500Default, 5.0f, 5.8f);
        Assert.InRange(at9500Radial, 5.0f, 5.8f);
        Assert.InRange(at10000Radial, 5.5f, 6.3f);
        // Near is what dominates: native's own near=5 at the same range is 5x finer.
        Assert.InRange(RadialVisibility.DepthQuantization(9500f, 5f, defaultFar), 0.8f, 1.2f);
        // Nothing degenerates on a zero/inverted range (defensive).
        Assert.True(float.IsPositiveInfinity(RadialVisibility.DepthQuantization(1f, 1f, 1f)));
    }
}

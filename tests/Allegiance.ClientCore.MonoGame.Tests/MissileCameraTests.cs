using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P295 external-missile camera
/// (<see cref="MissileCamera"/>, the TrackCamera port) — no graphics device,
/// mirroring the <see cref="ChaseCameraOrientationTests"/> discipline (all
/// MonoGame math types here are pure CPU structs).
/// </summary>
/// <remarks>
/// The two things that must hold for "watching a missile fly":
/// 1. the camera frames the missile AND its reference point (the ship/target)
///    together — both project inside the view frustum; and
/// 2. as the missile moves in flight, the camera eye tracks it (the missile is
///    the object the camera is rooted to, so the shot follows it), which is
///    exactly what native's TrackCamera provides for cmExternalMissile.
/// </remarks>
public class MissileCameraTests
{
    /// <summary>Project a world point into NDC (x,y ∈ [-1,1]) through a combined
    /// view*projection, MonoGame row-vector convention. This camera uses the
    /// camera-relative rendering convention (the view looks FROM the origin, so
    /// every object is drawn at <c>worldPos - cameraEye</c>), so the point must
    /// be translated by the eye first to land in the same space the view
    /// operates in.</summary>
    private static Vector2 ProjectToNdc(in Vector3 worldPoint, in Matrix viewProj, in Vector3 eye)
    {
        var clip = Vector4.Transform(new Vector4(worldPoint - eye, 1f), viewProj);
        float w = clip.W;
        if (Math.Abs(w) < 1e-6f) return new Vector2(float.NaN, float.NaN);
        return new Vector2(clip.X / w, clip.Y / w);
    }

    private static bool InFrustum(in Vector2 ndc) =>
        ndc.X >= -1f && ndc.X <= 1f && ndc.Y >= -1f && ndc.Y <= 1f;

    private const float Aspect = 1.777f;   // 16:9

    [Fact]
    public void BothPoints_ProjectInsideFrustum_WhenSeparatedAlongAxis()
    {
        var cam = new MissileCamera(framingFactor: 0.35f);
        var missile = new Vector3(0f, 0f, 0f);
        var ship = new Vector3(0f, 0f, 1000f);   // missile flying straight at the ship
        var (view, proj) = cam.Matrices(missile, ship, Aspect, out var eye);
        var viewProj = view * proj;

        Assert.True(InFrustum(ProjectToNdc(missile, viewProj, eye)),
            $"missile fell out of the frustum: {ProjectToNdc(missile, viewProj, eye)}");
        Assert.True(InFrustum(ProjectToNdc(ship, viewProj, eye)),
            $"ship fell out of the frustum: {ProjectToNdc(ship, viewProj, eye)}");

        // The camera must not be sitting on either object (a valid camera is
        // offset behind-and-to-the-side of the missile, per TrackCamera).
        Assert.True(Vector3.Distance(eye, missile) > 1f, "camera eye coincides with the missile");
        Assert.True(Vector3.Distance(eye, ship) > 1f, "camera eye coincides with the ship");
    }

    [Fact]
    public void Eye_TracksTheMissile_AsItFliesTowardTheShip()
    {
        var cam = new MissileCamera(framingFactor: 0.35f);
        var ship = new Vector3(0f, 0f, 1000f);

        var (_, _) = cam.Matrices(new Vector3(0f, 0f, 0f), ship, Aspect, out var eye0);
        // Missile advances 200 units toward the ship along the axis.
        var (_, _) = cam.Matrices(new Vector3(0f, 0f, 200f), ship, Aspect, out var eye1);

        // The missile moves +Z; the camera is rooted to the missile, so its eye
        // must move the same way (eye.z increases), proving the shot follows the
        // flying missile rather than staying fixed.
        Assert.True(eye1.Z > eye0.Z + 50f,
            $"camera eye did not track the missile's forward flight (eye0.Z={eye0.Z:F1}, eye1.Z={eye1.Z:F1})");

        // And the whole pair is still in view after the move.
        var (view1, proj1) = cam.Matrices(new Vector3(0f, 0f, 200f), ship, Aspect, out var eye1b);
        var vp1 = view1 * proj1;
        Assert.True(InFrustum(ProjectToNdc(new Vector3(0f, 0f, 200f), vp1, eye1b)), "missile out of view after tracking");
        Assert.True(InFrustum(ProjectToNdc(ship, vp1, eye1b)), "ship out of view after tracking");
    }

    [Fact]
    public void CoincidentPoints_AreGuarded_ProduceNoNaN()
    {
        var cam = new MissileCamera(framingFactor: 0.35f);
        var (view, proj) = cam.Matrices(new Vector3(5f, 5f, 5f), new Vector3(5f, 5f, 5f), Aspect, out var eye);
        Assert.False(float.IsNaN(view.M11) || float.IsNaN(proj.M11) || float.IsNaN(eye.X),
            "degenerate coincident missile/ship produced NaN");
    }

    [Fact]
    public void AxisParallelToWorldUp_UsesFallbackUp_ProduceNoNaN()
    {
        // Missile directly below/above the ship -> the tracking axis is vertical,
        // parallel to world-up; the fallback 'up' must kick in without NaNs.
        var cam = new MissileCamera(framingFactor: 0.35f);
        var (view, proj) = cam.Matrices(new Vector3(0f, 0f, 0f), new Vector3(0f, 1000f, 0f), Aspect, out var eye);
        Assert.False(float.IsNaN(view.M11) || float.IsNaN(view.M22) || float.IsNaN(eye.Y),
            "vertical tracking axis produced NaN view/camera");
        var vp = view * proj;
        Assert.True(InFrustum(ProjectToNdc(new Vector3(0f, 0f, 0f), vp, eye)), "missile out of view on vertical axis");
        Assert.True(InFrustum(ProjectToNdc(new Vector3(0f, 1000f, 0f), vp, eye)), "ship out of view on vertical axis");
    }

    [Fact]
    public void JustLaunchedMissile_SmallSeparation_IsNotSwallowedByNearPlane()
    {
        // P340/U1 regression. Immediately after launch the missile↔ship
        // separation is still small (e.g. 5 units), so the camera eye sits only
        // ~2×framingFactor×separation ≈ 3.5 units behind the missile. With the
        // old nearPlane=10 that put the missile INSIDE the near clip volume
        // (view-space depth ~3.5 < near 10), so missile-cam rasterized nothing
        // at exactly the moment the player toggled to it. nearPlane now defaults
        // to 1 (matching the cockpit camera), so the just-launched missile's
        // view-space depth must clear the near plane.
        //
        // NOTE: this must be checked via view-space DEPTH, not NDC x/y — a point
        // closer than the near plane still projects to valid x/y (it is inside
        // the view pyramid), so a frustum-membership test on x/y alone cannot
        // see a near-plane clip.
        var cam = new MissileCamera(framingFactor: 0.35f);   // near=1 (the fix)
        var missile = new Vector3(0f, 0f, 0f);
        var ship = new Vector3(0f, 0f, 5f);                  // separation 5, right after launch
        var (view, proj) = cam.Matrices(missile, ship, Aspect, out var eye);

        // Premise check: the eye really is only ~3.5u behind the missile (i.e.
        // well inside the OLD 10-unit near plane), which is what made U1 real.
        float eyeDist = Vector3.Distance(eye, missile);
        Assert.True(eyeDist is > 1f and < 10f,
            $"expected the just-launched eye to sit inside the old 10-unit near plane, got {eyeDist:F2}");

        // Near-plane membership lives in the PROJECTION (clip space), not the
        // view matrix — a point closer than near maps to NDC z < -1 (RH), where
        // it is clipped by the rasterizer. So project through view*proj and
        // assert the missile's clip-space z/w is on the visible (> -1) side.
        var clip = Vector4.Transform(new Vector4(missile - eye, 1f), view * proj);
        float ndcZ = clip.Z / clip.W;
        Assert.True(clip.W > 0f && ndcZ > -1f,
            $"just-launched missile swallowed by the near plane (clip ndc-z {ndcZ:F2} <= -1)");
        Assert.True(ndcZ < 1f,
            $"mis-built premise: expected the missile inside the OLD near=10 (ndc-z {ndcZ:F2})");
    }
}

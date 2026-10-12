using System;
using Microsoft.Xna.Framework;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P612 — pure tests for the smooth camera-transition easing
/// (<see cref="CameraEasing"/>) and the pose round-trip (<see cref="CameraPose"/>).
/// Verifies the brief's properties: monotonic (no overshoot), ends at the target,
/// and the "instant" setting (no easing).
/// </summary>
public class P612CameraEasingTests
{
    [Fact]
    public void Blend_instant_returns_target_immediately()
    {
        // duration <= 0 = the native-purist "instant" cut: no easing.
        Assert.Equal(10f, CameraEasing.Blend(0f, 10f, 0.0, 0.0));
        Assert.Equal(10f, CameraEasing.Blend(0f, 10f, 999.0, -1.0));
    }

    [Fact]
    public void Blend_starts_at_from_and_ends_at_target()
    {
        const float from = 0f, to = 10f;
        double dur = 0.7;
        Assert.Equal(from, CameraEasing.Blend(from, to, 0.0, dur), precision: 5);
        Assert.Equal(to, CameraEasing.Blend(from, to, dur, dur), precision: 4);
        Assert.Equal(to, CameraEasing.Blend(from, to, dur + 5.0, dur), precision: 4);
    }

    [Fact]
    public void Blend_is_monotonic_and_never_overshoots()
    {
        const float from = 0f, to = 10f;
        double dur = 0.7;
        float prev = from;
        double lastDist = Math.Abs(to - prev);
        for (int i = 1; i <= 40; i++)
        {
            double t = dur * i / 40.0;
            float v = CameraEasing.Blend(from, to, t, dur);
            // monotonic toward the target (no overshoot: never past `to`)
            Assert.True(v >= prev - 1e-4f, $"not monotonic at t={t}: {v} < {prev}");
            Assert.True(v <= to + 1e-4f, $"overshoot at t={t}: {v} > {to}");
            double dist = Math.Abs(to - v);
            Assert.True(dist <= lastDist + 1e-4f, $"distance grew at t={t}");
            lastDist = dist;
            prev = v;
        }
    }

    [Fact]
    public void Blend_vector_and_quaternion_are_instant_when_instant()
    {
        var a = new Vector3(1f, 2f, 3f);
        var b = new Vector3(9f, -5f, 0f);
        Assert.Equal(b, CameraEasing.Blend(a, b, 0.0, 0.0));
        var qa = Quaternion.CreateFromYawPitchRoll(0f, 0f, 0f);
        var qb = Quaternion.CreateFromYawPitchRoll(1f, 0.5f, -0.2f);
        Assert.Equal(qb, CameraEasing.Blend(qa, qb, 0.0, 0.0));
    }

    [Fact]
    public void CameraPose_captures_and_rebuilds_a_view_projection()
    {
        var eye = new Vector3(10f, -4f, 30f);
        var target = new Vector3(0f, 0f, 0f);
        var view = Matrix.CreateLookAt(eye, target, Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50f), 16f / 9f, 1f, 5000f);

        var pose = CameraPose.FromViewProjection(view, proj, eye);

        // FOV round-trips (50°).
        Assert.Equal(50f, pose.FovDeg, precision: 2);
        // The rebuilt view puts the eye at the original eye and looks along the
        // same direction (so a point at the target projects to screen centre).
        var rebuilt = pose.View();
        var rebuiltEye = CameraPose.EyeFromView(rebuilt);
        Assert.True((rebuiltEye - eye).Length() < 1e-2f, $"eye mismatch {(rebuiltEye - eye).Length()}");
        var rebuiltProj = pose.Projection(16f / 9f, CameraPose.NearFromProjection(proj), CameraPose.FarFromProjection(proj));
        Assert.Equal(CameraPose.FovFromProjection(proj), CameraPose.FovFromProjection(rebuiltProj), precision: 2);
    }

    [Fact]
    public void CameraPose_relative_view_round_trips_a_camera_relative_flight_view()
    {
        // The production flight cameras are camera-relative: the view looks from
        // the ORIGIN and draws subtract the eye from object positions. Rebuilding
        // must reproduce that view exactly (no eye translation), or the whole
        // scene drifts by the camera's distance from the sector origin.
        var eye = new Vector3(1200f, -350f, 2400f);
        var dir = new Vector3(0.3f, 0.1f, -1f);
        var up = Vector3.Normalize(new Vector3(0.1f, 1f, 0.05f));
        var view = Matrix.CreateLookAt(Vector3.Zero, dir, up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60f), 16f / 9f, 1f, 5000f);

        var rebuilt = CameraPose.FromViewProjection(view, proj, eye).RelativeView();

        var worldPoint = new Vector3(1500f, -200f, 1800f);
        var a = Vector3.Transform(worldPoint - eye, view);
        var b = Vector3.Transform(worldPoint - eye, rebuilt);
        Assert.True((a - b).Length() < 1e-2f, $"camera-relative point moved by {(a - b).Length()}");
        Assert.True(rebuilt.Translation.Length() < 1e-4f, "camera-relative view must not translate");
    }

    [Fact]
    public void CameraPose_forward_points_where_the_camera_looks()
    {
        var eye = new Vector3(0f, 0f, 50f);
        var target = Vector3.Zero;
        var view = Matrix.CreateLookAt(eye, target, Vector3.Up);
        var pose = CameraPose.FromViewProjection(view, Matrix.Identity, eye);
        var fwd = pose.Forward;
        fwd.Normalize();
        // looking from +Z toward the origin ⇒ forward ≈ −Z
        Assert.True(fwd.Z < 0f, $"forward.z should be < 0, got {fwd.Z}");
        Assert.True(fwd.Length() > 0.99f);
    }
}

using System;
using Microsoft.Xna.Framework;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P565 — pure tests for the hull inspector's orbit/zoom/pan/focus camera.</summary>
public class InspectorOrbitCameraTests
{
    private static void AssertVec(Vector3 a, Vector3 b, float tol = 1e-3f)
        => Assert.True((a - b).Length() <= tol, $"expected {a} ~ {b}, diff {(a - b).Length()}");

    [Fact]
    public void Default_eye_sits_on_plus_Z_at_level_elevation()
    {
        var cam = new InspectorOrbitCamera();
        var b = cam.Basis();
        AssertVec(b.Eye, new Vector3(0f, 0f, cam.Distance));
        // elevation 0 => eye at target's level, so eye.Y == target.Y == 0
        Assert.Equal(0f, b.Eye.Y, precision: 5);
    }

    [Fact]
    public void Orbit_yaw_moves_the_eye_around_the_target_and_preserves_distance()
    {
        var cam = new InspectorOrbitCamera();
        float d0 = cam.Distance;
        cam.Orbit(100, 0);            // drag right 100px -> yaw decreases
        var b = cam.Basis();
        Assert.Equal(0f, b.Eye.Y, precision: 4);
        Assert.Equal(d0, (b.Eye - cam.Target).Length(), precision: 4);
        // yaw<0 means eye swung toward -X (camera moved right around target)
        Assert.True(b.Eye.X < 0f);
    }

    [Fact]
    public void Orbit_elevation_is_clamped_within_poles()
    {
        var cam = new InspectorOrbitCamera();
        cam.Orbit(0, 50000);          // huge drag up -> clamp near +pi/2
        var b = cam.Basis();
        Assert.True(MathF.Abs(cam.Pitch) < MathF.PI / 2f);
        Assert.True(cam.Pitch > 0f);
        Assert.True(b.Eye.Y > 0f);
    }

    [Fact]
    public void Zoom_in_shrinks_distance_and_out_grows_it()
    {
        var cam = new InspectorOrbitCamera();
        float d0 = cam.Distance;
        cam.Zoom(+2);                 // scroll up = zoom in
        Assert.True(cam.Distance < d0);
        float dIn = cam.Distance;
        cam.Zoom(-2);                 // scroll down = zoom out
        Assert.True(cam.Distance > dIn);
    }

    [Fact]
    public void Zoom_is_clamped_to_min_max()
    {
        var cam = new InspectorOrbitCamera { MinDistance = 1f, MaxDistance = 100f };
        cam.Distance = 50f;
        cam.Zoom(10000);
        Assert.True(cam.Distance >= 1f);
        cam.Zoom(-10000);
        Assert.True(cam.Distance <= 100f);
    }

    [Fact]
    public void Pan_moves_target_without_changing_distance_or_yaw()
    {
        var cam = new InspectorOrbitCamera();
        cam.Distance = 1000f;
        float y0 = cam.Yaw, p0 = cam.Pitch;
        cam.Pan(10, 0);
        Assert.NotEqual(Vector3.Zero, cam.Target);
        Assert.Equal(y0, cam.Yaw, precision: 4);
        Assert.Equal(p0, cam.Pitch, precision: 4);
        Assert.Equal(1000f, cam.Distance, precision: 4);
    }

    [Fact]
    public void Focus_repoints_the_rig_at_a_new_point()
    {
        var cam = new InspectorOrbitCamera();
        cam.Focus(new Vector3(5f, -3f, 2f));
        AssertVec(cam.Target, new Vector3(5f, -3f, 2f));
        var b = cam.Basis();
        // eye still one distance away from the new target
        Assert.Equal(cam.Distance, (b.Eye - cam.Target).Length(), precision: 4);
    }

    [Fact]
    public void Reset_restores_native_level_on_axis_framing()
    {
        var cam = new InspectorOrbitCamera();
        cam.Orbit(200, 200);
        cam.Zoom(3);
        cam.Focus(new Vector3(1f, 1f, 1f));
        cam.Reset(500f);
        Assert.Equal(0f, cam.Yaw);
        Assert.Equal(0f, cam.Pitch);
        AssertVec(cam.Target, Vector3.Zero);
        Assert.Equal(500f, cam.Distance, precision: 3);
    }

    [Fact]
    public void Auto_orbit_advances_yaw_over_time()
    {
        var cam = new InspectorOrbitCamera { AutoOrbit = true, AutoOrbitSpeedRadPerSec = 1f };
        float y0 = cam.Yaw;
        cam.Step(0.5);
        Assert.Equal(y0 + 0.5f, cam.Yaw, precision: 4);
    }

    [Fact]
    public void Frame_radius_sizes_distance_from_fov()
    {
        var cam = new InspectorOrbitCamera();
        cam.FrameRadius(200f, MathHelper.ToRadians(50f));
        float halfTan = MathF.Tan(MathHelper.ToRadians(25f));
        Assert.Equal(200f / halfTan * 1.35f, cam.Distance, precision: 1);
        Assert.Equal(0f, cam.Pitch);
    }
}

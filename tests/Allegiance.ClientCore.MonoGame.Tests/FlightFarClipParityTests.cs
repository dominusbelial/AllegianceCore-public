using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Flight cameras' far clip matches native: the world camera is
/// <c>SetZClip(5.0f, 10000.0f)</c> (<c>WinTrek/WinTrek.cpp:323</c>). The port used 6000
/// until 2026-11-08, which hid an aleph 6-10 km dead ahead; it appeared only when the
/// ship turned so it sat near the screen edge, because the far clip is a PLANE
/// (view-space depth = distance x cos(off-axis angle)). The radial "no pop-in" shape is
/// a separate, flagged visual enhancement (P979); this pins the native range.
/// </summary>
public sealed class FlightFarClipParityTests
{
    private const float Aspect = 16f / 9f;

    /// <summary>Clip-space depth of a point straight ahead of the camera at
    /// <paramref name="distance"/>: inside the far clip when z/w is in [0, 1].</summary>
    private static bool InsideDepthRange(Matrix view, Matrix projection, Vector3 eye, Vector3 forward, float distance)
    {
        var clip = Vector4.Transform(new Vector4(eye + forward * distance, 1f), view * projection);
        float z = clip.Z / clip.W;
        return clip.W > 0 && z >= 0f && z <= 1f;
    }

    [Fact]
    public void Native_far_clip_is_10000()
    {
        Assert.Equal(10000f, ExternalCameras.FarPlane);
        Assert.Equal(ExternalCameras.FarPlane, LinearDepth.DefaultFar);
    }

    [Fact]
    public void Soft_particle_fade_keeps_its_24_unit_world_softness()
    {
        Assert.Equal(24f, LinearDepth.SoftnessNormalized * (LinearDepth.DefaultFar - LinearDepth.DefaultNear), 3);
    }

    [Theory]
    [InlineData(7000f, true)]    // hidden by the old 6000 plane, visible in native
    [InlineData(9500f, true)]
    [InlineData(10500f, false)]  // past native's 10000 plane
    public void Cockpit_camera_draws_dead_ahead_objects_out_to_native_range(float distance, bool visible)
    {
        var cam = new CockpitCamera();
        var (view, proj) = cam.Matrices(Vector3.Zero, Matrix.Identity, Aspect, out var eye);
        Assert.Equal(visible, InsideDepthRange(view, proj, eye, Vector3.Forward, distance));
    }

    [Theory]
    [InlineData(7000f, true)]
    [InlineData(9500f, true)]
    [InlineData(10500f, false)]
    public void Chase_camera_draws_dead_ahead_objects_out_to_native_range(float distance, bool visible)
    {
        // The game's own chase camera parameters (Game1.Lifecycle.cs).
        var cam = new ChaseCamera(distanceBehind: 350f, heightAbove: 90f, nearPlane: 10f, farPlane: ExternalCameras.FarPlane);
        var (view, proj) = cam.Matrices(Vector3.Zero, Matrix.Identity, Aspect, 0f, out var eye);
        // Measure from the eye along the camera's own forward axis.
        var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.Forward, Matrix.Invert(view)));
        Assert.Equal(visible, InsideDepthRange(view, proj, eye, forward, distance));
    }
}

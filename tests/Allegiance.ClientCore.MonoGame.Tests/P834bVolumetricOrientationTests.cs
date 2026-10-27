using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P834b — the volumetric near-plane / scene clamp must hold for ANY camera
/// orientation.
///
/// P834 clamped the raymarch interval to the visible depth range but measured the
/// view-axis cosine as <c>-rd.z</c>. <c>rd</c> is a WORLD-space ray direction
/// (the renderer passes camera-relative WORLD positions with the eye at the
/// origin; the camera rotation lives only in the view matrix), so that expression
/// is the cosine only for a camera looking down world −Z — the one orientation
/// P834 measured. Flying toward world +Z the term is negative, the shader's
/// <c>1/max(1e-4, cos)</c> became 10⁴, <c>tNear</c> jumped past the sphere exit
/// and every volume was <c>clip()</c>ped: explosions vanished and the volumetric
/// pass collapsed to ~0.1 ms (measured at yaw 135°/180° in
/// <c>docs/reports/P834_VOLUMETRIC_NEAR_COST.md</c> §9).
///
/// The fix hands the shader the camera's forward axis (<c>ViewForward</c>, derived
/// on the CPU from the view matrix) and the clamp uses
/// <c>dot(rd, ViewForward)</c>. These tests drive the CPU mirror
/// (<see cref="VolumetricGeometry.ClampVisibleRange"/>) through real
/// <see cref="Matrix.CreateLookAt"/> view matrices at many orientations; the
/// first two fail against the P834 formula at every heading that is not world
/// −Z.
/// </summary>
public class P834bVolumetricOrientationTests
{
    // The harness/production numbers: a bomber plume (r = 165 u) 60 u ahead, the
    // game's near plane, and a "far" scene depth (no occluder) as the shipped
    // 1×1 white depth texture decodes to.
    private const float Radius = 165f;
    private const float Distance = 60f;
    private const float Near = 1f;
    private const float FarPlane = 7900f;
    private static readonly float InvRange = 1f / (FarPlane - Near);
    private const float SceneNormalized = 1f;      // white depth → far scene

    /// <summary>Yaw about +Y and pitch about the camera's right axis, the same
    /// parametrization the P834b harness uses for its orientation sweep.</summary>
    private static (SimVec Forward, SimVec Up, SimVec Right) Basis(float yawDeg, float pitchDeg)
    {
        float yaw = MathHelper.ToRadians(yawDeg), pitch = MathHelper.ToRadians(pitchDeg);
        // Row-vector convention: yaw about +Y first, then pitch about the
        // camera's own right axis — the harness/renderer parametrization.
        var rot = Matrix.CreateRotationX(pitch) * Matrix.CreateRotationY(yaw);
        SimVec Dir(float x, float y, float z)
        {
            var v = Vector3.TransformNormal(new Vector3(x, y, z), rot);
            return new SimVec(v.X, v.Y, v.Z);
        }
        return (Dir(0, 0, -1), Dir(0, 1, 0), Dir(1, 0, 0));
    }

    private static Matrix View(float yawDeg, float pitchDeg)
    {
        var (fwd, up, _) = Basis(yawDeg, pitchDeg);
        return Matrix.CreateLookAt(Vector3.Zero,
            new Vector3(fwd.X, fwd.Y, fwd.Z), new Vector3(up.X, up.Y, up.Z));
    }

    private static SimVec ViewForward(Matrix view)
        => VolumetricGeometry.ViewForward(view.M13, view.M23, view.M33);

    /// <summary>The camera-relative blast centre: <see cref="Distance"/> straight
    /// ahead, exactly what the renderer feeds the shader.</summary>
    private static SimVec BlastCentre(SimVec forward)
        => forward * Distance;

    /// <summary>The same ray–sphere interval the shader's <c>RaySphere</c> gets
    /// with the eye inside the bounding sphere (t0 = 0, t1 = exit).</summary>
    private static (float T0, float T1) SphereChord(SimVec centre, SimVec rayDir)
    {
        float tca = centre * rayDir;
        float d2 = centre * centre - tca * tca;
        float thc = MathF.Sqrt(MathF.Max(Radius * Radius - d2, 0f));
        return (MathF.Max(tca - thc, 0f), tca + thc);
    }

    private static (float T0, float T1) ClampFor(float yawDeg, float pitchDeg, SimVec rayDirWorld)
    {
        var forward = Basis(yawDeg, pitchDeg).Forward;
        var centre = BlastCentre(forward);
        var (t0, t1) = SphereChord(centre, rayDirWorld);
        var r = VolumetricGeometry.ClampVisibleRange(
            t0, t1, Near, SceneNormalized, InvRange, rayDirWorld, ViewForward(View(yawDeg, pitchDeg)));
        return (r.Start, r.End);
    }

    public static IEnumerable<object[]> Orientations()
    {
        foreach (var yaw in new float[] { 0f, 30f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
            yield return new object[] { yaw, 0f };
        foreach (var pitch in new float[] { -75f, -40f, -20f, 20f, 40f, 75f })
            yield return new object[] { 37f, pitch };
        yield return new object[] { 135f, 30f };
        yield return new object[] { 200f, -55f };
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void View_axis_cosine_is_one_for_a_ray_that_looks_where_the_camera_looks(float yaw, float pitch)
    {
        var forward = Basis(yaw, pitch).Forward;
        float cos = forward * ViewForward(View(yaw, pitch));
        Assert.Equal(1f, cos, 3);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void Clamp_is_identical_at_every_orientation_for_the_same_on_screen_pixel(float yaw, float pitch)
    {
        // A ray through the blast centre: (0,0) in view space at every heading,
        // so the marched interval must be the same one.
        var forward = Basis(yaw, pitch).Forward;
        var (t0, t1) = SphereChord(BlastCentre(forward), forward);
        var clamped = ClampFor(yaw, pitch, forward);

        Assert.True(clamped.T1 > clamped.T0,
            $"yaw {yaw} pitch {pitch}: the volume was clipped away (t0 {clamped.T0}, t1 {clamped.T1})");
        Assert.Equal(MathF.Max(t0, Near), clamped.T0, 2);      // near plane only
        Assert.Equal(t1, clamped.T1, 2);                       // far scene never clamps
        Assert.Equal(t1 - MathF.Max(t0, Near), clamped.T1 - clamped.T0, 2);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void Off_axis_rays_clamp_identically_at_every_orientation(float yaw, float pitch)
    {
        // 25° off the view axis, in the camera's own basis: the same screen pixel
        // at every heading. The marched span must not change.
        var (forward, up, right) = Basis(yaw, pitch);
        float a = MathHelper.ToRadians(25f);
        var ray = (forward * MathF.Cos(a) + up * MathF.Sin(a)).Normalize();

        var (t0, t1) = SphereChord(BlastCentre(forward), ray);
        var clamped = ClampFor(yaw, pitch, ray);

        Assert.True(clamped.T1 > clamped.T0, $"yaw {yaw} pitch {pitch}: clipped away");
        // cos = cos 25° → tNear = Near / cos ≈ 1.10, and the far scene is far
        // away, so the sphere exit survives unchanged.
        Assert.Equal(t1, clamped.T1, 2);
        Assert.Equal(Near / MathF.Cos(a), clamped.T0, 2);
    }

    [Fact]
    public void The_P834_formula_would_have_clipped_the_volume_looking_toward_world_plus_z()
    {
        // Regression witness: the OLD cosine for a camera at yaw 180 (looking
        // down world +Z) is -rd.z = -1, so P834's 1/max(1e-4, cos) = 10⁴ and
        // tNear = 10⁴ ≫ the sphere exit → nothing visible (the bug).
        var forward = Basis(180f, 0f).Forward;
        var (t0, t1) = SphereChord(BlastCentre(forward), forward);
        float oldCos = -forward.Z;                      // P834's "-rd.z"
        float oldInvZ = 1f / MathF.Max(1e-4f, oldCos);
        var oldRange = VolumetricGeometry.ClampMarchRange(t0, t1, Near * oldInvZ, FarPlane * oldInvZ);
        Assert.False(oldRange.Visible);

        // The fixed mirror keeps the plume: the near plane clamps the start,
        // the sphere exit the end.
        var fixedRange = VolumetricGeometry.ClampVisibleRange(
            t0, t1, Near, SceneNormalized, InvRange, forward, ViewForward(View(180f, 0f)));
        Assert.True(fixedRange.Visible);
        Assert.Equal(t1, fixedRange.End, 2);

        // ... and the same at 90° (sideways), where the old term was ~0.
        var side = Basis(90f, 0f).Forward;
        var (s0, s1) = SphereChord(BlastCentre(side), side);
        var sideways = VolumetricGeometry.ClampVisibleRange(
            s0, s1, Near, SceneNormalized, InvRange, side, ViewForward(View(90f, 0f)));
        Assert.True(sideways.Visible);
        Assert.Equal(s1, sideways.End, 2);
    }

    [Fact]
    public void View_forward_is_the_camera_forward_axis_at_any_heading()
    {
        Assert.Equal(-1f, ViewForward(View(0f, 0f)).Z, 3);         // world -Z camera
        Assert.Equal(1f, ViewForward(View(180f, 0f)).Z, 3);        // world +Z camera
        Assert.Equal(-1f, ViewForward(View(90f, 0f)).X, 3);        // looking down -X
        Assert.Equal(1f, ViewForward(View(-90f, 0f)).X, 3);
        var up = ViewForward(View(0f, 80f));
        Assert.True(up.Y > 0.9f, $"pitch up should look up, got {up}");   // pitched up
        var down = ViewForward(View(0f, -80f));
        Assert.True(down.Y < -0.9f, $"pitch down should look down, got {down}");
    }

    [Fact]
    public void View_axis_inverse_cosine_matches_the_shader_guard()
    {
        // The shader uses 1/max(1e-4, cos): a backward ray must not divide by
        // ~0 (it would become 10⁴ and wipe the volume out).
        var back = new SimVec(0f, 0f, 1f);
        var forward = new SimVec(0f, 0f, -1f);
        Assert.Equal(10_000f, VolumetricGeometry.ViewAxisInvCos(back, forward), 1);
        Assert.Equal(1f, VolumetricGeometry.ViewAxisInvCos(forward, forward), 4);
    }

    [Fact]
    public void The_near_plane_and_the_scene_still_clamp_through_the_view_axis()
    {
        // 40° off the view axis (a corner-ish screen pixel), a hull 40 u ahead of
        // the lens, a sphere chord 0..330: the near plane starts the march and
        // the hull ends it — both in RAY distance, i.e. scaled by 1/cos(40°).
        float yaw = 135f, a = MathHelper.ToRadians(40f);
        var (forward, up, _) = Basis(yaw, 0f);
        var viewForward = ViewForward(View(yaw, 0f));
        var ray = (forward * MathF.Cos(a) + up * MathF.Sin(a)).Normalize();
        var r = VolumetricGeometry.ClampVisibleRange(
            0f, 330f, Near, LinearDepthNormalized(40f), InvRange, ray, viewForward);
        float cos = ray * viewForward;          // = cos 40°
        Assert.Equal(MathF.Cos(a), cos, 3);
        Assert.Equal(Near / cos, r.Start, 2);
        Assert.Equal(40f / cos, r.End, 2);      // 40 u along the VIEW axis
        Assert.True(r.Visible);
    }

    /// <summary>Mirrors <c>LinearDepth.Normalize(dist, Near, FarPlane)</c> — what
    /// the depth pass stores for a hull at <paramref name="distance"/>.</summary>
    private static float LinearDepthNormalized(float distance)
        => (distance - Near) / (FarPlane - Near);
}

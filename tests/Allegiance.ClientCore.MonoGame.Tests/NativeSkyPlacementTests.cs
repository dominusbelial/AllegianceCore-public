using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P576b — the native sky's CAMERA-RELATIVE placement. The flight view keeps
/// the eye at the origin (ChaseCamera.cs:205, CockpitCamera.cs:78), so the
/// sky — a backdrop centred on the camera at infinity — must be placed around
/// the origin and NEVER offset by <c>cameraEye</c> (which in view space would
/// push it thousands of units away, past the far plane: the P576b hard-edged
/// slabs / black wedges). Each placement helper deliberately ignores
/// <c>cameraEye</c>; these tests pin that "takes but ignores" invariant.
///
/// Failing-first: each camera-eye-independence test FAILS on the buggy
/// (pre-fix / deployed) formulation that adds <c>cameraEye</c> to the position
/// or world translation, and PASSES on the fixed camera-relative code.
/// </summary>
public class NativeSkyPlacementTests
{
    private static readonly Vector3 EyeA = new(0f, 0f, 0f);
    private static readonly Vector3 EyeB = new(5000f, 0f, 3000f);   // 5000+ u apart

    private static readonly NativeStar Star =
        new(0.603977f, -0.716095f, 0.041994f, 0.8743f);   // arbitrary, incl. non-unit dir

    [Fact]
    public void StarShellPosition_IsIdenticalAcrossTwoEyes()
    {
        // THE P576b invariant: the sky is drawn identically at two camera
        // positions 5000 u apart (same direction). Fails if cameraEye leaks in.
        Assert.Equal(
            NativeSkyPlacement.StarShellPosition(Star, 7000f, EyeA),
            NativeSkyPlacement.StarShellPosition(Star, 7000f, EyeB));
    }

    [Fact]
    public void StarShellPosition_EqualsDirectionTimesRadius()
    {
        var p = NativeSkyPlacement.StarShellPosition(Star, 7700f, EyeA);
        var dir = new Vector3(Star.DirX, Star.DirY, Star.DirZ);
        var expected = dir * 7700f;
        Assert.Equal(expected, p);
        // Position sits exactly on the shell: distance from origin == radius×|dir|.
        Assert.Equal(7700f * dir.Length(), p.Length(), 0.01f);
    }

    [Fact]
    public void PlanetShellCenter_IsIdenticalAcrossTwoEyes()
    {
        var dir = new Vector3(0.605f, -0.714f, 0.055f);
        Assert.Equal(
            NativeSkyPlacement.PlanetShellCenter(dir, 6900f, EyeA),
            NativeSkyPlacement.PlanetShellCenter(dir, 6900f, EyeB));
    }

    [Fact]
    public void PlanetShellCenter_EqualsDirectionTimesDist()
    {
        var dir = new Vector3(0.625f, -0.771f, 0.037f);
        var c = NativeSkyPlacement.PlanetShellCenter(dir, 6850f, EyeA);
        Assert.Equal(dir * 6850f, c);
        Assert.Equal(6850f * dir.Length(), c.Length(), 0.001f);
    }

    [Fact]
    public void DomeWorld_HasZeroTranslation()
    {
        // The globe surrounds the camera at the origin: Identity world, never a
        // cameraEye translation (which is what offsets it past the far plane).
        var w = NativeSkyPlacement.DomeWorld(EyeB);
        Assert.True(w.Translation.Length() < 1e-5f,
            $"dome world must carry NO translation, got {w.Translation}");
    }

    [Fact]
    public void DomeWorld_IsIdenticalAcrossTwoEyes()
    {
        Assert.Equal(NativeSkyPlacement.DomeWorld(EyeA), NativeSkyPlacement.DomeWorld(EyeB));
    }

    [Fact]
    public void BillboardAxes_AreOrthonormalToFacing()
    {
        var dir = Vector3.Normalize(new Vector3(0.5f, -0.866f, 0.031f));
        var right = NativeSkyPlacement.BillboardRight(dir);
        var up = NativeSkyPlacement.BillboardUp(dir, right);
        Assert.Equal(1f, right.Length(), 0.0001f);
        Assert.Equal(1f, up.Length(), 0.0001f);
        Assert.True(Math.Abs(Vector3.Dot(right, dir)) < 1e-4f);
        Assert.True(Math.Abs(Vector3.Dot(up, dir)) < 1e-4f);
        Assert.True(Math.Abs(Vector3.Dot(right, up)) < 1e-4f);
    }

    [Fact]
    public void SkyProjection_EnclosesTheDomeRadius()
    {
        // Scene far plane ~6000 is NEARER than the 7500 dome shell; the sky's own
        // projection must hold the whole dome inside [near, far].
        var sceneProj = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(60f), 16f / 9f, 0.1f, 6500f);
        var sky = NativeSkyPlacement.SkyProjection(sceneProj);

        // Unproject-friendly check: extract the reconstructed near/far/FOV.
        // For CreatePerspectiveFieldOfView(fov, aspect, near, far):
        //   M33 = far/(near-far), M43 = near*far/(near-far); near = M43/M33 ...
        // We just assert the projected z of a point at the dome radius (7500)
        // in front of the camera falls strictly inside (0,1] (NDC), i.e. not
        // far-clipped, and that a point at near-distance is valid.
        Vector4 domeClip = Vector4.Transform(new Vector4(0f, 0f, -7500f, 1f), sky);
        float ndcZ = domeClip.Z / domeClip.W;
        Assert.InRange(ndcZ, 0f, 1f);

        // Same point through the SCENE projection would be far-clipped
        // (a dome radius point beyond the 6500 far plane).
        Vector4 sceneClip = Vector4.Transform(new Vector4(0f, 0f, -7500f, 1f), sceneProj);
        Assert.True(sceneClip.Z / sceneClip.W > 1f,
            "expect the dome radius to exceed the scene far plane (precondition)");
    }

    [Fact]
    public void SkyProjection_RecoversSceneFovAndAspect()
    {
        var sceneProj = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(50f), 4f / 3f, 0.1f, 8000f);
        var sky = NativeSkyPlacement.SkyProjection(sceneProj);

        float skyFocus = sky.M22;                       // cot(vFov/2)
        float skyFov = 2f * MathF.Atan(1f / skyFocus);
        Assert.Equal(MathHelper.ToRadians(50f), skyFov, 0.0001f);
        float skyAspect = sky.M22 / sky.M11;
        Assert.Equal(4f / 3f, skyAspect, 0.0001f);
    }
}

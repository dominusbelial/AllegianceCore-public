using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using NumVec = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the chase camera's orientation lag
/// (<see cref="SmoothedOrientation"/>) plus a ChaseCamera-level discriminator
/// for the P240 roll-following fix — no graphics device involved, mirroring the
/// <see cref="WarpMeshFactoryTests"/>/<see cref="RockMeshFactoryTests"/>
/// discipline (all MonoGame math types here are pure CPU structs).
/// </summary>
/// <remarks>
/// The whole point of P240 is that the chase camera tracks the ship's FULL
/// orientation — roll included — so a real roll must (a) survive the lag math
/// (not be averaged out), and (b) actually tip the camera's "up" away from
/// world-up in <see cref="ChaseCamera.Matrices"/>. These tests pin both.
/// </remarks>
public class ChaseCameraOrientationTests
{
    private static readonly Quat IdentityQ = Quat.Identity;

    /// <summary>Angular separation between two unit quaternions, in degrees
    /// (shortest arc; sign-insensitive — q and -q are the same rotation).</summary>
    private static double DegreesBetween(Quat a, Quat b)
    {
        float dot = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        dot = Math.Min(1f, dot);
        return 2.0 * Math.Acos(dot) * (180.0 / Math.PI);
    }

    private static double Deg(double rad) => rad * (180.0 / Math.PI);

    [Fact]
    public void FirstCall_SnapsExactlyToTarget()
    {
        var smoother = new SmoothedOrientation(tauSeconds: 0.18f);
        var target = Quat.CreateFromYawPitchRoll(0.3f, -0.2f, 0.5f);
        var result = smoother.Update(target, deltaSeconds: 0.016f);
        Assert.Equal(0.0, DegreesBetween(result, target), 5);
    }

    [Fact]
    public void ZeroOrNegativeTau_SnapsOnEveryCall()
    {
        var smoother = new SmoothedOrientation(tauSeconds: 0f);
        var a = Quat.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f);
        var b = Quat.CreateFromYawPitchRoll(-0.5f, 0.7f, 1.0f);
        // Even a huge delta must snap straight to the target, never overshoot
        // or lag — this is the "no smoothing" mode the camera uses when the
        // caller passes turnSmoothingTau=0.
        Assert.Equal(0.0, DegreesBetween(smoother.Update(a, 10f), a), 5);
        Assert.Equal(0.0, DegreesBetween(smoother.Update(b, 10f), b), 5);
    }

    [Fact]
    public void RepeatedUpdates_ConvergeMonotonicallyToward_FullRotationIncludingRoll()
    {
        var smoother = new SmoothedOrientation(tauSeconds: 0.09f);
        // A genuinely full 3D rotation: yaw + pitch + roll all non-zero.
        var target = Quat.CreateFromYawPitchRoll(0.5236f, 0.3491f, 0.7854f); // 30°/20°/45°
        // Seed the smoother at a DIFFERENT orientation (identity) so there is
        // real distance to converge across; the first-ever call always snaps.
        smoother.Update(IdentityQ, 0f);
        double prev = DegreesBetween(IdentityQ, target);
        for (int i = 0; i < 420; i++)
        {
            var cur = smoother.Update(target, deltaSeconds: 0.014f);
            double now = DegreesBetween(cur, target);
            // Allow a tiny float-precision epsilon near convergence (the
            // angular distance plateaus around ~0.05° where rounding can make
            // successive samples equal or jitter by a hair).
            Assert.True(now < prev + 1e-5, $"distance did not shrink: {now:F5} >= {prev:F5} at step {i}");
            prev = now;
        }
        Assert.True(prev < 0.5, $"did not converge to the full-rotation target; left {prev:F4} deg");
    }

    [Fact]
    public void Roll_IsRetainedNotErased_AcrossConvergence()
    {
        var smoother = new SmoothedOrientation(tauSeconds: 0.08f);
        // Target is a PURE roll of the ship about its nose (identity = no roll).
        var rollTarget = Quat.CreateFromYawPitchRoll(0f, 0f, MathF.PI / 2f); // 90° roll about Z
        Quat smoothed = smoother.Update(rollTarget, 0f); // snap to start
        for (int i = 0; i < 200; i++)
            smoothed = smoother.Update(rollTarget, deltaSeconds: 0.03f);

        // The smoother must end up AT the rolled orientation — a roll-erasing
        // implementation would collapse back toward the unrolled identity.
        Assert.True(DegreesBetween(smoothed, rollTarget) < 0.5,
            $"roll was lost through the lag ({DegreesBetween(smoothed, rollTarget):F3} deg from rolled target)");

        // Belt-and-suspenders: with a 90° roll about Z retained, the world "up"
        // direction (0,1,0) must have been swung onto a horizontal axis.
        NumVec up = NumVec.Transform(NumVec.UnitY, smoothed);
        Assert.True(Math.Abs(up.Y) < 0.04,
            $"implied up stayed near vertical (up.Y={up.Y:F4}); roll was erased");
    }

    [Fact]
    public void NegatedEquivalentTarget_YieldsIdenticalResult()
    {
        // q and -q are the SAME rotation; the lag must be insensitive to the
        // quaternion's sign (otherwise the shortest-arc handling is broken and
        // an equivalent orientation could be approached the long way around).
        var smootherA = new SmoothedOrientation(tauSeconds: 0.06f);
        var smootherB = new SmoothedOrientation(tauSeconds: 0.06f);
        var target = Quat.CreateFromYawPitchRoll(0.4f, -0.3f, 0.6f);
        var targetNeg = new Quat(-target.X, -target.Y, -target.Z, -target.W);

        Quat a = smootherA.Update(target, 0f);
        Quat b = smootherB.Update(targetNeg, 0f);
        for (int i = 0; i < 40; i++)
        {
            a = smootherA.Update(target, 0.025f);
            b = smootherB.Update(targetNeg, 0.025f);
        }
        Assert.Equal(0.0, DegreesBetween(a, b), 5);
    }

    [Fact]
    public void LargerTau_LagsFurtherThanSmallerTau_AfterOneStep()
    {
        var lazy = new SmoothedOrientation(tauSeconds: 1.0f);
        var eager = new SmoothedOrientation(tauSeconds: 0.05f);
        var target = Quat.CreateFromYawPitchRoll(0f, 0f, MathF.PI / 2f); // 90° roll
        // Seed both at identity so the first update is a genuine single lagged
        // step (a brand-new smoother always snaps on its first call).
        lazy.Update(IdentityQ, 0f);
        eager.Update(IdentityQ, 0f);
        var l = lazy.Update(target, 0.016f);
        var e = eager.Update(target, 0.016f);
        Assert.True(DegreesBetween(l, target) > DegreesBetween(e, target),
            $"larger tau should lag more (lazy left {DegreesBetween(l, target):F3} deg, eager {DegreesBetween(e, target):F3})");
    }

    [Fact]
    public void AllZeroQuaternion_IsGuarded_ReturnsIdentityNotNaN()
    {
        var smoother = new SmoothedOrientation(tauSeconds: 0.18f);
        var result = smoother.Update(new Quat(0f, 0f, 0f, 0f), 0.016f);
        Assert.False(float.IsNaN(result.X) || float.IsNaN(result.W),
            "degenerate input leaked NaN through the normalizer");
        Assert.Equal(0.0, DegreesBetween(result, IdentityQ), 5);
    }

    // ---- ChaseCamera-level discriminator for the actual P240 bug -----------

    private static Vector3 CameraUpFromView(in Matrix view)
    {
        // CreateLookAt's second column (M12,M22,M32) is the camera's up axis in
        // world space (see the class's remarks on the basis construction).
        var up = new Vector3(view.M12, view.M22, view.M32);
        up.Normalize();
        return up;
    }

    [Fact]
    public void Matrices_FullRoll_CameraUpFollowsShipRoll_NotWorldUp()
    {
        // Zero smoothing -> the camera snaps straight onto the ship's full
        // orientation, isolating the roll-following from the lag.
        var cam = new ChaseCamera(distanceBehind: 100f, heightAbove: 40f, turnSmoothingTauSeconds: 0f);

        // Pure 90° roll of the ship about its own nose (forward = Vector3.Forward).
        var shipRotation = Matrix.CreateFromAxisAngle(Vector3.Forward, MathHelper.PiOver2);
        var (view, _) = cam.Matrices(Vector3.Zero, shipRotation, aspectRatio: 1.777f, deltaSeconds: 0f, out _);

        Vector3 up = CameraUpFromView(view);
        // A world-up-stabilized camera would keep up == (0,1,0) regardless of
        // the ship's roll (up.Y == 1). P240 must tip up onto the ship's rolled
        // lateral axis (up.Y -> ~0, lateral dominance -> ~1).
        Assert.True(up.Y < 0.5f,
            $"camera up still world-up-dominated (up.Y={up.Y:F3}); the roll-stripping bug is back");
        float lateral = MathF.Sqrt(up.X * up.X + up.Z * up.Z);
        Assert.True(lateral > 0.866f,
            $"camera up should be laterally dominant after a 90° roll, got lateral={lateral:F3} (up={up})");
    }

    [Fact]
    public void Matrices_NoRoll_UpStaysWorldUp_AndStillLagsThenTracksTurn()
    {
        var cam = new ChaseCamera(distanceBehind: 100f, heightAbove: 40f, turnSmoothingTauSeconds: 0.10f);

        // 1) Unrolled straight flight keeps the camera predominantly upright
        //    (the view's "up" column is orthogonalized by CreateLookAt because
        //    a chase camera looks down at the ship from above-behind, so it is
        //    near-but-not-exactly world-up) — i.e. ordinary non-rolling flight
        //    is not pitched sideways by the change.
        var upright = Matrix.CreateFromYawPitchRoll(0f, 0f, 0f);
        var (v0, _) = cam.Matrices(Vector3.Zero, upright, 1.777f, 0.033f, out _);
        Vector3 up0 = CameraUpFromView(v0);
        Assert.True(up0.Y > 0.8f, $"plain flight should stay upright, got up.Y={up0.Y:F4}");

        // 2) The smoothing rationale still holds with the new full-orientation
        //    lag. The camera eye position encodes the smoothed forward/up
        //    (eye = ship + up*height - forward*distance), so it is the clean
        //    observable for the lag: right after a sharp yaw the camera should
        //    trail (not snap instantly to the new offset), then converge to it.
        var yawed = Matrix.CreateFromYawPitchRoll(1.0472f, 0f, 0f); // 60° yaw
        var yq = Quaternion.CreateFromRotationMatrix(yawed);
        Vector3 fwdY = Vector3.Transform(Vector3.Forward, yq);
        Vector3 upY = Vector3.Transform(Vector3.Up, yq);
        Vector3 eyeInstant = upY * 40f - fwdY * 100f;      // converged eye (ship at origin)
        Vector3 eyeBefore = new(0f, 40f, 100f);            // eye at identity orientation
        float fullOffset = Vector3.Distance(eyeBefore, eyeInstant);

        var (_, _) = cam.Matrices(Vector3.Zero, yawed, 1.777f, 0.016f, out var e1);
        float oneStepLeft = Vector3.Distance(e1, eyeInstant);
        // One frame after the snap-yaw the camera must still be noticeably shy
        // of the new offset — i.e. NOT an instant-snap (the old inert-feeling
        // complaint). With tau=0.10 it covers ~15% of the offset per frame.
        Assert.True(oneStepLeft > 0.075f * fullOffset,
            $"camera snapped (almost) instantly to the yaw: {oneStepLeft:F1} of {fullOffset:F1} units left");

        // Over enough frames it converges onto the yawed offset (tracks the turn).
        Vector3 eye = e1;
        for (int i = 0; i < 440; i++)
            cam.Matrices(Vector3.Zero, yawed, 1.777f, 0.024f, out eye);
        Assert.True(Vector3.Distance(eye, eyeInstant) < 1.5f,
            $"camera never caught up to the yawed offset (left {Vector3.Distance(eye, eyeInstant):F2} of {fullOffset:F1} units)");
    }
}

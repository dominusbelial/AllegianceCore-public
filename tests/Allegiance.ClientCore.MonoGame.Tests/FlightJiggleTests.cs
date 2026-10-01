using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure tests for <see cref="FlightJiggle"/> (P449b — native camera jiggle,
/// WinTrek.cpp:374-399) and the P449b FOV default. No graphics device needed.
/// The jiggle model is deliberately separated from the cameras so the native
/// accumulator/gate math can be pinned deterministically with an injected RNG.
/// </summary>
public class FlightJiggleTests
{
    /// <summary>A deterministic <see cref="ITrailRandom"/> whose
    /// <see cref="Range"/> always returns <paramref name="max"/> — exercising the
    /// model at the extreme of each sampled interval so offset-bound tests pin
    /// the true ceiling (native random(a,b) may return up to b).</summary>
    private sealed class StubRand : ITrailRandom
    {
        public float Range(float min, float max) => max;
        public Allegiance.Core.Collision.Vector3 Direction() => new(0f, 1f, 0f);
    }

    private const float Eps = 1e-4f;

    [Fact]
    public void Decay_MultipliesByPow025Dt()
    {
        // Native: m_jiggle *= pow(jiggleHalfLife=0.25f, dt) (WinTrek.cpp:380).
        // The named "jiggleHalfLife" is the DECAY BASE; the exact factor is
        // 0.25^dt — reproduce it literally (0.25^1 = 0.25, then 0.25 again).
        var j = new FlightJiggle(new StubRand());
        j.SetJiggle(1.0f);

        j.Step(dt: 1.0f, afterburnerPower: 0f, afterburnerMounted: false);
        Assert.Equal(0.25f, j.Accumulator, 5);          // 1.0 * 0.25^1
        j.Step(dt: 1.0f, afterburnerPower: 0f, afterburnerMounted: false);
        Assert.Equal(0.0625f, j.Accumulator, 5);        // 0.25 * 0.25^1

        // And a fractional interval follows the same exponent: 1.0 * 0.25^0.5.
        var k = new FlightJiggle(new StubRand());
        k.SetJiggle(1.0f);
        k.Step(dt: 0.5f, afterburnerPower: 0f, afterburnerMounted: false);
        Assert.Equal(MathF.Pow(0.25f, 0.5f), k.Accumulator, 5);
    }

    [Fact]
    public void OffsetMagnitude_IsBoundedByHalfDegreeTimesPower()
    {
        // amount = RadiansFromDegrees(0.5) * power; each axis within ±amount
        // (WinTrek.cpp:393). Stub returns the max, so offsets land exactly at
        // the bound (the largest magnitude the model may produce).
        float power = 1.0f;
        var j = new FlightJiggle(new StubRand());     // returns max → +amount
        var (yaw, pitch) = j.Step(dt: 0f, afterburnerPower: power, afterburnerMounted: true);

        float amount = MathHelper.ToRadians(0.5f) * power;
        Assert.InRange(MathF.Abs(yaw), 0f, amount + Eps);
        Assert.InRange(MathF.Abs(pitch), 0f, amount + Eps);

        // The stub returning max gives exactly +amount — sanity that the upper
        // bound is actually achievable (not silently tighter than native).
        Assert.Equal(amount, yaw, 5);
    }

    [Fact]
    public void NoOffset_WithNoAfterburnerAndLowResidual()
    {
        // Gate: power = m_jiggle + (afterburner? power : 0) is only computed
        // when an afterburner is mounted OR m_jiggle > 1.0. A residual ≤ 1 with
        // no afterburner must shake nothing (WinTrek.cpp:387-399).
        var j = new FlightJiggle(new StubRand());
        j.SetJiggle(0.5f);                              // ≤ 1 residual, no AB
        var (yaw, pitch) = j.Step(dt: 0f, afterburnerPower: 0f, afterburnerMounted: false);

        Assert.Equal(0f, yaw);
        Assert.Equal(0f, pitch);
    }

    [Fact]
    public void ResidualAboveOne_ShakesEvenWithoutAfterburner()
    {
        // A strong impact residual (> 1) shakes on its own even with no AB.
        var j = new FlightJiggle(new StubRand());    // Range returns max → +amount
        j.SetJiggle(2.0f);
        var (yaw, _) = j.Step(dt: 0f, afterburnerPower: 0f, afterburnerMounted: false);

        Assert.NotEqual(0f, yaw);   // power = 2.0 > 0 → offset present
    }

    [Fact]
    public void SetJiggle_ReplacesTheAccumulator()
    {
        // SetJiggle(j) assigns outright — native's SetJiggle (WinTrek.cpp:503-508).
        var j = new FlightJiggle(new StubRand());
        j.SetJiggle(5.0f);
        j.Step(dt: 1.0f, afterburnerPower: 0f, afterburnerMounted: false);   // decays → 5*0.25 = 1.25
        j.SetJiggle(2.0f);                               // replace, ignoring the 1.25

        Assert.Equal(2.0f, j.Accumulator, 5);
    }

    [Fact]
    public void AfterburnerPowerPositive_YieldsNonZeroOffset_ZeroYieldsNone()
    {
        // Client-level contract (verification item): afterburner power > 0 with
        // an AB mounted must tilt the camera; power 0 (idle AB, no residual) must
        // not — otherwise a mounted-but-idle afterburner would shake forever.
        var j = new FlightJiggle(new StubRand());   // a mid-range offset

        var (yawOn, pitchOn) = j.Step(dt: 0f, afterburnerPower: 1f, afterburnerMounted: true);
        Assert.NotEqual(0f, yawOn);
        Assert.NotEqual(0f, pitchOn);

        var (yawOff, pitchOff) = j.Step(dt: 0f, afterburnerPower: 0f, afterburnerMounted: true);
        Assert.Equal(0f, yawOff);
        Assert.Equal(0f, pitchOff);
    }

    [Fact]
    public void PerturbView_IdentityOffsetsLeaveViewUntouched()
    {
        var view = Matrix.CreateLookAt(Vector3.Zero, new Vector3(0f, 0f, -1f), Vector3.Up);
        Assert.Equal(view, FlightJiggle.PerturbView(view, 0f, 0f));
    }

    [Fact]
    public void PerturbView_NonZeroOffsetsRotateTheCamera()
    {
        var view = Matrix.CreateLookAt(Vector3.Zero, new Vector3(0f, 0f, -1f), Vector3.Up);
        var shaken = FlightJiggle.PerturbView(view, MathHelper.ToRadians(0.2f), MathHelper.ToRadians(0.1f));

        // A pure camera-space rotation changes the view's orientation rows but
        // must stay a rigid rotation (orthonormal upper-left 3x3).
        Assert.NotEqual(view, shaken);
        for (int r = 0; r < 3; r++)
            Assert.Equal(1f, new Vector3(shaken[r, 0], shaken[r, 1], shaken[r, 2]).Length(), 4);
    }

    [Fact]
    public void ImpactIntensity_UsesNativeCoefficient()
    {
        // 2 * damage * (1/20) — shipIGC.cpp:624,836 (c_impactJiggleCoefficient).
        Assert.Equal(0.1f, FlightJiggle.ImpactIntensity(1f), 5);
        Assert.Equal(1.0f, FlightJiggle.ImpactIntensity(10f), 5);
    }

    // ---- P449b FOV: the cameras apply the value and default to native 50° ----

    private static float FovHalfCotangent(float fovDegrees) =>
        1f / MathF.Tan(MathHelper.ToRadians(fovDegrees) / 2f);   // projection M22

    [Fact]
    public void ChaseCamera_DefaultFieldOfView_IsNative50Deg()
    {
        // Domingo's decision: match native s_fDefaultFOV = 50° (WinTrek.h:26).
        var cam = new ChaseCamera(distanceBehind: 350f, heightAbove: 90f);
        var (_, proj) = cam.Matrices(
            Vector3.Zero, Matrix.Identity, aspectRatio: 1.777f, deltaSeconds: 0f, out _);
        Assert.Equal(FovHalfCotangent(50f), proj.M22, 4);
    }

    [Fact]
    public void CockpitCamera_DefaultFieldOfView_IsNative50Deg()
    {
        var cam = new CockpitCamera();
        var (_, proj) = cam.Matrices(Vector3.Zero, Matrix.Identity, aspectRatio: 1.777f, out _);
        Assert.Equal(FovHalfCotangent(50f), proj.M22, 4);
    }

    [Fact]
    public void Camera_AppliesCustomFovValue()
    {
        // The configurable value (--flightFov) is plumbed in as fieldOfViewDegrees;
        // verify a non-default value is genuinely applied to the projection.
        var cam = new ChaseCamera(distanceBehind: 350f, heightAbove: 90f, fieldOfViewDegrees: 60f);
        var (_, proj) = cam.Matrices(
            Vector3.Zero, Matrix.Identity, aspectRatio: 1.777f, deltaSeconds: 0f, out _);
        Assert.Equal(FovHalfCotangent(60f), proj.M22, 4);
        Assert.NotEqual(FovHalfCotangent(50f), proj.M22, 4);
    }

    [Fact]
    public void LaunchOptions_FlightFovDefaultsToNative50()
    {
        var opts = new Game1.LaunchOptions();
        Assert.Equal(50f, opts.FlightFovDegrees);
    }
}

using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="MouseFlightController"/> — the port of the
/// original "virtual joystick" (<c>JoystickImageImpl::MouseMove</c>,
/// <c>src/effect/efimage.cpp:182-209</c>). No graphics device involved. The
/// decisive behaviours being pinned down here: it <em>integrates</em> deltas
/// rather than replacing the deflection, it clamps to the unit circle preserving
/// direction, and it does <strong>not</strong> auto-centre.
/// </summary>
public class MouseFlightControllerTests
{
    // A round multiplier (0.01) far from the default so the arithmetic in these
    // tests is easy to reason about exactly; direction-preservation asserts use
    // it to hit lengths comfortably past 1.
    private const float S = 0.01f;

    private static MouseFlightController Controller(float sens = S) => new(sens);

    private static void AssertClose((float Yaw, float Pitch) expected, MouseDeflection actual, int places = 4)
    {
        Assert.Equal(expected.Yaw, actual.Yaw, places);
        Assert.Equal(expected.Pitch, actual.Pitch, places);
    }

    [Fact]
    public void SingleDelta_ProducesScaledDeflection()
    {
        var c = Controller();
        // dx = 50 px * 0.01 = 0.5, integrated (subtracted) onto the zero start.
        var d = c.Update(50f, 0f);
        AssertClose((-0.5f, 0f), d);
        Assert.Equal(-0.5f, c.Current.Yaw, 4);
        Assert.Equal(0f, c.Current.Pitch, 4);
    }

    [Fact]
    public void SameDirectionDeltas_IntegrateNotReplace()
    {
        var c = Controller();
        c.Update(50f, 0f);   // yaw -0.5
        var d = c.Update(50f, 0f); // yaw -1.0 — accumulated, not overwritten
        AssertClose((-1.0f, 0f), d);
    }

    [Fact]
    public void MagnitudeBeyondOne_ClampsToUnitCircle_PreservingDirection()
    {
        var c = Controller(S);
        // raw (200, 400) * 0.01 = (2.0, 4.0) → length √20 ≈ 4.472 > 1 → clamps.
        var d = c.Update(200f, 400f);

        float len = MathF.Sqrt(d.Yaw * d.Yaw + d.Pitch * d.Pitch);
        Assert.Equal(1f, len, 4);

        // Direction must match the un-normalised (2,4)-negation = (-2,-4)/√20
        // (the integration *subtracts* the scaled delta, so both components go
        // negative here, then the clamp scales them to unit length).
        float inv = 1f / MathF.Sqrt(20f);
        AssertClose((-2f * inv, -4f * inv), d); // i.e. (-0.44721, -0.89443)
    }

    [Fact]
    public void NoFurtherInput_DeflectionHoldsStill_NoAutoCentre()
    {
        var c = Controller();
        c.Update(60f, 30f); // yaw -0.6, pitch -0.3
        var d = c.Update(0f, 0f); // zero delta — must hold, not drift back to 0
        AssertClose((-0.6f, -0.3f), d);
    }

    [Fact]
    public void OppositeDirectionDelta_ReducesTowardZero()
    {
        var c = Controller();
        c.Update(100f, 0f); // yaw -1.0
        // Move the other way: dx = -50 → yaw = -1.0 - (-0.5) = -0.5.
        var d = c.Update(-50f, 0f);
        AssertClose((-0.5f, 0f), d);
        // And a further opposite pull brings it back through and past zero.
        var e = c.Update(-100f, 0f); // yaw = -0.5 - (-1.0) = +0.5
        AssertClose((0.5f, 0f), e);
    }

    [Fact]
    public void Reset_ZeroesStoredDeflection()
    {
        var c = Controller();
        c.Update(100f, 50f);
        Assert.NotEqual(0f, c.Current.Yaw);
        Assert.NotEqual(0f, c.Current.Pitch);

        c.Reset();
        AssertClose((0f, 0f), c.Current);

        // A fresh delta after reset starts from zero again.
        var d = c.Update(20f, 0f);
        AssertClose((-0.2f, 0f), d);
    }

    [Fact]
    public void FirstUpdateAfterEnable_IsSkipped()
    {
        var c = Controller();
        c.Enable(); // arm the just-enabled flag
        Assert.True(c.JustEnabled);

        // The delta from wherever the OS cursor was must be discarded.
        var d = c.Update(10000f, 10000f);
        AssertClose((0f, 0f), d);
        Assert.False(c.JustEnabled);

        // And only the *next* delta actually applies.
        var e = c.Update(50f, 0f);
        AssertClose((-0.5f, 0f), e);
    }

    [Fact]
    public void Constructor_DefaultSensitivity_MatchesOriginal()
    {
        var c = new MouseFlightController();
        Assert.Equal(1f / 512f, c.Sensitivity);
    }

    [Fact]
    public void NonPositiveSensitivity_Rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MouseFlightController(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MouseFlightController(-1f));
    }
}

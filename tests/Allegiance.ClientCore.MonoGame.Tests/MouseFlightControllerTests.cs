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
        // dx = 50 px * 0.01 = 0.5, SUBTRACTED (native sign — mouse-right is a
        // negative yaw deflection, which the sim turns into a right turn)
        // onto the zero start.
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

        // Direction: BOTH yaw and pitch are SUBTRACTED (matches native), so
        // yaw goes negative and pitch goes negative, then the clamp scales
        // both to unit length.
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
        c.Update(100f, 0f); // yaw -1.0 (0 - 1.0)
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

    // ---- M-1: autopilot manual-input deadzone (native 2×-deadzone threshold) ----

    // Real hand-rest tremor is a HIGH-FREQUENCY oscillation of the cursor
    // about its rest point (a few px at tens of Hz). The mouse joystick
    // INTEGRATES raw deltas, so the integrated deflection equals the cursor's
    // instantaneous offset from centre — which trembles within a few px of the
    // engage-time baseline and never accumulates a one-way drift. Generate
    // deltas from that physical model (cursor offset = small sine), so the
    // trace is a faithful stand-in for a hand held still-ish on the mouse.
    private static IEnumerable<(int Dx, int Dy)> OscillatoryTremor(int frames, float ampPx = 3f, float periodFrames = 6f)
    {
        double prevX = 0, prevY = 0;
        for (int i = 0; i < frames; i++)
        {
            double ang = 2.0 * Math.PI * i / periodFrames;
            double x = ampPx * Math.Sin(ang);
            double y = ampPx * Math.Sin(ang + 0.7); // slightly different phase
            int dx = (int)Math.Round(x - prevX);
            int dy = (int)Math.Round(y - prevY);
            prevX = x; prevY = y;
            yield return (dx, dy);
        }
    }

    // Peak absolute per-axis integrated deflection (vs baseline) over a tremor trace.
    private static (float MaxDx, float MaxDy) TremorPeakDeviation(int frames = 4800)
    {
        var c = new MouseFlightController(); // default 1/512 sensitivity
        var baseline = c.Current;
        float maxDx = 0f, maxDy = 0f;
        foreach (var (dx, dy) in OscillatoryTremor(frames))
        {
            c.Update(dx, dy);
            maxDx = MathF.Max(maxDx, MathF.Abs(c.Current.Yaw - baseline.Yaw));
            maxDy = MathF.Max(maxDy, MathF.Abs(c.Current.Pitch - baseline.Pitch));
        }
        return (maxDx, maxDy);
    }

    [Fact]
    public void TremorTrace_NeverLeavesAutopilotDisengageDeadzone()
    {
        // The whole point of M-1: ordinary hand-rest tremor while autopilot is
        // engaged must NOT dump it. Across thousands of trembling frames the
        // integrated deflection must stay well inside native's 2×-deadzone band.
        var (maxDx, maxDy) = TremorPeakDeviation();
        float threshold = MouseFlightController.AutopilotDisengageThreshold;
        Assert.True(maxDx < threshold, $"tremor yaw peaked at {maxDx:F4} ≥ {threshold:F4}");
        Assert.True(maxDy < threshold, $"tremor pitch peaked at {maxDy:F4} ≥ {threshold:F4}");
    }

    [Fact]
    public void TremorTrace_DeflectionMovedPast_StaysFalse()
    {
        // Direct end-to-end assertion through the exact method the wiring calls.
        var c = new MouseFlightController();
        var baseline = c.Current;
        int frame = 0;
        foreach (var (dx, dy) in OscillatoryTremor(4800))
        {
            c.Update(dx, dy);
            Assert.False(
                c.DeflectionMovedPast(baseline, MouseFlightController.AutopilotDisengageThreshold),
                $"frame {frame}: tremor must not count as manual flight input (defl {c.Current.Yaw:F4},{c.Current.Pitch:F4})");
            frame++;
        }
    }

    [Fact]
    public void MeaningfulStickMove_PastTwiceDeadzone_Disengages()
    {
        var c = new MouseFlightController(); // default sensitivity
        var baseline = c.Current;
        // A real turn pulls the virtual stick well past 2× the deadzone
        // (≈103px of integrated deflection at 1/512 sensitivity).
        c.Update(300f, 0f); // 300px → yaw deflection 0.586 ≫ 0.2
        Assert.True(c.DeflectionMovedPast(baseline, MouseFlightController.AutopilotDisengageThreshold));
        Assert.Equal(MouseFlightController.DeadZone * 2f, MouseFlightController.AutopilotDisengageThreshold, 5);
    }

    [Fact]
    public void EngagedWhileAlreadyDeflected_SmallMovesDoNotDisengage_ThenBigMoveDoes()
    {
        // Mirror native's baseline-at-engage: if autopilot engages while the
        // stick is already held off-centre, tremor around THAT position must
        // not disengage; only leaving the baseline by > 2× deadzone does.
        var c = new MouseFlightController();
        c.Update(-120f, 40f); // pre-existing deflection before engage
        var baseline = c.Current;

        // Small oscillatory tremor about the engaged deflection — stays engaged.
        int frame = 0;
        foreach (var (dx, dy) in OscillatoryTremor(2400))
        {
            c.Update(dx, dy);
            Assert.False(c.DeflectionMovedPast(baseline, MouseFlightController.AutopilotDisengageThreshold), $"frame {frame}");
            frame++;
        }

        // A genuine large deflection from the baseline disengages.
        c.Update(-260f, 0f);
        Assert.True(c.DeflectionMovedPast(baseline, MouseFlightController.AutopilotDisengageThreshold));
    }

    // ---- P342: invert-mouse-pitch toggle (I) ----

    [Fact]
    public void Default_InvertOff_NegativeDyGivesNegativePitch()
    {
        // Native convention, unchanged: a downward mouse pull (positive dy in
        // OS pixels) integrates to a NEGATIVE pitch deflection (pitch = _pitch - dy).
        // S=0.01 → dy = 100*0.01 = 1.0 → pitch = 0 - 1.0 = -1.0 (unit circle, no clamp).
        var c = Controller();
        var d = c.Update(0f, 100f);
        Assert.Equal(0f, d.Yaw, 4);
        Assert.Equal(-1f, d.Pitch, 4);
        Assert.False(c.InvertPitch);
    }

    [Fact]
    public void InvertOn_NegativeDyGivesPositivePitch_YawUnaffected()
    {
        // Inverted: the same +100 px pull now flips dy before integration, so
        // pitch = 0 - (-1.0) = +1.0. Yaw path is byte-for-byte untouched.
        var c = Controller();
        c.InvertPitch = true;
        Assert.True(c.InvertPitch);

        var d = c.Update(0f, 100f);
        Assert.Equal(0f, d.Yaw, 4);
        Assert.Equal(+1f, d.Pitch, 4);

        // And with yaw + pitch together, only pitch's sign flips; magnitude/direction
        // of the yaw axis is identical to the uninverted controller's.
        var norm = Controller();
        var dn = norm.Update(40f, 60f);   // dy=0.6 → pitch -0.6, yaw -0.4
        var di = Controller();
        di.InvertPitch = true;
        var dd = di.Update(40f, 60f);     // dy=0.6 → pitch +0.6, yaw -0.4
        Assert.Equal(dn.Yaw, dd.Yaw, 4);
        Assert.Equal(-dn.Pitch, dd.Pitch, 4);
    }

    [Fact]
    public void TogglingPropertyAlone_DoesNotJumpCurrentDeflection()
    {
        // Load-bearing: flipping the flag must NOT rewrite the stored deflection.
        // The virtual stick stays exactly where it was; only FUTURE deltas take
        // the new sign.
        var c = Controller();
        c.Update(50f, 30f);          // settle at yaw -0.5, pitch -0.3
        var before = c.Current;
        c.InvertPitch = true;        // toggle with no motion
        Assert.Equal(before.Yaw, c.Current.Yaw, 4);
        Assert.Equal(before.Pitch, c.Current.Pitch, 4);

        // A subsequent opposite-direction pull integrates with the flipped sign,
        // but the base deflection is continuous (no jump).
        var after = c.Update(0f, 10f); // dy=0.1 → inverted → pitch = -0.3 - (-0.1) = -0.2
        Assert.Equal(before.Yaw, after.Yaw, 4);
        Assert.Equal(-0.2f, after.Pitch, 4);
    }

    [Fact]
    public void InvertOn_UnitCircleClampStillBinds()
    {
        // A sign flip preserves magnitude, so the unit-circle clamp behaves
        // identically with inversion on: repeated large dy saturates to length 1.
        var c = Controller();
        c.InvertPitch = true;
        var d = c.Update(0f, 900f); // dy_raw=9.0 → inverted dy=-9.0 → pitch=+9 → clamps to 1
        float len = MathF.Sqrt(d.Yaw * d.Yaw + d.Pitch * d.Pitch);
        Assert.Equal(1f, len, 4);
        Assert.Equal(1f, d.Pitch, 4);
    }

    [Fact]
    public void InvertOn_DeflectionMovedPastStillTrips()
    {
        // DeflectionMovedPast is abs-based — a real inverted stick move still
        // leaves the engage-time baseline by > threshold.
        var c = Controller();
        var baseline = c.Current;
        c.InvertPitch = true;
        c.Update(0f, 350f); // inverted dy → pitch climbs to +1 (clamped) ≫ 0.2
        Assert.True(c.DeflectionMovedPast(baseline, MouseFlightController.AutopilotDisengageThreshold));
    }
}

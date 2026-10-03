using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P506 mouse-flight RESPONSE CURVE:
/// <see cref="MouseFlightController.MapResponse"/> — the deadzone + rescale +
/// quadratic mapping applied to the integrated virtual-joystick deflection
/// before it becomes <c>ControlData</c> yaw/pitch — and the response-curve
/// settings exposed on <see cref="MouseFlightController"/> (deadzone preset,
/// linear-vs-quadratic, native-range mouse sensitivity). This is the fix for
/// Domingo's 2026-10-02 playtest ("the center dead zone is too small ... hard
/// to keep the ship steady"): the port previously fed the deflection straight
/// to yaw/pitch with no dead zone at all.
///
/// Native ground truth: MapJoystick deadzone/rescale (WinTrek.cpp:10783-10789,
/// g_fJoystickDeadZone default 0.1 = "Small"), the quadratic curve default
/// (value *= fabsf(value), WinTrek.cpp:10792), presets 0.04/0.1/0.3
/// (WinTrek.cpp:93-95), and MouseSensitivity 0.6 / [0.1,2.0] / ±0.01
/// (WinTrek.cpp:2809, AdjustMouseSens :5512-5531). NOTE: native applies NONE of
/// this to the mouse (efimage.cpp:193-194 bypasses MapJoystick); applying the
/// physical-stick curve to the mouse is a deliberate P506 improvement — see the
/// P506 report.
/// </summary>
public class MouseResponseCurveTests
{
    private const float Dz = MouseFlightController.DeadZone; // 0.1

    // The native MapJoystick's negative divisor (dz-1) inverts sign; MapResponse
    // negates it to preserve the MOUSE's sim-aligned sign. These expected values
    // are the sign-preserving deadzone+rescale (+optional square).

    [Theory]
    [InlineData(0f)]
    [InlineData(0.010f)]
    [InlineData(0.090f)]
    [InlineData(0.099f)]
    [InlineData(0.100f)]   // strictly > deadzone is required; == deadzone stays zero
    [InlineData(-0.030f)]
    [InlineData(-0.098f)]
    [InlineData(-0.100f)]
    public void Deflection_InsideOrAtDeadzone_IsExactlyZero(float axis)
    {
        var (yaw, pitch) = MouseFlightController.MapResponse(new MouseDeflection(axis, axis), Dz, linear: false);
        Assert.Equal(0f, yaw);
        Assert.Equal(0f, pitch);
    }

    [Fact]
    public void JustOutsideDeadzone_RampsFromZeroWithNoStep()
    {
        // Sweep a hair past the deadzone edge up to 0.2: the mapped magnitude must
        // climb monotonically from ~0 (a smooth ramp, no discontinuity).
        float prev = 0f;
        for (float v = 0.1001f; v <= 0.2f; v += 0.005f)
        {
            var (yaw, _) = MouseFlightController.MapResponse(new MouseDeflection(v, 0f), Dz, linear: false);
            Assert.True(MathF.Abs(yaw) > prev, $"not monotonic at v={v}: {yaw} <= {prev}");
            prev = MathF.Abs(yaw);
        }
        // The first value just past the edge is tiny (a gentle start, not a kick).
        var (firstYaw, _) = MouseFlightController.MapResponse(new MouseDeflection(0.1005f, 0f), Dz, linear: false);
        Assert.True(MathF.Abs(firstYaw) < 0.003f, $"first-out value {firstYaw} should be ~0");
    }

    [Theory]
    [InlineData(0.5f, 0.44444f, 0.19753f)]   // linear vs quadratic at half deflection
    [InlineData(-0.5f, -0.44444f, -0.19753f)]
    [InlineData(0.25f, 0.16667f, 0.02778f)]
    [InlineData(-0.25f, -0.16667f, -0.02778f)]
    public void Quadratic_Versus_Linear_CurveValues(float input, float linearExpected, float quadExpected)
    {
        var lin = MouseFlightController.MapResponse(new MouseDeflection(input, 0f), Dz, linear: true);
        var quad = MouseFlightController.MapResponse(new MouseDeflection(input, 0f), Dz, linear: false);
        Assert.Equal(linearExpected, lin.Yaw, 4);
        Assert.Equal(quadExpected, quad.Yaw, 4);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    public void FullDeflection_StillReachesFullMagnitude(float input)
    {
        // Deadzone eats 0.1 of travel, but the rescale maps the remaining span
        // back to the full ±1, so MAXIMUM deflection (magnitude 1.0, the unit-circle
        // clamp ceiling) still saturates at ±1 in BOTH linear and quadratic
        // (squaring 1 stays 1).
        var lin = MouseFlightController.MapResponse(new MouseDeflection(input, 0f), Dz, linear: true);
        var quad = MouseFlightController.MapResponse(new MouseDeflection(input, 0f), Dz, linear: false);
        Assert.Equal(Math.Sign(input), lin.Yaw, 4);
        Assert.Equal(Math.Sign(input), quad.Yaw, 4);
    }

    [Fact]
    public void BelowFullDeflection_MapsProportionallyUnderOne()
    {
        // Sanity: a strong-but-not-maximal deflection maps to a value strictly
        // under ±1 (the rescale spans (dz,1] -> (0,±1]).
        var m = MouseFlightController.MapResponse(new MouseDeflection(0.85f, 0f), Dz, linear: false);
        Assert.True(MathF.Abs(m.Yaw) < 1f && MathF.Abs(m.Yaw) > 0f, $"expected 0<magnitude<1, got {m.Yaw}");
    }

    [Fact]
    public void MouseSign_IsPreserved_NotInvertedLikePhysicalStick()
    {
        // REGRESSION GUARD: native's MapJoystick flips sign for the physical stick
        // (its raw axis convention opposes the sim's). The mouse virtual stick is
        // already sim-aligned, so MapResponse must NOT invert it — a naive reuse
        // would reverse the mouse controls. A rightward push is a negative yaw
        // deflection (MouseFlightController.Update: yaw = _yaw - dx); it must
        // emerge NEGATIVE, and a downward push (negative pitch) must stay negative.
        var d = MouseFlightController.MapResponse(new MouseDeflection(-0.5f, -0.3f), Dz, linear: false);
        Assert.True(d.Yaw < 0f, $"yaw must keep the mouse's sign (got {d.Yaw})");
        Assert.True(d.Pitch < 0f, $"pitch must keep the mouse's sign (got {d.Pitch})");
    }

    // ---- response-curve settings on the controller (defaults are native's) ----

    [Fact]
    public void DefaultSettings_AreNative_QuadraticAndSmallDeadzone()
    {
        var c = new MouseFlightController();
        Assert.Equal(MouseFlightController.DeadZone, c.Deadzone, 5);   // 0.1 = "Small" preset
        Assert.False(c.LinearControls);                                  // quadratic is the default
        Assert.Equal(1f, c.MouseSensitivity, 5);                         // port's neutral 1/512 feel
    }

    [Fact]
    public void DeadzonePresets_MatchNativeValues()
    {
        Assert.Equal(new[] { 0.04f, 0.1f, 0.3f }, MouseFlightController.DeadzonePresets.Select(d => MathF.Round(d, 3)));
    }

    [Theory]
    [InlineData(5f, 2.0f)]        // native max clamp
    [InlineData(0.001f, 0.1f)]    // native min clamp
    [InlineData(0.5f, 0.5f)]      // in-range unchanged
    [InlineData(-1f, 0.1f)]       // below min clamps
    public void MouseSensitivity_ClampsToNativeRange(float input, float expected)
    {
        var c = new MouseFlightController();
        c.MouseSensitivity = input;
        Assert.Equal(expected, c.MouseSensitivity, 4);
    }

    [Fact]
    public void MouseSensitivity_MultipliesThePixelDelta()
    {
        // S=0.01 base. At the default 1.0 sensitivity a 50px rightward push is a
        // yaw deflection of -0.5; doubling sensitivity doubles the deflection.
        var c = new MouseFlightController(0.01f);
        Assert.Equal(-0.5f, c.Update(50f, 0f).Yaw, 4);
        c.Reset();

        c.MouseSensitivity = 2f;
        Assert.Equal(-1.0f, c.Update(50f, 0f).Yaw, 4);   // 50*0.01*2 = 1.0
    }
}

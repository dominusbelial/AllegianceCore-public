using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="JoystickFlightController"/> — the port of
/// native <c>SenseJoystick</c> (WinTrek.cpp:10818-11016) onto MonoGame's
/// joystick API. No graphics device involved: the raw axis integers are fed in
/// synthetically and the deadzone/rescale, the throttle latch and the axis-layout
/// gathering are pinned down exactly.
/// </summary>
public class JoystickFlightControllerTests
{
    private const float Dz = JoystickFlightController.DeadZone; // 0.1

    // ---- native MapJoystick (WinTrek.cpp:10783-10789) — exact linear formula ----

    [Theory]
    [InlineData(0.00f)]
    [InlineData(0.049f)]
    [InlineData(0.099f)]
    [InlineData(0.100f)]   // strictly > deadzone is required; == deadzone stays zero
    [InlineData(-0.050f)]
    [InlineData(-0.099f)]
    [InlineData(-0.100f)]
    public void MapJoystick_InsideOrAtDeadzone_IsZero(float value)
    {
        Assert.Equal(0f, JoystickFlightController.MapJoystick(value), 5);
    }

    [Theory]
    [InlineData(0.1001f, -0.000111f)]
    [InlineData(0.20f, -0.11111f)]
    [InlineData(0.50f, -0.44444f)]
    [InlineData(1.00f, -1.0f)]
    [InlineData(-0.1001f, 0.000111f)]
    [InlineData(-0.50f, 0.44444f)]
    [InlineData(-1.00f, 1.0f)]
    public void MapJoystick_OutsideDeadzone_RescalesAndInvertsSign(float input, float expected)
    {
        // Native: g_fInverseJoystickDeadZone = dz - 1.0f = -0.9 (WinTrek.cpp:99),
        // so MapJoystick both rescales to preserve the ±1 range AND inverts sign.
        Assert.Equal(expected, JoystickFlightController.MapJoystick(input), 3);
    }

    [Fact]
    public void MapJoystick_FullDeflection_PreservesFullRangeAtTheEnds()
    {
        // Past the deadzone edge the usable travel maps 0→±1, so full deflection
        // still hits ±1 despite the deadzone subtracting 0.1 of range.
        Assert.Equal(-1f, JoystickFlightController.MapJoystick(1f), 5);
        Assert.Equal(1f, JoystickFlightController.MapJoystick(-1f), 5);
    }

    // ---- axis-layout gathering (single editable place: JoystickAxisLayout) ----

    [Fact]
    public void GatherRoles_DjiFpvRc3_PlacesRawAxesIntoRoleOrder()
    {
        // DJI Mode-2: raw[0]=roll, raw[1]=pitch, raw[2]=yaw, raw[3]=throttle.
        var axes = JoystickFlightController.GatherRoles(
            new[] { 16000, -16000, 32000, -10000 }, JoystickAxisLayout.DjiFpvRc3);
        Assert.Equal(32000f / 32767f, axes.Yaw, 4);
        Assert.Equal(-16000f / 32767f, axes.Pitch, 4);
        Assert.Equal(16000f / 32767f, axes.Roll, 4);
        Assert.Equal(-10000f / 32767f, axes.Throttle, 4);
    }

    [Fact]
    public void GatherRoles_NativeHotas_MatchesHotasOrder()
    {
        // Native default (dinput.cpp:726-793): yaw=0, pitch=1, roll=2, throttle=3.
        var axes = JoystickFlightController.GatherRoles(
            new[] { 32000, 6400, -6400, 3300 }, JoystickAxisLayout.NativeHotas);
        Assert.Equal(32000f / 32767f, axes.Yaw, 4);
        Assert.Equal(6400f / 32767f, axes.Pitch, 4);
        Assert.Equal(-6400f / 32767f, axes.Roll, 4);
        Assert.Equal(3300f / 32767f, axes.Throttle, 4);
    }

    [Fact]
    public void GatherRoles_Invert_FlipsRawSignBeforeMapping()
    {
        var laid = new JoystickAxisLayout(0, 1, 2, 3, InvertYaw: true, InvertPitch: true);
        var axes = JoystickFlightController.GatherRoles(new[] { 32000, 32000, 0, 0 }, laid);
        Assert.Equal(-32000f / 32767f, axes.Yaw, 4);
        Assert.Equal(-32000f / 32767f, axes.Pitch, 4);
    }

    [Fact]
    public void GatherRoles_ShortArrayOrBadIndex_IsSafeZero()
    {
        // A too-short raw array must not throw; each role whose configured index is
        // out of range resolves to a safe neutral 0. Here only index 0 (DJI roll) is
        // present; yaw(2)/pitch(1)/throttle(3) are out of range -> 0.
        var axes = JoystickFlightController.GatherRoles(new[] { 31415 }, JoystickAxisLayout.DjiFpvRc3);
        Assert.Equal(0f, axes.Yaw, 5);
        Assert.Equal(0f, axes.Pitch, 5);
        Assert.Equal(31415f / 32767f, axes.Roll, 5);
        Assert.Equal(0f, axes.Throttle, 5);
    }

    // ---- resting stick is held at zero (no drift) + activity signal ----

    [Fact]
    public void RestingStickWithinDeadzone_YieldsZeroDeflection_AndNoActivity()
    {
        var c = new JoystickFlightController();
        var r = c.Update(new JoystickAxes(0.005f, -0.006f, 0.009f, 0.004f));
        Assert.Equal(0f, r.Yaw, 5);
        Assert.Equal(0f, r.Pitch, 5);
        Assert.Equal(0f, r.Roll, 5);
        Assert.False(r.TurnAxesActive);
        Assert.False(r.YawActive);
        Assert.False(r.PitchActive);
        Assert.False(r.RollActive);
    }

    [Fact]
    public void TurnAxisMovedPastDeadzone_ActivatesOnlyThatAxis()
    {
        // A stick deflected on yaw only must flag yaw as active (eligible to override
        // the keyboard base layer) while leaving pitch/roll inactive — so a centered
        // pitch/roll keeps the keyboard arrow value standing (the "override only when
        // outside deadzone" contract the Game1 glue relies on).
        var c = new JoystickFlightController();
        var r = c.Update(new JoystickAxes(0.5f, 0f, 0f, 0f));
        Assert.True(r.YawActive);
        Assert.False(r.PitchActive);
        Assert.False(r.RollActive);
        Assert.True(r.TurnAxesActive);
    }

    [Fact]
    public void TurnAxisMovedPastDeadzone_ProducesMappedDeflection_AndActivity()
    {
        var c = new JoystickFlightController();
        // yaw raw +0.5 -> MapJoystick = (0.5-0.1)/(-0.9) = -0.4444 (sign-inverted).
        var r = c.Update(new JoystickAxes(0.5f, 0f, 0f, 0f));
        Assert.Equal(-0.4444f, r.Yaw, 3);
        Assert.Equal(0f, r.Pitch, 5);
        Assert.Equal(0f, r.Roll, 5);
        Assert.True(r.TurnAxesActive);
    }

    [Fact]
    public void TurnAxisMovesThroughCenter_SignFollowsNativeInversion()
    {
        var c = new JoystickFlightController();
        Assert.Equal(-0.8889f, c.Update(new JoystickAxes(0.9f, 0f, 0f, 0f)).Yaw, 3);
        Assert.Equal(0.6667f, c.Update(new JoystickAxes(-0.7f, 0f, 0f, 0f)).Yaw, 3);
    }

    // ---- full chain matches FlightInput keyboard conventions (same flight response) ----

    [Fact]
    public void Chain_RollRightStick_ProducesNegativeRoll_LikeFlightInputRollRight()
    {
        // FlightInput: RollRight (D) -> -1 (bank right). Through the DJI layout
        // (raw[0]=roll), pushing the right stick right (+0.8 raw) should map to a
        // negative roll after the native sign-inverting MapJoystick — i.e. bank right.
        var c = new JoystickFlightController();
        var raw = JoystickFlightController.GatherRoles(
            new[] { (int)(0.8f * 32767f), 0, 0, 0 }, JoystickAxisLayout.DjiFpvRc3);
        var r = c.Update(raw);
        Assert.True(r.Roll < 0f, $"expected negative roll (bank right), got {r.Roll}");
        var kb = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, false, true));
        Assert.Equal(kb.Controls.Roll, MathF.Sign(r.Roll) * 1f, 3);
    }

    [Fact]
    public void Chain_YawStickMovedMapsToSimYawSpace_OfFlightInput()
    {
        // YawRight key -> -1 (turn nose right). A stick deflection on the yaw axis
        // must land in the same signed space (magnitude proportional, sign per native).
        var c = new JoystickFlightController();
        var raw = JoystickFlightController.GatherRoles(
            new[] { 0, 0, (int)(0.999f * 32767f), 0 }, JoystickAxisLayout.DjiFpvRc3);
        var r = c.Update(raw);
        var kb = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, true, false, false));
        Assert.Equal(kb.Controls.Yaw, MathF.Sign(r.Yaw) * 1f, 3);
    }

    // ---- throttle latch (WinTrek.cpp:10855-10882) ----

    [Fact]
    public void Throttle_IdleNeverMovesPastDeadzone_KeyboardKeepsControl()
    {
        var c = new JoystickFlightController();
        // First sample baselines at 0; a resting-but-nonzero 0.05 never exceeds the
        // 0.1 deadzone from its own baseline, so the joystick never seizes throttle.
        var r0 = c.Update(new JoystickAxes(0, 0, 0, 0.05f));
        Assert.Null(r0.ThrottleOverride);
        var r1 = c.Update(new JoystickAxes(0, 0, 0, 0.045f));  // still within deadzone of baseline
        Assert.Null(r1.ThrottleOverride);
    }

    [Fact]
    public void Throttle_FirstSampleEstablishesBaseline_WithoutEngaging()
    {
        var c = new JoystickFlightController();
        // Even a large first-sample value must not seize throttle immediately: the
        // latch needs a CHANGE past deadzone from the baseline, not a resting offset.
        var r = c.Update(new JoystickAxes(0, 0, 0, 0.9f));
        Assert.Null(r.ThrottleOverride);
    }

    [Fact]
    public void Throttle_MovingPastDeadzone_TakesOverAndTracks()
    {
        var c = new JoystickFlightController();
        c.Update(new JoystickAxes(0, 0, 0, 0f));          // baseline 0
        var r1 = c.Update(new JoystickAxes(0, 0, 0, 0.3f)); // |0.3-0| > 0.1 -> engage
        Assert.Equal(0.3f, r1.ThrottleOverride!.Value, 5);
        var r2 = c.Update(new JoystickAxes(0, 0, 0, 0.3f)); // stays engaged, tracks 0.3
        Assert.Equal(0.3f, r2.ThrottleOverride!.Value, 5);
        var r3 = c.Update(new JoystickAxes(0, 0, 0, 0.8f)); // continues to track
        Assert.Equal(0.8f, r3.ThrottleOverride!.Value, 5);
    }

    [Fact]
    public void Throttle_SmallReturnMovementsStayEngaged_NoReleaseChatter()
    {
        var c = new JoystickFlightController();
        c.Update(new JoystickAxes(0, 0, 0, 0f));
        c.Update(new JoystickAxes(0, 0, 0, 0.5f));   // engage at 0.5
        // Small jitter within deadzone of the current tracked position stays engaged.
        var r = c.Update(new JoystickAxes(0, 0, 0, 0.405f)); // |0.405-0.5|=0.095 < 0.1
        Assert.Equal(0.405f, r.ThrottleOverride!.Value, 5);
    }

    [Fact]
    public void Throttle_Reset_ClearsEngagementAndRebaselines()
    {
        var c = new JoystickFlightController();
        c.Update(new JoystickAxes(0, 0, 0, 0f));
        c.Update(new JoystickAxes(0, 0, 0, 0.5f));
        Assert.NotNull(c.Update(new JoystickAxes(0, 0, 0, 0.5f)).ThrottleOverride);
        c.Reset();
        // After reset the next sample is a fresh baseline — a resting 0.5 no longer
        // rides a previously-engaged latch.
        Assert.Null(c.Update(new JoystickAxes(0, 0, 0, 0.5f)).ThrottleOverride);
    }

    [Fact]
    public void Throttle_NegativeSide_EngagesSymmetrically()
    {
        var c = new JoystickFlightController();
        c.Update(new JoystickAxes(0, 0, 0, 0f));
        var r = c.Update(new JoystickAxes(0, 0, 0, -0.4f));
        Assert.Equal(-0.4f, r.ThrottleOverride!.Value, 5);
    }

    // ---- no joystick path is untouched: controller with neutral input equals keyboard neutral ----

    [Fact]
    public void NeutralJoystick_LeavesThrottleNull_SoCallerKeepsIdleMinusOne()
    {
        // The latch contract the Game1 glue relies on: a centered/resting joystick
        // reports ThrottleOverride=null so the caller keeps FlightInput's idle -1
        // (ControlData.Neutral.Throttle == -1), and the turn axes read 0.
        var c = new JoystickFlightController();
        var r = c.Update(new JoystickAxes(0f, 0f, 0f, 0f));
        Assert.Null(r.ThrottleOverride);
        Assert.Equal(-1f, ControlData.Neutral.Throttle, 5);
    }
}

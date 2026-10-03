using Allegiance.ClientCore.MonoGame.Flight;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P499 — pure-logic tests for the Bluetooth gamepad → flight-roles mapping
/// (<see cref="GamePadFlightRoles"/>). The mapper converts the four raw MonoGame
/// thumbstick floats (X right-positive, Y up-positive, −1..+1) into the same
/// role-ordered, sign-corrected <see cref="JoystickAxes"/> the P380
/// <see cref="JoystickFlightController"/> consumes — so a wrong sign here makes
/// the ship respond backwards on the pad, exactly what these tests pin down
/// before any device is involved.
/// </summary>
public class GamePadFlightRolesTests
{
    [Fact]
    public void Centered_sticks_yield_neutral_axes()
    {
        var a = GamePadFlightRoles.Gather(0f, 0f, 0f, 0f);
        Assert.Equal(0f, a.Yaw);
        Assert.Equal(0f, a.Pitch);
        Assert.Equal(0f, a.Roll);
        Assert.Equal(0f, a.Throttle);
        Assert.True(JoystickAxes.None == a);
    }

    [Fact]
    public void Right_stick_right_is_positive_yaw()
    {
        // Right stick X right (+1) -> yaw. Convention: pushing right = yaw right,
        // which the joystick controller's MapJoystick negation will deliver (see
        // the class remarks); at the gather stage the raw yaw is simply +1.
        var a = GamePadFlightRoles.Gather(0f, 0f, 1f, 0f);
        Assert.True(a.Yaw > 0f, $"expected positive raw yaw, got {a.Yaw}");
        Assert.Equal(0f, a.Pitch);
    }

    [Fact]
    public void Right_stick_up_pitches_up_with_default_layout()
    {
        // Default layout inverts pitch (to cancel the controller's MapJoystick
        // sign negation), so a raw stick-up (+1) maps to a NEGATIVE raw pitch;
        // feeding that through MapJoystick yields a positive ControlData.Pitch
        // = pitch up. Pin the raw value here so the convention can't drift.
        var a = GamePadFlightRoles.Gather(0f, 0f, 0f, 1f, GamePadFlightRoles.DefaultLayout);
        Assert.True(a.Pitch < 0f, $"expected negative raw pitch for stick-up under InvertPitch, got {a.Pitch}");
    }

    [Fact]
    public void Right_stick_up_is_positive_raw_pitch_when_not_inverted()
    {
        var a = GamePadFlightRoles.Gather(0f, 0f, 0f, 1f, new GamePadFlightRoles.Layout());
        Assert.True(a.Pitch > 0f);
    }

    [Fact]
    public void Left_stick_up_is_full_throttle_forward()
    {
        // Left stick Y up (+1) -> throttle. The joystick controller passes the
        // throttle AXIS through un-negated (only the turn axes go through
        // MapJoystick), so +1 here = full forward (+1), matching ControlData.
        var a = GamePadFlightRoles.Gather(0f, 1f, 0f, 0f);
        Assert.Equal(1f, a.Throttle);
        Assert.Equal(0f, a.Roll);
    }

    [Fact]
    public void Left_stick_right_is_positive_roll()
    {
        // Left stick X right (+1) -> roll. MapJoystick negates, yielding a
        // negative ControlData.Roll = bank right (the port's convention).
        var a = GamePadFlightRoles.Gather(1f, 0f, 0f, 0f);
        Assert.True(a.Roll > 0f, $"expected positive raw roll, got {a.Roll}");
        Assert.Equal(0f, a.Throttle);
    }

    [Fact]
    public void Role_values_are_clamped_and_independent()
    {
        // Overshoot clamps; a full-left-stick deflection does not bleed into the
        // right-stick roles (independent evaluation of the two sticks).
        var a = GamePadFlightRoles.Gather(1.5f, -1.5f, -2f, 2f);
        Assert.Equal(1f, a.Roll);
        Assert.Equal(-1f, a.Throttle);
        Assert.Equal(-1f, a.Yaw);   // right stick left (-1)
        Assert.Equal(-1f, a.Pitch); // default layout inverts the +2 clamp too
    }

    [Fact]
    public void Every_axis_inversion_flag_flips_its_role_sign()
    {
        var rightYaw = GamePadFlightRoles.Gather(0f, 0f, 1f, 0f);
        var flippedYaw = GamePadFlightRoles.Gather(0f, 0f, 1f, 0f, new GamePadFlightRoles.Layout(InvertYaw: true));
        Assert.Equal(-flippedYaw.Yaw, rightYaw.Yaw, precision: 4);

        var upThrottle = GamePadFlightRoles.Gather(0f, 1f, 0f, 0f);
        var flippedThrottle = GamePadFlightRoles.Gather(0f, 1f, 0f, 0f, new GamePadFlightRoles.Layout(InvertThrottle: true));
        Assert.Equal(-flippedThrottle.Throttle, upThrottle.Throttle, precision: 4);

        var rightRoll = GamePadFlightRoles.Gather(1f, 0f, 0f, 0f);
        var flippedRoll = GamePadFlightRoles.Gather(1f, 0f, 0f, 0f, new GamePadFlightRoles.Layout(InvertRoll: true));
        Assert.Equal(-flippedRoll.Roll, rightRoll.Roll, precision: 4);
    }

    [Fact]
    public void Convenience_left_stick_isolates_throttle_and_roll()
    {
        var a = GamePadFlightRoles.LeftStick(0.5f, 0.75f);
        Assert.Equal(0.5f, a.Roll);
        Assert.Equal(0.75f, a.Throttle);
        Assert.Equal(0f, a.Yaw);
        Assert.Equal(0f, a.Pitch);
    }
}

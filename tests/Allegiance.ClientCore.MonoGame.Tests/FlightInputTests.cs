using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="FlightInput"/> — every real key combo this
/// POC supports must translate to exactly the expected <see cref="ControlData"/>
/// and movement-bit <c>stateM</c>. No graphics device involved.
/// </summary>
public class FlightInputTests
{
    [Fact]
    public void NoKeys_NeutralControls_ZeroStateM()
    {
        var cmd = FlightInput.Translate(FlightInputState.None);
        AssertAxis(cmd.Controls, yaw: 0, pitch: 0, roll: 0);
        Assert.Equal(0, cmd.StateM);
    }

    [Theory]
    [InlineData(true, false, false, false)] // W → forward
    [InlineData(false, true, false, false)] // S → back
    [InlineData(false, false, true, false)] // Q → strafe left
    [InlineData(false, false, false, true)] // E → strafe right
    public void MovementKeys_SetExactlyTheirButton(bool w, bool s, bool q, bool e)
    {
        var cmd = FlightInput.Translate(new FlightInputState(w, s, q, e, false, false, false, false, false, false));
        int expected = 0;
        if (w) expected |= ShipControlState.ForwardButton;
        if (s) expected |= ShipControlState.BackwardButton;
        if (q) expected |= ShipControlState.LeftButton;
        if (e) expected |= ShipControlState.RightButton;
        Assert.Equal(expected, cmd.StateM);
        AssertAxis(cmd.Controls, 0, 0, 0);
    }

    [Fact]
    public void WAndSBothHeld_ThrustBitsBothLatched()
    {
        var cmd = FlightInput.Translate(new FlightInputState(true, true, false, false, false, false, false, false, false, false));
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.BackwardButton, cmd.StateM);
    }

    [Fact]
    public void WPlusQ_YawNeutralThrustPlusStrafeCompose()
    {
        var cmd = FlightInput.Translate(new FlightInputState(true, false, true, false, false, false, false, false, false, false));
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.LeftButton, cmd.StateM);
    }

    [Theory]
    [InlineData(true, false, 1f, 0f)]   // Up → pitch +1
    [InlineData(false, true, -1f, 0f)]  // Down → pitch −1
    [InlineData(false, false, 0f, 0f)]
    [InlineData(true, true, 0f, 0f)]    // both → cancel
    public void PitchArrows_SetSignedPitchAxis(bool up, bool down, float expectPitch, float _)
    {
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, up, down, false, false, false, false));
        AssertAxis(cmd.Controls, 0, expectPitch, 0);
        Assert.Equal(0, cmd.StateM);
    }

    [Theory]
    [InlineData(true, false, -1f, 0f)]  // Left → yaw −1
    [InlineData(false, true, 1f, 0f)]   // Right → yaw +1
    [InlineData(true, true, 0f, 0f)]    // both → cancel
    public void YawArrows_SetSignedYawAxis(bool left, bool right, float expectYaw, float _)
    {
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, left, right, false, false));
        AssertAxis(cmd.Controls, expectYaw, 0, 0);
    }

    [Theory]
    [InlineData(true, false, 1f)]   // A → roll +1 (banks left — live-playtest confirmed sign)
    [InlineData(false, true, -1f)]  // D → roll −1 (banks right)
    [InlineData(true, true, 0f)]    // both → cancel
    public void RollKeys_SetSignedRollAxis(bool left, bool right, float expectRoll)
    {
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, left, right));
        AssertAxis(cmd.Controls, 0, 0, expectRoll);
    }

    [Fact]
    public void FullMotion_AllAxesAndThrustTogether()
    {
        var cmd = FlightInput.Translate(new FlightInputState(
            ThrottleForward: true, ThrottleBack: false,
            StrafeLeft: false, StrafeRight: true,
            PitchUp: true, PitchDown: false,
            YawLeft: false, YawRight: true,
            RollLeft: false, RollRight: true));
        AssertAxis(cmd.Controls, yaw: 1f, pitch: 1f, roll: -1f);
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.RightButton, cmd.StateM);
    }

    private static void AssertAxis(ControlData c, float yaw, float pitch, float roll)
    {
        Assert.Equal(yaw, c.Yaw, 3);
        Assert.Equal(pitch, c.Pitch, 3);
        Assert.Equal(roll, c.Roll, 3);
        Assert.Equal(-1f, c.Throttle, 3);  // idles at -1 (buttons drive fore/aft; see Translate's remarks)
    }
}

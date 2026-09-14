using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// The keyboard state translated to plain booleans — deliberately free of any
/// MonoGame / <c>Microsoft.Xna.Framework.Input</c> type so this translation is
/// testable without a graphics device. <see cref="Game1"/> performs the actual
/// <c>Keyboard.GetState()</c> sampling each frame and hands the resulting plain
/// flags in here.
/// </summary>
/// <remarks>
/// Key map (keyboard-only POC, adapted from the MIT-licensed MonoGame
/// <c>ShipGame</c> sample):
///   W / S      → throttle forward / backward
///   Q / E      → strafe left / right
///   Up/Down    → pitch up / down
///   Left/Right → yaw left / right
///   A / D      → roll left / right
/// Fore/aft and lateral motion ride the sim's movement-button bits in
/// <c>stateM</c> (the same bits <c>Allegiance.ClientCore.Explorer</c> drives —
/// <c>ShipControlState.ForwardButton</c>/<c>BackwardButton</c>/<c>LeftButton</c>/
/// <c>RightButton</c>); the four joystick axes carry yaw/pitch/roll only, with
/// the throttle axis left at its neutral 0 (Explorer likewise passes 0f).
/// </remarks>
public readonly record struct FlightInputState(
    bool ThrottleForward,   // W
    bool ThrottleBack,      // S
    bool StrafeLeft,        // Q
    bool StrafeRight,       // E
    bool PitchUp,           // ↑
    bool PitchDown,         // ↓
    bool YawLeft,           // ←
    bool YawRight,          // →
    bool RollLeft,          // A
    bool RollRight)         // D
{
    /// <summary>All-neutral input (no key held).</summary>
    public static FlightInputState None => new();
}

/// <summary>The fully-resolved command for a frame: the four-axis
/// <see cref="ControlData"/> plus the movement-bit <c>stateM</c>.</summary>
public readonly record struct FlightCommand(ControlData Controls, int StateM);

/// <summary>
/// Pure keyboard-to-<see cref="ControlData"/>/<c>stateM</c> translation. No
/// MonoGame types in the public surface; hostile-to-graphics testing is the
/// whole point of separating this from <see cref="Game1"/>.
/// </summary>
public static class FlightInput
{
    /// <summary>
    /// Translate one sampled keyboard state into a <see cref="FlightCommand"/>.
    /// Opposing presses on the same axis cancel (both up and down held → 0),
    /// mirroring how a real stick would sit centred; the movement buttons set
    /// independently so W+S or Q+E may both be latched at once.
    /// </summary>
    public static FlightCommand Translate(FlightInputState input)
    {
        // Three turn axes: sum the signed contributions of opposing keys.
        float yaw   = (input.YawRight   ?  1f : 0f) + (input.YawLeft   ? -1f : 0f);
        float pitch = (input.PitchUp    ?  1f : 0f) + (input.PitchDown ? -1f : 0f);
        float roll  = (input.RollRight  ?  1f : 0f) + (input.RollLeft  ? -1f : 0f);

        int stateM = 0;
        if (input.ThrottleForward) stateM |= ShipControlState.ForwardButton;
        if (input.ThrottleBack)    stateM |= ShipControlState.BackwardButton;
        if (input.StrafeLeft)      stateM |= ShipControlState.LeftButton;
        if (input.StrafeRight)     stateM |= ShipControlState.RightButton;

        // Throttle axis stays neutral (0) — fore/aft is driven by the
        // movement buttons above, exactly as Explorer does.
        return new FlightCommand(new ControlData(yaw, pitch, roll, 0f), stateM);
    }
}

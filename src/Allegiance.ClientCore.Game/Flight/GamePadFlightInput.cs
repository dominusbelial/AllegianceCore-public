namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// Maps a MonoGame <c>GamePadState</c>'s analog sticks onto the sim's four
/// flight roles (yaw, pitch, roll, throttle) so the P499 Android head — and
/// any desktop pad plugged into the DesktopGL head — can fly through the
/// SAME <see cref="JoystickFlightController"/> the P380 real-joystick path
/// uses (that controller already supplies native's exact deadzone/rescale and
/// the P445-aligned throttle latch; this class only decides which stick feeds
/// which role and the sign, mirroring <see cref="JoystickAxisLayout"/>'s "one
/// place to edit" contract).
///
/// Pure by construction (no MonoGame type on the public surface): it takes the
/// four raw thumbstick floats MonoGame reports (−1..+1, X right-positive, Y
/// up-positive) and returns the role-ordered, sign-corrected
/// <see cref="JoystickAxes"/> in the same convention
/// <see cref="JoystickFlightController.GatherRoles"/> produces for a real
/// joystick, so it is fully unit-testable without a graphics device or pad —
/// the same discipline as <see cref="JoystickFlightController"/> itself.
/// </summary>
/// <remarks>
/// <para><b>Layout:</b> a conventional Mode-2 pad — the RIGHT stick steers
/// (X→yaw, Y→pitch) and the LEFT stick manages roll (X) + throttle (Y). This
/// mirrors the DJI Mode-2 transmitter layout the port already standardises on
/// (<see cref="JoystickAxisLayout.DjiFpvRc3"/>), so a Bluetooth controller
/// and the existing USB joystick feel alike. The sign constants are the
/// single edit point for a device that feels inverted (push right = yaw
/// right, push stick up = pitch up, push left stick up = throttle full
/// forward); like the P380 real-hardware mapping they are PROVISIONAL until
/// verified hands-on on a device.</para>
/// <para>Triggers/shoulder buttons are NOT axes here — they are handled in
/// the Game1 glue as held buttons (fire / afterburner) and throttle steps,
/// reusing the exact P221 fire / P220 booster / P445 throttle-step semantics
/// of the keyboard (see <c>Game1.FlightInput.ApplyGamePadFlight</c>).</para>
/// </remarks>
public static class GamePadFlightRoles
{
    /// <summary>
    /// Sign-per-role toggles for the pad stick mapping. Flip one for any role
    /// that responds backwards on the target hardware; the default assumes
    /// stick-up = pitch-up / throttle-forward and stick-right = yaw-right /
    /// roll-right, which is the conventional pad sense.
    /// </summary>
    public readonly record struct Layout(
        bool InvertYaw = false,
        bool InvertPitch = false,
        bool InvertRoll = false,
        bool InvertThrottle = false);

    /// <summary>The default (conventional) pad sense. Pitch is inverted because
    /// the shared joystick controller's <c>MapJoystick</c> negates the turn axes
    /// (it divides by a negative deadzone term — see
    /// <see cref="JoystickFlightController.MapJoystick"/>), so a raw stick-up
    /// (+1) becomes a negative <c>ControlData.Pitch</c> = pitch DOWN; inverting
    /// at the gather stage cancels that so stick-up still pitches UP. The yaw/
    /// roll axes and the throttle pass through that negation the opposite way
    /// and already read intuititively, so they stay uninverted.</summary>
    public static readonly Layout DefaultLayout = new(InvertPitch: true);

    /// <summary>
    /// Convert one frame's raw thumbstick floats into role-ordered,
    /// sign-corrected <see cref="JoystickAxes"/> (−1..+1 each) using the
    /// conventional default sense (<see cref="DefaultLayout"/>). Convenience
    /// overload; the explicit-layout form below is the implementation.
    /// </summary>
    public static JoystickAxes Gather(float leftX, float leftY, float rightX, float rightY)
        => GatherCore(leftX, leftY, rightX, rightY, DefaultLayout);

    /// <summary>
    /// As above, with an explicit <see cref="Layout"/> (the single edit point
    /// for a device that feels inverted).
    /// </summary>
    public static JoystickAxes Gather(float leftX, float leftY, float rightX, float rightY, in Layout layout)
        => GatherCore(leftX, leftY, rightX, rightY, layout);

    private static JoystickAxes GatherCore(
        float leftX, float leftY, float rightX, float rightY, in Layout layout)
    {
        static float Norm(float v) => v > 1f ? 1f : v < -1f ? -1f : v;
        static float Sign(float v, bool invert) => invert ? -v : v;

        float yaw = Sign(Norm(rightX), layout.InvertYaw);
        // Stick up (+1) pitches UP; pass +pitch through. Negated later by the
        // joystick controller's MapJoystick sign-inversion (see JoystickFlightController),
        // so this raw value is the sign the CONTROLLER expects.
        float pitch = Sign(Norm(rightY), layout.InvertPitch);
        float roll = Sign(Norm(leftX), layout.InvertRoll);
        float throttle = Sign(Norm(leftY), layout.InvertThrottle);

        if (yaw == 0f && pitch == 0f && roll == 0f && throttle == 0f)
            return JoystickAxes.None;
        return new JoystickAxes(yaw, pitch, roll, throttle);
    }

    /// <summary>Convenience: the case where the right stick is centered but the
    /// left stick still carries roll/throttle. Equivalent to <see cref="Gather"/>
    /// with centered right-X/Y, using the default sense.</summary>
    public static JoystickAxes RightStick(float rightX, float rightY)
        => GatherCore(0f, 0f, rightX, rightY, DefaultLayout);

    /// <summary>As <see cref="RightStick(float, float)"/>, explicit layout.</summary>
    public static JoystickAxes RightStick(float rightX, float rightY, in Layout layout)
        => GatherCore(0f, 0f, rightX, rightY, layout);

    /// <summary>Convenience: the left stick's roll+throttle contribution.</summary>
    public static JoystickAxes LeftStick(float leftX, float leftY)
        => GatherCore(leftX, leftY, 0f, 0f, DefaultLayout);

    /// <summary>As <see cref="LeftStick(float, float)"/>, explicit layout.</summary>
    public static JoystickAxes LeftStick(float leftX, float leftY, in Layout layout)
        => GatherCore(leftX, leftY, 0f, 0f, layout);
}

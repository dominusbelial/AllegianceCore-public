namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// The four flight axes read from a real joystick, already placed into the
/// sim's role order (<see cref="Allegiance.Core.Sim.ControlData"/>: yaw, pitch,
/// roll, throttle) and sign-corrected per the active
/// <see cref="JoystickAxisLayout"/> — each in the same normalized −1..+1 space
/// <see cref="FlightInput"/>'s keyboard axes live in.
/// </summary>
public readonly record struct JoystickAxes(float Yaw, float Pitch, float Roll, float Throttle)
{
    /// <summary>All-neutral input (a centered, still joystick).</summary>
    public static JoystickAxes None => new();
}

/// <summary>
/// The output of one <see cref="JoystickFlightController.Update"/>: the
/// deadzone-mapped turn-axis deflections (each accompanied by a per-axis
/// "active" flag meaning that axis is outside the deadzone and therefore
/// eligible to override the keyboard base layer), the latched throttle
/// override (or null so the caller keeps keyboard throttle control), and
/// whether any turn axis is actually deflected (native's autopilot manual-input
/// signal).
/// </summary>
public readonly record struct JoystickFlightResult(
    float Yaw,
    float Pitch,
    float Roll,
    bool YawActive,
    bool PitchActive,
    bool RollActive,
    float? ThrottleOverride,
    bool TurnAxesActive)
{
    /// <summary>A result with every turn axis centered and the throttle unlatchable (keyboard control).</summary>
    public static JoystickFlightResult Neutral => new(0f, 0f, 0f, false, false, false, null, false);
}

/// <summary>
/// Which raw joystick axis index feeds each flight role, plus a per-role
/// invert flag to correct physical direction. <b>This is the one place to
/// edit</b> the real-device mapping — the rest of the joystick pipeline
/// consumes the roles it lays out. Keeping it a plain value type means the
/// whole deadzone/throttle/latch math is unit-testable with synthetic data
/// before any hardware is involved.
/// </summary>
/// <remarks>
/// <para>Native's default convention (<c>engine/dinput.cpp:726-793</c>) is the
/// traditional HOTAS/flight-stick order <c>Yaw=0, Pitch=1, Roll=2,
/// Throttle=3</c> — see <see cref="NativeHotas"/>. Domingo's real hardware is a
/// DJI FPV Remote Controller 3, a drone RC transmitter that conventionally uses
/// a "Mode 2" stick layout (left stick = throttle+yaw, right stick = pitch+
/// roll) whose physical axes land on different raw indices — the same four
/// logical roles, arranged differently. That layout is
/// <see cref="DjiFpvRc3"/> and is the default for this port. It is provisional
/// until verified hands-on (see the P380 report); the constants below are the
/// single point to correct it, exactly as this remark asks.</para>
/// <para>Signs: a <c>true</c> invert negates the <em>raw</em> axis value before
/// the (sign-inverting) <see cref="JoystickFlightController.MapJoystick"/>
/// math runs. Combined with native's own inversion, an invert flips the final
/// role sign. Flip one for any axis that responds backwards.</para>
/// </remarks>
public readonly record struct JoystickAxisLayout(
    int YawAxis,
    int PitchAxis,
    int RollAxis,
    int ThrottleAxis,
    bool InvertYaw = false,
    bool InvertPitch = false,
    bool InvertRoll = false,
    bool InvertThrottle = false)
{
    /// <summary>
    /// Native's default HOTAS axis→role order
    /// (<c>GUID_XAxis→yaw, GUID_YAxis→pitch, GUID_Slider/ZAxis→roll,
    /// GUID_RzAxis→throttle</c>; <c>engine/dinput.cpp:726-793</c>).
    /// </summary>
    public static JoystickAxisLayout NativeHotas { get; } = new(0, 1, 2, 3);

    /// <summary>
    /// The DJI FPV Remote Controller 3 as seen by Linux/SDL — a Mode-2 drone
    /// transmitter. The two sticks occupy raw axes 0-3 (5 axes reported; axis 4
    /// is a spare switch/knob, unused here) with the Mode-2 arrangement:
    /// right-stick X→roll, right-stick Y→pitch, left-stick X→yaw, left-stick
    /// Y→throttle. Signs are provisional pending live confirmation (see the
    /// P380 report). Every constant here is the single edit point to correct
    /// the real mapping.
    /// </summary>
    public static JoystickAxisLayout DjiFpvRc3 { get; } = new(
        YawAxis: 3, PitchAxis: 0, RollAxis: 1, ThrottleAxis: 2);
}

/// <summary>
/// Pure-logic port of the native <c>SenseJoystick</c> flight-input mechanics
/// (<c>WinTrek/WinTrek.cpp:10818-11016</c>): the exact linear deadzone/rescale
/// of <c>MapJoystick</c> (<c>WinTrek.cpp:10783-10789</c>) for the three turn
/// axes, and the throttle-latch behaviour
/// (<c>WinTrek.cpp:10855-10882</c>). Deliberately free of any MonoGame type in
/// its public surface so the whole thing is unit-testable without a graphics
/// device — the same discipline as <see cref="FlightInput"/> and
/// <see cref="MouseFlightController"/>. This class owns the per-frame
/// conversion of raw axes into <see cref="ControlData"/>-ready deflections; the
/// MonoGame glue in <see cref="Game1"/> only gathers the raw axis integers and
/// calls <see cref="Update"/>.
/// </summary>
/// <remarks>
/// <para><b>Deadzone/rescale is linear and exact, not approximate.</b> Native's
/// <c>MapJoystick</c> zeroes everything inside ±<paramref name="deadzone"/> and,
/// outside it, rescales by the reciprocal of
/// <c>g_fInverseJoystickDeadZone = g_fJoystickDeadZone − 1.0f</c> (a negative
/// number, <c>WinTrek.cpp:98-99</c>) so the full ±1 range is preserved past the
/// deadzone edge <em>and the sign is inverted</em>. The default deadzone is
/// <c>g_fJoystickDeadZoneSmall = 0.1f</c> (<c>WinTrek.cpp:94</c>; also 0.04/0.3
/// presets exist). The formula here reproduces that exactly — do not replace
/// it with a different curve.</para>
/// <para><b>Throttle is latched, not a direct pass-through.</b> Mirrors native's
/// <c>trekClient.joyThrottle</c>/<c>fOldJoyThrottle</c>: the joystick throttle
/// axis only takes over once it moves more than the deadzone from its last
/// sampled value; otherwise the caller keeps its existing (keyboard) throttle
/// control — in this port that is <see cref="FlightInput"/>'s W/S movement
/// buttons with <see cref="ControlData"/> idling at −1 (see that type's remarks
/// for why −1, not 0). This prevents a resting-but-nonzero throttle axis from
/// fighting keyboard taps.</para>
/// </remarks>
public sealed class JoystickFlightController
{
    /// <summary>
    /// Native's real default deadzone, <c>g_fJoystickDeadZoneSmall = 0.1f</c>
    /// (<c>WinTrek.cpp:94</c>) in normalized-axis units — the same unit space
    /// the raw axes are gathered into.
    /// </summary>
    public const float DeadZone = 0.1f;

    /// <summary>
    /// Joystick axis normalization divisor: MonoGame/SDL report raw int16 axis
    /// values centered at 0 (roughly ±32767 full deflection), and this converts
    /// one into the −1..+1 space <see cref="FlightInput"/> and
    /// <see cref="ControlData"/> live in. Matches native, whose DirectInput
    /// values were likewise normalized to −1..+1 downstream.
    /// </summary>
    public const float AxisNormDivisor = 32767f;

    /// <summary>
    /// Default provisional joystick button mapping: button 0 fires weapons
    /// (TK_FireWeapon) and button 1 boosts (TK_FireBooster). The exact DJI
    /// physical buttons need hands-on confirmation (see the P380 report);
    /// these two are the only edit point for that.
    /// </summary>
    public const int FireButton = 1;
    /// <summary>See <see cref="FireButton"/>.</summary>
    public const int BoosterButton = 2;

    private float _oldJoyThrottle;
    private bool _joyThrottleEngaged;
    private bool _seenFirstSample;

    /// <summary>
    /// The exact native <c>MapJoystick(float value)</c> (<c>WinTrek.cpp:10783-
    /// 10789</c>): inside ±<paramref name="deadzone"/> → 0; outside, linearly
    /// rescaled by <c>(value ∓ deadzone) / (deadzone − 1.0f)</c> so the full
    /// ±1 range is preserved past the deadzone edge — and, because the divisor
    /// is negative, the sign is inverted (native's convention). No curve, no
    /// approximation.
    /// </summary>
    public static float MapJoystick(float value, float deadzone = DeadZone)
    {
        // g_fInverseJoystickDeadZone = g_fJoystickDeadZone - 1.0f (WinTrek.cpp:99).
        if (value > deadzone)
            return (value - deadzone) / (deadzone - 1.0f);
        if (value < -deadzone)
            return (value + deadzone) / (deadzone - 1.0f);
        return 0.0f;
    }

    /// <summary>
    /// Gather the normalized, sign-corrected role axes from a raw MonoGame
    /// <c>int[]</c> joystick axis sample, applying the <paramref name="layout"/>.
    /// Out-of-range indices and a short array yield 0 for that role (a safe
    /// neutral). Pure and testable.
    /// </summary>
    public static JoystickAxes GatherRoles(
        IReadOnlyList<int> rawAxes,
        in JoystickAxisLayout layout,
        float divisor = AxisNormDivisor)
    {
        static float Axis(IReadOnlyList<int> a, int index, bool invert, float div)
        {
            if (index < 0 || index >= a.Count)
                return 0f;
            float v = a[index] / div;
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            return invert ? -v : v;
        }

        return new JoystickAxes(
            Axis(rawAxes, layout.YawAxis, layout.InvertYaw, divisor),
            Axis(rawAxes, layout.PitchAxis, layout.InvertPitch, divisor),
            Axis(rawAxes, layout.RollAxis, layout.InvertRoll, divisor),
            Axis(rawAxes, layout.ThrottleAxis, layout.InvertThrottle, divisor));
    }

    /// <summary>
    /// Clear all latched state (throttle engagement + last-sample tracking).
    /// Call if the joystick disappears so a reconnect starts from a clean
    /// slate rather than inheriting a stale throttle baseline.
    /// </summary>
    public void Reset()
    {
        _oldJoyThrottle = 0f;
        _joyThrottleEngaged = false;
        _seenFirstSample = false;
    }

    /// <summary>
    /// Convert one frame's raw (already role-ordered, sign-corrected, −1..+1)
    /// axes into the flight deflections for that frame.
    /// </summary>
    /// <param name="raw">The gathered role axes (<see cref="GatherRoles"/>).</param>
    /// <param name="deadzone">Deadzone for both the turn-axis map and the
    /// throttle latch, defaulting to <see cref="DeadZone"/>.</param>
    public JoystickFlightResult Update(in JoystickAxes raw, float deadzone = DeadZone)
    {
        // The three turn axes go through native's exact deadzone/rescale.
        float yaw = MapJoystick(raw.Yaw, deadzone);
        float pitch = MapJoystick(raw.Pitch, deadzone);
        float roll = MapJoystick(raw.Roll, deadzone);

        // Throttle latch (native WinTrek.cpp:10855-10882): the raw throttle
        // axis (already sign-corrected, NOT deadzone-mapped — native uses the
        // raw value for the latch, gating only the take-over decision) only
        // seizes control once it has moved more than the deadzone from its
        // last sample; otherwise the caller keeps keyboard throttle.
        if (!_seenFirstSample)
        {
            _seenFirstSample = true;
            _oldJoyThrottle = raw.Throttle;
            _joyThrottleEngaged = false;
        }
        else if (MathF.Abs(raw.Throttle - _oldJoyThrottle) > deadzone)
        {
            _joyThrottleEngaged = true;
        }

        float? throttleOverride = null;
        if (_joyThrottleEngaged)
        {
            // While engaged, native tracks fOldJoyThrottle with the live value
            // each frame (WinTrek.cpp:10879-10881) so the latch comparison stays
            // relative to the current resting position.
            _oldJoyThrottle = raw.Throttle;
            throttleOverride = raw.Throttle;
        }

        // Native disengages autopilot on the three TURN axes only (not throttle)
        // moving past deadzone (WinTrek.cpp:8326-8333). A turn axis is "active"
        // (eligible to override the keyboard base layer) exactly when its mapped
        // value is non-zero, i.e. it is outside the deadzone.
        bool yawActive = yaw != 0f;
        bool pitchActive = pitch != 0f;
        bool rollActive = roll != 0f;
        bool turnActive = yawActive || pitchActive || rollActive;
        return new JoystickFlightResult(yaw, pitch, roll, yawActive, pitchActive, rollActive, throttleOverride, turnActive);
    }
}

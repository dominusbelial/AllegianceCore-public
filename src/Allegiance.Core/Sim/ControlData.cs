namespace Allegiance.Core.Sim;

/// <summary>
/// The ship's control inputs — a faithful port of <c>struct ControlData</c>
/// (<c>src/Igc/igc.h:1662-1671</c>). Carries the four joystick axes
/// (<c>jsValues[4]</c>): yaw, pitch, roll, throttle, where throttle ranges
/// −1 (full reverse) … +1 (full forward) and <c>Reset()</c> puts it at −1.
/// The movement math reads only these four values; the manoeuvre buttons live
/// in <c>Ship.StateM</c>, exactly as the C++ separates the joystick axes from
/// the <c>ShipControlStateIGC</c> bitmask.
/// </summary>
/// <remarks>
/// Indexes are <c>c_axisYaw=0, c_axisPitch=1, c_axisRoll=2, c_axisThrottle=3</c>
/// (<c>igc.h:855-858</c>), mirrored here as named constants so callers never
/// index magic integers.
/// </remarks>
public readonly record struct ControlData(float Yaw, float Pitch, float Roll, float Throttle)
{
    /// <summary>c_axisYaw = 0.</summary>
    public const int AxisYaw = 0;
    /// <summary>c_axisPitch = 1.</summary>
    public const int AxisPitch = 1;
    /// <summary>c_axisRoll = 2.</summary>
    public const int AxisRoll = 2;
    /// <summary>c_axisThrottle = 3.</summary>
    public const int AxisThrottle = 3;

    /// <summary>
    /// <c>ControlData::Reset()</c> (<c>igc.h:1666-1670</c>): the three turn
    /// axes idle at 0 and the throttle at full reverse (−1).
    /// </summary>
    public static ControlData Neutral => new(0f, 0f, 0f, -1f);

    /// <summary>The value of a turn axis by index (yaw/pitch/roll), for the C++'s <c>for i &lt; 3</c> loop (<c>shipIGC.cpp:2898</c>).</summary>
    public float TurnAxis(int axis) => axis switch
    {
        AxisYaw => Yaw,
        AxisPitch => Pitch,
        _ => Roll,
    };
}

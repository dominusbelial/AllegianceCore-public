using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Native SenseJoystick: read analog axes first (WinTrek.cpp:10822-10840),
/// then held keys replace each axis (10887-10915). Right/down win simultaneous
/// opposing keys; yaw keys with TK_RollModifier replace roll after the roll keys.
/// The browser uses this in every pilot camera, matching the port's shared flight
/// keys in chase view; native gates its keyboard override on bInternalCamera.</summary>
public static class BrowserFlightSteering
{
    public static FlightCommand Combine(FlightCommand command, FlightInputState keys,
        bool mouse, float mouseYaw = 0, float mousePitch = 0)
    {
        var controls = command.Controls;
        if (mouse) controls = controls with
        {
            Yaw = keys.RollModifier ? 0 : mouseYaw, Pitch = mousePitch,
            Roll = keys.RollModifier ? mouseYaw : controls.Roll,
        };
        if (keys.RollRight || keys.RollLeft) controls = controls with { Roll = keys.RollRight ? -1 : 1 };
        if (keys.YawRight || keys.YawLeft)
        {
            float axis = keys.YawRight ? -1 : 1;
            controls = keys.RollModifier ? controls with { Roll = axis, Yaw = 0 } : controls with { Yaw = axis };
        }
        if (keys.PitchDown || keys.PitchUp) controls = controls with { Pitch = keys.PitchDown ? -1 : 1 };
        return command with { Controls = controls };
    }
}

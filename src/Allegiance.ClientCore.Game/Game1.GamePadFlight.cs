using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

// The desktop and browser call exactly the same pad bindings/axis override.
public sealed partial class Game1
{
    /// <summary>
    /// P499 — post-Translate gamepad override over a keyboard-based command,
    /// the same shape as the P380 joystick block above: a connected pad's
    /// analog sticks override the turn axes (when outside deadzone) and latch
    /// the throttle; its buttons OR into stateM alongside the keyboard (fire /
    /// booster, never replace); its shoulder buttons step the P445 throttle
    /// axis exactly like the = / - keys. Returns the (possibly unchanged)
    /// command.
    /// </summary>
    private FlightCommand ApplyGamePadFlight(FlightCommand cmd)
    {
        var pad = CurrentPadState();
        if (pad is not { } gp || !gp.IsConnected)
            return cmd;

        var raw = GamePadFlightRoles.Gather(
            gp.ThumbSticks.Left.X, gp.ThumbSticks.Left.Y,
            gp.ThumbSticks.Right.X, gp.ThumbSticks.Right.Y,
            GamePadFlightRoles.DefaultLayout);
        var gpr = _gamePadFlight.Update(raw);
        cmd = cmd with
        {
            Controls = cmd.Controls with
            {
                Yaw = gpr.YawActive ? gpr.Yaw : cmd.Controls.Yaw,
                Pitch = gpr.PitchActive ? gpr.Pitch : cmd.Controls.Pitch,
                Roll = gpr.RollActive ? gpr.Roll : cmd.Controls.Roll,
                Throttle = gpr.ThrottleOverride ?? cmd.Controls.Throttle,
            },
        };

        // Buttons: A (or right trigger) fires, B (or left trigger) boosts — the
        // same P221/P220 held-continuous semantics as Space / Shift. Use
        // FireContribution so group-fire/single-weapon selection still applies.
        // (MonoGame: face buttons live on gp.Buttons, triggers on gp.Triggers.)
        var b = gp.Buttons;
        if (b.A == ButtonState.Pressed || gp.Triggers.Right > 0.5f)
            cmd = cmd with { StateM = cmd.StateM | FlightInput.FireContribution(true, _groupFire, _selectedMount) };
        if (b.B == ButtonState.Pressed || gp.Triggers.Left > 0.5f)
            cmd = cmd with { StateM = cmd.StateM | ShipControlState.AfterburnerButton };

        // Shoulder throttle steps (RB up / LB down) — edge-detected like the
        // = / - keys so holding never re-fires, stepping the same P445 axis.
        if (b.RightShoulder == ButtonState.Pressed && !_padRbWasDown)
        {
            _throttle = FlightInput.StepThrottleUp(_throttle);
            Console.WriteLine($"[monogame] pad RB -> throttle {_throttle:F2}");
        }
        if (b.LeftShoulder == ButtonState.Pressed && !_padLbWasDown)
        {
            _throttle = FlightInput.StepThrottleDown(_throttle);
            Console.WriteLine($"[monogame] pad LB -> throttle {_throttle:F2}");
        }
        _padRbWasDown = b.RightShoulder == ButtonState.Pressed;
        _padLbWasDown = b.LeftShoulder == ButtonState.Pressed;

        return cmd;
    }

}

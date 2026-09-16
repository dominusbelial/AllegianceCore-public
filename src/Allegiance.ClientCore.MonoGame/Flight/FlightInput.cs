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
///   Shift      → afterburner (TK_FireBooster, continuous while held; ORs
///                ShipControlState.AfterburnerButton into stateM)
///   Space      → fire weapon (TK_FireWeapon, continuous while held; ORs
///                ShipControlState.AllWeapons into stateM — group-fire mode)
/// Fore/aft and lateral motion ride the sim's movement-button bits in
/// <c>stateM</c> (the same bits <c>Allegiance.ClientCore.Explorer</c> drives —
/// <c>ShipControlState.ForwardButton</c>/<c>BackwardButton</c>/<c>LeftButton</c>/
/// <c>RightButton</c>); the four joystick axes carry yaw/pitch/roll, with the
/// throttle axis idling at -1 (full reverse end of the axis — see
/// <see cref="Translate"/>'s remarks for why 0, Explorer's own default, is
/// actually wrong: it makes the server's cruise-control branch auto-target
/// half max speed forward whenever no movement button is held).
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
    bool RollRight,         // D
    bool Afterburner = false, // Shift (either side) — TK_FireBooster
    bool Fire = false)        // Space — TK_FireWeapon
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
        // Sign flipped from the naive mapping (live-playtest fix, post-P209):
        // A (RollLeft) must bank the ship left. Confirmed live that
        // RollRight=+1/RollLeft=-1 actually banked right on A — i.e. the
        // sim's Orientation.Roll(+theta) direction is the opposite of what a
        // naive "left=negative" mapping assumes — so the sign here is
        // inverted relative to yaw/pitch's mapping just above.
        float roll  = (input.RollRight  ? -1f : 0f) + (input.RollLeft  ?  1f : 0f);

        int stateM = 0;
        if (input.ThrottleForward) stateM |= ShipControlState.ForwardButton;
        if (input.ThrottleBack)    stateM |= ShipControlState.BackwardButton;
        if (input.StrafeLeft)      stateM |= ShipControlState.LeftButton;
        if (input.StrafeRight)     stateM |= ShipControlState.RightButton;

        // P220 — afterburner (TK_FireBooster → ShipControlState.AfterburnerButton,
        // the 128·coastButtonIGC bit, igc.h:817). Continuous while Shift is held
        // (native's IsTrekKeyDown semantics), composed with any movement bits —
        // NOT a toggle (TK_ToggleBooster is deliberately left unwired here).
        if (input.Afterburner) stateM |= ShipControlState.AfterburnerButton;

        // P221 — fire weapon (TK_FireWeapon → ShipControlState.AllWeapons, the
        // 32768·allWeaponsIGC bit, igc.h:826). Held, continuous while Space is
        // held (native's IsTrekKeyDown semantics, exactly like afterburner).
        // Group-fire "all weapons" mode: OR-ing AllWeapons is precisely the
        // lever the wingman NPC pulls itself to make its guns fire
        // (Ship.cs:4558,4603-4605), and Weapon.IsWeaponSelected
        // (Weapon.cs:372) arms every mounted fixed weapon off this bit. This
        // gives the human pilot the same fire mechanism the AI already uses —
        // no new sim behaviour. Single-weapon selection (OneWeapon /
        // SelectedWeaponMask) is deliberately not wired (out of scope, see
        // docs/WEAPON_FIRE_P221.md).
        if (input.Fire) stateM |= ShipControlState.AllWeapons;

        // Throttle idles at -1 (full reverse end of the -1..+1 axis — see
        // ControlData's own doc remarks: Reset() puts it at -1). Live-playtest
        // bug fix: this was 0 (Explorer's own default too), but the server's
        // cruise-control branch (Ship.cs OnUpdate, taken whenever no movement
        // button is held) computes its target speed as
        // -0.5*(1+Throttle)*maxSpeed — at Throttle=0 that's HALF max speed
        // forward, not zero, so releasing every key made the ship
        // auto-accelerate to a 50%-speed forward cruise instead of coasting/
        // stopping. -1 makes the idle target speed exactly 0.
        return new FlightCommand(new ControlData(yaw, pitch, roll, -1f), stateM);
    }
}

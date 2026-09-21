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
    bool Fire = false,        // Space — TK_FireWeapon (held)
    // P225 — weapon-selection state. Unlike the keystroke flags above these
    // are NOT per-keyboard-sample; they are the client-local selection carried
    // in from Game1 each frame (_groupFire / _selectedMount — the same pattern
    // as _cameraMode / _mouseFlightEnabled). GroupFire=true is the default,
    // reproducing P221's all-weapons behaviour so nothing regresses; when it is
    // false, Fire ORs OneWeapon | (SelectedMount & SelectedWeaponMask) instead,
    // and Weapon.IsWeaponSelected fires exactly the fixed weapon mounted at
    // that id (Weapon.cs:372). Kept on the state struct (rather than an extra
    // parameter on Translate) so the whole selection→stateM decision stays pure
    // and testable without MonoGame types.
    bool GroupFire = true,    // TK_ToggleGroupFire (this port's ` key)
    int SelectedMount = 0)    // TK_NextWeapon/PrevWeapon ([ / ]); the fixed-mount id in OneWeapon mode
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
        //
        // Yaw sign follows native (WinTrek.cpp SenseJoystick, ~10902-10905):
        // TK_YawRight → -1, TK_YawLeft → +1. A positive yaw control value is a
        // LEFT turn in the sim (Orientation.Yaw(+θ) is a left rotation about
        // +y — Orientation.cs), so → sends -1 and turns the nose right, while ←
        // sends +1 and turns the nose left. (P310: this was previously the
        // naive +1-for-right mapping, which turned the nose LEFT on →.)
        float yaw   = (input.YawRight   ? -1f : 0f) + (input.YawLeft   ?  1f : 0f);
        float pitch = (input.PitchUp    ?  1f : 0f) + (input.PitchDown ? -1f : 0f);
        // Roll: RollLeft must bank the ship left. Orientation.Roll(+θ) banks
        // the opposite of what a naive "left=negative" mapping assumes
        // (confirmed live, post-P209: the naive RollRight=+1/RollLeft=-1
        // banked right on A) — so the sign here is inverted relative to pitch's
        // mapping above, and coincides with yaw's now that yaw is likewise
        // right = -1.
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
        // SelectedWeaponMask) is P225: when group-fire is toggled OFF, the same
        // Fire input instead ORs OneWeapon | the selected mount id so exactly
        // that mount's weapon fires (see docs/WEAPON_FIRE_P221.md).
        stateM |= FireContribution(input.Fire, input.GroupFire, input.SelectedMount);

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

    /// <summary>
    /// The <c>stateM</c> contribution of the fire input under the pilot's
    /// current weapon selection (P225). <c>false</c> when not firing; group-fire
    /// ORs <see cref="ShipControlState.AllWeapons"/> (every fixed weapon, the
    /// P221 behaviour); single-weapon mode ORs
    /// <see cref="ShipControlState.OneWeapon"/> plus the selected mount id
    /// masked to the 2-bit <see cref="ShipControlState.SelectedWeaponMask"/> —
    /// the exact two-mode shape <c>Weapon.IsWeaponSelected</c> (Weapon.cs:363-375)
    /// already branches on. Pure so the single-vs-group decision is unit-tested
    /// without a graphics device.
    /// </summary>
    public static int FireContribution(bool fire, bool groupFire, int selectedMount)
    {
        if (!fire) return 0;
        return groupFire
            ? ShipControlState.AllWeapons
            : ShipControlState.OneWeapon | (selectedMount & ShipControlState.SelectedWeaponMask);
    }

    /// <summary>
    /// Step the selected fixed-mount id forward (TK_NextWeapon), wrapping at
    /// <paramref name="mountCount"/> — the largest legal id is
    /// <c>mountCount - 1</c>, and the next press returns to 0. A non-positive
    /// count (no selectable mounts) yields 0.
    /// </summary>
    public static int NextMount(int current, int mountCount) =>
        mountCount <= 0 ? 0 : (current + 1) % mountCount;

    /// <summary>
    /// Step the selected fixed-mount id backward (TK_PrevWeapon), wrapping at
    /// <paramref name="mountCount"/> — from 0 the previous press lands on
    /// <c>mountCount - 1</c>. A non-positive count yields 0.
    /// </summary>
    public static int PrevMount(int current, int mountCount) =>
        mountCount <= 0 ? 0 : (current - 1 + mountCount) % mountCount;
}

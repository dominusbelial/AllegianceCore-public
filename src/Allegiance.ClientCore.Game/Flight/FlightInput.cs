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
///   Q / E      → strafe left / right
///   Up/Down    → pitch up / down
///   Left/Right → yaw left / right
///   A / D      → roll left / right
///   Shift      → afterburner (TK_FireBooster, continuous while held; ORs
///                ShipControlState.AfterburnerButton into stateM)
///   Space      → fire weapon (TK_FireWeapon, continuous while held; ORs
///                ShipControlState.AllWeapons into stateM — group-fire mode)
///   W / S      → P504 — RAMP the persistent cruise throttle while held (see
///                <see cref="RampThrottle"/>). This deliberately DEVIATES from
///                native (where W/S are momentary TK_ThrustForward/Backward
///                manual thrust and the cruise throttle is stepped by =/-) —
///                Domingo's explicit control-scheme choice. Holding W raises
///                _throttle toward +1, holding S lowers it toward -1 (zero
///                thrust), releasing keeps the value; they no longer set the
///                sim's ForwardButton/BackwardButton movement bits (so the
///                reverse-thrust and S-to-brake uses are gone — see the P504
///                report). The strafe/axes/afterburner/fire above are unchanged.
/// Lateral motion rides the sim's movement-button bits in
/// <c>stateM</c> (the same bits <c>Allegiance.ClientCore.Explorer</c> drives —
/// <c>ShipControlState.LeftButton</c>/<c>RightButton</c>); the four joystick
/// axes carry yaw/pitch/roll, with the throttle axis idling at -1 (full
/// reverse end of the axis — see <see cref="Translate"/>'s remarks for why 0,
/// Explorer's own default, is actually wrong: it makes the server's
/// cruise-control branch auto-target half max speed forward whenever no
/// movement button is held).
/// </remarks>
public readonly record struct FlightInputState(
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
    bool Cloak = false,       // F — TK_ToggleCloak (latched toggle, see remarks)
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
    int SelectedMount = 0,    // TK_NextWeapon/PrevWeapon ([ / ]); the fixed-mount id in OneWeapon mode
    // P445 — the client-local persistent throttle axis in [-1, 1] (native's
    // trekClient.trekThrottle, WinTrek.cpp). Carried in from Game1 each frame
    // like GroupFire/SelectedMount and sent verbatim as ControlData.Throttle —
    // NOT derived from any keystroke here. Defaults to -1 (idle) so the struct
    // alone, without an explicit value, reproduces the old hard-coded idle.
    float Throttle = -1f)
{
    /// <summary>All-neutral input (no key held, throttle idled to stop -1).
    /// NOTE: the plain parameterless <c>new()</c> would give Throttle the raw
    /// float default (0) rather than the primary constructor's declared -1,
    /// so None pins the idle explicitly (the server's cruise target at -1 is
    /// exactly 0 — see <see cref="CruiseFraction"/>).</summary>
    public static FlightInputState None => new() { Throttle = -1f };
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

        // P413 — cloak toggle (TK_ToggleCloak → ShipControlState.CloakActive,
        // the 0x800 bit, igc.h:820). Carried in from Game1 as the LATCHED toggle
        // state (like GroupFire), NOT the momentary key-down: native XORs the
        // cloakActiveIGC bit per key press (WinTrek.cpp:9468-9471), so once
        // engaged the client keeps requesting it every frame until toggled off.
        // The server does the real work — Ship.UpdateCloak (Ship.cs:6157) gates
        // on a mounted ET_Cloak part + not docked/boarded/ripcording and drains
        // energy, so a ship without a cloak (or with an empty energy pool) simply
        // never becomes IsCloaked; toggling the bit is harmless there.
        if (input.Cloak) stateM |= ShipControlState.CloakActive;

        // P445 — the persistent throttle axis is owned by the client (native's
        // trekClient.trekThrottle in [-1, 1]) and carried in on the state each
        // frame, so it is sent verbatim as ControlData.Throttle. Historically
        // this was hard-coded to -1 (the idle end of the axis), with the
        // rationale documented in ControlData's remarks: the server's cruise-
        // control branch (Ship.cs OnUpdate, taken whenever no movement button
        // is held) computes its target speed as -0.5*(1+Throttle)*maxSpeed, so
        // at Throttle=0 that's HALF max speed forward rather than stop. With
        // a real persistent throttle the pilot chooses the cruise speed: -1 =
        // stop/idle, +1 = full, and the sim's manual branch still overrides
        // while W/S (movement buttons) are held.
        return new FlightCommand(new ControlData(yaw, pitch, roll, input.Throttle), stateM);
    }

    // ── P445 — pure throttle stepping / presets ───────────────────────────

    /// <summary>
    /// The step a single ThrottleUp/ThrottleDown press applies (±0.2), per
    /// native's TK_ThrottleUp/ThrottleDown (<c>WinTrek.cpp:10515-10527</c>).
    /// </summary>
    public const float ThrottleStep = 0.2f;

    /// <summary>The persistent throttle reset value on launch/undock (native's
    /// on-line default of full, <c>WinTrek.cpp:10860-10862</c>).</summary>
    public const float DefaultThrottle = 1f;

    /// <summary>Step the throttle one notch up, clamping to +1 with native's
    /// exact rule — <c>trekThrottle &lt; 0.8f ? trekThrottle + 0.2f : 1.0f</c>
    /// (<c>WinTrek.cpp:10515-10518</c>): any value at or above 0.8 saturates
    /// to full rather than overshooting.</summary>
    public static float StepThrottleUp(float current) =>
        current < 0.8f ? current + 0.2f : 1f;

    /// <summary>Step the throttle one notch down, clamping to -1 with native's
    /// exact rule — <c>trekThrottle &gt; -0.8f ? trekThrottle - 0.2f : -1.0f</c>
    /// (<c>WinTrek.cpp:10917-10920</c>): any value at or below -0.8 saturates
    /// to full-reverse rather than undershooting.</summary>
    public static float StepThrottleDown(float current) =>
        current > -0.8f ? current - 0.2f : -1f;

    /// <summary>The discrete throttle presets native's TK_ThrottleZero/33/66/
    /// Full set the axis to (<c>WinTrek.cpp:10939-10960</c>). 33%/66% are
    /// UNBOUND in this port (as in native's defaultinputmap.mdl); Zero/Full
    /// are the two the port binds (Backspace / Backslash).</summary>
    public enum ThrottlePreset
    {
        /// <summary>-1 — TK_ThrottleZero: stop the ship (zero cruise target).</summary>
        Zero,
        /// <summary>-0.3 — TK_Throttle33: one-third cruise (unbound by default).</summary>
        ThirtyThree,
        /// <summary>+0.3 — TK_Throttle66: two-thirds cruise (unbound by default).</summary>
        SixtySix,
        /// <summary>+1 — TK_ThrottleFull: full cruise (the launch default).</summary>
        Full,
    }

    /// <summary>Resolve a <see cref="ThrottlePreset"/> to its axis value.</summary>
    public static float PresetValue(ThrottlePreset preset) => preset switch
    {
        ThrottlePreset.Zero        => -1f,
        ThrottlePreset.ThirtyThree => -0.3f,
        ThrottlePreset.SixtySix    => 0.3f,
        ThrottlePreset.Full        => 1f,
        _                          => -1f,
    };

    /// <summary>The HUD/cruise readout mapping — throttle axis [-1, 1] to the
    /// 0..1 fraction of cruise speed the sim's cruise branch targets:
    /// <c>-0.5*(1+throttle)/-maxSpeed = 0.5*(1+throttle)</c>. Idle (-1) is 0%,
    /// full (+1) is 100%. Shared so the HUD gauge and any caller agree.</summary>
    public static float CruiseFraction(float throttle) => 0.5f * (1f + throttle);

    // ── P504 — W/S persistent-throttle ramp (Domingo's control scheme) ────

    /// <summary>
    /// Rate at which holding W (up) or S (down) sweeps the persistent throttle
    /// across its axis, in axis-units per second. The axis spans [-1, +1]
    /// (width <see cref="ThrottleSpan"/>), so at the default rate a full sweep
    /// takes ≈1.7 s (<see cref="ThrottleRampFullSweepSeconds"/>) — comfortably
    /// inside the task's "about 1.5-2 s". Easy to tune by editing this single
    /// number. Chosen so the ramp feels deliberate but responsive and is
    /// frame-rate independent (integrated by real elapsed seconds).
    /// </summary>
    public const float ThrottleRampRatePerSec = 1.2f;

    /// <summary>The full width of the throttle axis ([−1, +1]).</summary>
    public const float ThrottleSpan = 2f;

    /// <summary>Seconds for a full-axis sweep at the default ramp rate
    /// (<see cref="ThrottleSpan"/> / <see cref="ThrottleRampRatePerSec"/>).
    /// A convenience for reporting/tuning the feel.</summary>
    public static float ThrottleRampFullSweepSeconds => ThrottleSpan / ThrottleRampRatePerSec;

    /// <summary>
    /// The held-key ramp direction, mirroring the other throttle keys'
    /// <c>composing</c> gate so typing in chat / navigating the quick-chat menu
    /// never moves the throttle: <c>+1</c> while W is held (raise), <c>-1</c>
    /// while S is held (lower), <c>0</c> when neither is held OR the modal UI
    /// is composing (the ramp yields to nothing, exactly like =/-/wheel).
    /// Opposing W+S cancel to <c>0</c>.
    /// </summary>
    public static float RampDirection(bool wHeld, bool sHeld, bool composing) =>
        composing ? 0f : (wHeld ? 1f : 0f) + (sHeld ? -1f : 0f);

    /// <summary>
    /// Integrate one frame of the held-key throttle ramp.
    /// <paramref name="direction"/> is the <see cref="RampDirection"/> output
    /// (+1 raise, −1 lower, 0 hold). Frame-rate independent: the result depends
    /// only on the total elapsed <paramref name="deltaSeconds"/>, never on how
    /// many frames that time is split across (linear in dt). Releases keep the
    /// value (direction 0 returns <paramref name="current"/> untouched).
    /// Clamps to the axis bounds [−1, +1].
    /// </summary>
    public static float RampThrottle(float current, float direction, float deltaSeconds)
    {
        if (direction == 0f) return current;
        return Math.Clamp(current + direction * ThrottleRampRatePerSec * deltaSeconds, -1f, 1f);
    }

    // ── P504 — mouse-wheel direction ──────────────────────────────────────

    /// <summary>The ScrollWheelValue travel of one wheel detent (a notch).
    /// Shared with the MonoGame wheel accumulation in Game1 so the pure mapping
    /// and the live sampler agree on the notch size.</summary>
    public const int WheelNotch = 120;

    /// <summary>
    /// The scalar that corrects the raw mouse-wheel delta to MonoGame's
    /// documented convention (ScrollWheelValue increases on scroll-up, decreases
    /// on scroll-down — MouseState docs; the SDL backend accumulates
    /// <c>ev.Wheel.Y * 120</c>, SDLGamePlatform.cs:141-144). On Linux the
    /// underlying SDL/Wayland wheel delivery historically INVERTS that sign
    /// (MonoGame issue #2686 documents the Linux inversion), and Domingo's live
    /// playtest confirmed it: scrolling DOWN to slow the ship only ever produced
    /// "throttle wheel UP" lines and pinned the throttle at 100%. Negating on
    /// Linux restores "scroll-down → throttle down"; other platforms already
    /// deliver the documented sign and are passed through. See the P504 report
    /// for the evidence and the raw-delta diagnostic that confirms it live.
    /// </summary>
    public static float WheelSignNormalizer => OperatingSystem.IsLinux() ? -1f : 1f;

    /// <summary>Apply <see cref="WheelSignNormalizer"/> to a raw per-frame
    /// ScrollWheelValue delta, yielding the delta in the corrected
    /// (documented) sign space: positive = scroll up, negative = scroll down.</summary>
    public static float NormalizeWheelDelta(int rawDelta) => rawDelta * WheelSignNormalizer;

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

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
    bool StrafeUp = false,  // P624 — vertical strafe up (Insert) — TK_ThrustUp
    bool StrafeDown = false,// P624 — vertical strafe down (X) — TK_ThrustDown
    bool Afterburner = false, // Shift (either side) — TK_FireBooster
    bool Fire = false,        // Space — TK_FireWeapon (held)
    bool Cloak = false,       // F — TK_ToggleCloak (latched toggle, see remarks)
    // P624 — vector lock (TK_LockVector). The LATCHED client toggle state set
    // by the L key (edge-triggered), carried in each frame exactly like Cloak;
    // Translate folds it into the outgoing stateM as CoastButton (8, the
    // "vector lock" bit, ShipControlState.CoastButton — native toggles
    // coastButtonIGC, WinTrek.cpp:9458-9464). Native defaults L (mdl line 39).
    bool VectorLock = false,
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
    float Throttle = -1f,
    // P761 — TK_RollModifier (igc.h:434, name "Roll Modifier", trekinput.cpp:183):
    // the WHILE-HELD modifier that repurposes the yaw axis as roll. Native
    // SenseJoystick (WinTrek.cpp:10825-10840): held → yaw axis 0, the X axis
    // feeds c_axisRoll; not held → X feeds yaw and axis 2 feeds roll. The
    // internal-camera key block (WinTrek.cpp:10895-10903) does the same for the
    // TK_YawLeft/TK_YawRight keys: with the modifier down they drive roll and
    // yaw stays 0.
    bool RollModifier = false)
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
    /// <summary>WinTrek/WinTrek.cpp:9027-9058, 9650-9671: occupied fixed
    /// weapon selects single fire; pressing that selection again groups.</summary>
    public static (bool GroupFire, int SelectedMount) ToggleWeapon(bool groupFire,
        int selected, int requested, int fixedCount, bool occupied)
        => !occupied || requested < 0 || requested >= fixedCount
            ? (groupFire, selected)
            : (!groupFire && requested == selected, requested);

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

        // P761 — TK_RollModifier (igc.h:434): while held the yaw axis IS the roll
        // axis and yaw is 0 (WinTrek.cpp:10828-10834 joystick block: modifier →
        // jsValues[c_axisYaw]=0, c_axisRoll=MapJoystick(GetAxis(0)); the key
        // block at :10895-10903 routes TK_YawLeft/Right to c_axisRoll the same
        // way). The yaw contribution (opposing keys already cancelled above)
        // moves to roll; the existing roll keys keep contributing (native's
        // else-if overwrite is order-equivalent for the single-key common case).
        if (input.RollModifier)
        {
            roll += yaw;
            yaw = 0f;
        }

        int stateM = 0;
        if (input.ThrottleForward) stateM |= ShipControlState.ForwardButton;
        if (input.ThrottleBack)    stateM |= ShipControlState.BackwardButton;
        if (input.StrafeLeft)      stateM |= ShipControlState.LeftButton;
        if (input.StrafeRight)     stateM |= ShipControlState.RightButton;
        // P624 — vertical strafe (TK_ThrustUp/Down → UpButton/DownButton,
        // igc.h:46/47): the same lateral-thrust model as Q/E (ShipKinematics
        // already sums Left/Right/Up/Down into the manual engine vector at
        // ShipKinematics.cs:157-162), so these just OR the matching movement
        // bits — no new sim behaviour.
        if (input.StrafeUp)        stateM |= ShipControlState.UpButton;
        if (input.StrafeDown)      stateM |= ShipControlState.DownButton;

        // P220 — afterburner (TK_FireBooster → ShipControlState.AfterburnerButton,
        // the 128·coastButtonIGC bit, igc.h:817). Continuous while Shift is held
        // (native's IsTrekKeyDown semantics), composed with any movement bits —
        // P641 folds the booster latch into this same boolean in the host.
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

        // P624 — vector lock (TK_LockVector → ShipControlState.CoastButton, the
        // "vector lock"/coast bit, igc.h:807/igc.h:813 note). Latched client
        // toggle like Cloak: while engaged the CoastButton is OR-ed every frame
        // so the sim's coast branch (ShipKinematics.cs:167-171) kills lateral
        // velocity and holds heading+speed, exactly native's coastButtonIGC.
        // The server re-derives it from the transmitted stateM, so a locally
        // stepped own-ship agrees with the server's replay.
        if (input.VectorLock) stateM |= ShipControlState.CoastButton;

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

    // ── P624 — match speed (native GetThrottle, WinTrek.cpp:122-134) ────────

    /// <summary>
    /// The cruise-throttle value [-1, 1] that makes the pilot's own ship match
    /// a target's current speed — native's <c>GetThrottle</c>
    /// (<c>WinTrek.cpp:122-134</c>): <c>speed ≥ myMaxSpeed ? 1.0 : 2·speed/
    /// myMaxSpeed − 1</c>. A stationary target yields −1 (stop); a target at or
    /// above the pilot's max speed yields +1 (full). The TK_MatchSpeed key is
    /// HELD (native <c>IsTrekKeyDown</c>, WinTrek.cpp:10966-10974) and while
    /// held the port re-derives <c>_throttle</c> from the current target each
    /// frame, exactly as native overwrites <c>trekClient.trekThrottle</c>.
    /// </summary>
    public static float MatchSpeedThrottle(float targetSpeed, float myMaxSpeed)
        => targetSpeed >= myMaxSpeed ? 1f : (2f * targetSpeed / myMaxSpeed) - 1f;

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

using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="FlightInput"/> — every real key combo this
/// POC supports must translate to exactly the expected <see cref="ControlData"/>
/// and movement-bit <c>stateM</c>. No graphics device involved.
/// </summary>
public class FlightInputTests
{
    [Fact]
    public void NoKeys_NeutralControls_ZeroStateM()
    {
        var cmd = FlightInput.Translate(FlightInputState.None);
        AssertAxis(cmd.Controls, yaw: 0, pitch: 0, roll: 0);
        Assert.Equal(0, cmd.StateM);
    }

    [Theory]
    [InlineData(true, false, false, false)] // W → forward
    [InlineData(false, true, false, false)] // S → back
    [InlineData(false, false, true, false)] // Q → strafe left
    [InlineData(false, false, false, true)] // E → strafe right
    public void MovementKeys_SetExactlyTheirButton(bool w, bool s, bool q, bool e)
    {
        // P516 — native held thrust restored: W sets the sim's ForwardButton,
        // S sets BackwardButton (reverse / brake), Q/E the strafe bits. (P504
        // had repurposed W/S as a persistent-throttle ramp with no thrust bits;
        // reverted per Domingo 2026-10-03.)
        var cmd = FlightInput.Translate(new FlightInputState(w, s, q, e, false, false, false, false, false, false));
        int expected = 0;
        if (w) expected |= ShipControlState.ForwardButton;
        if (s) expected |= ShipControlState.BackwardButton;
        if (q) expected |= ShipControlState.LeftButton;
        if (e) expected |= ShipControlState.RightButton;
        Assert.Equal(expected, cmd.StateM);
        AssertAxis(cmd.Controls, 0, 0, 0);
    }

    [Fact]
    public void WAndSBothHeld_ThrustBitsBothLatched()
    {
        // W+S both held latshes forward AND backward — the sim's manual branch
        // sees the movement bits and overrides cruise; the persistent throttle
        // just rides along and governs the moment both are released.
        var cmd = FlightInput.Translate(new FlightInputState(true, true, false, false, false, false, false, false, false, false));
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.BackwardButton, cmd.StateM);
    }

    [Fact]
    public void WPlusQ_YawNeutralThrustPlusStrafeCompose()
    {
        // W (forward thrust) + Q (strafe left) compose to both bits.
        var cmd = FlightInput.Translate(new FlightInputState(true, false, true, false, false, false, false, false, false, false));
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.LeftButton, cmd.StateM);
    }

    [Theory]
    [InlineData(true, false, 1f, 0f)]   // Up → pitch +1
    [InlineData(false, true, -1f, 0f)]  // Down → pitch −1
    [InlineData(false, false, 0f, 0f)]
    [InlineData(true, true, 0f, 0f)]    // both → cancel
    public void PitchArrows_SetSignedPitchAxis(bool up, bool down, float expectPitch, float _)
    {
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, up, down, false, false, false, false));
        AssertAxis(cmd.Controls, 0, expectPitch, 0);
        Assert.Equal(0, cmd.StateM);
    }

    [Theory]
    [InlineData(true, false, 1f, 0f)]   // Left → yaw +1 (nose left — matches native TK_YawLeft → +1)
    [InlineData(false, true, -1f, 0f)]  // Right → yaw −1 (nose right — matches native TK_YawRight → −1)
    [InlineData(true, true, 0f, 0f)]    // both → cancel
    public void YawArrows_SetSignedYawAxis(bool left, bool right, float expectYaw, float _)
    {
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, left, right, false, false));
        AssertAxis(cmd.Controls, expectYaw, 0, 0);
    }

    [Theory]
    [InlineData(true, false, 1f)]   // A → roll +1 (banks left — live-playtest confirmed sign)
    [InlineData(false, true, -1f)]  // D → roll −1 (banks right)
    [InlineData(true, true, 0f)]    // both → cancel
    public void RollKeys_SetSignedRollAxis(bool left, bool right, float expectRoll)
    {
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, left, right));
        AssertAxis(cmd.Controls, 0, 0, expectRoll);
    }

    [Fact]
    public void FullMotion_AllAxesAndThrustTogether()
    {
        var cmd = FlightInput.Translate(new FlightInputState(
            ThrottleForward: true, ThrottleBack: false,
            StrafeLeft: false, StrafeRight: true,
            PitchUp: true, PitchDown: false,
            YawLeft: false, YawRight: true,
            RollLeft: false, RollRight: true));
        AssertAxis(cmd.Controls, yaw: -1f, pitch: 1f, roll: -1f);
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.RightButton, cmd.StateM);
    }

    [Fact]
    public void HoldingFire_SetsAllWeaponsInStateM()
    {
        // Space → TK_FireWeapon: ORs ShipControlState.AllWeapons into the
        // outgoing stateM payload (group-fire "all weapons" mode — the exact
        // bit the wingman NPC sets itself, Ship.cs:4558).
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, false, false, Fire: true));
        Assert.Equal(ShipControlState.AllWeapons, cmd.StateM);
        AssertAxis(cmd.Controls, 0, 0, 0);
    }

    [Fact]
    public void ReleasingFire_LeavesStateMFreeOfAllWeapons()
    {
        // The brief's release-clears requirement: firing is NOT latched once
        // the key is lifted (NoKeys already asserts total zero; this pins the
        // specific AllWeapons bit).
        var cmd = FlightInput.Translate(FlightInputState.None);
        Assert.Equal(0, cmd.StateM & ShipControlState.AllWeapons);
    }

    [Fact]
    public void HoldingFire_ComposesWithMovementAndAfterburner()
    {
        // Fire is additive like afterburner — a held Space must not disturb
        // any simultaneous movement/afterburner bits. W held (forward thrust)
        // coexists with the afterburner and all-weapons bits.
        var cmd = FlightInput.Translate(new FlightInputState(true, false, false, false, false, false, false, false, false, false, Afterburner: true, Fire: true));
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.AfterburnerButton | ShipControlState.AllWeapons, cmd.StateM);
    }

    // ── P225 — single-weapon selection ────────────────────────────────────

    [Fact]
    public void GroupFireDefault_SingleMountIgnored_FiresAllWeapons()
    {
        // GroupFire defaults to true (the P221 behaviour), so even with a
        // nonzero SelectedMount the Fire input ORs AllWeapons — nothing regresses.
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, false, false, Fire: true, SelectedMount: 1));
        Assert.Equal(ShipControlState.AllWeapons, cmd.StateM);
    }

    [Fact]
    public void SingleWeapon_GroupFireOff_SetsOneWeaponPlusMount()
    {
        // Group-fire off + a selected mount: Fire ORs OneWeapon | the mount id
        // — the exact shape Weapon.IsWeaponSelected's fixed-mount branch reads.
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, false, false, Fire: true, GroupFire: false, SelectedMount: 1));
        Assert.Equal(ShipControlState.OneWeapon | 1, cmd.StateM);
    }

    [Fact]
    public void SingleWeapon_SelectedMountIsMaskedToSelectionWidth()
    {
        // The mount id is masked to the 2-bit SelectedWeaponMask (3), so an
        // out-of-range id aliases onto a legal one rather than leaking high bits.
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, false, false, Fire: true, GroupFire: false, SelectedMount: 5));
        Assert.Equal(ShipControlState.OneWeapon | (5 & ShipControlState.SelectedWeaponMask), cmd.StateM);
    }

    [Fact]
    public void SingleWeapon_ReleaseClears_NoFireBits()
    {
        // Fire released in single-weapon mode leaves stateM free of BOTH the
        // AllWeapons and OneWeapon bits (release-clears, not latched).
        var cmd = FlightInput.Translate(new FlightInputState(false, false, false, false, false, false, false, false, false, false, Fire: false, GroupFire: false, SelectedMount: 1));
        Assert.Equal(0, cmd.StateM & (ShipControlState.AllWeapons | ShipControlState.OneWeapon));
    }

    [Theory]
    [InlineData(0, 2, 1)]  // 0 -> 1 on a two-mount hull
    [InlineData(1, 2, 0)]  // 1 -> 0 wraps
    [InlineData(0, 4, 1)]
    [InlineData(3, 4, 0)]  // 3 -> 0 wraps
    [InlineData(1, 0, 0)]  // no selectable mounts -> 0
    public void NextMount_AdvancesAndWrapsAtCount(int current, int count, int expect)
    {
        Assert.Equal(expect, FlightInput.NextMount(current, count));
    }

    [Theory]
    [InlineData(0, 2, 1)]  // 0 -> 1 wraps backward
    [InlineData(1, 2, 0)]  // 1 -> 0
    [InlineData(0, 4, 3)]  // 0 -> 3 wraps backward
    [InlineData(3, 4, 2)]  // 3 -> 2
    [InlineData(1, 0, 0)]  // no selectable mounts -> 0
    public void PrevMount_AdvancesAndWrapsAtCount(int current, int count, int expect)
    {
        Assert.Equal(expect, FlightInput.PrevMount(current, count));
    }

    // ── P413 — cloak toggle ────────────────────────────────────────────────

    [Fact]
    public void CloakEngaged_SetsCloakActiveBitInStateM()
    {
        // The control-bit wiring under test (P413): the latched cloak state
        // folds into the outgoing control stateM as ShipControlState.CloakActive
        // (0x800) — the exact bit the server's Ship.UpdateCloak reads to activate
        // a mounted ET_Cloak part (via FM_C_SHIP_UPDATE → Server.Dispatch).
        var cmd = FlightInput.Translate(new FlightInputState(
            false, false, false, false, false, false, false, false, false, false,
            Cloak: true));
        Assert.NotEqual(0, cmd.StateM & ShipControlState.CloakActive);
    }

    [Fact]
    public void CloakDisengaged_LeavesStateMFreeOfCloakActive()
    {
        // Toggling cloak back off clears the bit — nothing leaks into the frame.
        var cmd = FlightInput.Translate(FlightInputState.None);
        Assert.Equal(0, cmd.StateM & ShipControlState.CloakActive);
    }

    [Fact]
    public void CloakEngaged_ComposesWithMovementAfterburnerAndFire()
    {
        // Cloak is additive like afterburner — engaging it while moving/firing
        // must not disturb the other bits. W held adds the forward-thrust bit.
        var cmd = FlightInput.Translate(new FlightInputState(
            true, false, false, false, false, false, false, false, false, false,
            Afterburner: true, Fire: true, Cloak: true));
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.AfterburnerButton
                     | ShipControlState.AllWeapons | ShipControlState.CloakActive, cmd.StateM);
    }

    // ── P445 — persistent throttle pass-through ───────────────────────────

    [Fact]
    public void Throttle_PassedThroughNotHardcodedMinusOne()
    {
        // P445 — the client owns a persistent throttle (native trekThrottle);
        // Translate must send the value it was handed, not always -1. Written
        // FIRST and run against the unfixed code: this must FAIL (Translate
        // hard-codes the throttle axis to -1 every frame).
        var cmd = FlightInput.Translate(new FlightInputState { Throttle = 0.5f });
        Assert.Equal(0.5f, cmd.Controls.Throttle, 3);
    }

    [Theory]
    // Up: current < 0.8 → +0.2
    [InlineData(-1f, -0.8f)]
    [InlineData(-0.8f, -0.6f)]
    [InlineData(0.6f, 0.8f)]
    // Up: current >= 0.8 → clamp to exactly +1 (native: < 0.8 ? +0.2 : 1.0)
    [InlineData(0.8f, 1f)]
    [InlineData(0.95f, 1f)]
    [InlineData(1f, 1f)]
    public void StepThrottleUp_AddsTwoTenths_ClampsAtFull(float current, float expect)
    {
        Assert.Equal(expect, FlightInput.StepThrottleUp(current), 5);
    }

    [Theory]
    // Down: current > -0.8 → -0.2
    [InlineData(1f, 0.8f)]
    [InlineData(0.8f, 0.6f)]
    [InlineData(-0.6f, -0.8f)]
    // Down: current <= -0.8 → clamp to exactly -1 (native: > -0.8 ? -0.2 : -1.0)
    [InlineData(-0.8f, -1f)]
    [InlineData(-0.95f, -1f)]
    [InlineData(-1f, -1f)]
    public void StepThrottleDown_SubtractsTwoTenths_ClampsAtReverse(float current, float expect)
    {
        Assert.Equal(expect, FlightInput.StepThrottleDown(current), 5);
    }

    [Theory]
    [InlineData(FlightInput.ThrottlePreset.Zero, -1f)]
    [InlineData(FlightInput.ThrottlePreset.ThirtyThree, -0.3f)]
    [InlineData(FlightInput.ThrottlePreset.SixtySix, 0.3f)]
    [InlineData(FlightInput.ThrottlePreset.Full, 1f)]
    public void Presets_MapToNativeAxisValues(FlightInput.ThrottlePreset preset, float expect)
    {
        Assert.Equal(expect, FlightInput.PresetValue(preset), 5);
    }

    [Fact]
    public void Throttle_StepsAndSends_NeverClipBeyondBounds()
    {
        // Full stepping from the launch default reaches exactly +1 then sticks;
        // from full-reverse it sticks at -1. Covers the "persistent, clamped"
        // lifecycle end to end through Translate (not just the pure helpers).
        float t = FlightInput.DefaultThrottle;
        t = FlightInput.StepThrottleUp(t);   // 1 -> stays 1
        var cmd = FlightInput.Translate(new FlightInputState { Throttle = t });
        Assert.Equal(1f, cmd.Controls.Throttle, 5);

        t = FlightInput.StepThrottleDown(t);  // 1 -> 0.8
        t = FlightInput.StepThrottleDown(t);  // 0.8 -> 0.6
        t = FlightInput.StepThrottleDown(t);  // 0.6 -> 0.4
        t = FlightInput.StepThrottleDown(t);  // 0.4 -> 0.2
        t = FlightInput.StepThrottleDown(t);  // 0.2 -> 0.0
        t = FlightInput.StepThrottleDown(t);  // 0.0 -> -0.2
        t = FlightInput.StepThrottleDown(t);  // -0.2 -> -0.4
        t = FlightInput.StepThrottleDown(t);  // -0.4 -> -0.6
        t = FlightInput.StepThrottleDown(t);  // -0.6 -> -0.8
        t = FlightInput.StepThrottleDown(t);  // -0.8 -> -1 (clamped)
        t = FlightInput.StepThrottleDown(t);  // -1 -> stays -1
        Assert.Equal(-1f, t, 5);
    }

    [Fact]
    public void MovementButtons_ComposeWithPassedThrottle()
    {
        // P445/P516 — movement bits and the throttle travel together: holding
        // W/S (manual thrust) sets the stateM bits while the throttle axis is
        // still sent verbatim (the sim's manual branch overrides cruise while a
        // movement button is held; the throttle just rides along and governs
        // the moment W is released). Also pins that holding W leaves the
        // persistent throttle UNCHANGED (it is not a throttle modifier).
        var cmd = FlightInput.Translate(new FlightInputState
            { ThrottleForward = true, ThrottleBack = true, Throttle = 0.6f });
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.BackwardButton, cmd.StateM);
        Assert.Equal(0.6f, cmd.Controls.Throttle, 3);
        Assert.Equal(0f, cmd.Controls.Yaw, 3);
        Assert.Equal(0f, cmd.Controls.Pitch, 3);
        Assert.Equal(0f, cmd.Controls.Roll, 3);
    }

    [Fact]
    public void Release_ReturnsToCruise_NoStickyMovementBits()
    {
        // P516 — releasing W/S clears the movement bits (nothing latches), and
        // the frame that follows rides the persistent cruise throttle alone —
        // the sim's cruise-control branch takes back over with the throttle
        // verbatim. (This is the "release returns to cruise" half of the P504
        // reversal: no ramp, no sticky thrust.)
        var cmd = FlightInput.Translate(new FlightInputState { Throttle = 0.6f });
        Assert.Equal(0, cmd.StateM & (ShipControlState.ForwardButton | ShipControlState.BackwardButton));
        Assert.Equal(0.6f, cmd.Controls.Throttle, 3);
    }

    [Fact]
    public void LaunchDefault_IsFullThrottle()
    {
        // P445 — the client resets the persistent throttle to full (+1) on
        // launch/undock, native's on-line default (WinTrek.cpp:10860-10862).
        // The single shared constant ties the Game1 reset to this expectation.
        Assert.Equal(1f, FlightInput.DefaultThrottle, 5);
        Assert.Equal(FlightInput.DefaultThrottle, FlightInput.PresetValue(FlightInput.ThrottlePreset.Full), 5);
    }

    [Theory]
    [InlineData(-1f, 0f)]   // idle / stop   -> 0% cruise
    [InlineData(-0.3f, 0.35f)] // 33% preset  -> 35%
    [InlineData(0f, 0.5f)]  // centre        -> 50%
    [InlineData(0.3f, 0.65f)] // 66% preset  -> 65%
    [InlineData(1f, 1f)]    // full          -> 100%
    public void CruiseFraction_MapsThrottleToCruisePercent(float throttle, float expect)
    {
        // The HUD readout mapping: 0.5*(1+throttle) = fraction of max cruise speed.
        Assert.Equal(expect, FlightInput.CruiseFraction(throttle), 5);
    }

    // ── P504 (kept) — mouse-wheel direction ───────────────────────────────

    [Fact]
    public void WheelNormalizer_ReflectsDocConvention()
    {
        // P504 — on Linux the delivered wheel sign is inverted (documented
        // MonoGame issue #2686; confirmed live by Domingo's only-UP / stuck-at-
        // 100% playtest), so the normaliser flips it back to MonoGame's
        // documented convention (+ = scroll up, - = scroll down). On other
        // platforms the delivered sign already matches and is passed through.
        Assert.Equal(+FlightInput.WheelNotch, FlightInput.NormalizeWheelDelta(+FlightInput.WheelNotch) * (OperatingSystem.IsLinux() ? -1f : 1f), 3);
        Assert.Equal(-FlightInput.WheelNotch, FlightInput.NormalizeWheelDelta(-FlightInput.WheelNotch) * (OperatingSystem.IsLinux() ? -1f : 1f), 3);
    }

    [Theory]
    // A real scroll-DOWN is delivered as a POSITIVE raw delta on Linux (the
    // inversion); after normalisation it must be negative → a DOWN throttle step.
    [InlineData(+120, -1)]
    // A real scroll-UP is delivered as a NEGATIVE raw delta on Linux; after
    // normalisation it must be positive → an UP throttle step.
    [InlineData(-120, +1)]
    public void WheelNormalizer_MapsDeliveredDeltaToCorrectDirection(int rawDelta, int expectSign)
    {
        // The delivered-sign → corrected-sign mapping is platform-dependent
        // (Linux inverts); normalise the expectation so this stays green when
        // the suite is run off-Linux too.
        int deliveredSign = Math.Sign(rawDelta);
        int correctedSign = deliveredSign * (OperatingSystem.IsLinux() ? -1 : 1);
        Assert.Equal(correctedSign, expectSign);   // sanity: this platform's convention
        Assert.Equal(correctedSign, Math.Sign(FlightInput.NormalizeWheelDelta(rawDelta)));
    }

    private static void AssertAxis(ControlData c, float yaw, float pitch, float roll)
    {
        Assert.Equal(yaw, c.Yaw, 3);
        Assert.Equal(pitch, c.Pitch, 3);
        Assert.Equal(roll, c.Roll, 3);
        Assert.Equal(-1f, c.Throttle, 3);  // idles at -1 (buttons drive fore/aft; see Translate's remarks)
    }
}

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
        var cmd = FlightInput.Translate(new FlightInputState(true, true, false, false, false, false, false, false, false, false));
        Assert.Equal(ShipControlState.ForwardButton | ShipControlState.BackwardButton, cmd.StateM);
    }

    [Fact]
    public void WPlusQ_YawNeutralThrustPlusStrafeCompose()
    {
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
        // any simultaneous movement/afterburner bits.
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

    private static void AssertAxis(ControlData c, float yaw, float pitch, float roll)
    {
        Assert.Equal(yaw, c.Yaw, 3);
        Assert.Equal(pitch, c.Pitch, 3);
        Assert.Equal(roll, c.Roll, 3);
        Assert.Equal(-1f, c.Throttle, 3);  // idles at -1 (buttons drive fore/aft; see Translate's remarks)
    }
}

using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P761 — <c>TK_RollModifier</c> (igc.h:434, name "Roll Modifier",
/// trekinput.cpp:183): while held the steering X axis drives ROLL and yaw is 0;
/// released, X → yaw and the roll axis → roll (native SenseJoystick,
/// WinTrek/WinTrek.cpp:10825-10840; the internal-camera key block routes
/// TK_YawLeft/Right to roll the same way at :10895-10903). Both states pinned
/// through <see cref="FlightInput.Translate"/> (the keyboard-axis path).
/// </summary>
public class P761RollModifierTests
{
    private static FlightInputState State(bool yawLeft = false, bool yawRight = false,
        bool rollLeft = false, bool rollRight = false, bool modifier = false) =>
        new(ThrottleForward: false, ThrottleBack: false, StrafeLeft: false, StrafeRight: false,
            PitchUp: false, PitchDown: false, YawLeft: yawLeft, YawRight: yawRight,
            RollLeft: rollLeft, RollRight: rollRight, RollModifier: modifier);

    [Fact]
    public void ModifierHeld_YawKeysDriveRoll_AndYawIsZero()
    {
        // ← = +1 yaw natively; with the modifier it becomes +1 roll (the key
        // block at WinTrek.cpp:10895-10903: TK_RollModifier+TK_YawLeft → roll
        // +1), yaw 0 (the joystick block zeroes c_axisYaw, :10828-10829).
        var cmd = FlightInput.Translate(State(yawLeft: true, modifier: true));
        Assert.Equal(0f, cmd.Controls.Yaw);
        Assert.Equal(1f, cmd.Controls.Roll);

        var right = FlightInput.Translate(State(yawRight: true, modifier: true));
        Assert.Equal(0f, right.Controls.Yaw);
        Assert.Equal(-1f, right.Controls.Roll);
    }

    [Fact]
    public void ModifierReleased_XDrivesYaw_AndRollKeysDriveRoll()
    {
        var cmd = FlightInput.Translate(State(yawLeft: true));
        Assert.Equal(1f, cmd.Controls.Yaw);
        Assert.Equal(0f, cmd.Controls.Roll);

        var roll = FlightInput.Translate(State(rollRight: true));
        Assert.Equal(0f, roll.Controls.Yaw);
        Assert.Equal(-1f, roll.Controls.Roll);
    }

    [Fact]
    public void ModifierHeld_ComposesWithTheRollKeys_OppositesCancel()
    {
        // Modifier + roll key: both contributions feed the roll axis (native's
        // else-if overwrite is order-equivalent for the single-key case; the
        // sum mirrors the opposing-key cancellation convention above it).
        var both = FlightInput.Translate(State(yawLeft: true, rollLeft: true, modifier: true));
        Assert.Equal(0f, both.Controls.Yaw);
        Assert.Equal(2f, both.Controls.Roll);

        // Opposing yaw keys cancel even through the modifier (no net roll).
        var cancel = FlightInput.Translate(State(yawLeft: true, yawRight: true, modifier: true));
        Assert.Equal(0f, cancel.Controls.Yaw);
        Assert.Equal(0f, cancel.Controls.Roll);
    }

    [Fact]
    public void Catalog_HasABindableRollModifierAction_UnboundLikeMouseWheel()
    {
        // The action exists and is rebindable; this checkout ships no
        // defaultinputmap.mdl so there is no native default to copy (the
        // MouseWheel precedent) — it starts unbound.
        var def = Assert.Single(InputActionCatalog.Definitions, d => d.Name == "RollModifier");
        Assert.Equal(Microsoft.Xna.Framework.Input.Keys.None, def.Default.Key);
        // The catalogue names the native key identity.
        Assert.Contains("TK_RollModifier", def.Description);
    }

    [Fact]
    public void ReboundModifier_FlowsThroughTheActionMap()
    {
        // Bind the modifier to a real key (the F6 Controls flow) and confirm a
        // bound key reaches the same Translate state through the action map.
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.Null(map.SetBinding("RollModifier", new KeyBinding(Microsoft.Xna.Framework.Input.Keys.Pause, InputModifiers.None)));
        var state = new Microsoft.Xna.Framework.Input.KeyboardState(Microsoft.Xna.Framework.Input.Keys.Pause);
        Assert.True(map.IsDown(state, "RollModifier"));
    }
}

using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Tests for the training control masks — <c>TrainingController.ApproveControls</c>/<c>ApproveActions</c>
/// (the C# port of <c>Training.cpp:253-300</c>) and the actions that write the masks
/// (<c>SetControlConstraintsAction</c>, <c>SetControlsAction</c>, <c>TurnToAction</c>). The global mask
/// state is reset before each test so no test leaks into another.
/// </summary>
[Collection("Training")]
public class TrainingControlMaskTests
{
    public TrainingControlMaskTests() => TrainingControlState.Reset();

    [Fact]
    public void ApproveControls_DefaultMasks_LeaveInputsUnchanged()
    {
        // defaults: all scalars 1.0f, nothing forced (Training.cpp:41-53)
        var input = new ControlData(Yaw: 0.5f, Pitch: -0.25f, Roll: 0.75f, Throttle: 0.0f);
        var approved = TrainingController.ApproveControls(input);

        Assert.Equal(0.5f, approved.Yaw, 3);
        Assert.Equal(-0.25f, approved.Pitch, 3);
        Assert.Equal(0.75f, approved.Roll, 3);
        Assert.Equal(0.0f, approved.Throttle, 3);
    }

    [Fact]
    public void ApproveControls_ZeroScalars_KillTheAxes()
    {
        // ScaleInputControl(axis, 0.0f) -> g_allowableControlScalars[axis] = 0 (mission2 locks the ship)
        TrainingControlState.AllowableControlScalars[TrainingControlState.AxisYaw] = 0.0f;
        TrainingControlState.AllowableControlScalars[TrainingControlState.AxisThrottle] = 0.0f;

        var approved = TrainingController.ApproveControls(new ControlData(Yaw: 0.9f, Pitch: 0.9f, Roll: 0.9f, Throttle: 1.0f));
        Assert.Equal(0.0f, approved.Yaw, 3);
        // throttle translated to 0..1, scaled, translated back -> 0 means full reverse
        Assert.Equal(-1.0f, approved.Throttle, 3);
        Assert.Equal(0.9f, approved.Pitch, 3);
    }

    [Fact]
    public void ApproveControls_ForcedInputs_OverrideThePlayer()
    {
        // g_setInputControls override (Training.cpp:272-279); forced throttle maps 0..1 back to -1..1
        TrainingControlState.SetInputControls[TrainingControlState.AxisThrottle] = 0.33f;
        TrainingControlState.SetInputControls[TrainingControlState.AxisYaw] = 0.1f;

        var approved = TrainingController.ApproveControls(new ControlData(Yaw: -0.9f, Pitch: 0.5f, Roll: 0.0f, Throttle: -1.0f));
        Assert.Equal(0.1f, approved.Yaw, 3);                       // forced yaw
        Assert.Equal((0.33f * 2.0f) - 1.0f, approved.Throttle, 3); // 0.33 (0..1) -> -0.34
    }

    [Fact]
    public void ApproveActions_MasksOutDisallowed_AndOrsForcedBits()
    {
        // ApproveActions: AND g_iAllowableActionMask, OR g_iSetInputAction (Training.cpp:287-300)
        TrainingControlState.AllowableActionMask = 0b1100;
        TrainingControlState.SetInputAction = 0b0001;

        int result = TrainingController.ApproveActions(0b1111);
        Assert.Equal(0b1101, result);
    }

    [Fact]
    public void SetControlConstraintsAction_ScalesAndEditsActionMask()
    {
        TrainingControlState.Reset();
        var a = new SetControlConstraintsAction();
        a.ScaleInputControl(TrainingControlState.AxisRoll, 0.0f);
        a.DisableInputAction(0b0000_1000);
        a.EnableInputAction(0b0000_0010);
        a.Execute();

        Assert.Equal(0.0f, TrainingControlState.AllowableControlScalars[TrainingControlState.AxisRoll]);
        Assert.Equal(1.0f, TrainingControlState.AllowableControlScalars[TrainingControlState.AxisYaw]);

        // disallow bit 3 cleared, enable bit 1 set
        Assert.Equal(0, TrainingControlState.AllowableActionMask & 0b1000);
        Assert.NotEqual(0, TrainingControlState.AllowableActionMask & 0b0010);
    }

    [Fact]
    public void SetControlsAction_ForcesAndClears()
    {
        TrainingControlState.Reset();

        var force = new SetControlsAction();
        force.SetInputControl(TrainingControlState.AxisThrottle, 0.33f);
        force.SetInputAction(TrainingControlIds.AfterburnerButton);
        force.Execute();

        Assert.Equal(0.33f, TrainingControlState.SetInputControls[TrainingControlState.AxisThrottle]);
        Assert.NotEqual(0, TrainingControlState.SetInputAction & TrainingControlIds.AfterburnerButton);

        // an untouched SetControlsAction clears the forced state (missions "reset the controls")
        new SetControlsAction().Execute();
        Assert.Equal(TrainingControlState.Na, TrainingControlState.SetInputControls[TrainingControlState.AxisThrottle]);
        Assert.Equal(0, TrainingControlState.SetInputAction);
    }

    [Fact]
    public void SetControlsAction_SetInputAction_HonoursMask()
    {
        // SetInputAction(i, mask) = (m_iInputAction & mask) | i (SetControlsAction.cpp:51-54)
        TrainingControlState.Reset();
        var a = new SetControlsAction();
        a.SetInputAction(0b0011, mask: 0b1100); // current 0 & 1100 = 0, then | 0011 = 0011
        a.SetInputAction(0b1000, mask: 0b0011); // current 0011 & 0011 = 0011, then | 1000 = 1011
        a.Execute();
        Assert.Equal(0b1011, TrainingControlState.SetInputAction);
    }

    [Fact]
    public void TurnToAction_WritesDampedTurnAxes_WhenObjectsExist()
    {
        TrainingControlState.Reset();
        var site = new FakeTrainingSite();
        var player = site.AddObject(ObjectType.Ship, 2, System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX);
        var target = site.AddObject(ObjectType.Station, 99, new System.Numerics.Vector3(10f, 0f, 0f), System.Numerics.Vector3.UnitX);

        var turn = new TurnToAction(site, player.Ref, target.Ref);
        turn.Execute();

        // fake TurnToFace returns (0,0,0); damped by 0.5 -> still set (not NA), proving the write path
        Assert.NotEqual(TrainingControlState.Na, TrainingControlState.SetInputControls[TrainingControlState.AxisYaw]);
        Assert.NotEqual(TrainingControlState.Na, TrainingControlState.SetInputControls[TrainingControlState.AxisPitch]);
        Assert.NotEqual(TrainingControlState.Na, TrainingControlState.SetInputControls[TrainingControlState.AxisRoll]);
    }

    [Fact]
    public void TurnToAction_ClearsAxes_WhenTargetMissing()
    {
        // TurnToAction.cpp:68-72 — missing objects reset the forced turn axes to NA
        TrainingControlState.Reset();
        var site = new FakeTrainingSite();
        var player = site.AddObject(ObjectType.Ship, 2, System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX);

        var turn = new TurnToAction(site, player.Ref, new TargetRef(ObjectType.Station, 999)); // not registered
        turn.Execute();

        Assert.Equal(TrainingControlState.Na, TrainingControlState.SetInputControls[TrainingControlState.AxisYaw]);
        Assert.Equal(TrainingControlState.Na, TrainingControlState.SetInputControls[TrainingControlState.AxisPitch]);
        Assert.Equal(TrainingControlState.Na, TrainingControlState.SetInputControls[TrainingControlState.AxisRoll]);
    }
}

/// <summary>Small alias so the control-mask tests can read the afterburner bit by name.</summary>
internal static class TrainingControlIds
{
    public const int AfterburnerButton = 512;
}

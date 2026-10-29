using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// The client-side training controller — the C# analogue of the native module-level entry points in
/// <c>src/training/Training.cpp</c>. It owns the global "a training mission is running" flag
/// (<c>g_pMission != 0</c>, <c>Training.cpp:33,138</c>) and implements the two control-approval
/// functions the flight-input path calls:
/// <list type="bullet">
/// <item><c>ApproveControls</c> (<c>Training.cpp:253-284</c>) — scale the axes by the allowable
///   scalars, then override any forced <c>g_setInputControls</c>.</item>
/// <item><c>ApproveActions</c> (<c>Training.cpp:287-300</c>) — AND the allowed mask, then OR the
///   forced action bits.</item>
/// </list>
/// Everything is inert when <see cref="IsTraining"/> is false, so enabling the mask logic has no
/// effect outside a running mission. TR-2 wires these two methods into the flight-input path behind
/// <c>IsTraining</c>; until then they are exercised directly by unit tests.
/// </summary>
public static class TrainingController
{
    /// <summary>Whether a training mission is currently running (native <c>g_pMission != 0</c>).</summary>
    public static bool IsTraining { get; private set; }

    /// <summary>
    /// Begin a mission: reset the control masks and arm the training flag. Faithful to
    /// <c>StartMission</c> (<c>Training.cpp:39-99</c>) minus the mission instantiation itself
    /// (done by the host in TR-2).
    /// </summary>
    public static void BeginMission()
    {
        if (TrainingClock.IsPaused) TrainingClock.Continue();
        TrainingControlState.Reset();
        IsTraining = true;
    }

    /// <summary>End the current mission, disarming the flag (<c>EndMission</c>, <c>Training.cpp:116-126</c>).</summary>
    public static void EndMission()
    {
        if (TrainingClock.IsPaused) TrainingClock.Continue(); // trainingmission.cpp:123-125
        IsTraining = false;
    }

    /// <summary>
    /// Apply the training control masks to a <c>ControlData</c>. A faithful translation of
    /// <c>ApproveControls</c> (<c>Training.cpp:253-284</c>): capture the raw input, multiply the
    /// three turn axes by their allowable scalar, translate the throttle from ±1 to 0..1 before
    /// scaling and back, then override any forced axis with <c>g_setInputControls</c>.
    /// </summary>
    public static ControlData ApproveControls(ControlData inputControls)
    {
        // copy the input controls for later retrieval (g_inputControls = inputControls)
        TrainingControlState.InputControls = inputControls;

        float yaw = inputControls.Yaw * TrainingControlState.AllowableControlScalars[TrainingControlState.AxisYaw];
        float pitch = inputControls.Pitch * TrainingControlState.AllowableControlScalars[TrainingControlState.AxisPitch];
        float roll = inputControls.Roll * TrainingControlState.AllowableControlScalars[TrainingControlState.AxisRoll];

        // throttle maps joystick -1..1 to the more useful 0..1; translate, scale, translate back
        float throttle =
            ((inputControls.Throttle + 1.0f) * TrainingControlState.AllowableControlScalars[TrainingControlState.AxisThrottle]) - 1.0f;

        // override any explicitly-forced axis
        if (TrainingControlState.SetInputControls[TrainingControlState.AxisYaw] != TrainingControlState.Na)
            yaw = TrainingControlState.SetInputControls[TrainingControlState.AxisYaw];
        if (TrainingControlState.SetInputControls[TrainingControlState.AxisPitch] != TrainingControlState.Na)
            pitch = TrainingControlState.SetInputControls[TrainingControlState.AxisPitch];
        if (TrainingControlState.SetInputControls[TrainingControlState.AxisRoll] != TrainingControlState.Na)
            roll = TrainingControlState.SetInputControls[TrainingControlState.AxisRoll];
        if (TrainingControlState.SetInputControls[TrainingControlState.AxisThrottle] != TrainingControlState.Na)
            throttle = (TrainingControlState.SetInputControls[TrainingControlState.AxisThrottle] * 2.0f) - 1.0f;

        return new ControlData(yaw, pitch, roll, throttle);
    }

    /// <summary>
    /// Apply the action mask to a manoeuvre-button bitmask. A faithful translation of
    /// <c>ApproveActions</c> (<c>Training.cpp:287-300</c>): AND the allowed mask, then OR the forced
    /// action bits.
    /// </summary>
    public static int ApproveActions(int inputMask) =>
        (inputMask & TrainingControlState.AllowableActionMask) | TrainingControlState.SetInputAction;
}

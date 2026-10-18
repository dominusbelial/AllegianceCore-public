using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// The training control masks — a faithful port of the native module globals declared in
/// <c>src/training/Training.cpp:28-34</c>:
/// <list type="bullet">
/// <item><c>g_allowableControlScalars</c> — per-axis multipliers the commander sets
///   (<c>SetControlConstraintsAction</c> writes these).</item>
/// <item><c>g_inputControls</c> — the raw controls the player is feeding in, captured by
///   <c>ApproveControls</c>.</item>
/// <item><c>g_setInputControls</c> — controls the mission forces on the player
///   (<c>SetControlsAction</c> / <c>TurnToAction</c> write these).</item>
/// <item><c>g_iAllowableActionMask</c> — allowed manoeuvre-button bits.</item>
/// <item><c>g_iSetInputAction</c> — forced manoeuvre-button bits.</item>
/// </list>
/// The flight-input path (TR-2) reads these through <see cref="TrainingController.ApproveControls"/>
/// and <see cref="TrainingController.ApproveActions"/>, exactly as native <c>ApproveControls</c>
/// (<c>Training.cpp:253-284</c>) and <c>ApproveActions</c> (<c>Training.cpp:287-300</c>) do.
/// </summary>
public static class TrainingControlState
{
    /// <summary>Axes: c_axisYaw=0, c_axisPitch=1, c_axisRoll=2, c_axisThrottle=3
    /// (<c>src/Igc/igc.h:855-858</c>).</summary>
    public const int AxisYaw = ControlData.AxisYaw;
    public const int AxisPitch = ControlData.AxisPitch;
    public const int AxisRoll = ControlData.AxisRoll;
    public const int AxisThrottle = ControlData.AxisThrottle;

    /// <summary>The sentinel the native code calls <c>NA</c> for control values: an axis value of
    /// <c>NA</c> means "not forced / leave it alone" (<c>Training.cpp:49-52</c>).</summary>
    public const float Na = -1e9f;

    /// <summary>g_allowableControlScalars — per-axis multipliers, default 1.0f.</summary>
    public static float[] AllowableControlScalars = { 1.0f, 1.0f, 1.0f, 1.0f };

    /// <summary>g_inputControls — the player's raw inputs captured by ApproveControls.</summary>
    public static ControlData InputControls = ControlData.Neutral;

    /// <summary>g_setInputControls — the controls the mission forces, NA-by-default (unforced).</summary>
    public static float[] SetInputControls = { Na, Na, Na, Na };

    /// <summary>g_iAllowableActionMask — allowed manoeuvre-button bits, default all.</summary>
    public static int AllowableActionMask = unchecked((int)0xffffffff);

    /// <summary>g_iSetInputAction — forced manoeuvre-button bits, default 0.</summary>
    public static int SetInputAction;

    /// <summary>
    /// Restore the default masks — the body of native <c>StartMission</c>'s initialisation
    /// (<c>Training.cpp:41-53</c>). Called by <see cref="TrainingController.BeginMission"/>.
    /// </summary>
    public static void Reset()
    {
        AllowableControlScalars = new[] { 1.0f, 1.0f, 1.0f, 1.0f };
        InputControls = ControlData.Neutral;
        SetInputControls = new[] { Na, Na, Na, Na };
        AllowableActionMask = unchecked((int)0xffffffff);
        SetInputAction = 0;
    }
}

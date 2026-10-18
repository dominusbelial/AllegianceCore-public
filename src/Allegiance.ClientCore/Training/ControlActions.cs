using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Rewrites the allowable control scalars and action mask — port of <c>SetControlConstraintsAction</c>
/// (<c>SetControlConstraintsAction.cpp:27-99</c>). On <c>Execute</c>:
/// <list type="bullet">
/// <item>any axis scalar set via <c>ScaleInputControl</c> is copied into
///   <c>g_allowableControlScalars</c> (lines 48-55);</item>
/// <item>each disabled action bit is cleared from <c>g_iAllowableActionMask</c> (lines 58-63);</item>
/// <item>enabled action bits are OR-ed in (line 66).</item>
/// </list>
/// <c>EnableInputAction</c>/<c>DisableInputAction</c> are mutually exclusive (lines 70-91).
/// </summary>
public sealed class SetControlConstraintsAction : Action
{
    private readonly float[] _scalars = { TrainingControlState.Na, TrainingControlState.Na, TrainingControlState.Na, TrainingControlState.Na };
    private int _enableActionsMask;
    private int _disableActionsMask;

    public override void Execute()
    {
        if (_scalars[TrainingControlState.AxisYaw] != TrainingControlState.Na)
            TrainingControlState.AllowableControlScalars[TrainingControlState.AxisYaw] = _scalars[TrainingControlState.AxisYaw];
        if (_scalars[TrainingControlState.AxisPitch] != TrainingControlState.Na)
            TrainingControlState.AllowableControlScalars[TrainingControlState.AxisPitch] = _scalars[TrainingControlState.AxisPitch];
        if (_scalars[TrainingControlState.AxisRoll] != TrainingControlState.Na)
            TrainingControlState.AllowableControlScalars[TrainingControlState.AxisRoll] = _scalars[TrainingControlState.AxisRoll];
        if (_scalars[TrainingControlState.AxisThrottle] != TrainingControlState.Na)
            TrainingControlState.AllowableControlScalars[TrainingControlState.AxisThrottle] = _scalars[TrainingControlState.AxisThrottle];

        for (int i = 0; i < 32; i++)
        {
            int action = _disableActionsMask & (1 << i);
            if ((TrainingControlState.AllowableActionMask & action) != 0)
                TrainingControlState.AllowableActionMask ^= action;
        }
        TrainingControlState.AllowableActionMask |= _enableActionsMask;
    }

    public void EnableInputAction(int inputAction)
    {
        _enableActionsMask |= inputAction;
        for (int i = 0; i < 32; i++)
        {
            int action = _disableActionsMask & (1 << i);
            if ((inputAction & action) != 0)
                _disableActionsMask ^= action;
        }
    }

    public void DisableInputAction(int inputAction)
    {
        _disableActionsMask |= inputAction;
        for (int i = 0; i < 32; i++)
        {
            int action = _enableActionsMask & (1 << i);
            if ((inputAction & action) != 0)
                _enableActionsMask ^= action;
        }
    }

    public void ScaleInputControl(int axis, float scalar) => _scalars[axis] = scalar;
}

/// <summary>
/// Forces controls and actions on the player — port of <c>SetControlsAction</c>
/// (<c>SetControlsAction.cpp:27-62</c>). <c>Execute</c> overwrites <c>g_setInputControls</c> and
/// <c>g_iSetInputAction</c> wholesale (lines 43-48); <c>SetInputControl</c> sets one forced axis;
/// <c>SetInputAction</c> OR-s an action bit into the forced mask (respecting an optional mask).
/// A freshly-constructed action forces nothing (all axes NA, mask 0) — calling <c>Execute</c> with no
/// setters therefore <em>clears</em> the forced state, which is how missions "reset the controls".
/// </summary>
public sealed class SetControlsAction : Action
{
    private readonly float[] _controls = { TrainingControlState.Na, TrainingControlState.Na, TrainingControlState.Na, TrainingControlState.Na };
    private int _inputAction;

    public override void Execute()
    {
        for (int axis = 0; axis < 4; axis++)
            TrainingControlState.SetInputControls[axis] = _controls[axis];
        TrainingControlState.SetInputAction = _inputAction;
    }

    public void SetInputAction(int inputAction, int mask = unchecked((int)0xffffffff))
        => _inputAction = (_inputAction & mask) | inputAction;

    public void SetInputControl(int axis, float scalar) => _controls[axis] = scalar;
}

/// <summary>
/// Steers a ship to face a target by writing turn controls into <c>g_setInputControls</c> — port of
/// <c>TurnToAction</c> (<c>TurnToAction.cpp:27-73</c>). Computes the turn-to-face deltas via the native
/// <c>turnToFace</c> helper and damps them to 50% (lines 62-65); when either object is gone it clears the
/// forced turn axes back to NA (lines 68-72). Because the real turn-to-face math lives in the ship
/// simulator, the C# version asks the site to compute the turn deltas; TR-2 supplies the real
/// implementation.
/// </summary>
public sealed class TurnToAction : Action
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _object;
    private readonly TargetRef _target;

    public TurnToAction(ITrainingSite site, TargetRef obj, TargetRef target)
    {
        _site = site;
        _object = obj;
        _target = target;
    }

    public override void Execute()
    {
        ITrainingObject? ship = _site.Resolve(_object);
        ITrainingObject? target = _site.Resolve(_target);
        if (ship is { } s && target is { } t)
        {
            // turnToFace(delta, dt, ship, &g_setInputControls) then damp each turn axis by 0.5
            var (yaw, pitch, roll) = _site.TurnToFace(s.Ref, t.Ref);
            TrainingControlState.SetInputControls[TrainingControlState.AxisYaw] = yaw * 0.5f;
            TrainingControlState.SetInputControls[TrainingControlState.AxisPitch] = pitch * 0.5f;
            TrainingControlState.SetInputControls[TrainingControlState.AxisRoll] = roll * 0.5f;
        }
        else
        {
            TrainingControlState.SetInputControls[TrainingControlState.AxisYaw] = TrainingControlState.Na;
            TrainingControlState.SetInputControls[TrainingControlState.AxisPitch] = TrainingControlState.Na;
            TrainingControlState.SetInputControls[TrainingControlState.AxisRoll] = TrainingControlState.Na;
        }
    }
}

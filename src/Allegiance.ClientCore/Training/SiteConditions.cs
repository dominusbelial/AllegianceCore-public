using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Game-state query conditions — the C# ports of the <c>Get*Condition</c> classes that read ship and
/// player state through <see cref="ITrainingSite"/>.
/// </summary>

/// <summary>
/// TRUE once a specific key is pressed — port of <c>GetKeyCondition</c>
/// (<c>getkeycondition.cpp:27-59</c>). <c>Start</c> resets the flag; every <c>Evaluate</c> consumes
/// (clears) the press so it fires once per press (lines 47-52). The key is a <c>TrekKey</c> id (e.g.
/// <c>TK_FireBooster = 50</c>, <c>igc.h:273</c>).
/// </summary>
public sealed class GetKeyCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly int _key;

    public GetKeyCondition(ITrainingSite site, int key)
    {
        _site = site;
        _key = key;
    }

    public override bool Start() => false;

    public override bool Evaluate() => _site.ConsumeKey(_key);
}

/// <summary>
/// TRUE once a chat is sent to an expected recipient — port of <c>GetChatCondition</c>
/// (<c>getchatcondition.cpp:27-59</c>). <c>Evaluate</c> consumes the flag so it fires once.
/// </summary>
public sealed class GetChatCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly int _expectedRecipient;

    public GetChatCondition(ITrainingSite site, int expectedRecipient)
    {
        _site = site;
        _expectedRecipient = expectedRecipient;
    }

    public override bool Start() => false;

    public override bool Evaluate() => _site.ConsumeChat(_expectedRecipient);
}

/// <summary>
/// TRUE when a control axis is within 10% of an expected value — port of
/// <c>GetControlActiveCondition</c> (<c>getcontrolactivecondition.cpp:26-52</c>). Reads either the raw
/// player input (<c>g_inputControls</c>) when <paramref name="useInputControls"/> is set, or the controls
/// actually applied to the ship (<c>IshipIGC::GetControls()</c>). The throttle is translated from ±1 to
/// the 0..1 range before comparison (lines 44-46); other axes are compared by absolute value.
/// </summary>
public sealed class GetControlActiveCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly int _axis;
    private readonly float _expectedValue;
    private readonly bool _useInputControls;

    public GetControlActiveCondition(ITrainingSite site, TargetRef ship, int axis, float expectedValue, bool useInputControls = false)
    {
        _site = site;
        _ship = ship;
        _axis = axis;
        _expectedValue = expectedValue;
        _useInputControls = useInputControls;
    }

    public override bool Evaluate()
    {
        float fValue = _useInputControls ? AxisValue(_site.GetInputControls(), _axis)
                                         : AxisValue(_site.GetShipControls(_ship), _axis);
        if (_axis == TrainingControlState.AxisThrottle)
            fValue = (fValue + 1.0f) * 0.5f; // translate -1..1 to 0..1
        else
            fValue = MathF.Abs(fValue);
        return MathF.Abs(fValue - _expectedValue) < 0.1f;
    }

    /// <summary>Read one of the four <c>ControlData</c> axes by index (c_axisYaw..c_axisThrottle),
    /// matching native <c>jsValues[axis]</c> (<c>getcontrolactivecondition.cpp:43</c>).</summary>
    private static float AxisValue(ControlData c, int axis) => axis switch
    {
        ControlData.AxisYaw => c.Yaw,
        ControlData.AxisPitch => c.Pitch,
        ControlData.AxisRoll => c.Roll,
        _ => c.Throttle,
    };
}

/// <summary>
/// TRUE when a ship's state-mask bits are set — port of <c>GetStateBitsCondition</c>
/// (<c>getstatebitscondition.cpp:21-38</c>). Uses <c>IshipIGC::GetStateM()</c>; the bit is e.g.
/// <c>afterburnerButtonIGC = 512</c> (<c>igc.h:817</c>).
/// </summary>
public sealed class GetStateBitsCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly int _bits;

    public GetStateBitsCondition(ITrainingSite site, TargetRef ship, int bits)
    {
        _site = site;
        _ship = ship;
        _bits = bits;
    }

    public override bool Evaluate() => (_site.GetStateM(_ship) & _bits) != 0;
}

/// <summary>
/// TRUE when a ship's autopilot is engaged — port of <c>GetAutopilotCondition</c>
/// (<c>getautopilotcondition.cpp:33-37</c>).
/// </summary>
public sealed class GetAutopilotCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;

    public GetAutopilotCondition(ITrainingSite site, TargetRef ship)
    {
        _site = site;
        _ship = ship;
    }

    public override bool Evaluate() => _site.GetAutopilot(_ship);
}

/// <summary>
/// TRUE when a ship's current command target equals an expected target — port of
/// <c>GetTargetCondition</c> (<c>gettargetcondition.cpp:50-53</c>).
/// </summary>
public sealed class GetTargetCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly TargetRef _expectedTarget;

    public GetTargetCondition(ITrainingSite site, TargetRef ship, TargetRef expectedTarget)
    {
        _site = site;
        _ship = ship;
        _expectedTarget = expectedTarget;
    }

    public override bool Evaluate() => _site.GetCommandTarget(_ship, /* c_cmdCurrent = */ 2) == _expectedTarget;
}

/// <summary>
/// TRUE when a ship is essentially stationary — port of <c>GetShipIsStoppedCondition</c>
/// (<c>getshipisstoppedcondition.cpp:39-46</c>): velocity magnitude squared below 1.0.
/// </summary>
public sealed class GetShipIsStoppedCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;

    public GetShipIsStoppedCondition(ITrainingSite site, TargetRef ship)
    {
        _site = site;
        _ship = ship;
    }

    public override bool Evaluate()
    {
        var ship = _site.Resolve(_ship);
        if (ship is null)
            return false;
        return ship.Velocity.LengthSquared() < 1.0f;
    }
}

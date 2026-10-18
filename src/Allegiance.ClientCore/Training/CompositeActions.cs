namespace Allegiance.ClientCore.Training;

/// <summary>
/// Composite / structural actions — the C# ports of <c>conditionalaction</c>, <c>onetimeaction</c>,
/// <c>proxyaction</c> and <c>noaction</c>.
/// </summary>

/// <summary>
/// Runs an action when a condition is true — port of <c>ConditionalAction</c>
/// (<c>conditionalaction.cpp:21-87</c>). Two execution modes:
/// <list type="bullet">
/// <item><b>IMMEDIATE</b>: on Start/Evaluate, test the condition and run the action in the same pass if
///   true (<c>conditionalaction.cpp:40-43,61-70</c>).</item>
/// <item><b>DELAYED</b>: the action fires on the pass AFTER the condition went true — "was true last
///   time we tested" (<c>conditionalaction.cpp:72-83</c>).</item>
/// </list>
/// <c>m_bRunOnce</c> (added AEM 7.9.07, header line 35) stops further evaluations after one fire.
/// </summary>
public sealed class ConditionalAction : Condition
{
    public const bool ImmediateExecution = true;
    public const bool DelayedExecution = false;

    private readonly Condition _condition;
    private readonly Action _action;
    private readonly bool _immediateExecute;
    private readonly bool _runOnce;
    private bool _execute;
    private bool _ranOnce;

    /// <summary>The wrapped action, exposed for tests to observe side effects.</summary>
    public Action InnerAction => _action;

    public ConditionalAction(Condition condition, Action action, bool immediateExecute = ImmediateExecution, bool runOnce = false)
    {
        _condition = condition;
        _action = action;
        _immediateExecute = immediateExecute;
        _runOnce = runOnce;
        _ranOnce = false;
    }

    public override bool Start()
    {
        _execute = _condition.Start();
        if (_immediateExecute == ImmediateExecution && _execute == true)
        {
            // intent of m_bRunOnce (AEM 7.9.07): the wrapped action runs once in total. Mark it here so
            // a subsequent Evaluate does not fire it a second time after Start already did.
            _ranOnce = true;
            _action.Execute();
        }
        return _execute;
    }

    public override void Stop()
    {
        _condition.Stop();
        _action.Stop();
    }

    public override bool Evaluate()
    {
        if ((_ranOnce == false && _runOnce == true) || _runOnce == false)
        {
            if (_immediateExecute == ImmediateExecution)
            {
                _execute = _condition.Evaluate();
                if (_execute == true)
                {
                    _ranOnce = true;
                    _action.Execute();
                }
            }
            else
            {
                if (_execute == true)
                {
                    _ranOnce = true;
                    _action.Execute();
                }
                _execute = _condition.Evaluate();
            }
            return _execute;
        }
        return false;
    }
}

/// <summary>
/// Executes its inner action exactly once, no matter how often <c>Execute</c> is called — port of
/// <c>OneTimeAction</c> (<c>onetimeaction.cpp:34-47</c>).
/// </summary>
public sealed class OneTimeAction : Action
{
    private readonly Action _inner;
    private bool _hasExecuted;

    public OneTimeAction(Action inner)
    {
        _inner = inner;
        _hasExecuted = false;
    }

    public override void Execute()
    {
        if (!_hasExecuted)
        {
            _hasExecuted = true;
            _inner.Execute();
        }
    }

    public override void Stop() => _inner.Stop();
}

/// <summary>
/// Forwards <c>Execute</c> to a shared action without owning (deleting) it — port of
/// <c>ProxyAction</c> (<c>ProxyAction.cpp:21-39</c>). The native doc comment (lines 31-33) states the
/// point explicitly: allow one action to be held at more than one point without double-deletion.
/// </summary>
public sealed class ProxyAction : Action
{
    private readonly Action _inner;

    public ProxyAction(Action inner) => _inner = inner;

    public override void Execute() => _inner.Execute();
}

/// <summary>
/// Does nothing — port of <c>NoAction</c> (<c>noaction.cpp:26-28</c>). Used as a placeholder start
/// action.
/// </summary>
public sealed class NoAction : Action
{
    public override void Execute()
    {
    }
}

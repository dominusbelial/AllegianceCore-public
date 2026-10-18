namespace Allegiance.ClientCore.Training;

/// <summary>
/// A training objective: a success condition plus optional start actions, constraint conditions and a
/// skip condition — port of <c>Goal</c> (<c>goal.cpp:22-107</c>, <c>Goal.h:29-49</c>). The native
/// semantics that matter:
/// <list type="bullet">
/// <item><c>Start()</c>: if the success condition is already true, the goal is skipped
///   (<c>goal.cpp:40,60</c>). Otherwise, if a skip-goal condition is set and true, skip
///   (<c>goal.cpp:43-46</c>); else execute start actions, start constraint conditions, and return
///   incomplete (<c>goal.cpp:48-55</c>).</item>
/// <item><c>Evaluate()</c>: complete if the success condition is true; otherwise walk the constraint
///   conditions (ignoring their return, since they may carry actions) (<c>goal.cpp:79-90</c>).</item>
/// </list>
/// </summary>
public sealed class Goal : Condition
{
    public const bool Complete = true;
    public const bool Incomplete = false;

    private readonly Condition _successCondition;
    private readonly ActionList _startActions = new();
    private readonly ConditionList _constraints = new();
    private Condition? _skipGoalCondition;

    public Goal(Condition successCondition) => _successCondition = successCondition;

    public override bool Start()
    {
        // if the success condition is already true, prepare to evaluate the goal
        if (_successCondition.Start() == Incomplete)
        {
            // if a skip condition is set and true, skip the goal
            if (_skipGoalCondition is { } skip && skip.Start())
                return Complete;

            _startActions.Execute();
            _constraints.Start();
            return Incomplete;
        }
        return Complete;
    }

    public override void Stop()
    {
        _successCondition.Stop();
        _skipGoalCondition?.Stop();
        _startActions.Stop();
        _constraints.Stop();
    }

    public override bool Evaluate()
    {
        if (_successCondition.Evaluate())
            return Complete;

        // constraint conditions carry actions; evaluate (ignore result) to let them fire
        _constraints.Evaluate();
        return Incomplete;
    }

    public void AddStartAction(Action a) => _startActions.AddAction(a);

    public void AddConstraintCondition(Condition c) => _constraints.AddCondition(c);

    public void SetSkipGoalCondition(Condition c) => _skipGoalCondition = c;
}

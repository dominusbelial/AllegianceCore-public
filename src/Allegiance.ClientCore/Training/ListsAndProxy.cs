namespace Allegiance.ClientCore.Training;

/// <summary>
/// An ordered collection of conditions treated as a single condition — port of
/// <c>ConditionList</c> (<c>conditionlist.cpp:21-84</c>). Native semantics:
/// <list type="bullet">
/// <item><c>Start</c> returns true only if every child starts true, but always walks all children
///   because any may carry actions (lines 31-46).</item>
/// <item><c>Evaluate</c> is true only if the list is non-empty and every child evaluates true
///   (lines 58-76).</item>
/// </list>
/// Used as a goal's constraint-condition collection and start-action collection host.
/// </summary>
public sealed class ConditionList : Condition
{
    private readonly List<Condition> _conditions = new();

    public override bool Start()
    {
        bool result = true;
        foreach (var c in _conditions)
            if (c.Start() == false)
                result = false;
        return result;
    }

    public override void Stop()
    {
        foreach (var c in _conditions)
            c.Stop();
    }

    public override bool Evaluate()
    {
        if (_conditions.Count < 1)
            return false;
        bool result = true;
        foreach (var c in _conditions)
            if (c.Evaluate() == false)
                result = false;
        return result;
    }

    public void AddCondition(Condition c) => _conditions.Add(c);
}

/// <summary>
/// An ordered collection of actions executed in sequence — port of <c>ActionList</c>
/// (<c>actionlist.cpp:21-57</c>).
/// </summary>
public sealed class ActionList : Action
{
    private readonly List<Action> _actions = new();

    public override void Execute()
    {
        foreach (var a in _actions)
            a.Execute();
    }

    public override void Stop()
    {
        foreach (var a in _actions)
            a.Stop();
    }

    public void AddAction(Action a) => _actions.Add(a);
}

/// <summary>
/// A transparent condition pass-through that deliberately does NOT own (delete) its inner condition —
/// port of <c>ProxyCondition</c> (<c>ProxyCondition.cpp:21-37</c>). Lets one condition participate in
/// two places in the graph without double-deletion, e.g. mission2 reuses <c>pObjectWithinRadiusCondition</c>
/// in a second constraint (<c>mission2.cpp:130</c>).
/// </summary>
public sealed class ProxyCondition : Condition
{
    private readonly Condition _inner;

    public ProxyCondition(Condition inner) => _inner = inner;

    public override bool Evaluate() => _inner.Evaluate();
}

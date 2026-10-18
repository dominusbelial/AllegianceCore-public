namespace Allegiance.ClientCore.Training;

/// <summary>
/// Boolean combinator conditions — the C# ports of <c>andcondition</c>, <c>orcondition</c>,
/// <c>predicate</c>/<c>notcondition</c> and <c>truecondition</c>/<c>falsecondition</c>.
/// </summary>

/// <summary>
/// TRUE iff both operands are true. Faithful port of <c>AndCondition</c>
/// (<c>andcondition.cpp:35-58</c>): both operands are always <em>started</em> / <em>evaluated</em>
/// before the results are combined, because either may have side effects (actions attached).
/// Takes ownership of its operands (the native <c>delete</c> in <c>andcondition.cpp:28-32</c>).
/// </summary>
public sealed class AndCondition : Condition
{
    private readonly Condition _a;
    private readonly Condition _b;

    public AndCondition(Condition a, Condition b)
    {
        _a = a;
        _b = b;
    }

    public override bool Start()
    {
        bool ra = _a.Start();
        bool rb = _b.Start();
        return ra && rb;
    }

    public override void Stop()
    {
        _a.Stop();
        _b.Stop();
    }

    public override bool Evaluate()
    {
        bool ra = _a.Evaluate();
        bool rb = _b.Evaluate();
        return ra && rb;
    }
}

/// <summary>
/// TRUE iff either operand is true. Faithful port of <c>OrCondition</c>
/// (<c>orcondition.cpp:35-58</c>).
/// </summary>
public sealed class OrCondition : Condition
{
    private readonly Condition _a;
    private readonly Condition _b;

    public OrCondition(Condition a, Condition b)
    {
        _a = a;
        _b = b;
    }

    public override bool Start()
    {
        bool ra = _a.Start();
        bool rb = _b.Start();
        return ra || rb;
    }

    public override void Stop()
    {
        _a.Stop();
        _b.Stop();
    }

    public override bool Evaluate()
    {
        bool ra = _a.Evaluate();
        bool rb = _b.Evaluate();
        return ra || rb;
    }
}

/// <summary>
/// Inverts (or asserts) a condition against an expected value — the port of <c>Predicate</c>
/// (<c>Predicate.cpp:34-49</c>), which the native <c>NotCondition(p)</c> macro expands to
/// (<c>notcondition.h:22</c>) and <c>FalseCondition</c> to <c>Predicate(new TrueCondition, false)</c>
/// (<c>falsecondition.h:26</c>).
/// </summary>
public sealed class Predicate : Condition
{
    private readonly Condition _inner;
    private readonly bool _expected;

    public Predicate(Condition inner, bool expected)
    {
        _inner = inner;
        _expected = expected;
    }

    public override bool Start() => _inner.Start() == _expected;

    public override void Stop() => _inner.Stop();

    public override bool Evaluate() => _inner.Evaluate() == _expected;
}

/// <summary>
/// The native <c>NotCondition(p)</c> macro (<c>notcondition.h:22</c>): predicate expecting false.
/// </summary>
public static class NotCondition
{
    public static Condition Of(Condition c) => new Predicate(c, false);
}

/// <summary>
/// Always-true condition — port of <c>TrueCondition</c> (<c>truecondition.cpp:27-34</c>). The
/// native code frequently constructs <c>new TrueCondition</c> directly (e.g. a periodic reminder).
/// </summary>
public sealed class TrueCondition : Condition
{
    public override bool Start() => true;
    public override bool Evaluate() => true;
}

/// <summary>
/// Always-false condition — port of the native <c>FalseCondition</c> macro (<c>falsecondition.h:26</c>),
/// <c>NotCondition(new TrueCondition)</c>. Used where a goal can only complete via an external event
/// (e.g. mission2 goal 09 waits for landing).
/// </summary>
public sealed class FalseCondition : Condition
{
    public override bool Start() => false;
    public override bool Evaluate() => false;
}

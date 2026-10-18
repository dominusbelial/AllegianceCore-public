namespace Allegiance.ClientCore.Training;

/// <summary>
/// Base of the training script condition graph — the C# port of <c>training/Condition.h</c>
/// (<c>Condition.h:21-28</c>). Native semantic: <c>Start()</c> (once, when a goal begins) defaults
/// to evaluating immediately (<c>Condition.cpp:26-29</c>); <c>Stop()</c> is a no-op hook;
/// <c>Evaluate()</c> is queried every frame. Each subclass overrides whichever make sense.
/// </summary>
public abstract class Condition
{
    public virtual bool Start() => Evaluate();
    public virtual void Stop()
    {
    }
    public abstract bool Evaluate();
}

/// <summary>
/// Base of the training script actions — the C# port of <c>training/Action.h</c>
/// (<c>Action.h:21-27</c>). <c>Execute()</c> runs the action; <c>Stop()</c> is a no-op hook.
/// </summary>
public abstract class Action
{
    public abstract void Execute();
    public virtual void Stop()
    {
    }
}

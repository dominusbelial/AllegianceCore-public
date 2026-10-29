namespace Allegiance.ClientCore.Training;

/// <summary>
/// An ordered sequence of goals advanced in lockstep — port of <c>GoalList</c>
/// (<c>goallist.cpp:21-105</c>, <c>GoalList.h:25-38</c>). The single most important piece of script
/// state-machine machinery:
/// <list type="bullet">
/// <item><c>Start()</c> walks from the current iterator, advancing past goals that start already-
///   complete; it stops and returns incomplete at the first goal that starts incomplete
///   (<c>goallist.cpp:37-57</c>).</item>
/// <item><c>Evaluate()</c>: if the current goal completes, <c>Stop()</c> it, advance, and <c>Start()</c>
///   the next (recursing through the above loop); otherwise return incomplete (<c>goallist.cpp:68-92</c>).</item>
/// </list>
/// The iterator is (re)set to <c>begin()</c> on every <c>AddGoal</c> (<c>goallist.cpp:95-105</c>) and
/// initialised to <c>end()</c> in the ctor.
/// </summary>
public sealed class GoalList : Condition
{
    private readonly List<Goal> _goals = new();
    private int _index; // the native list iterator

    internal int CurrentNumber => _index + 1;
    internal Goal? CurrentGoal => _index < _goals.Count ? _goals[_index] : null;

    public GoalList() => _index = _goals.Count; // end()

    public override bool Start()
    {
        while (_index < _goals.Count)
        {
            if (_goals[_index].Start() == Goal.Complete)
                _index++;
            else
                return Goal.Incomplete;
        }
        return Goal.Complete;
    }

    public override void Stop()
    {
        if (_index < _goals.Count)
            _goals[_index].Stop();
    }

    public override bool Evaluate()
    {
        if (_index < _goals.Count)
        {
            if (_goals[_index].Evaluate() == true)
            {
                Stop();
                _index++;
                return Start();
            }
            return Goal.Incomplete;
        }
        return Goal.Complete;
    }

    public void AddGoal(Goal g)
    {
        _goals.Add(g);
        _index = 0; // reset iterator to begin() on each addition (goallist.cpp:104)
    }
}

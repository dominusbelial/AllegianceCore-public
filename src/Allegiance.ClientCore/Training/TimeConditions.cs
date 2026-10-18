namespace Allegiance.ClientCore.Training;

/// <summary>
/// Time-gated conditions — the C# ports of <c>periodiccondition</c>, <c>ntimescondition</c> and the
/// <c>ElapsedTimeCondition</c> macro.
/// </summary>

/// <summary>
/// Re-evaluates its inner condition only when a wall-clock period elapses. Faithful port of
/// <c>PeriodicCondition</c> (<c>periodiccondition.cpp:21-71</c>). The native records the start time in
/// ticks (<c>m_dwPeriod = fPeriod * Time::fResolution()</c>, line 23) and, on each Evaluate, advances
/// the clock base by exactly one period so drift does not accumulate ("clock accurate" per lines 59-62).
/// The C# port uses seconds from <see cref="ITrainingSite.TimeSeconds"/>.
/// </summary>
public sealed class PeriodicCondition : Condition
{
    private readonly Condition _inner;
    private readonly float _period;
    private readonly ITrainingSite _site;
    private float _startTime;

    public PeriodicCondition(Condition inner, float period, ITrainingSite site)
    {
        _inner = inner;
        _period = period;
        _site = site;
    }

    public override bool Start()
    {
        _startTime = _site.TimeSeconds;
        // always evaluate the condition the first time through (periodiccondition.cpp:40-41)
        return _inner.Start();
    }

    public override void Stop() => _inner.Stop();

    public override bool Evaluate()
    {
        float elapsed = _site.TimeSeconds - _startTime;
        if (elapsed > _period)
        {
            // advance to the beginning of the NEXT period (drift-free)
            _startTime += _period;
            return _inner.Evaluate();
        }
        return false;
    }
}

/// <summary>
/// TRUE once its inner condition has fired a given number of times. Faithful port of
/// <c>NTimesCondition</c> (<c>ntimescondition.cpp:21-59</c>): <c>Start</c> primes the counter from the
/// inner condition (line 38) and always returns false; <c>Evaluate</c> increments on each true inner
/// evaluation and, when it reaches <c>iTimes</c>, returns true and (unless <c>sticky</c>) resets the
/// count to zero so the whole cycle can repeat.
/// </summary>
public sealed class NTimesCondition : Condition
{
    private readonly Condition _inner;
    private readonly uint _times;
    private readonly bool _sticky;
    private uint _count;

    public NTimesCondition(Condition inner, uint times, bool sticky = false)
    {
        _inner = inner;
        _times = times;
        _sticky = sticky;
    }

    public override bool Start()
    {
        _count = _inner.Start() ? 1u : 0u;
        return false;
    }

    public override void Stop() => _inner.Stop();

    public override bool Evaluate()
    {
        if (_count < _times)
            _count += _inner.Evaluate() ? 1u : 0u;
        if (_count == _times)
        {
            _count = _sticky ? _count : 0u;
            return true;
        }
        return false;
    }
}

/// <summary>
/// TRUE once a wall-clock duration has elapsed — the native <c>ElapsedTimeCondition(fTime)</c> macro
/// (<c>elapsedtimecondition.h:30</c>): <c>NTimesCondition(new PeriodicCondition(new TrueCondition, fTime), 2, true)</c>.
/// The double period/2-count trick makes it fire exactly once, shortly after the period elapses.
/// </summary>
public static class ElapsedTimeCondition
{
    public static Condition Create(float time, ITrainingSite site) =>
        new NTimesCondition(new PeriodicCondition(new TrueCondition(), time, site), 2, sticky: true);
}

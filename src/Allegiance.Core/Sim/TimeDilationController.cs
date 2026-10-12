namespace Allegiance.Core.Sim;

/// <summary>
/// P618 — the tunable parameters of the load-driven time-dilation controller
/// (EVE-style TiDi). Tuning ranges are chosen so the controller engages only
/// when the tick loop genuinely cannot hold real time, recovers with hysteresis
/// once load drops, and moves the sim time scale in small rate-limited steps so
/// clients never see sudden speed jumps. See <see cref="TimeDilationController"/>.
/// </summary>
public sealed class TimeDilationParams
{
    /// <summary>The nominal (full-speed) sim period in seconds — the wall time a
    /// healthy tick is supposed to take (<c>1 / UpdatesPerSecond</c>).</summary>
    public double NominalPeriod { get; }

    /// <summary>The lowest allowed sim time scale (10× slow motion).</summary>
    public double MinScale { get; }

    /// <summary>The highest allowed sim time scale (normal speed; &gt;1 would
    /// speed the world up — out of scope, TiDi only slows).</summary>
    public double MaxScale { get; }

    /// <summary>Exponential-moving-average smoothing of the measured tick cost.
    /// Lower = smoother but slower to react; higher = livelier but noisier.</summary>
    public double Alpha { get; }

    /// <summary>Engage auto-slow-motion when the smoothed tick cost rises above
    /// <c>EngageRatio × NominalPeriod</c> (default 1.0 = the server can't hold
    /// real time).</summary>
    public double EngageRatio { get; }

    /// <summary>Release back to full speed when the smoothed cost falls below
    /// <c>ReleaseRatio × NominalPeriod</c> (default 0.9). The gap between
    /// <see cref="EngageRatio"/> and <see cref="ReleaseRatio"/> is the hysteresis
    /// that stops the controller from flickering in and out around the boundary.</summary>
    public double ReleaseRatio { get; }

    /// <summary>Minimum |target − scale| (as a fraction) that justifies a change.
    /// Suppresses pointless micro-adjustments once converged — the "no
    /// oscillation" guarantee.</summary>
    public double Deadband { get; }

    /// <summary>Maximum the scale may move per wall second (rate limit) so a
    /// client never sees a sudden speed jump and the world eases into slow motion.</summary>
    public double MaxChangePerSecond { get; }

    /// <summary>Minimum wall time between scale adjustments — the control
    /// interval that, combined with <see cref="MaxChangePerSecond"/>, bounds
    /// both the slew rate and the number of <see cref="TimescaleUpdate"/>
    /// broadcasts per second.</summary>
    public double ControlIntervalSeconds { get; }

    public TimeDilationParams(
        double nominalPeriod,
        double minScale = 0.1,
        double maxScale = 1.0,
        double alpha = 0.05,
        double engageRatio = 1.0,
        double releaseRatio = 0.9,
        double deadband = 0.012,
        double maxChangePerSecond = 0.033,
        double controlIntervalSeconds = 0.5)
    {
        NominalPeriod = nominalPeriod;
        MinScale = minScale;
        MaxScale = maxScale;
        Alpha = alpha;
        EngageRatio = engageRatio;
        ReleaseRatio = releaseRatio;
        Deadband = deadband;
        MaxChangePerSecond = maxChangePerSecond;
        ControlIntervalSeconds = controlIntervalSeconds;
    }
}

/// <summary>
/// P618 — the load-driven sim time-scale controller (EVE-style Time Dilation).
///
/// When the server can't hold real time (tick processing cost exceeds the
/// nominal sim period, e.g. ~390 ships in one sector), the naive response —
/// the current port's — is to simply run fewer ticks with irregular gaps, so
/// the client sees a stuttering "slide show". TiDi instead SLOWS THE GAME
/// CLOCK: the controller lowers the sim time scale toward
/// <c>nominalPeriod / smoothedCost</c> (clamped to
/// [<see cref="TimeDilationParams.MinScale"/>, <see cref="TimeDilationParams.MaxScale"/>]),
/// so each tick still completes and advances <em>less</em> sim time, the world
/// plays in smooth slow motion, and everything stays internally consistent.
///
/// Pure and deterministic (no wall-clock reads except the caller-fed
/// <see cref="Observe"/> inputs), so it is fully unit-testable in isolation.
///
/// Behaviour contract:
/// <list type="bullet">
/// <item><b>Convergence.</b> Under a steady overload (constant <c>cost</c>) the
/// active scale converges to <c>clamp(nominal/cost, min, max)</c>.</item>
/// <item><b>No oscillation.</b> Once converged within <see cref="TimeDilationParams.Deadband"/>,
/// further observations leave the scale untouched.</item>
/// <item><b>Recovery.</b> When load drops below
/// <see cref="TimeDilationParams.ReleaseRatio"/> the scale is eased back to 1.0
/// at the same rate limit — never snapped.</item>
/// <item><b>Hysteresis.</b> Engage/release thresholds differ, so the controller
/// doesn't churn around the boundary.</item>
/// <item><b>Rate limit.</b> The scale moves at most
/// <see cref="TimeDilationParams.MaxChangePerSecond"/> per wall second and only
/// once per <see cref="TimeDilationParams.ControlIntervalSeconds"/>, so clients
/// get a slow ramp and few <see cref="Wire.TimescaleUpdate"/> broadcasts.</item>
/// <item><b>Manual override wins.</b> <see cref="SetManual"/> pins the scale and
/// suspends auto control until <see cref="ClearManual"/> (an admin
/// <c>timescale</c> cheat sets it; see <c>Server.SetTimeScale</c>).</item>
/// </list>
/// </summary>
public sealed class TimeDilationController
{
    private readonly TimeDilationParams _p;
    private double _emaCost;
    private bool _hasCost;
    private bool _engaged;
    private double _scale = 1.0;
    private double _accruedWall;
    private bool _manualOverride;

    /// <summary>The active sim time scale (1.0 = normal).</summary>
    public double Scale => _scale;

    /// <summary>The smoothed (EMA) tick processing cost, in seconds.</summary>
    public double SmoothedCost => _emaCost;

    /// <summary>True once the controller has decided the server can't hold real time
    /// and is actively slowing the game clock (past <see cref="TimeDilationParams.EngageRatio"/>).</summary>
    public bool Engaged => _engaged;

    /// <summary>True after <see cref="SetManual"/> — auto control is suspended.</summary>
    public bool ManualOverride => _manualOverride;

    public TimeDilationController(TimeDilationParams p) => _p = p;

    /// <summary>Pin the scale and suspend auto control (admin <c>timescale</c> cheat).</summary>
    public void SetManual(double scale)
    {
        _manualOverride = true;
        _scale = Math.Clamp(scale, _p.MinScale, _p.MaxScale);
    }

    /// <summary>Resume auto control. Returns true if a manual override was active.</summary>
    public bool ClearManual()
    {
        if (!_manualOverride) return false;
        _manualOverride = false;
        return true;
    }

    /// <summary>
    /// Feed one tick's measured processing cost (seconds of busy sim work) and
    /// the wall time elapsed since the previous observation. Returns true when
    /// the active <see cref="Scale"/> changed (the caller should rebroadcast the
    /// new scale to clients). Call once per tick, on the sim thread.
    /// </summary>
    public bool Observe(double cost, double wallDeltaSeconds)
    {
        if (cost < 0) cost = 0;
        if (wallDeltaSeconds < 0) wallDeltaSeconds = 0;

        // EMA of the tick cost — the smoothed "can we keep up?" signal.
        _emaCost = _hasCost ? _p.Alpha * cost + (1 - _p.Alpha) * _emaCost : cost;
        _hasCost = true;

        if (_manualOverride) return false;

        // Only reconsider once per control interval (wall time accrual), so the
        // scale changes at a bounded cadence regardless of tick frequency.
        _accruedWall += wallDeltaSeconds;
        if (_accruedWall < _p.ControlIntervalSeconds) return false;

        double ratio = _emaCost / _p.NominalPeriod;
        if (!_engaged && ratio > _p.EngageRatio) _engaged = true;
        else if (_engaged && ratio < _p.ReleaseRatio) _engaged = false;

        // Target scale: slow the clock so each tick still completes and stays
        // consistent. When not engaged we hold full speed.
        double target = _engaged
            ? Math.Clamp(_p.NominalPeriod / _emaCost, _p.MinScale, _p.MaxScale)
            : 1.0;

        bool changed = false;
        if (Math.Abs(target - _scale) > _p.Deadband)
        {
            double maxStep = _p.MaxChangePerSecond * _accruedWall;
            double step = Math.Clamp(Math.Abs(target - _scale), 0.0, maxStep);
            if (step > 0)
            {
                _scale += target > _scale ? step : -step;
                changed = true;
            }
        }
        _accruedWall = 0;
        return changed;
    }
}

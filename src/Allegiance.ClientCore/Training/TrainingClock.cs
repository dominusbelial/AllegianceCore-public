namespace Allegiance.ClientCore.Training;

/// <summary>
/// P785 — the training-mission game clock: the C# port of the static <c>Time</c>
/// class (<c>zlib/ztime.h:20-100</c>, <c>zlib/ztime.cpp:11-67</c>).
/// <list type="bullet">
/// <item><c>TK_PauseTM</c> (trainingmission.cpp:347-358) pauses/resumes this clock —
///   native <c>Time::Pause()</c>/<c>Time::Continue()</c> (ztime.cpp:15-49): pausing
///   stamps the clock and <c>Time::Now()</c> then returns the frozen stamp; resuming
///   adds the paused span to the negative offset so the game clock never jumps.</item>
/// <item>The debug clock-speed keys move the shift value by ±1, clamped to ±3
///   (<c>Time::SetShift</c>, ztime.cpp:53-67): the elapsed frame time is scaled by
///   2^shift before it is accumulated (ztime.h:57-63).</item>
/// </list>
/// A static instance mirrors native's process-global TLS clock — training runs one
/// mission per process, exactly like native's <c>g_pMission</c>.
/// </summary>
public static class TrainingClock
{
    /// <summary>
    /// Mirrors native's <c>_DEBUG_TRAINING</c> preprocessor gate. The goal-skip and
    /// clock-speed key handlers are compiled only under <c>#ifdef _DEBUG_TRAINING</c>
    /// (trainingmission.cpp:359-384; ztime.cpp:28-31,53-67) and the define appears in
    /// NO project file (VS2008/VS2022 projects grepped), so in every shipped build the
    /// keys are registered (trekinput.cpp:353-357 — the guard there is commented out)
    /// but fall through to the mission's key conditions: inert. The port keeps the same
    /// handlers behind this flag (default <c>false</c> = shipping behaviour); tests and
    /// headless proofs set it to exercise the debug paths.
    /// </summary>
    public static bool DebugTrainingKeys { get; set; }

    /// <summary>Max shift (native ztime.cpp:58-67 clamps to ±3).</summary>
    public const int MaxShift = 3;

    private const double MaxFrameSeconds = 0.25;  // native dwMaxDeltaTime = 4 FPS (ztime.h:57)

    private static double _accumulated;      // s_dwAccumulatedTime
    private static double _negativeOffset;   // s_dwNegativeOffset
    private static double _pauseStart;       // s_dwPauseStart (native: 0 = not paused)
    private static int _shift;               // s_iShift (debug builds only)
    private static bool _paused;

    /// <summary>Whether the training clock is paused (native <c>Time::IsPaused()</c>,
    /// ztime.cpp:28-31 — true iff s_dwPauseStart is set).</summary>
    public static bool IsPaused => _paused;

    /// <summary>Current shift value (native <c>Time::GetShift()</c>, ztime.cpp:53-56).</summary>
    public static int GetShift() => _shift;

    /// <summary>Set the shift value, clamped to ±3 (native <c>Time::SetShift</c>,
    /// ztime.cpp:58-67).</summary>
    public static void SetShift(int shift) => _shift = Math.Clamp(shift, -MaxShift, MaxShift);

    /// <summary>Freeze the game clock at its current value (native <c>Time::Pause()</c>,
    /// ztime.cpp:15-25 — stamps the raw accumulated clock; <c>Time::Now()</c> returns
    /// the stamp while paused).</summary>
    public static void Pause()
    {
        _pauseStart = _accumulated;   // native: s_dwPauseStart = s_dwAccumulatedTime (ztime.cpp:21)
        _paused = true;
    }

    /// <summary>Resume the game clock (native <c>Time::Continue()</c>, ztime.cpp:34-49 —
    /// adds the paused span to the negative offset so <c>Now()</c> continues from the
    /// freeze point).</summary>
    public static void Continue()
    {
        _negativeOffset += _accumulated - _pauseStart;  // native ztime.cpp:41
        _paused = false;
    }

    /// <summary>
    /// Advance the clock by one real frame and return the game-clock time in seconds —
    /// the port of <c>Time::Now()</c> (ztime.h:41-77): the frame time is clamped to
    /// 0.25 s (native's 4 FPS cap), scaled by 2^shift in the debug build, accumulated
    /// only while unpaused, and reported as <c>(paused ? pauseStart : accumulated) −
    /// negativeOffset</c>. While paused the returned time is frozen, so every training
    /// condition keyed off the clock stops too (native pauses the whole client clock;
    /// in the port the mission conditions all read <c>Site.TimeSeconds</c>, which Game1
    /// feeds from here).
    /// </summary>
    public static double Advance(double realDeltaSeconds)
    {
        double dt = Math.Clamp(realDeltaSeconds, 0.0, MaxFrameSeconds);
        if (!_paused)
        {
            // The shift scaling lives behind the debug gate like native: in shipping
            // builds Time::Now() accumulates real time unscaled (ztime.h:76-77) — only
            // the _DEBUG_TRAINING build multiplies by 2^shift (ztime.h:62-63).
            _accumulated += TrainingClock.DebugTrainingKeys ? dt * Math.Pow(2.0, _shift) : dt;
        }
        // native Now(): (s_dwPauseStart != 0 ? s_dwPauseStart : s_dwAccumulatedTime) - s_dwNegativeOffset
        return (_paused ? _pauseStart : _accumulated) - _negativeOffset;
    }

    /// <summary>Test/reset hook — full teardown between missions (native's clock is
    /// process state that survives missions; the port resets it in tests only).</summary>
    public static void ResetForTests()
    {
        _accumulated = 0;
        _negativeOffset = 0;
        _pauseStart = 0;
        _paused = false;
        _shift = 0;
    }
}

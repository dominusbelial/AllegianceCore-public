namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Native trekigc.cpp:1016-1075: speak each change of
/// 1 + int(timeLeft), resetting to 11 while inactive.</summary>
public sealed class RipcordCountdownSound
{
    /// <summary>sounddef.mdl:353-362 applies -7 dB to the countdown waves.</summary>
    public static float Gain => MathF.Pow(10f, -7f / 20f);
    private int _lastSecond = 11;
    private long? _startedAt;
    private long _lastClockMs;
    private double _elapsedSeconds;
    private float _durationSeconds;

    /// <summary>Integrate the server's simulation scale across frames so the
    /// countdown follows the sim timer (shipIGC.cpp:283-289), including changes
    /// of scale while ripcording. Shared by the voice and HUD expiry.</summary>
    public float TimeLeft(bool active, long? startedAt, long nowMs, float duration, double timeScale)
    {
        if (!active || startedAt is null)
        {
            _startedAt = null;
            _elapsedSeconds = 0;
            return 0;
        }
        if (_startedAt != startedAt)
        {
            _startedAt = startedAt;
            _lastClockMs = startedAt.Value;
            _elapsedSeconds = 0;
            _durationSeconds = duration;
        }
        // Native ResetRipcordTimeLeft captures the GA-adjusted duration once.
        // Permit a late core load to supply a duration that was initially unknown.
        if (_durationSeconds <= 0f && duration > 0f) _durationSeconds = duration;
        _elapsedSeconds += Math.Max(0, nowMs - _lastClockMs) / 1000.0 * timeScale;
        _lastClockMs = nowMs;
        return Math.Max(0, _durationSeconds - (float)_elapsedSeconds);
    }

    public int? Update(bool active, float timeLeft)
    {
        if (!active || timeLeft <= 0f)
        {
            _lastSecond = 11;
            return null;
        }
        int second = 1 + (int)timeLeft;
        if (second == _lastSecond) return null;
        _lastSecond = second;
        return CountdownSound.TickAssetFor(second) is not null ? second : null;
    }
}

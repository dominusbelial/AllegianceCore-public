using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Allegiance.ClientCore.MonoGame.Diagnostics;

/// <summary>
/// P889 — the client's per-window frame-time log. AIX2 (the shared-machine
/// AIX-S variant) defines the format and
/// <c>scripts/allegiance-index.py --frames</c> consumes it:
///
/// <code>
/// 2026-10-29T16:28:21.017Z [frameprof] window 5s frames=298 frame-wall mean=9.8ms p95=16.4ms
/// </code>
///
/// One line per finished 5-second window, with a stopwatch delta per
/// <c>Update</c> (the same wall frame time the P789 session recorder already
/// measures). Fixed 0.5 ms histogram, zero allocations per frame, no I/O on the
/// frame path — <see cref="Record"/> only returns a formatted line when a
/// window closes, and the caller writes it.
/// </summary>
public sealed class FrameProfileWindow
{
    /// <summary>Window length AIX2 parses; non-5-second windows are ignored by
    /// the index tool, so this is a contract, not a tuning knob.</summary>
    public const double WindowSeconds = 5;
    public const int BucketCount = 1024;        // 0–512 ms; the last bucket is overflow
    public const double BucketMs = 0.5;
    /// <summary>Env switch; the line is ON by default so any recorded match can
    /// feed AIX-S without a special build. <c>0</c>/<c>false</c>/<c>off</c> off.</summary>
    public const string EnvironmentVariable = "ALLEGIANCE_FRAMEPROF";

    private readonly double _windowMs;
    private readonly Func<long> _now;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int[] _buckets = new int[BucketCount];
    private long _start;
    private int _frames;
    private double _sum;

    public FrameProfileWindow(double windowSeconds = WindowSeconds, Func<long>? timestamp = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        _windowMs = windowSeconds * 1000;
        _now = timestamp ?? Stopwatch.GetTimestamp;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True when the environment leaves frame profiling on (default).</summary>
    public static bool EnabledFromEnvironment(Func<string, string?>? environment = null)
    {
        string? value = (environment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value)
            || value.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }

    public int Frames => _frames;

    /// <summary>
    /// One frame. Returns the finished <c>[frameprof]</c> line when this frame
    /// closed the window (otherwise null). The first frame only starts the
    /// clock.
    /// </summary>
    public string? Record(double frameMs)
    {
        long now = _now();
        if (_start == 0) { _start = now; return null; }
        if (!double.IsFinite(frameMs) || frameMs < 0) frameMs = 0;
        int bucket = (int)(frameMs / BucketMs);
        if (bucket >= BucketCount) bucket = BucketCount - 1;
        _buckets[bucket]++;
        _frames++;
        _sum += frameMs;
        if (now - _start < _windowMs * (Stopwatch.Frequency / 1000.0)) return null;
        string line = FormatLine();
        Reset();
        return line;
    }

    /// <summary>The line for the frames recorded so far (also used by tests).</summary>
    public string FormatLine()
    {
        double mean = _frames > 0 ? _sum / _frames : 0;
        var text = new StringBuilder(128);
        text.Append(_clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        text.Append(" [frameprof] window ").Append(FormatSeconds()).Append("s frames=").Append(_frames)
            .Append(" frame-wall mean=").Append(mean.ToString("0.0", CultureInfo.InvariantCulture)).Append("ms p95=")
            .Append(Percentile(0.95).ToString("0.0", CultureInfo.InvariantCulture)).Append("ms");
        return text.ToString();
    }

    private string FormatSeconds() =>
        _windowMs % 1000 == 0 ? (_windowMs / 1000).ToString("0", CultureInfo.InvariantCulture)
                             : (_windowMs / 1000).ToString("0.###", CultureInfo.InvariantCulture);

    private double Percentile(double p)
    {
        if (_frames == 0) return 0;
        long rank = Math.Max(1, (long)Math.Ceiling(_frames * p));
        long seen = 0;
        for (int i = 0; i < BucketCount; i++)
        {
            seen += _buckets[i];
            if (seen >= rank) return (i + 0.5) * BucketMs;
        }
        return (BucketCount - 0.5) * BucketMs;
    }

    public void Reset()
    {
        Array.Clear(_buckets);
        _frames = 0;
        _sum = 0;
        _start = _now();
    }
}

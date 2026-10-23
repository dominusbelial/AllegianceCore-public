using System.Diagnostics;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;

namespace Allegiance.ClientCore.MonoGame.Diagnostics;

/// <summary>What kind of match the session recorder is watching. P789 —
/// <see cref="ShowcaseFixed"/> is the fixed-seed tune Showcase (seed 729, fixed
/// size and duration) whose score is comparable across machines; any other
/// Showcase is <see cref="Showcase"/>; every other match is <see cref="Normal"/>.
/// Only a <see cref="ShowcaseFixed"/> score is ranked.</summary>
public enum SessionMatchKind { Normal, Showcase, ShowcaseFixed }

/// <summary>
/// P789 — the one always-on, per-match performance recorder. Runs in EVERY
/// match (local skirmish, Showcase, remote server) at fixed cost:
///
/// <list type="bullet">
/// <item><b>Bounded memory:</b> one fixed 512-bucket frame-time histogram
/// (0.5 ms buckets, 0–256 ms + overflow) allocated once per match — never
/// per-frame samples.</item>
/// <item><b>O(1) per frame, zero allocations:</b> <see cref="RecordFrame"/>
/// is pure arithmetic on preallocated fields; <see cref="SampleWorld"/> is a
/// plain counter update. No LINQ, no strings, no boxing.</item>
/// <item><b>No locks, no I/O on the frame path:</b> the caller feeds values it
/// already copied out (frame ms, HUD time dilation, ship counts sampled under
/// locks the frame code already takes). The recorder takes no lock of its own;
/// it is only touched from the single game thread. Saving/publishing happens
/// once per match end through <see cref="SessionPerformanceHandoff"/>
/// (single-slot, never waits, never queues).</item>
/// </list>
///
/// The summary reuses the P729 <see cref="Distribution"/> shape so the panel
/// and the share payload speak the same language.
/// </summary>
public sealed class SessionPerformanceRecorder
{
    /// <summary>Frame-time histogram buckets (0.5 ms each → 0–256 ms; the last
    /// bucket is the ≥256 ms overflow).</summary>
    public const int BucketCount = 512;
    public const double BucketMs = 0.5;

    private readonly int[] _frameBuckets = new int[BucketCount];
    private long _frames;
    private double _frameMsSum;
    private double _worstFrameMs;
    private double _tidiMinPercent = 100;
    private double _tidiWeightedSum;   // Σ(tidi% × frameMs) → time-weighted mean
    private double _tidiWeightMs;      // Σ(frameMs)
    private double _shipsInViewSum;
    private long _shipsSamples;
    private int _shipsInViewPeak;
    private int _totalPlayersPeak;
    private SessionMatchKind _kind;
    private long _startTimestamp;
    private bool _primed;

    public long Frames => _frames;

    /// <summary>Begin a new match. Fixed-size state is reset in place — the
    /// recorder never allocates again after construction.</summary>
    public void Start(SessionMatchKind kind)
    {
        _kind = kind;
        Array.Clear(_frameBuckets);
        _frames = 0; _frameMsSum = 0; _worstFrameMs = 0;
        _tidiMinPercent = 100; _tidiWeightedSum = 0; _tidiWeightMs = 0;
        _shipsInViewSum = 0; _shipsSamples = 0; _shipsInViewPeak = 0; _totalPlayersPeak = 0;
        _startTimestamp = Stopwatch.GetTimestamp();
        _primed = false;
    }

    /// <summary>One frame. O(1), zero allocations, no lock, no I/O.
    /// <paramref name="frameMs"/> is the real wall frame time; <paramref name="tidiPercent"/>
    /// is the HUD's time-dilation percentage (100 = full speed). Values the
    /// caller has not observed yet (e.g. spectator ship counts) simply keep
    /// their last sampled value via <see cref="SampleWorld"/>.</summary>
    public void RecordFrame(double frameMs, float tidiPercent)
    {
        // The first frame after Start carries whatever blocked the game thread
        // before the recorder began (connect handshake, level load) — it has no
        // baseline, so prime the clock and measure from the second frame on.
        if (!_primed) { _primed = true; return; }
        if (!double.IsFinite(frameMs) || frameMs < 0) frameMs = 0;
        int bucket = (int)(frameMs / BucketMs);
        if (bucket >= BucketCount) bucket = BucketCount - 1;
        _frameBuckets[bucket]++;
        _frames++;
        _frameMsSum += frameMs;
        if (frameMs > _worstFrameMs) _worstFrameMs = frameMs;
        if (double.IsFinite(tidiPercent))
        {
            if (tidiPercent < _tidiMinPercent) _tidiMinPercent = tidiPercent;
            _tidiWeightedSum += tidiPercent * frameMs;
            _tidiWeightMs += frameMs;
        }
    }

    /// <summary>Throttled world sample (the caller keeps it ≤10 Hz under a
    /// lock it already holds): ships that can draw in the viewed sector and
    /// the total players seen in the match. O(1), zero allocations.</summary>
    public void SampleWorld(int shipsInView, int totalPlayers)
    {
        if (shipsInView > _shipsInViewPeak) _shipsInViewPeak = shipsInView;
        if (totalPlayers > _totalPlayersPeak) _totalPlayersPeak = totalPlayers;
        _shipsInViewSum += shipsInView;
        _shipsSamples++;
    }

    /// <summary>Mean frame time of the worst 1 % of frames, from the fixed
    /// histogram (bucket centres, 0.25 ms resolution). 0 when nothing was
    /// recorded.</summary>
    public double FrameMsOnePercentLow()
    {
        if (_frames == 0) return 0;
        long lowCount = Math.Max(1, (long)Math.Ceiling(_frames / 100d));
        long seen = 0;
        double msSum = 0;
        for (int i = BucketCount - 1; i >= 0; i--)
        {
            long take = Math.Min(lowCount - seen, _frameBuckets[i]);
            if (take <= 0) continue;
            msSum += take * (i + 0.5) * BucketMs;
            seen += take;
            if (seen >= lowCount) break;
        }
        return msSum / seen;
    }

    private double Percentile(double p)
    {
        if (_frames == 0) return 0;
        long rank = Math.Max(1, (long)Math.Ceiling(_frames * p));
        long seen = 0;
        for (int i = 0; i < BucketCount; i++)
        {
            seen += _frameBuckets[i];
            if (seen >= rank) return (i + 0.5) * BucketMs;
        }
        return (BucketCount - 0.5) * BucketMs;
    }

    /// <summary>Build the immutable per-match summary. Pure computation over
    /// the fixed histogram — microseconds, safe on the game-over frame; the
    /// caller hands any I/O to <see cref="SessionPerformanceHandoff"/>.</summary>
    public SessionPerformanceSummary Finish(BenchmarkRecommendation? settings, double? serverTickP95Ms, string bottleneck)
    {
        double matchSeconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        double meanFrameMs = _frames > 0 ? _frameMsSum / _frames : 0;
        double fpsLow = _frames > 0 ? 1000 / FrameMsOnePercentLow() : 0;
        double fpsAverage = meanFrameMs > 0 ? 1000 / meanFrameMs : 0;
        double tidiMean = _tidiWeightMs > 0 ? _tidiWeightedSum / _tidiWeightMs : 100;
        double shipsMean = _shipsSamples > 0 ? _shipsInViewSum / _shipsSamples : 0;
        bool measured = _frames > 0;
        var frameMs = new Distribution((int)Math.Min(int.MaxValue, _frames), meanFrameMs,
            Percentile(0.5), Percentile(0.95), Percentile(0.99), _worstFrameMs);
        return new SessionPerformanceSummary
        {
            Kind = _kind,
            Ranked = _kind == SessionMatchKind.ShowcaseFixed,
            MatchSeconds = matchSeconds,
            Settings = settings,
            Frames = _frames,
            FpsAverage = fpsAverage,
            FpsOnePercentLow = fpsLow,
            FrameMs = frameMs,
            TidiMinPercent = _tidiMinPercent,
            TidiTimeWeightedMeanPercent = tidiMean,
            ShipsInViewMean = shipsMean,
            ShipsInViewPeak = _shipsInViewPeak,
            TotalPlayersPeak = _totalPlayersPeak,
            ServerTickP95Ms = serverTickP95Ms,
            Bottleneck = bottleneck,
            ScoreMeasured = measured,
            Score = measured
                ? ShowcaseScore.Compute(shipsMean, fpsLow, tidiMean)
                : 0,
        };
    }
}

/// <summary>P789 — the per-match performance numbers the game-over panel shows.
/// Immutable, pure data: built by <see cref="SessionPerformanceRecorder.Finish"/>.</summary>
public sealed record SessionPerformanceSummary
{
    public SessionMatchKind Kind { get; init; }
    public bool Ranked { get; init; }
    public double MatchSeconds { get; init; }
    public BenchmarkRecommendation? Settings { get; init; }
    public long Frames { get; init; }
    public double FpsAverage { get; init; }
    public double FpsOnePercentLow { get; init; }
    public Distribution? FrameMs { get; init; }
    public double TidiMinPercent { get; init; }
    public double TidiTimeWeightedMeanPercent { get; init; }
    public double ShipsInViewMean { get; init; }
    public int ShipsInViewPeak { get; init; }
    public int TotalPlayersPeak { get; init; }
    public double? ServerTickP95Ms { get; init; }
    /// <summary>Mean CPU/GPU ms where available (the frame telemetry's own
    /// averages, P643); null when no GPU timer / telemetry ran this match.</summary>
    public double? CpuMs { get; init; }
    public double? GpuMs { get; init; }
    public string Bottleneck { get; init; } = "";
    public bool ScoreMeasured { get; init; }
    public int Score { get; init; }
}

/// <summary>P789 — single-slot off-thread hand-off for per-match-end work
/// (saving the summary JSON, sharing, hardware probing). Never waits the
/// caller: if the slot is busy the new work is DROPPED (there is exactly one
/// publication per match, so a drop can only happen after a previous match's
/// save is still running — and a save can never pile up into a queue).
/// Never blocks the game thread; never floods the pool (at most one worker
/// at a time).</summary>
public sealed class SessionPerformanceHandoff
{
    private int _busy;
    public bool Busy => Volatile.Read(ref _busy) == 1;

    /// <summary>Post work to the pool. Returns false (dropped) when the single
    /// slot is still occupied; the caller never waits.</summary>
    public bool TryPost(Action work)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        Task.Run(() =>
        {
            try { work(); }
            catch (Exception) { /* Diagnostics can never fail the game. */ }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
        return true;
    }
}

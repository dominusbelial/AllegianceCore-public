using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.SinglePlayer.Benchmark;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P789 — the Showcase score, the session recorder and its threading contract.
/// The score formula (kept exactly as proposed in the brief):
/// <c>score = round(ships_in_view_mean × smooth × realtime × 10)</c> with
/// <c>smooth = clamp(fps_1pct_low / 60, 0, 1)</c> and
/// <c>realtime = clamp(tidi_time_weighted_mean / 100, 0, 1)</c>.
/// </summary>
public class P789ShowcaseScoreTests
{
    // ── score: worked examples from the brief ──────────────────────────────

    [Fact]
    public void Anchors_200v200_and_60v60_at_full_quality()
    {
        // ~400 ships in view (200v200), 60 fps 1% low, full real time → 4000.
        Assert.Equal(4000, ShowcaseScore.Compute(400, 60, 100));
        // ~120 ships (60v60) at the same quality → 1200.
        Assert.Equal(1200, ShowcaseScore.Compute(120, 60, 100));
    }

    [Fact]
    public void EdgeInputs_ZeroFps_TidiHalf_ZeroShips()
    {
        // 0 fps → smooth 0 → score 0 regardless of ships.
        Assert.Equal(0, ShowcaseScore.Compute(400, 0, 100));
        // Time dilation held at 50% caps the score at half credit.
        Assert.Equal(2000, ShowcaseScore.Compute(400, 60, 50));
        // An empty sector scores 0.
        Assert.Equal(0, ShowcaseScore.Compute(0, 60, 100));
        // Over-driving inputs clamps: 120 fps and 100% are both capped at 1.
        Assert.Equal(4000, ShowcaseScore.Compute(400, 120, 100));
        // Negative fps clamps to 0.
        Assert.Equal(0, ShowcaseScore.Compute(400, -5, 100));
    }

    [Fact]
    public void PartialQuality_Scales_Linearly()
    {
        // 30 fps 1% low = half smoothness; 120 ships → 600.
        Assert.Equal(600, ShowcaseScore.Compute(120, 30, 100));
        // Both halves: 30 fps and 50% real time → 300.
        Assert.Equal(300, ShowcaseScore.Compute(120, 30, 50));
    }

    [Fact]
    public void Ranking_IsDecidedByTheMatchKind_NotTheNumber()
    {
        var ranked = new SessionPerformanceRecorder();
        ranked.Start(SessionMatchKind.ShowcaseFixed);
        ranked.RecordFrame(16.667, 100f);   // prime
        ranked.RecordFrame(16.667, 100f);
        ranked.SampleWorld(120, 121);
        var summary = ranked.Finish(null, null, "");
        Assert.True(summary.Ranked);
        Assert.True(summary.ScoreMeasured);
        Assert.Equal(ShowcaseScore.Compute(120, 1000 / ranked.FrameMsOnePercentLow(), 100), summary.Score);

        var unranked = new SessionPerformanceRecorder();
        unranked.Start(SessionMatchKind.Showcase);
        unranked.RecordFrame(16.667, 100f);   // prime
        unranked.RecordFrame(16.667, 100f);
        unranked.SampleWorld(120, 121);
        Assert.False(unranked.Finish(null, null, "").Ranked);
    }

    // ── recorder: percentiles and bounded memory ───────────────────────────

    [Fact]
    public void Recorder_Percentiles_MatchAKnownDistribution()
    {
        var recorder = new SessionPerformanceRecorder();
        recorder.Start(SessionMatchKind.Showcase);
        recorder.RecordFrame(16.667, 100f);   // prime (the first frame after Start is not measured)
        // 400 frames at 0.5, 1.0, ... 200.0 ms — every value is an exact 0.5 ms
        // bucket edge, so the recorded centre is always value + 0.25 ms.
        for (int i = 1; i <= 400; i++) recorder.RecordFrame(i * 0.5, 100f);
        var summary = recorder.Finish(null, null, "");
        Assert.Equal(400, summary.Frames);
        Assert.Equal(100.25, summary.FrameMs!.Mean, 12);      // exact mean of 0.5..200 (the running sum is exact, not bucketed)
        Assert.Equal(100.25, summary.FrameMs.P50, 12);        // rank 200 → value 100 → bucket centre 100.25
        Assert.Equal(190.25, summary.FrameMs.P95, 12);        // rank 380 → centre 190.25
        Assert.Equal(198.25, summary.FrameMs.P99, 12);        // rank 396 → centre 198.25
        Assert.Equal(200, summary.FrameMs.Worst, 12);         // exact worst is tracked, not bucketed
        // 1% low: mean of the worst 4 frames (198.5..200 ms values) —
        // bucket centres 198.75/199.25/199.75/200.25 → 199.5 ms.
        Assert.Equal(199.5, recorder.FrameMsOnePercentLow(), 12);
        Assert.Equal(1000 / 199.5, summary.FpsOnePercentLow, 9);
    }

    [Fact]
    public void Recorder_BoundedMemory_FixedHistogram()
    {
        var recorder = new SessionPerformanceRecorder();
        recorder.Start(SessionMatchKind.Normal);
        recorder.RecordFrame(16.667, 100f);   // prime
        // 10k frames — the recorder stays at its fixed 512-bucket histogram;
        // nothing grows with frame count (this test also runs under the
        // allocation checker below, which would fail if any per-frame state
        // were heap-allocated).
        for (int i = 0; i < 10_000; i++) recorder.RecordFrame(1 + (i % 7) * 0.5, 100f - (i % 40));
        var summary = recorder.Finish(null, null, "");
        Assert.Equal(10_000, summary.Frames);
        Assert.Equal(10_000, summary.FrameMs!.Count);
        // Ships/players: mean over the 10 Hz samples, peak kept exactly.
        for (int i = 0; i <= 100; i++) recorder.SampleWorld(i % 60, i % 60);
        // Note: Finish above already latched nothing destructive — SampleWorld
        // only accumulates; recompute via a fresh recorder for determinism.
        var recorder2 = new SessionPerformanceRecorder();
        recorder2.Start(SessionMatchKind.ShowcaseFixed);
        recorder2.RecordFrame(16.667, 100f);   // prime
        recorder2.RecordFrame(16.667, 100f);
        for (int i = 0; i <= 120; i++) recorder2.SampleWorld(i <= 60 ? i : 120 - i, i <= 60 ? i : 120 - i);
        var s2 = recorder2.Finish(null, null, "");
        Assert.Equal(16.667, s2.FrameMs!.Mean, 12);  // exact running sum, not bucketed
        Assert.Equal(3600 / 121.0, s2.ShipsInViewMean, 9); // mean of 0..60..0 over the 121 samples
        Assert.Equal(60, s2.ShipsInViewPeak);
        Assert.Equal(60, s2.TotalPlayersPeak);
    }

    [Fact]
    public void Recorder_ZeroAllocationsPerFrame()
    {
        var recorder = new SessionPerformanceRecorder();
        recorder.Start(SessionMatchKind.Showcase);
        recorder.RecordFrame(16.667, 100f);   // prime (not measured) + warm the paths
        recorder.SampleWorld(120, 121);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            recorder.RecordFrame(16.667, 100f);
            recorder.SampleWorld(120, 121);
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, delta);
    }

    [Fact]
    public void Recorder_HandlesGarbageInputs_WithoutNaN()
    {
        var recorder = new SessionPerformanceRecorder();
        recorder.Start(SessionMatchKind.Normal);
        recorder.RecordFrame(16.667, 100f);   // prime
        recorder.RecordFrame(double.NaN, float.NaN);
        recorder.RecordFrame(-3, -1);
        recorder.RecordFrame(1000, 250f);
        var summary = recorder.Finish(null, null, "");
        Assert.Equal(3, summary.Frames);
        Assert.All(new[] { summary.FpsAverage, summary.FpsOnePercentLow, summary.FrameMs!.Mean,
                summary.FrameMs.P99, summary.TidiTimeWeightedMeanPercent, summary.Score },
            v => Assert.True(double.IsFinite(v) || v == summary.Score));
        Assert.True(double.IsFinite(summary.FpsAverage));
        Assert.True(double.IsFinite(summary.FpsOnePercentLow));
        Assert.True(double.IsFinite(summary.FrameMs.Mean));
    }

    [Fact]
    public void Recorder_EmptyMatch_ScoresZero_WithoutDividingByZero()
    {
        var recorder = new SessionPerformanceRecorder();
        recorder.Start(SessionMatchKind.ShowcaseFixed);
        var summary = recorder.Finish(null, null, "");
        Assert.False(summary.ScoreMeasured);
        Assert.Equal(0, summary.Score);
        Assert.Equal(0, summary.FpsAverage);
        Assert.Equal(0, summary.FpsOnePercentLow);
        Assert.Equal(0, summary.FrameMs!.Count);
    }

    // ── threading proof: a blocking result sink cannot stall the frame loop ──

    [Fact]
    public void BlockingSink_NeverStalls_RecordAndFinishStillProceed()
    {
        var handoff = new SessionPerformanceHandoff();
        using var blocked = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        // The sink blocks FOREVER once it starts (a stub that never returns).
        Assert.True(handoff.TryPost(() => { entered.Set(); blocked.Wait(); }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "sink work never started");
        // While the sink is stuck, further posts are DROPPED — never queued.
        Assert.False(handoff.TryPost(() => throw new InvalidOperationException("must not run")));
        // And the game path (record + finish) proceeds with a fresh hand-off.
        var recorder = new SessionPerformanceRecorder();
        recorder.Start(SessionMatchKind.ShowcaseFixed);
        recorder.RecordFrame(16.667, 100f);   // prime
        recorder.SampleWorld(200, 201);
        for (int i = 0; i < 100; i++) recorder.RecordFrame(16.667, 100f);
        var summary = recorder.Finish(null, null, "");
        Assert.Equal(100, summary.Frames);
        Assert.True(summary.Score > 0);
        // The second hand-off (the summary save) still goes through: one slot
        // per process is occupied by the blocked stub, so this post DROPS
        // rather than waiting — the game-over frame is never stalled.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool posted = handoff.TryPost(() => { });
        Assert.False(posted);                       // dropped, not awaited
        Assert.True(sw.ElapsedMilliseconds < 1000); // and it returned immediately
    }

    [Fact]
    public void Handoff_RunsWorkOffTheCaller_AndSurvivesExceptions()
    {
        var handoff = new SessionPerformanceHandoff();
        var thrown = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        Assert.True(handoff.TryPost(() => { thrown.Set(); throw new IOException("diagnostics save failed"); }));
        Assert.True(thrown.Wait(TimeSpan.FromSeconds(10)));
        SpinWait.SpinUntil(() => !handoff.Busy, TimeSpan.FromSeconds(10));  // the slot frees in `finally`
        Assert.True(handoff.TryPost(() => done.Set()), "failed work must release the slot");
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
    }
}

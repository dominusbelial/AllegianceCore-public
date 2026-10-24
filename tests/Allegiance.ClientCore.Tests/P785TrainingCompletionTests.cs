using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Training;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P785 — training completion: the six training clock/goal keys, the slideshow
/// progression model, the Introduction slide inventory, the HUD training-callout
/// table and the 30 s training critical-tip lifetime. Native citations inline;
/// the production-handler tests live in <see cref="P785TrainingHandlerTests"/>.
/// </summary>
[Collection("Training")]
public class P785TrainingCompletionTests
{
    private sealed class TestMission : TrainingMission
    {
        public TestMission(ITrainingSite site) : base(site) { }
        public override int GetMissionID() => Tm2BasicFlight;
        public override int GetStartSectorId() => 1010;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new Goal(new TrueCondition());
    }

    // ── 1. The training clock (zlib/ztime.cpp) ──────────────────────────────

    [Fact]
    public void Pause_freezes_the_clock_and_continue_resumes_from_the_freeze_point()
    {
        // native Time::Pause/Continue (ztime.cpp:15-49): Now() returns the frozen
        // stamp while paused; resuming adds the paused span to the negative offset
        // so the game clock continues from the freeze with no jump. Frame times are
        // clamped to 0.25 s (ztime.h:57, the 4 FPS cap) like the port's Advance.
        TrainingClock.ResetForTests();
        Assert.Equal(0.25, TrainingClock.Advance(0.25), 6);
        TrainingClock.Pause();
        Assert.True(TrainingClock.IsPaused, "Pause() must set IsPaused (ztime.cpp:28-31)");
        Assert.Equal(0.25, TrainingClock.Advance(10.0), 6);   // frozen — the 10 s span is dropped
        TrainingClock.Continue();
        Assert.False(TrainingClock.IsPaused);
        Assert.Equal(0.5, TrainingClock.Advance(0.25), 6);    // resumes at the freeze point
    }

    [Fact]
    public void SetShift_clamps_to_plus_minus_three()
    {
        // native Time::SetShift (ztime.cpp:58-67): "limit the amount of speed change".
        TrainingClock.ResetForTests();
        TrainingClock.SetShift(10);
        Assert.Equal(3, TrainingClock.GetShift());
        TrainingClock.SetShift(-10);
        Assert.Equal(-3, TrainingClock.GetShift());
        TrainingClock.SetShift(0);
        Assert.Equal(0, TrainingClock.GetShift());
    }

    [Fact]
    public void Shift_scales_the_clock_only_behind_the_debug_gate()
    {
        // native: the 2^shift scaling lives in the _DEBUG_TRAINING Time::Now
        // (ztime.h:57-63); the shipping Now() accumulates real time unscaled
        // (ztime.h:76-77). The port gates the same way.
        TrainingClock.ResetForTests();
        TrainingClock.SetShift(1);
        Assert.Equal(0.25, TrainingClock.Advance(0.25), 6);   // shipping: shift ignored
        TrainingClock.DebugTrainingKeys = true;
        Assert.Equal(0.75, TrainingClock.Advance(0.25), 6);   // debug: dt << 1
        TrainingClock.DebugTrainingKeys = false;
        TrainingClock.SetShift(0);
    }

    // ── 2. RecordKeyPress: the six training keys (trainingmission.cpp:342-413) ──

    [Fact]
    public void TK_PauseTM_toggles_pause_and_never_reaches_the_key_conditions()
    {
        // native case TK_PauseTM (trainingmission.cpp:347-358): toggle
        // Time::Pause/Continue and return WITHOUT iterating the key conditions.
        TrainingClock.ResetForTests();
        var site = new FakeTrainingSite();
        var mission = new TestMission(site);

        Assert.False(TrainingClock.IsPaused);
        Assert.True(mission.RecordKeyPress(KeyPauseTm));
        Assert.True(TrainingClock.IsPaused, "first TK_PauseTM press must pause");
        Assert.True(mission.RecordKeyPress(KeyPauseTm));
        Assert.False(TrainingClock.IsPaused, "second TK_PauseTM press must resume");
        Assert.False(site.ConsumeKey(KeyPauseTm), "TK_PauseTM must not reach the key conditions");
    }

    [Fact]
    public void TK_CommandFinishTM_is_delivered_to_the_key_conditions()
    {
        // TK_CommandFinishTM (igc.h:433) has NO case in RecordKeyPress, so it falls
        // to the default branch (trainingmission.cpp:402-410) and reaches every
        // GetKeyCondition — native ships the binding (trekinput.cpp:347,1614).
        var site = new FakeTrainingSite();
        var mission = new TestMission(site);
        Assert.True(mission.RecordKeyPress(KeyCommandFinishTm));
        Assert.True(site.ConsumeKey(KeyCommandFinishTm), "TK_CommandFinishTM must reach the key-condition queue");
    }

    [Fact]
    public void Clock_and_skip_keys_fall_through_to_key_conditions_in_shipping_mode()
    {
        // shipping native: the four handlers are compiled out (#ifdef _DEBUG_TRAINING,
        // trainingmission.cpp:359; no project defines it), so the keys reach the
        // default branch — the key conditions — inertly.
        TrainingClock.ResetForTests();
        var site = new FakeTrainingSite();
        var mission = new TestMission(site);
        foreach (var key in new[] { KeySkipTmGoal, KeyIncreaseTMClockSpeed, KeyDecreaseTMClockSpeed, KeyResetTMClockSpeed })
        {
            Assert.True(mission.RecordKeyPress(key), $"TK {key} must be consumed");
            Assert.True(site.ConsumeKey(key), $"shipping-mode TK {key} must reach the key conditions (default branch)");
        }
        Assert.Equal(0, TrainingClock.GetShift());
    }

    [Fact]
    public void Debug_clock_keys_change_the_clock_and_skip_goal_is_NYI()
    {
        // debug native (#ifdef _DEBUG_TRAINING, trainingmission.cpp:359-384):
        // Increase/Decrease/Reset move the shift; SkipTMGoal is "XXX NYI".
        TrainingClock.ResetForTests();
        TrainingClock.DebugTrainingKeys = true;
        try
        {
            var site = new FakeTrainingSite();
            var mission = new TestMission(site);

            Assert.True(mission.RecordKeyPress(KeyIncreaseTMClockSpeed));
            Assert.Equal(1, TrainingClock.GetShift());
            Assert.True(mission.RecordKeyPress(KeyIncreaseTMClockSpeed));
            Assert.Equal(2, TrainingClock.GetShift());
            Assert.False(site.ConsumeKey(KeyIncreaseTMClockSpeed), "handled debug keys must not reach the key conditions");

            Assert.True(mission.RecordKeyPress(KeyDecreaseTMClockSpeed));
            Assert.Equal(1, TrainingClock.GetShift());

            Assert.True(mission.RecordKeyPress(KeyResetTMClockSpeed));
            Assert.Equal(0, TrainingClock.GetShift());

            Assert.True(mission.RecordKeyPress(KeySkipTmGoal));   // consumed, no effect (XXX NYI)
            Assert.False(site.ConsumeKey(KeySkipTmGoal));

            // clamp while mashing the keys (ztime.cpp:58-67)
            for (int i = 0; i < 8; i++) mission.RecordKeyPress(KeyIncreaseTMClockSpeed);
            Assert.Equal(3, TrainingClock.GetShift());
        }
        finally { TrainingClock.DebugTrainingKeys = false; TrainingClock.ResetForTests(); }
    }

    // ── 3. The slideshow model (WinTrek/Slideshow.cpp) ──────────────────────

    private static SlideshowModel SlideShow(int count) => new(
        Enumerable.Range(0, count).Select(i => new SlideshowModel.Slide($"slide {i}", null, $"stem_{i}")).ToArray(),
        "Test");

    [Fact]
    public void Slideshow_starts_on_the_first_slide_and_advances_by_key()
    {
        // Slideshow constructor (Slideshow.cpp:33-61) shows the first slide;
        // OnKey advances (Slideshow::OnKey :179-183); past the end → Dismiss
        // (Slideshow.cpp:107-113) and the in-slideshow flag clears (:67).
        var s = SlideShow(3);
        s.Start();
        Assert.True(s.IsInSlideShow);
        Assert.Equal(0, s.Index);
        s.OnKey();
        Assert.Equal(1, s.Index);
        Assert.False(s.Dismissed);
        s.OnKey();
        s.OnKey();
        // the last slide stays current through the dismiss frame (the wrap image
        // still shows it while the screen navigates away) — so Current returns the
        // last slide and the caller reads Dismissed instead.
        Assert.True(s.Dismissed, "advancing past the last slide dismisses");
        Assert.False(s.IsInSlideShow);
        Assert.Equal(2, s.Index);
    }

    [Fact]
    public void Slideshow_sound_finish_arms_the_one_second_timer()
    {
        // Slideshow::OnEvent (Slideshow.cpp:123-146): the sound-finish event arms
        // a 1.0 s window timer; the timer fire advances to the next slide.
        var s = SlideShow(2);
        s.Start();
        s.OnSoundFinished();
        Assert.True(s.TimerArmed, "finish event must arm the timer");
        s.Tick(0.9);
        Assert.False(s.Dismissed);
        Assert.Equal(0, s.Index);          // still inside the 1 s delay
        s.Tick(0.2);
        Assert.Equal(1, s.Index);          // timer expired → next slide
        Assert.False(s.TimerArmed);
    }

    [Fact]
    public void Slideshow_key_press_cancels_the_timer_and_advances_immediately()
    {
        // Slideshow::OnEvent key path + OnKey (Slideshow.cpp:138-146,179-183):
        // a key press skips the armed timer and advances at once.
        var s = SlideShow(2);
        s.Start();
        s.OnSoundFinished();
        s.OnKey();
        Assert.False(s.TimerArmed, "the key press must cancel the armed timer");
        Assert.Equal(1, s.Index);
    }

    // ── 4. The Introduction slide inventory ─────────────────────────────────

    [Fact]
    public void Introduction_slideshow_carries_the_native_slides()
    {
        // TM1 has no mission: Training::StartMission returns false for
        // c_TM_1_Introduction (Training.cpp:85-89) and OnButtonTrain routes it to
        // ScreenIDPostTrainSlideshow (trainingscreen.cpp:182-185) with the
        // tm_1_introduction namespace (WinTrek.cpp:2503). The tm_1_introduction.mdl
        // slides list carries 15 (image, SoundID) pairs, tm_slide_1_01..1_14 (04a/04b
        // split), extracted verbatim into TrainingSlideData.
        var slides = TrainingSlides.Introduction;
        Assert.Equal(15, slides.Count);
        Assert.Equal("tm_slide_1_01", slides.Slides[0].SoundStem);
        Assert.Equal("tm_slide_1_04a", slides.Slides[3].SoundStem);
        Assert.Equal("tm_slide_1_04b", slides.Slides[4].SoundStem);
        Assert.Equal("tm_slide_1_14", slides.Slides[^1].SoundStem);
        // The first slide's native narration text rides the model (visual stand-in).
        Assert.Contains("Welcome, Recruits", slides.Slides[0].Caption);
        // Pre/post slideshows resolve from the same table (WinTrek.cpp:2473-2510).
        Assert.Equal("tm_slide_2_01", TrainingSlides.PreMission(Tm2BasicFlight, "Basic Flight").Slides[0].SoundStem);
        Assert.Equal("tm_slide_2_post_01", TrainingSlides.PostMission(Tm2BasicFlight, "Basic Flight").Slides[0].SoundStem);
        Assert.Equal("tm_slide_5_01", TrainingSlides.PreMission(Tm5CommandView, "Command View").Slides[0].SoundStem);
    }

    // ── 5. The HUD training-callout table (indicator.cpp:461-701) ───────────

    [Fact]
    public void Roll_callout_carries_the_native_text_and_second_line()
    {
        // RollTrainingOverlay (indicator.cpp:634-641): "PUSH Q AND E TO ROLL LEFT
        // AND RIGHT" plus the mouse-control second line — the native roll cue the
        // HUD row asked for (the port's numeric roll gauge stays an extension).
        var roll = HudTrainingCallout.For(17);
        Assert.NotNull(roll);
        Assert.Equal("PUSH Q AND E TO ROLL LEFT AND RIGHT", roll!.Value.Description);
        Assert.Equal("PUSH V TO TOGGLE MOUSE CONTROL", roll.Value.Description2);
        // y-up native anchor (-110,-170) flips to (−110,+170) below centre in the
        // port's top-left space.
        Assert.Equal(-110f, roll.Value.AnchorX);
        Assert.Equal(170f, roll.Value.AnchorY);
    }

    [Fact]
    public void Every_training_overlay_id_resolves_and_none_resolves_for_zero()
    {
        // NoTrainingOverlay returns immediately (indicator.cpp:486-488).
        Assert.Null(HudTrainingCallout.For(0));
        for (int id = 1; id <= 23; id++)
            Assert.NotNull(HudTrainingCallout.For(id));
    }
    // ── 6. The 30 s training critical-tip lifetime (console.cpp:913) ────────

    [Fact]
    public void Critical_tip_lifetime_is_30s_in_training_and_7_5s_otherwise()
    {
        // fTipTextDuration = Training::IsTraining() ? 30.0f : 7.5f (console.cpp:913).
        Assert.Equal(7.5, HudConsoleFormat.CriticalTipLifetime(false), 6);
        Assert.Equal(30.0, HudConsoleFormat.CriticalTipLifetime(true), 6);
    }
}

using System;
using System.Linq;
using Allegiance.ClientCore.State;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P834 — failing-first tests for the resolution-aware volumetric near-cost fix.
///
/// The live symptom (2026-10-26, 200v200 rig, VOLUMETRIC tier): flying close to
/// a volumetric explosion dropped the frame rate. P579's fill-rate defences
/// were tuned at 2560×1440 on the old Arrow-Lake iGPU and had three holes this
/// suite pins shut:
///
///   1. <b>Resolution-blind thresholds/budget.</b> Coverage LOD thresholds were
///      absolute pixels and the per-frame budget a fixed 42 M pixel-steps, so a
///      4K/5K2K frame (2.4×/3× the pixels) could not honour it: a
///      screen-filling volume sat at the 64-step floor and blew through the
///      budget anyway. Now coverage is a FRACTION of the viewport height and the
///      budget is per screen pixel.
///   2. <b>No quarter-res rung.</b> Half of a 4K/5K2K frame is still &gt; 1080p.
///      Screen-filling volumes now render at quarter resolution.
///   3. <b>Camera-inside marched the whole chord.</b> With the eye inside the
///      bounding sphere RaySphere returns t0 = 0; the march now starts at the
///      near plane and stops at the opaque scene's depth.
///
/// Plus the level floor (`Minimal`, 48 steps) and the GPU-agnostic adaptive
/// budget controller. Each test fails against the pre-P834 code: the new
/// members do not exist there (compile-time red), and the ones that do exist
/// (ForPixelDiameter / AllocateLevels / MaxPixelSteps) answer the old policy.
/// </summary>
public class P834VolumetricNearCostTests
{
    // ── 1. The per-frame budget scales with the viewport ───────────────────
    [Fact]
    public void Budget_scales_with_viewport_pixels()
    {
        float fhd = VolumeBudget.MaxPixelStepsFor(1920, 1080);
        float qhd = VolumeBudget.MaxPixelStepsFor(2560, 1440);
        float uhd = VolumeBudget.MaxPixelStepsFor(3840, 2160);
        float fiveK = VolumeBudget.MaxPixelStepsFor(5120, 2160);

        // Strictly increasing in viewport pixels (the P834 "resolution-aware
        // budget"): the same scene may use more pixel-steps on a bigger frame.
        Assert.True(fhd < qhd, $"{fhd} !< {qhd}");
        Assert.True(qhd < uhd, $"{qhd} !< {uhd}");
        Assert.True(uhd < fiveK, $"{uhd} !< {fiveK}");

        // Above the P579 floor the budget is exactly the measured rate × pixels.
        Assert.Equal(VolumeBudget.PixelStepsPerViewportPixel * 2560 * 1440, qhd, 3);
        Assert.Equal(VolumeBudget.PixelStepsPerViewportPixel * 3840 * 2160, uhd, 3);
        Assert.Equal(VolumeBudget.PixelStepsPerViewportPixel * 5120 * 2160, fiveK, 3);

        // 5K2K is ~3× the pixels of 1440p → ~3× the budget, the number the
        // report states (≈199 M pixel-steps ≈ 8 ms of the B50's measured rate).
        Assert.InRange(fiveK, 190_000_000f, 205_000_000f);

        // Small viewports keep at least the P579 1440p-tuned absolute value.
        Assert.Equal(VolumeBudget.MaxPixelSteps, fhd);
        Assert.Equal(VolumeBudget.MaxPixelSteps, VolumeBudget.MaxPixelStepsFor(1280, 720));
        Assert.Equal(VolumeBudget.MaxPixelSteps, VolumeBudget.MaxPixelStepsFor(0, 0));
    }

    // ── 2. Coverage LOD is a screen fraction, not absolute pixels ──────────
    [Theory]
    [InlineData(0.02f, VolumeLod.Level.Ultra)]
    [InlineData(0.08f, VolumeLod.Level.High)]
    [InlineData(0.20f, VolumeLod.Level.Med)]
    [InlineData(0.50f, VolumeLod.Level.Low)]
    [InlineData(1.20f, VolumeLod.Level.Minimal)]   // screen-filling / camera inside
    public void Lod_selection_uses_screen_coverage_fraction(float fraction, VolumeLod.Level expected)
        => Assert.Equal(expected, VolumeLod.ForDiameterFraction(fraction));

    [Fact]
    public void Same_screen_fraction_picks_the_same_level_at_every_resolution()
    {
        // A volume covering 20 % of the frame height: 288 px at 1440p, 432 px at
        // 4K, 540 px at 5K2K. P834 must answer the same level for all three;
        // the old absolute-pixel thresholds answered Med / Low / Low.
        Assert.Equal(VolumeLod.Level.Med, VolumeLod.ForDiameterFraction(288f / 1440f));
        Assert.Equal(VolumeLod.Level.Med, VolumeLod.ForDiameterFraction(432f / 2160f));
        Assert.Equal(VolumeLod.Level.Med, VolumeLod.ForDiameterFraction(540f / 2160f));
    }

    [Fact]
    public void Reference_height_pixel_api_is_preserved_for_the_p579_tests()
    {
        // The P579 test expectations (56/160/420 px at 1440p) still hold.
        Assert.Equal(VolumeLod.Level.Ultra, VolumeLod.ForPixelDiameter(30f));
        Assert.Equal(VolumeLod.Level.High, VolumeLod.ForPixelDiameter(100f));
        Assert.Equal(VolumeLod.Level.Med, VolumeLod.ForPixelDiameter(300f));
        Assert.Equal(VolumeLod.Level.Low, VolumeLod.ForPixelDiameter(500f));
        // …and a volume that fills the 1440p height gets the new floor rung.
        Assert.Equal(VolumeLod.Level.Minimal, VolumeLod.ForPixelDiameter(1440f));
        Assert.Equal(VolumeLod.Level.Minimal, VolumeLod.ForPixelDiameter(3000f));
    }

    [Fact]
    public void Minimal_level_is_the_cheapest_rung_and_all_techniques_exist()
    {
        Assert.Equal(VolumeLod.LevelCount, Enum.GetValues<VolumeLod.Level>().Length);
        Assert.True(VolumeLod.TotalSteps(VolumeLod.Level.Minimal) < VolumeLod.TotalSteps(VolumeLod.Level.Low));
        Assert.Equal(48, VolumeLod.TotalSteps(VolumeLod.Level.Minimal));
        // Cost order == enum order == the budget's degradation ladder.
        for (int i = 1; i < VolumeLod.LevelCount; i++)
            Assert.True(VolumeLod.TotalSteps((VolumeLod.Level)(i - 1)) < VolumeLod.TotalSteps((VolumeLod.Level)i));
        var names = Enum.GetValues<VolumeLod.Level>().Select(VolumeLod.TechniqueName).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Contains("RaymarchMinimal", names);
    }

    // ── 3. Quarter-res level chosen by coverage ───────────────────────────
    [Theory]
    [InlineData(0.05f, VolumeRenderScale.Full)]
    [InlineData(0.20f, VolumeRenderScale.Half)]
    [InlineData(0.75f, VolumeRenderScale.Quarter)]
    [InlineData(1.00f, VolumeRenderScale.Quarter)]
    [InlineData(6.00f, VolumeRenderScale.Quarter)]   // camera inside a big volume
    public void Render_scale_is_chosen_by_coverage(float fraction, VolumeRenderScale expected)
        => Assert.Equal(expected, VolumeScreenPolicy.ScaleForDiameterFraction(fraction));

    [Fact]
    public void Quarter_res_marches_one_sixteenth_of_the_pixels()
    {
        Assert.Equal(1f, VolumeScreenPolicy.AreaFactor(VolumeRenderScale.Full));
        Assert.Equal(0.25f, VolumeScreenPolicy.AreaFactor(VolumeRenderScale.Half));
        Assert.Equal(1f / 16f, VolumeScreenPolicy.AreaFactor(VolumeRenderScale.Quarter));
        Assert.Equal(2560f * 1440f / 16f, VolumeScreenPolicy.PixelsAtScale(VolumeRenderScale.Quarter, 2560, 1440));
    }

    [Fact]
    public void Screen_filling_coverage_is_quarter_res_at_every_resolution()
    {
        // A bomber blast that covers 1.6 × the frame height at 1440p is 2304 px,
        // at 5K2K 3456 px; both are quarter-res (the old half-res-only ladder
        // left half of 5K2K ~2765 px = 177 M pixel-steps for ONE volume).
        Assert.Equal(VolumeRenderScale.Quarter, VolumeScreenPolicy.ScaleForDiameterFraction(2304f / 1440f));
        Assert.Equal(VolumeRenderScale.Quarter, VolumeScreenPolicy.ScaleForDiameterFraction(3456f / 2160f));
    }

    // ── 4. The budget counts pixels at the render scale ────────────────────
    [Fact]
    public void Quarter_res_volume_is_16x_cheaper_in_the_budget()
    {
        var full = new VolumeBudget.VolumeCost(0.5f, 1_000_000f, VolumeRenderScale.Full);
        var quarter = new VolumeBudget.VolumeCost(0.5f, 1_000_000f, VolumeRenderScale.Quarter);
        Assert.Equal(VolumeBudget.MarchedArea(full) / 16f, VolumeBudget.MarchedArea(quarter), 3);

        // Degrading the full-res volume to the floor already brings the pair
        // under 52 M (48 M + 4 M), so the quarter-res volume keeps its nominal
        // 64-step level: without the scale in the cost model both volumes cost
        // 48 M and the budget degraded the pair equally (the old behaviour).
        var levels = VolumeBudget.AllocateLevels(new[] { full, quarter }, budget: 52_000_000f);
        Assert.Equal(VolumeLod.Level.Minimal, levels[0]);
        Assert.Equal(VolumeLod.Level.Low, levels[1]);
    }

    [Fact]
    public void Screen_filling_blast_fits_the_5k2k_budget_after_quarter_res()
    {
        // The reported case: four sub-volumes of one close blast, each covering
        // the whole frame (a fighter/bomber plume around the camera).
        float w = 5120, h = 2160;
        var costs = Enumerable.Range(0, 4)
            .Select(_ => new VolumeBudget.VolumeCost(2.5f, w * h, VolumeRenderScale.Quarter))
            .ToArray();
        float budget = VolumeBudget.MaxPixelStepsFor((int)w, (int)h);
        var levels = VolumeBudget.AllocateLevels(costs, budget);
        float total = VolumeBudget.TotalPixelSteps(costs, levels);

        Assert.True(total <= budget, $"total {total} > budget {budget}");
        // The nominal level for a ≥ full-screen volume is already the 48-step
        // floor, so no budget degradation is needed…
        Assert.All(levels, l => Assert.Equal(VolumeLod.Level.Minimal, l));
        // …and even if the same blast were marched at Low (64 steps) it would
        // still fit (177 M < 199 M), which is the headroom the B50 needs.
        Assert.True(total < budget * 0.75f, $"{total} should leave headroom under {budget}");
        float atLow = 4f * (5120f * 2160f / 16f) * VolumeLod.TotalSteps(VolumeLod.Level.Low);
        Assert.True(atLow < budget, $"even the 64-step level ({atLow}) must fit {budget}");
    }

    [Fact]
    public void Absurd_area_is_clamped_to_the_viewport_before_budgeting()
    {
        // The renderer clamps a camera-inside volume's projected area to the
        // viewport (its raw diameter² can be 650 M px). This documents the
        // budget consequence: a viewport-sized quarter-res volume costs
        // pixels/16 × steps, not diameter²/16 × steps.
        var inside = new VolumeBudget.VolumeCost(6f, 5120f * 2160f, VolumeRenderScale.Quarter);
        float steps = VolumeBudget.MarchedArea(inside) * VolumeLod.TotalSteps(VolumeLod.Level.Low);
        Assert.Equal(5120f * 2160f / 16f * 64f, steps, 1);
    }

    // ── 5. Camera-inside / near: clamp the march to the visible range ──────
    [Fact]
    public void Inside_case_starts_at_the_near_plane_and_stops_at_the_scene()
    {
        // Eye inside a radius-165 sphere: the ray enters at t0 = 0 and leaves at
        // t1 = 330; the near plane is 10, the scene (a hull) at 40.
        var r = VolumetricGeometry.ClampMarchRange(t0: 0f, t1: 330f, nearPlaneDistance: 10f, sceneDistance: 40f);
        Assert.True(r.Visible);
        Assert.Equal(10f, r.Start, 4);
        Assert.Equal(40f, r.End, 4);
        Assert.Equal(30f, r.Span, 4);
    }

    [Fact]
    public void Near_plane_clamp_removes_the_segment_in_front_of_the_lens()
    {
        // A volume in front of the camera whose sphere already starts at 2 u.
        var r = VolumetricGeometry.ClampMarchRange(2f, 120f, nearPlaneDistance: 10f, sceneDistance: 10_000f);
        Assert.Equal(10f, r.Start, 4);
        Assert.Equal(120f, r.End, 4);
    }

    [Fact]
    public void Range_already_inside_the_visible_window_is_untouched()
    {
        var r = VolumetricGeometry.ClampMarchRange(20f, 30f, nearPlaneDistance: 10f, sceneDistance: 100f);
        Assert.Equal(20f, r.Start, 4);
        Assert.Equal(30f, r.End, 4);
        Assert.Equal(10f, r.Span, 4);
    }

    [Theory]
    [InlineData(0f, 330f, 10f, 5f)]      // scene in front of the near plane
    [InlineData(50f, 60f, 10f, 40f)]     // whole volume behind the occluder
    [InlineData(10f, 10f, 10f, 1000f)]   // degenerate
    public void Fully_occluded_or_degenerate_ranges_are_not_visible(float t0, float t1, float near, float scene)
        => Assert.False(VolumetricGeometry.ClampMarchRange(t0, t1, near, scene).Visible);

    [Fact]
    public void No_depth_far_scene_never_clamps_the_march()
    {
        // --soft off binds a 1×1 white texture → normalized scene depth ≈ 1 →
        // a ~7900 u far distance; a drone-sized volume is never clipped by it.
        var r = VolumetricGeometry.ClampMarchRange(0f, 330f, nearPlaneDistance: 10f, sceneDistance: 7900f);
        Assert.Equal(330f, r.End, 4);
        Assert.Equal(320f, r.Span, 4);
    }

    // ── 6. GPU-agnostic adaptive budget (bounded, smooth) ─────────────────
    [Fact]
    public void Adaptive_budget_starts_at_the_measured_b50_value()
        => Assert.Equal(1f, new VolumeBudgetController().Scale);

    [Fact]
    public void Over_budget_frames_lower_the_scale_smoothly_and_stop_at_the_floor()
    {
        var c = new VolumeBudgetController();
        c.ApplyWindow(40.0);                       // 40 ms ≫ 19.2 ms
        Assert.Equal(1f - VolumeBudgetController.StepDown, c.Scale, 4);   // one step, not a cliff
        for (int i = 0; i < 100; i++) c.ApplyWindow(40.0);
        Assert.Equal(VolumeBudgetController.MinScale, c.Scale, 4);        // bounded
        Assert.True(c.Scale > 0f);
    }

    [Fact]
    public void Comfortable_frames_recover_headroom_but_never_overshoot()
    {
        var c = new VolumeBudgetController();
        for (int i = 0; i < 100; i++) c.ApplyWindow(40.0);
        Assert.Equal(VolumeBudgetController.MinScale, c.Scale, 4);
        for (int i = 0; i < 1000; i++) c.ApplyWindow(5.0);
        Assert.Equal(1f, c.Scale, 4);
    }

    [Fact]
    public void Vsync_locked_60hz_frames_do_not_adapt()
    {
        var c = new VolumeBudgetController();
        for (int i = 0; i < 100; i++) Assert.False(c.ApplyWindow(16.67));
        Assert.Equal(1f, c.Scale);
        Assert.Equal(0, c.Adjustments);
    }

    [Fact]
    public void Observe_averages_over_the_window_and_ignores_bad_values()
    {
        var c = new VolumeBudgetController();
        // 0.3 s of 30 ms frames = one window → one decrement.
        for (int i = 0; i < 10; i++) c.Observe(30.0);
        Assert.Equal(1f - VolumeBudgetController.StepDown, c.Scale, 4);
        float before = c.Scale;
        Assert.False(c.Observe(double.NaN));
        Assert.False(c.Observe(0));
        Assert.False(c.Observe(-5));
        Assert.Equal(before, c.Scale);
    }
}

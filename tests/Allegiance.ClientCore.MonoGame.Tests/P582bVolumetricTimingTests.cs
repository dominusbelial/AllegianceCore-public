using System;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P582b — the volumetric/displaced fireball timing compression. On today's
/// (P582) tree the shaders ramp growth AND cool across the FULL fire+smoke-tail
/// life (<c>tn = age/life</c> with life ≈ film + 2.4 s tail ≈ 3.9 s for a
/// fighter), so a fighter fireball reaches full radius only at ~life-end and
/// the peak lands out of sync with the ship's death (Domingo: "timing on those
/// volumetric explosions was kind of out of sync", "maybe it's all too slow?").
/// <see cref="VolumetricTiming"/> compresses growth/cool onto the FILM clock:
/// full radius + peak hot by 28% of the film (~0.42 s for a 1.5 s fighter film,
/// inside native's film peak) and cool-to-smoke across the film — while the
/// P582 erosion (displaced) still uses the full life so the surface holds
/// through the film and erodes over the tail.
///
/// Class/method absent on today's tree → failing-first (same convention as
/// P573/P582).
/// </summary>
public class P582bVolumetricTimingTests
{
    // Real fighter (SmallShip) film life: exp25(32f)/exp22(30f) at 20fps →
    // 1.6s / 1.5s (native film table).
    private const float FighterFilmLife = 1.5f;

    [Fact]
    public void Fighter_peak_time_is_within_native_film_peak_window()
    {
        // Native film peak for a fighter death ≈ 0.3–0.5 s; our 28% film frac
        // must land inside it.
        float peak = VolumetricTiming.PeakTimeSec(FighterFilmLife);
        Assert.InRange(peak, 0.30f, 0.50f);
    }

    [Fact]
    public void Growth_reaches_full_by_the_film_peak()
    {
        // At the defined peak time the volume must be essentially fully grown.
        float peak = VolumetricTiming.PeakTimeSec(FighterFilmLife);
        Assert.True(VolumetricTiming.Grow(peak, FighterFilmLife) >= 0.985f,
            $"growth at peak {peak:F3}s should be ~1, got {VolumetricTiming.Grow(peak, FighterFilmLife):F3}");
        Assert.True(VolumetricTiming.RadiusFrac(peak, FighterFilmLife) >= 0.987f);
    }

    [Fact]
    public void Fireball_is_visible_from_the_first_frame_not_a_slow_ramp()
    {
        // Birth radius floor is non-zero (0.18) so frame 0 already shows a hot
        // core; and within ~0.12 s the radius has grown meaningfully (native's
        // film frame 0 is fully visible, not a dot — the delayed-first-frame bug).
        float birth = VolumetricTiming.RadiusFrac(0f, FighterFilmLife);
        Assert.True(birth >= VolumetricTiming.BirthRadiusFrac - 1e-4f, $"birth radius {birth:F3}");

        float atFrame3 = VolumetricTiming.RadiusFrac(0.05f, FighterFilmLife);
        float atEarly = VolumetricTiming.RadiusFrac(0.12f, FighterFilmLife);
        // Growth is decisively underway within the first few frames (was a flat
        // slow ramp under the old whole-life curve — see Old_curve_delta test).
        Assert.True(atFrame3 > birth + 0.003f);
        Assert.True(atEarly > 0.25f, $"radius at 0.12s should be >0.25, got {atEarly:F3}");
    }

    [Fact]
    public void Growth_is_monotonically_non_decreasing_over_the_film_and_tail()
    {
        // From birth through well past the film (into the smoke tail) the radius
        // never shrinks.
        float prev = VolumetricTiming.Grow(0f, FighterFilmLife);
        for (float age = 0.002f; age <= 4.0f; age += 0.002f)
        {
            float cur = VolumetricTiming.Grow(age, FighterFilmLife);
            Assert.True(cur >= prev - 1e-5f, $"growth decreased at age {age:F3}");
            prev = cur;
        }
    }

    [Fact]
    public void Cool_stays_hot_through_peak_then_reaches_smoke_by_film_end()
    {
        // White-hot (0) at birth and through the peak; cold (1) by the film end
        // (native's bright-through-the-film decal).
        Assert.True(VolumetricTiming.Cool(0f, FighterFilmLife) < 0.01f);
        float peak = VolumetricTiming.PeakTimeSec(FighterFilmLife);
        Assert.True(VolumetricTiming.Cool(peak, FighterFilmLife) < 0.02f,
            $"should still be hot at peak, got cool={VolumetricTiming.Cool(peak, FighterFilmLife):F3}");
        Assert.True(VolumetricTiming.Cool(FighterFilmLife, FighterFilmLife) > 0.997f);
    }

    [Fact]
    public void Cool_is_monotonically_non_decreasing()
    {
        float prev = VolumetricTiming.Cool(0f, FighterFilmLife);
        for (float age = 0.002f; age <= 4.0f; age += 0.002f)
        {
            float cur = VolumetricTiming.Cool(age, FighterFilmLife);
            Assert.True(cur >= prev - 1e-5f, $"cool decreased at age {age:F3}");
            prev = cur;
        }
    }

    [Fact]
    public void Old_whole_life_curve_was_still_small_at_the_new_peak_this_fix_is_full()
    {
        // Document the magnitude of the regression: on today's code the growth
        // ramp spanned the WHOLE life (film 1.5 + tail 2.4 = 3.9 s), so at the
        // native film peak (~0.42 s) the old curve had grown to only ~0.18 —
        // the fireball was a small dim dot at the moment it should have peaked.
        // The fix is already full-size there.
        float fullLife = FighterFilmLife + VolumetricExplosionSampler.SmokeTailSeconds;
        float peak = VolumetricTiming.PeakTimeSec(FighterFilmLife);
        float oldTn = Math.Min(1f, peak / Math.Max(0.045f, fullLife));
        float oldGrow = oldTn * oldTn * (3f - 2f * oldTn);   // today's whole-life curve
        float oldRadius = 0.18f + 0.82f * oldGrow;

        float newRadius = VolumetricTiming.RadiusFrac(peak, FighterFilmLife);

        Assert.True(oldRadius < 0.30f, $"old whole-life curve unexpectedly already large at peak: {oldRadius:F3}");
        Assert.True(newRadius >= 0.987f, $"new curve should be full at peak: {newRadius:F3}");
    }

    [Fact]
    public void Sampler_wiring_keeps_film_clock_shorter_than_full_life()
    {
        // The renderer passes u.FilmLife as the shader growth/cool clock and
        // u.Life (= film + tail) for survival/erosion; verify the sampler yields
        // a film clock strictly shorter than the full life so growth really does
        // peak before the smoke tail.
        var preset = ExplosionPresets.For(Allegiance.Core.Sim.ExplosionType.SmallShip);
        var units = VolumetricExplosionSampler.SampleUnits(preset, 1.0f, Vector3.Zero, Vector3.Zero);
        Assert.NotEmpty(units);
        foreach (var u in units)
        {
            Assert.True(u.FilmLife > 0f);
            Assert.True(u.FilmLife < u.Life, $"film clock {u.FilmLife} must be shorter than full life {u.Life}");
        }
    }
}

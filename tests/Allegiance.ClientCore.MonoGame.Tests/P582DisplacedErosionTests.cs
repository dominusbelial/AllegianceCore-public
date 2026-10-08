using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P582 — the displaced-sphere erosion fix. Old code erased the fireball
/// LINEARLY from birth: <c>ClipThreshold = tn*1.25 - 0.15</c>
/// (DisplacedSphereRenderer.cs), so by ~40-50% of life most of the surface was
/// clipped — the "--fx displaced explosions erode away far too fast" bug.
/// The fix (<see cref="DisplacedSphereRenderer.ErodeClip"/>) HOLDS the full
/// fireball through the native film phase (explosion.cpp:187-189 keeps every
/// film frame visible; the decal is only removed once <c>frame &gt;=
/// frameCount</c>), then smoothstep-erodes over the added smoke tail, so the
/// visible duration tracks the Enhanced fireball+smoke.
///
/// Headless pure-logic tests: fail on today's tree (class/method absent →
/// failing-first, same convention as P573).
/// </summary>
public class P582DisplacedErosionTests
{
    // Real per-unit film fractions for a fighter (SmallShip) death:
    // films [exp25(32f), exp22(30f)] at 20fps → filmLife 1.6s / 1.5s, plus the
    // 2.4s smoke tail (VolumetricExplosionSampler.SmokeTailSeconds).
    private static readonly float[] RealFilmFractions = { 1.6f / 4.0f, 1.5f / 3.9f };

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.333f)]
    [InlineData(0.385f)]
    [InlineData(0.4f)]
    [InlineData(0.5f)]
    public void Fireball_holds_at_least_90_percent_coverage_through_the_film_phase(float filmFrac)
    {
        // Across the WHOLE film phase (tn 0..filmFrac) the clip threshold stays
        // below the shader's [0,1] displacement, so the surface is not eroded.
        for (float tn = 0f; tn <= filmFrac + 1e-4f; tn += 0.005f)
        {
            float clip = DisplacedSphereRenderer.ErodeClip(tn, filmFrac);
            float coverage = DisplacedSphereRenderer.CoverageAt(clip);
            Assert.True(coverage >= 0.90f,
                $"at tn={tn:F3} (filmFrac {filmFrac}) clip={clip:F3} coverage={coverage:P0} — fireball eroded mid-film");
        }
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.4f)]
    [InlineData(0.667f)]
    public void Fireball_is_fully_eroded_by_end_of_life(float filmFrac)
    {
        // At tn=1 (end of life) the clip has risen past 1 → every fragment
        // clipped → the volume has faded out (coverage ~0).
        float clip = DisplacedSphereRenderer.ErodeClip(1f, filmFrac);
        Assert.True(clip >= 1f, $"clip at end of life {clip:F3} should be >= 1");
        Assert.True(DisplacedSphereRenderer.CoverageAt(clip) <= 0.01f);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.769f)]
    public void Erosion_curve_is_monotonically_non_decreasing(float filmFrac)
    {
        float prev = DisplacedSphereRenderer.ErodeClip(0f, filmFrac);
        for (float tn = 0.002f; tn <= 1.001f; tn += 0.002f)
        {
            float cur = DisplacedSphereRenderer.ErodeClip(tn, filmFrac);
            Assert.True(cur >= prev - 1e-5f, $"erosion decreased at tn={tn:F3}: {prev:F4} -> {cur:F4}");
            prev = cur;
        }
    }

    [Fact]
    public void Old_linear_erosion_failed_the_film_phase_gate_today_fix_passes_it()
    {
        // Document the before/after: the old inline curve (tn*1.25 - 0.15) was
        // already clipping heavily BY THE END OF THE FILM (the reported bug),
        // whereas the fix holds full coverage through the film.
        foreach (float filmFrac in RealFilmFractions)
        {
            float oldClip = filmFrac * 1.25f - 0.15f;                 // today's formula
            float oldCoverage = DisplacedSphereRenderer.CoverageAt(oldClip);
            Assert.True(oldCoverage < 0.90f,
                $"old curve expected to fail the film gate (filmFrac {filmFrac}) but coverage {oldCoverage:P0}");

            float newClip = DisplacedSphereRenderer.ErodeClip(filmFrac, filmFrac);
            Assert.True(DisplacedSphereRenderer.CoverageAt(newClip) >= 0.90f);
        }
    }

    [Fact]
    public void Film_fraction_is_clamped_to_0_1()
    {
        // Degenerate inputs must not produce NaN/garbage.
        Assert.True(float.IsFinite(DisplacedSphereRenderer.ErodeClip(0.5f, -2f)));
        Assert.True(float.IsFinite(DisplacedSphereRenderer.ErodeClip(0.5f, 7f)));
        // Over-large filmFrac acts like a fully-holding curve (rarely reached).
        Assert.True(DisplacedSphereRenderer.CoverageAt(DisplacedSphereRenderer.ErodeClip(0.995f, 0.998f)) >= 0.90f);
    }

    // ── wiring: SampleUnits now carries the real per-unit FilmLife ─────────

    [Fact]
    public void Fighter_units_carry_the_real_film_life_and_matching_total_life()
    {
        // The renderer derives filmFrac from FilmLife/Life; verify the sampler
        // populates both consistently for a real fighter death.
        var preset = ExplosionPresets.For(Allegiance.Core.Sim.ExplosionType.SmallShip);
        var units = VolumetricExplosionSampler.SampleUnits(preset, 1.0f, Vector3.Zero, Vector3.Zero);

        Assert.NotEmpty(units);
        foreach (var u in units)
        {
            // SmallShip alternates exp25(32f)/exp22(30f) → 1.6s or 1.5s.
            Assert.True(u.FilmLife is >= 1.4f and <= 1.7f, $"FilmLife {u.FilmLife}");
            Assert.Equal(u.FilmLife + VolumetricExplosionSampler.SmokeTailSeconds, u.Life, 3);
            // The per-unit film fraction is a real value in (0,1).
            float filmFrac = u.FilmLife / u.Life;
            Assert.InRange(filmFrac, 0.3f, 0.5f);
            // Sanity: the new curve holds ≥90% through THIS unit's film phase.
            float clipAtFilmEnd = DisplacedSphereRenderer.ErodeClip(filmFrac, filmFrac);
            Assert.True(DisplacedSphereRenderer.CoverageAt(clipAtFilmEnd) >= 0.90f);
        }
    }
}

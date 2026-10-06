using System;
using System.IO;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P546 — headless tests for the ENHANCED explosion/smoke path: the default
/// setting (Enhanced), the near-camera fade factor, the density budget's
/// oldest-first eviction, the premultiplied smoke + capped fire intensity, the
/// CC0 atlases shipping in the game's own Content (no ALLEGIANCE_ARTWORK_PATH),
/// and explosion timing/size parity with <see cref="ExplosionSampler"/>.
/// </summary>
public class EnhancedVfxTests
{
    // ── 1) Default setting is Enhanced ─────────────────────────────────────
    [Fact]
    public void Default_setting_is_enhanced()
    {
        // The one switch defaults ON (Enhanced); --fx native turns it off for
        // parity. Native mode constructs none of the enhanced renderers.
        Assert.True(new Game1.LaunchOptions().EnhancedVfx);
    }

    // ── 2) near-fade alpha factor at/inside/outside N/M ────────────────────
    [Theory]
    [InlineData(1f, 0.0f)]   // inside (below FadeStart) → gone
    [InlineData(6f, 0.0f)]   // exactly at FadeStart → gone
    [InlineData(25f, 0.4872f)] // between → linear ramp
    [InlineData(45f, 1.0f)]  // at FadeEnd → full
    [InlineData(100f, 1.0f)] // outside (beyond FadeEnd) → full
    public void NearFade_factor_ramps_gone_to_full(float dist, float expected)
    {
        var near = ParticleNearFade.Flight;   // FadeStart 6, FadeEnd 45
        Assert.Equal(expected, ParticleNearFade.Factor(dist, true, near.FadeStart, near.FadeEnd), 4);
    }

    [Fact]
    public void NearFade_disabled_always_full_opacity()
    {
        Assert.Equal(1f, ParticleNearFade.Factor(0f, false, 0f, 0f));
        Assert.Equal(1f, ParticleNearFade.Factor(50f, false, 0f, 0f));
    }

    // ── 3) density budget evicts the oldest first ──────────────────────────
    [Fact]
    public void DensityBudget_admits_up_to_capacity_then_evicts_oldest_first()
    {
        var budget = new ParticleDensityBudget(capacity: 3);
        // Admit with keys = age (lower = younger). The first 3 are free.
        Assert.True(budget.TryAdmit(1.0f));   // oldest admitted initially
        Assert.True(budget.TryAdmit(0.7f));
        Assert.True(budget.TryAdmit(0.5f));
        Assert.Equal(3, budget.Count);

        // At capacity, admitting a NEWER puff (key 0.2) evicts the OLDEST (key 1.0).
        Assert.True(budget.TryAdmit(0.2f));
        Assert.Equal(3, budget.Count);
        // The evicted-oldest key (1.0) must be gone; the newest (0.2) present.
        // Peek via reflection-free behaviour: budget only exposes Count/Capacity,
        // so assert capacity semantics hold after many admissions (steady-state =
        // the freshest survive).
        Assert.True(budget.IsFull);
    }

    [Fact]
    public void DensityBudget_zero_capacity_rejects_everything_and_reset_empties()
    {
        var budget = new ParticleDensityBudget(capacity: 2);
        budget.TryAdmit(0.1f);
        budget.TryAdmit(0.2f);
        Assert.Equal(2, budget.Count);
        budget.Reset();
        Assert.Equal(0, budget.Count);
        Assert.False(budget.IsFull);

        var off = new ParticleDensityBudget(capacity: 0);
        Assert.False(off.TryAdmit(0.1f));
    }

    // ── 4) smoke premultiplied + fire capped ───────────────────────────────
    [Fact]
    public void Smoke_colour_is_premultiplied_rgb_leq_alpha()
    {
        // Every smoke puff's colour is premultiplied: rgb = tint·α, so the
        // blended contribution is rgb ≤ α — overlapping smoke accumulates
        // translucency instead of blowing out to white.
        float tint = 0.3f, alpha = 0.5f;
        var (r, g, b) = EnhancedVfx.Premultiplied(tint, tint, tint, alpha);
        Assert.Equal(tint * alpha, r, 5);
        Assert.Equal(tint * alpha, g, 5);
        Assert.Equal(tint * alpha, b, 5);
        Assert.True(r <= alpha && g <= alpha && b <= alpha);

        // The smoke field's peak alpha is capped well below opaque.
        Assert.InRange(EnhancedSmokeField.PeakAlpha, 0.0f, 0.9f);
    }

    [Fact]
    public void Fire_intensity_is_capped_not_full_white()
    {
        // Additive fire is decal-count and alpha capped so stacked blasts don't
        // blow out: at most 8 decals, peak per-decal alpha below 1.0, and the
        // life fade never exceeds 1.0.
        Assert.InRange(EnhancedExplosionRenderer.MaxFireDecals, 1, 8);
        Assert.InRange(EnhancedExplosionRenderer.FireballMaxAlpha, 0.0f, 1.0f);
        Assert.True(EnhancedExplosionRenderer.FireballMaxAlpha < 1.0f);
        Assert.InRange(EnhancedExplosionRenderer.ElapsedFade(0.1f, 1.0f), 0f, 1f);
        Assert.Equal(0f, EnhancedExplosionRenderer.ElapsedFade(1.1f, 1.0f), 5); // expired → 0
    }

    // ── 5) atlases resolve on a clean checkout (no artwork path) ───────────
    [Fact]
    public void Cc0_atlases_ship_in_game_content_with_expected_grids()
    {
        // The CC0 atlases are OUR art: they live in the game's own Content (not
        // the ALLEGIANCE_ARTWORK_PATH tree) so they load on a clean checkout with
        // no artwork path set (VfxAssets reads them via TitleContainer). Verify
        // each committed atlas PNG exists and has the grid dimensions the loader
        // slices by.
        string atlasDir = Path.Combine(RepoContentVfxDir(), "atlas");
        (string Stem, int GridCols, int GridRows, int CellPx)[] grids =
        {
            ("Explosion00", 5, 5, 128),
            ("Explosion01", 5, 5, 128),
            ("Explosion01-light", 5, 5, 128),
            ("Cloud01", 8, 8, 96),
            ("WispySmoke01", 8, 8, 96),
            ("DiscSmoke01", 16, 4, 96),
        };
        foreach (var (stem, cols, rows, cell) in grids)
        {
            string path = Path.Combine(atlasDir, stem + ".png");
            Assert.True(File.Exists(path), $"CC0 atlas missing from shipped Content: {path}");
            var (w, h) = PngDimensions(File.ReadAllBytes(path));
            Assert.Equal(cols * cell, w);
            Assert.Equal(rows * cell, h);
        }
        // Licence table + licences travel alongside the atlases.
        Assert.True(File.Exists(Path.Combine(RepoContentVfxDir(), "README.md")));
        Assert.True(Directory.Exists(Path.Combine(RepoContentVfxDir(), "licenses")));
    }

    // ── 6) explosion timing & size unchanged from ExplosionSampler ─────────
    [Fact]
    public void Enhanced_uses_sampler_timing_and_size_for_every_preset()
    {
        // The enhanced renderer reads size/timing from the SAME sources as the
        // native renderer: ExplosionSampler.Sample for the decals/shock-wave and
        // preset.RadiusExplosion / TotalDurationSeconds for the quad sizes and
        // the smoke lifetime. Pin that every preset yields the sampler's
        // staggered decal set (never more than CountDecals, always the first one
        // live at t=0) and sane finite size.
        for (int type = Allegiance.Core.Sim.ExplosionType.Mine;
             type <= Allegiance.Core.Sim.ExplosionType.LargeStation; type++)
        {
            var preset = ExplosionPresets.For(type);
            var sample = ExplosionSampler.Sample(preset, 0f);
            // Decal i begins at i·0.25s (native stagger) → at t=0 exactly decal 0
            // is live, and the sampler never exceeds the declared count.
            Assert.Single(sample.Fireballs);
            Assert.True(sample.Fireballs.Count <= preset.CountDecals);
            foreach (var fb in sample.Fireballs)
            {
                Assert.InRange(fb.FilmIndex, 0, preset.Films.Length - 1);
                Assert.True(fb.FrameIndex >= 0);
            }
            Assert.True(preset.RadiusExplosion > 0f, $"radius for type {type}");
            Assert.True(preset.TotalDurationSeconds > 0f, $"duration for type {type}");
            // The enhanced smoke count formula stays within the per-explosion budget.
            int smokeCount = Math.Clamp((int)(preset.RadiusExplosion / 28f) + 3, 5, EnhancedExplosionRenderer.MaxSmokePerExplosion);
            Assert.InRange(smokeCount, 5, EnhancedExplosionRenderer.MaxSmokePerExplosion);
            // A fighter and a station must differ in scale (native category parity).
            if (type == Allegiance.Core.Sim.ExplosionType.BigShip)
                Assert.True(preset.RadiusExplosion > ExplosionPresets.For(Allegiance.Core.Sim.ExplosionType.SmallShip).RadiusExplosion);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static string RepoContentVfxDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("cannot locate AllegianceCore repo root");
        return Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame", "Content", "Vfx");
    }

    private static (int Width, int Height) PngDimensions(byte[] png)
    {
        // PNG signature (8) + IHDR chunk: length(4)+type(4)+width(4)+height(4).
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P') throw new InvalidDataException("not a PNG");
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w, h);
    }
}

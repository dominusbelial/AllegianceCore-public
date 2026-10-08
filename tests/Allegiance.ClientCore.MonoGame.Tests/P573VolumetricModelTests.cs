using System;
using System.Linq;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P573 — pure-logic tests for the volumetric spike model. These exercise the
/// NATIVE 1:1 mapping (<see cref="VolumetricExplosionSampler"/> ports every rule
/// from <c>effect/explosion.cpp</c> and <c>trekigc.cpp:95-177</c> via
/// <see cref="ExplosionPresets"/>) plus the LOD / volume-cap / tier-selection
/// logic. All pure — no GraphicsDevice — so they run headless. Each class is
/// new in P573, so on today's (pre-P573) tree these fail to compile/run (the
/// failing-first precondition).
/// </summary>
public class P573VolumetricModelTests
{
    // ── VolumetricExplosionSampler: native parity ─────────────────────────

    [Fact]
    public void Mine_at_0_7s_yields_exactly_three_sub_volumes()
    {
        // Mine: count 4, stagger 0.25s (explosion.cpp:113). At 0.7s units born at
        // 0.0/0.25/0.5 are active, born-0.75 is not yet -> 3 units.
        var preset = ExplosionPresets.For(ExplosionType.Mine);
        var units = VolumetricExplosionSampler.SampleUnits(preset, 0.7f, new Vector3(0, 0, 0), new Vector3(0, 0, 0));
        Assert.Equal(3, units.Count);
        // Every unit's peak radius is the native radiusExplosion.
        Assert.All(units, u => Assert.Equal(preset.RadiusExplosion, u.PeakRadius));
    }

    [Fact]
    public void No_unit_renders_before_its_stagger_or_past_its_life()
    {
        var preset = ExplosionPresets.For(ExplosionType.BigShip);   // 16 decals, 0.25s stagger
        var early = VolumetricExplosionSampler.SampleUnits(preset, 0.0f, Vector3.Zero, Vector3.Zero);
        // At t=0 only the first unit (born 0) is active.
        Assert.Single(early);
        // Very late: everything has expired (life = filmLife + smoke tail).
        var late = VolumetricExplosionSampler.SampleUnits(preset, 100f, Vector3.Zero, Vector3.Zero);
        Assert.Empty(late);
    }

    [Fact]
    public void Centers_drift_with_the_wrecks_velocity_over_time()
    {
        // Native: position + t × m_dposition (explosion.cpp:161-163).
        var preset = ExplosionPresets.For(ExplosionType.SmallShip);
        var vel = new Vector3(10f, 20f, 30f);
        var t = 0.7f;
        var drifting = VolumetricExplosionSampler.SampleUnits(preset, t, Vector3.Zero, vel);
        var still = VolumetricExplosionSampler.SampleUnits(preset, t, Vector3.Zero, Vector3.Zero);
        Assert.Equal(drifting.Count, still.Count);
        for (int i = 0; i < drifting.Count; i++)
        {
            Assert.Equal(still[i].Center.X + vel.X * t, drifting[i].Center.X, 3);
            Assert.Equal(still[i].Center.Y + vel.Y * t, drifting[i].Center.Y, 3);
            Assert.Equal(still[i].Center.Z + vel.Z * t, drifting[i].Center.Z, 3);
        }
    }

    [Fact]
    public void Sub_volume_offsets_match_the_sprite_paths_native_offsets()
    {
        // The volumetric centres use the SAME deterministic offset the sprite path
        // (ExplosionSampler) uses, within ±radius*0.5 (explosion.cpp:117).
        var preset = ExplosionPresets.For(ExplosionType.BigShip);
        // At t=1.0 the units born at 0,0.25,0.5,0.75,1.0 are active => decals 0..4,
        // so units[i] is decal i (centre = its deterministic offset, velocity 0).
        var units = VolumetricExplosionSampler.SampleUnits(preset, 1.0f, Vector3.Zero, Vector3.Zero);
        Assert.Equal(5, units.Count);
        for (int i = 0; i < units.Count; i++)
        {
            var off = ExplosionSampler.OffsetFor(i, preset.RadiusExplosion * 0.5f);
            Assert.Equal(off.X, units[i].Center.X, 3);
            Assert.Equal(off.Y, units[i].Center.Y, 3);
            Assert.Equal(off.Z, units[i].Center.Z, 3);
        }
    }

    [Fact]
    public void Tint_for_mine_is_the_native_violet()
    {
        // trekigc.cpp:107-114 — mine tint (154,130,190)/255.
        var preset = ExplosionPresets.For(ExplosionType.Mine);
        var tint = VolumetricExplosionSampler.TintFor(preset);
        Assert.Equal(154f / 255f, tint.X, 3);
        Assert.Equal(130f / 255f, tint.Y, 3);
        Assert.Equal(190f / 255f, tint.Z, 3);
    }

    // ── VolumeLod ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(VolumeLod.Level.Low, 1, 64)]
    [InlineData(VolumeLod.Level.Med, 1, 128)]
    [InlineData(VolumeLod.Level.High, 1, 255)]
    [InlineData(VolumeLod.Level.Ultra, 2, 255)]
    public void Lod_steps_for_levels(VolumeLod.Level level, int slices, int inner)
    {
        var (s, i) = VolumeLod.StepsFor(level);
        Assert.Equal(slices, s);
        Assert.Equal(inner, i);
        Assert.Equal(slices * inner, VolumeLod.TotalSteps(level));
    }

    [Theory]
    [InlineData(30f, VolumeLod.Level.Ultra)]
    [InlineData(100f, VolumeLod.Level.High)]
    [InlineData(300f, VolumeLod.Level.Med)]
    [InlineData(500f, VolumeLod.Level.Low)]
    public void Lod_selection_by_on_screen_diameter(float px, VolumeLod.Level expected)
        => Assert.Equal(expected, VolumeLod.ForPixelDiameter(px));

    // ── VolumeBudget cap ──────────────────────────────────────────────────

    [Fact]
    public void Volume_budget_keeps_the_nearest_units()
    {
        var units = Enumerable.Range(0, 60)
            .Select(i => new VolumetricSubUnit(
                new Vector3(i * 100f, 0, 0), 25f, 0.7f, 0f, 3f, i, new Vector3(1, 1, 1)))
            .ToList();
        var kept = VolumeBudget.TrimNearest(units, new Vector3(0, 0, 0));
        Assert.Equal(VolumeBudget.MaxVolumes, kept.Count);
        // Nearest 40 = indices 0..39 (sorted ascending by distance).
        Assert.Equal(39f * 100f, kept[^1].Unit.Center.X, 3);
        Assert.All(kept.Select(k => k.Unit.Center.X), x => Assert.True(x <= 39f * 100f));
    }

    // ── EffectsQualityResolver (tier selection + fallback) ────────────────

    [Fact]
    public void Platform_default_desktop_is_volumetric_mobile_is_enhanced()
    {
        Assert.Equal(EffectsQuality.Volumetric, EffectsQualityResolver.PlatformDefault(isMobile: false));
        Assert.Equal(EffectsQuality.Enhanced, EffectsQualityResolver.PlatformDefault(isMobile: true));
    }

    [Fact]
    public void Volumetric_falls_back_to_enhanced_when_shader_unavailable_or_over_budget()
    {
        Assert.Equal(EffectsQuality.Volumetric,
            EffectsQualityResolver.Resolve(EffectsQuality.Volumetric, true, true));
        Assert.Equal(EffectsQuality.Enhanced,
            EffectsQualityResolver.Resolve(EffectsQuality.Volumetric, false, true));   // shader fail
        Assert.Equal(EffectsQuality.Enhanced,
            EffectsQualityResolver.Resolve(EffectsQuality.Volumetric, true, false));   // over budget
        Assert.True(EffectsQualityResolver.FellBack(EffectsQuality.Volumetric, EffectsQuality.Enhanced));
    }

    [Fact]
    public void Fx_values_parse_to_the_intended_tier_and_technique()
    {
        Assert.Equal((EffectsQuality.Native, VolumetricTechnique.RaymarchA), EffectsQualityResolver.ParseFx("native"));
        Assert.Equal((EffectsQuality.Enhanced, VolumetricTechnique.RaymarchA), EffectsQualityResolver.ParseFx("enhanced"));
        Assert.Equal((EffectsQuality.Volumetric, VolumetricTechnique.RaymarchA), EffectsQualityResolver.ParseFx("volumetric"));
        Assert.Equal((EffectsQuality.Volumetric, VolumetricTechnique.DisplacedSphereB), EffectsQualityResolver.ParseFx("displaced"));
        Assert.Null(EffectsQualityResolver.ParseFx("bogus"));
    }
}

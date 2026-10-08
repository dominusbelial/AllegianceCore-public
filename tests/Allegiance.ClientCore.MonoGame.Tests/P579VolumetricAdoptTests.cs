using System;
using System.Linq;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P579 — failing-first tests for the ADOPT fixes (port of the P573 spike onto
/// master with the live-found fixes hardened):
///
///   1. Camera-relative placement — a volume at world P with the camera at C
///      renders centred at P−C (the view matrix keeps the eye at the origin).
///      The P573 spike used absolute positions (the "no explosions" bug); the
///      fix is pinned in <see cref="VolumetricGeometry.CameraRelative"/>.
///   2. Depth fade — slope 250 (2000× the spike's 1/8) and a no-depth (--soft
///      off, "far" white texture) decode to fade = 1, not 0.
///   3. LOD monotonic in coverage — bigger on-screen ⇒ cheaper per pixel
///      (fewer, longer steps); the P573 policy was inverted (gave the biggest
///      volumes the MOST steps → 3–4 fps up close).
///   4. Budget degradation order — under a per-frame pixel/step budget, the
///      LARGEST-coverage (highest-cost) volumes are degraded first.
///
/// Each class exercised here is NEW in P579 (absent on the pre-P579 tree), so
/// on the P573 code these either fail to compile (missing type) or assert the
/// opposite of the P573 behaviour — the failing-first precondition.
/// </summary>
public class P579VolumetricAdoptTests
{
    // ── 1. Camera-relative placement ────────────────────────────────────────
    // A volume at world P with the camera at C must centre at P − C. Under the
    // P573 spike (absolute positions) this test fails: every volume was offset
    // by the whole camera position, so the camera-relative centre was nowhere
    // near P − C.
    [Fact]
    public void CameraRelative_volume_at_world_P_with_eye_at_C_centres_at_P_minus_C()
    {
        var p = new Vector3(100f, 200f, 300f);
        var c = new Vector3(10f, 20f, 30f);
        var rel = VolumetricGeometry.CameraRelative(p, c);
        Assert.Equal(p.X - c.X, rel.X, 6);
        Assert.Equal(p.Y - c.Y, rel.Y, 6);
        Assert.Equal(p.Z - c.Z, rel.Z, 6);
    }

    // An identity check: camera at the origin leaves the volume where it is.
    [Fact]
    public void CameraRelative_identity_when_eye_is_origin()
    {
        var p = new Vector3(5f, 6f, 7f);
        var rel = VolumetricGeometry.CameraRelative(p, Vector3.Zero);
        Assert.Equal(5f, rel.X, 6);
        Assert.Equal(6f, rel.Y, 6);
        Assert.Equal(7f, rel.Z, 6);
    }

    // ── 2. Depth fade: slope + no-depth = 1 ─────────────────────────────────
    // The spike's fade used * (1/8): a 1-unit depth gap yielded fade 0.125.
    // P579 uses slope 250 (the same SoftnessNormalized slope SoftParticle.fx
    // uses) and binds a 1×1 WHITE "far" depth texture when --soft is off, so a
    // no-depth read decodes to scene=1 → fade 1 (the spike read 0 → fade 0 and
    // erased every volume).
    [Fact]
    public void DepthFade_slope_is_250_not_one_eighth()
    {
        Assert.Equal(250f, VolumetricGeometry.SoftnessInv);
        // One eighth of the P573 policy would give 0.125 for this gap.
        Assert.True(VolumetricGeometry.DepthFade(0.5f, 0.5f - 1f / 250f) > 0.99f);
    }

    [Fact]
    public void DepthFade_no_depth_far_white_decodes_to_fade_one()
    {
        // "Far" scene (the 1×1 white texture decodes to linear depth 1): the
        // volume is never occluded → fade 1 at any sensible volume depth.
        Assert.Equal(1f, VolumetricGeometry.DepthFade(scene: 1f, volume: 0.5f));
        Assert.Equal(1f, VolumetricGeometry.DepthFade(scene: 1f, volume: 0.0f));
    }

    [Fact]
    public void DepthFade_fully_occluded_when_scene_closer()
    {
        // Scene much closer than the volume (gap ≫ 1/250): fully faded to 0.
        Assert.Equal(0f, VolumetricGeometry.DepthFade(scene: 0.4f, volume: 0.8f));
    }

    // ── 3. LOD monotonic in coverage: bigger ⇒ cheaper per pixel ────────────
    // The P573 policy mapped the LARGEST on-screen volumes to Ultra (nested,
    // 255+ steps over ~1 M px/bubble → 3–4 fps). P579 inverts it.
    [Theory]
    [InlineData(30f, VolumeLod.Level.Ultra)]   // tiny → many steps (cheap fill)
    [InlineData(100f, VolumeLod.Level.High)]
    [InlineData(300f, VolumeLod.Level.Med)]
    [InlineData(500f, VolumeLod.Level.Low)]    // screen-filling → few, long steps
    public void Coverage_LOD_bigger_on_screen_fewer_steps(float diameterPx, VolumeLod.Level expected)
        => Assert.Equal(expected, VolumeLod.ForPixelDiameter(diameterPx));

    [Fact]
    public void Total_steps_monotonically_decrease_with_on_screen_size()
    {
        // Ultra(510) > High(255) > Med(128) > Low(64): the biggest on-screen
        // volume spends the fewest steps.
        Assert.True(VolumeLod.TotalSteps(VolumeLod.Level.Ultra) > VolumeLod.TotalSteps(VolumeLod.Level.High));
        Assert.True(VolumeLod.TotalSteps(VolumeLod.Level.High) > VolumeLod.TotalSteps(VolumeLod.Level.Med));
        Assert.True(VolumeLod.TotalSteps(VolumeLod.Level.Med) > VolumeLod.TotalSteps(VolumeLod.Level.Low));
    }

    // The per-volume integration is LOD-uniform (each level's dt = 1/steps), so
    // the TOTAL optical depth is independent of the step count — a big Low-volume
    // is as bright as a small Ultra-volume.
    [Fact]
    public void Optical_depth_per_volume_is_lod_uniform()
    {
        foreach (var level in Enum.GetValues<VolumeLod.Level>())
            Assert.Equal(1.0, VolumeLod.TotalSteps(level) * (1.0 / VolumeLod.TotalSteps(level)), 6);
    }

    // ── 4. Budget degradation order: largest-coverage first ─────────────────
    [Fact]
    public void Budget_under_pressure_degrades_the_largest_coverage_first()
    {
        // Two volumes, both small enough to be nominal Ultra (510 steps).
        // A tight budget forces the LARGER-area one down (it costs the most).
        var vols = new (float DiameterPx, float AreaPx)[]
        {
            (30f, 30f * 30f),     // small coverage
            (50f, 50f * 50f),     // larger coverage — must degrade first
        };
        // Headroom for one Ultra + one Med (128): 900*510 + 2500*128 = 779k ≤ 1M.
        var levels = VolumeBudget.AllocateLevels(vols, 1_000_000f);
        Assert.Equal(VolumeLod.Level.Ultra, levels[0]);  // small stays untouched
        Assert.Equal(VolumeLod.Level.Med, levels[1]);    // large degraded (2 steps down)
    }

    [Fact]
    public void Budget_with_headroom_leaves_nominal_levels_untouched()
    {
        var vols = new (float DiameterPx, float AreaPx)[]
        {
            (30f, 900f),
            (300f, 300f * 300f),
        };
        var levels = VolumeBudget.AllocateLevels(vols, float.MaxValue);
        Assert.Equal(VolumeLod.Level.Ultra, levels[0]);
        Assert.Equal(VolumeLod.Level.Med, levels[1]);
    }

    [Fact]
    public void Budget_never_degrades_below_low()
    {
        // Even an enormous volume bottoms out at Low (64 steps) — never dropped
        // below the cheapest level by the budget allocator.
        var vols = new (float DiameterPx, float AreaPx)[] { (2000f, 2000f * 2000f) };
        var levels = VolumeBudget.AllocateLevels(vols, 0f);
        Assert.Equal(VolumeLod.Level.Low, levels[0]);
    }

    // The existing volume CAP (P573) still holds independent of the budget.
    [Fact]
    public void Volume_cap_still_keeps_the_nearest_units()
    {
        var units = Enumerable.Range(0, 60)
            .Select(i => new VolumetricSubUnit(new Vector3(i * 100f, 0, 0), 25f, 0.7f, 0f, 3f, i, new Vector3(1, 1, 1)))
            .ToList();
        var kept = VolumeBudget.TrimNearest(units, new Vector3(0, 0, 0));
        Assert.Equal(VolumeBudget.MaxVolumes, kept.Count);
    }
}

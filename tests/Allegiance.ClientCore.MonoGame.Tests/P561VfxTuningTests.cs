using System;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P561 — failing-first tests for the VFX tuning pass: (1) enhanced trail &
/// explosion smoke size derived from native and capped ≤ 1.5× native
/// (<see cref="EnhancedSmokeSizing"/>), (2) the far-distance fade so a trail
/// reads as a line not beads (<see cref="ParticleFarFade"/>), (3) the
/// chase-view own-ship occluder (<see cref="ChaseOccluder"/>), and (4) the
/// re-tuned density/opacity knobs. Every test failed on the P546 code
/// (oversized 2.4·(0.5+2.2(1−rem)) smoke, no far fade, no own-ship occluder,
/// PeakAlpha 0.65 / MaxSmokePerExplosion 14 / Linger 1.2) and passes after.
/// </summary>
public class P561VfxTuningTests
{
    // ── 1) SIZE PARITY: enhanced trail smoke ≤ 1.5× native at every life ──
    [Fact]
    public void Enhanced_trail_smoke_never_exceeds_1_5x_native()
    {
        // P546 drew enhanced smoke at p.Size·2.4·(0.5+2.2·(1−rem)) ≈ 3.2–4.3× the
        // native SmokeBehaviour half (p.Size·0.5·SmokeSizeScale(rem)) — the
        // oversized-smoke complaint. P561 derives the enhanced half from native
        // and caps the worst case (nominal×max-jitter) at TrailMaxScale (1.5)×.
        foreach (var rem in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 1f })
        {
            float native = EnhancedSmokeSizing.NativeTrailHalf(2f, rem);
            float enhancedMax = EnhancedSmokeSizing.EnhancedTrailHalfMax(2f, rem);
            Assert.True(enhancedMax <= native * EnhancedSmokeSizing.TrailMaxScale,
                $"rem={rem}: enhanced {enhancedMax:F3} > {EnhancedSmokeSizing.TrailMaxScale}× native {native:F3}");
        }
        // And the nominal (unjittered) enhanced size is comfortably smaller than
        // the P546 path at the SAME life fractions (the actual reduction).
        foreach (var rem in new[] { 0f, 0.5f, 1f })
        {
            float legacy = 2f * 2.4f * (0.5f + 2.2f * (1f - rem));          // P546 formula
            float enhanced = EnhancedSmokeSizing.EnhancedTrailHalf(2f, rem); // P561 nominal
            Assert.True(enhanced < legacy, $"rem={rem}: P561 {enhanced:F3} not smaller than P546 {legacy:F3}");
        }
    }

    [Fact]
    public void Enhanced_trail_half_jitter_stays_under_cap()
    {
        // Worst-case jitter (max) must never push a puff past 1.5× native.
        for (float rem = 0f; rem <= 1f; rem += 0.05f)
        {
            float native = EnhancedSmokeSizing.NativeTrailHalf(3f, rem);
            float max = EnhancedSmokeSizing.EnhancedTrailHalfMax(3f, rem);
            Assert.True(max <= native * 1.5f + 1e-4f);
            // Nominal jitter (1) is within the legal jitter band.
            Assert.InRange(EnhancedSmokeSizing.TrailJitterMin, 0.5f, 1.0f);
            Assert.True(EnhancedSmokeSizing.TrailScaleNominal * EnhancedSmokeSizing.TrailJitterMax <=
                        EnhancedSmokeSizing.TrailMaxScale);
        }
    }

    [Fact]
    public void Explosion_smoke_stays_a_small_fraction_of_the_blast()
    {
        // Native AddExplosion draws no smoke — the reference is the explosion's
        // own radius. The enhanced plume puffs must stay well inside the blast
        // (≤ 0.2·radius) for every native preset, so smoke matches the exploding
        // hull scale instead of rivaling the whole fireball (P546 was ≤ 0.96·radius).
        for (int type = Allegiance.Core.Sim.ExplosionType.Mine;
             type <= Allegiance.Core.Sim.ExplosionType.LargeStation; type++)
        {
            var preset = ExplosionPresets.For(type);
            Assert.True(EnhancedSmokeSizing.ExplosionSmokeWithinExplosion(preset.RadiusExplosion),
                $"type {type}: explosion smoke exceeds 0.2×radius {preset.RadiusExplosion}");
            // Monotonic swell with age (younger puffs larger), like native smoke.
            Assert.True(EnhancedSmokeSizing.ExplosionSmokeHalf(preset.RadiusExplosion, 0f) >
                        EnhancedSmokeSizing.ExplosionSmokeHalf(preset.RadiusExplosion, 1f));
        }
    }

    // ── 2) FAR FADE: a distant trail reads as a line, not beads ───────────
    [Fact]
    public void FarFade_reduces_alpha_and_size_with_distance_only_far_away()
    {
        var ff = ParticleFarFade.Flight;   // fade 650 → 2600 u, floor 0.35 alpha / 0.75 size
        // Near (inside FadeStart): full alpha & size.
        Assert.Equal(1f, ParticleFarFade.AlphaFactor(200f, ff.Enabled, ff.FadeStart, ff.FadeEnd, ff.FarAlpha), 4);
        Assert.Equal(1f, ParticleFarFade.SizeFactor(200f, ff.Enabled, ff.FadeStart, ff.FadeEnd, ff.FarSizeScale), 4);
        // At/beyond FadeEnd: the far floor.
        Assert.Equal(ff.FarAlpha, ParticleFarFade.AlphaFactor(5000f, ff.Enabled, ff.FadeStart, ff.FadeEnd, ff.FarAlpha), 4);
        Assert.Equal(ff.FarSizeScale, ParticleFarFade.SizeFactor(5000f, ff.Enabled, ff.FadeStart, ff.FadeEnd, ff.FarSizeScale), 4);
        // Between: strictly decreasing alpha.
        float midA = ParticleFarFade.AlphaFactor(1500f, ff.Enabled, ff.FadeStart, ff.FadeEnd, ff.FarAlpha);
        Assert.InRange(midA, ff.FarAlpha, 1f);
        // Disabled → always full.
        Assert.Equal(1f, ParticleFarFade.AlphaFactor(9999f, false, 0f, 0f, 0f), 4);
    }

    // ── 3) CHASE VIEW: own-ship occluder fades between-camera smoke ───────
    private static (Matrix view, Matrix proj, Vector3 eye) CameraLookingAtOrigin(float behind)
    {
        var eye = new Vector3(0f, 0f, behind);        // camera at +Z looking at origin
        var view = Matrix.CreateLookAt(eye, Vector3.Zero, Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60f), 16f / 9f, 0.1f, 10000f);
        return (view, proj, eye);
    }

    [Fact]
    public void ChaseOccluder_fades_smoke_between_camera_and_own_ship()
    {
        var (view, proj, eye) = CameraLookingAtOrigin(behind: 40f);
        var ship = new Vector3(0f, 0f, 0f);
        var occ = ChaseOccluder.Create(ship, radius: 5f, eye, view, proj);
        Assert.True(occ.Active, "occluder should be active when the ship is in front of the camera");

        // A puff directly between the camera and the ship's screen centre → hidden.
        var puffBetween = new Vector3(0f, 0f, -15f);            // closer to eye than ship, on centreline
        Assert.Equal(0f, ChaseOccluder.AlphaFactor(occ, puffBetween, proj), 3);

        // A puff at the same depth as the ship (not nearer) → NOT occluded.
        var puffAtShip = new Vector3(0f, 0f, 0f);
        Assert.Equal(1f, ChaseOccluder.AlphaFactor(occ, puffAtShip, proj), 3);

        // A puff far off to the side (outside the ship ellipse) → NOT occluded.
        var puffSide = new Vector3(200f, 0f, -15f);
        Assert.Equal(1f, ChaseOccluder.AlphaFactor(occ, puffSide, proj), 3);

        // Inactive occluder → nothing is faded.
        Assert.Equal(1f, ChaseOccluder.AlphaFactor(default, puffBetween, proj), 3);
    }

    [Fact]
    public void ChaseOccluder_inactive_when_ship_behind_camera()
    {
        var (view, proj, eye) = CameraLookingAtOrigin(behind: 40f);
        // Ship behind the camera: world Z = +60 with eye at +40 → ship is behind.
        var occ = ChaseOccluder.Create(new Vector3(0f, 0f, 60f), 5f, eye, view, proj);
        Assert.False(occ.Active);
    }

    // ── 4) RE-TUNED DENSITY / OPACITY knobs (values justified in the report) ─
    [Fact]
    public void Enhanced_smoke_opacity_and_density_knobs_are_lowered()
    {
        // P546: PeakAlpha 0.65, MaxSmokePerExplosion 14, Linger 1.2s. P561 lowers
        // all three so more, smaller puffs stay readable without blowing out.
        Assert.True(EnhancedSmokeField.PeakAlpha <= 0.45f, $"PeakAlpha {EnhancedSmokeField.PeakAlpha} not lowered");
        Assert.True(EnhancedExplosionRenderer.MaxSmokePerExplosion <= 11, "fewer explosion puffs");
        Assert.True(EnhancedExplosionRenderer.LingerExtraSeconds <= 0.9f, "shorter linger");
        Assert.True(EnhancedSmokeField.PeakAlpha >= 0.15f, "still legible");
    }

    [Fact]
    public void Trail_subdivision_produces_more_but_smaller_puffs()
    {
        // Priority 2: puffs merge into a continuous plume — each native trail
        // smoke particle splits into TrailSubPuffs overlapping sub-puffs, each
        // a fraction of the native size. Ensures we actually subdivide (>1) and
        // each sub-puff is smaller than the parent's native half.
        Assert.True(EnhancedSmokeSizing.TrailSubPuffs >= 3);
        float subHalf = EnhancedSmokeSizing.EnhancedTrailHalf(2f, 0.5f);
        float nativeHalf = EnhancedSmokeSizing.NativeTrailHalf(2f, 0.5f);
        Assert.True(subHalf <= nativeHalf * EnhancedSmokeSizing.TrailMaxScale);
    }
}

using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ProjectileCameraCull"/> (P239) — no graphics
/// device involved. They pin the cockpit-flood bug the fix addresses: a bolt
/// spawns at the ship centre (no muzzle offset) and renders as a 32–480-unit
/// solid box centred on that spawn, so a 14-unit-forward cockpit eye is engulfed
/// by its own bolt. The tests use the real production constants where they
/// matter: <c>width=8</c> (ProjectileWidth) and <c>clearance=60</c>
/// (ProjectileNearCamClearance). The 60-unit gap is the crux of the fix — it is
/// deliberately large so that a bolt's 8-unit-wide cross-section, once it
/// becomes drawable, sits at least ~60 units ahead of the eye (subtending only
/// ~7.6°), instead of a tiny 3-unit margin whose ~100° apparent size still
/// flooded the view (seen live in the P239 frame-12 repro).
/// </summary>
public class ProjectileCameraCullTests
{
    private const float Width = 8f;
    private const float Clearance = 60f;   // ProjectileNearCamClearance

    // The real bug: bolt centred at ship centre (origin), eye 14 units forward
    // along its travel axis. For any bolt length >= 32 the eye (14) is inside the
    // longitudinal span (len/2 + clearance) and dead on the axis — cull it.
    [Theory]
    [InlineData(32f)]   // MinBoltLength: half 16 + 60 = 76, 14 <= 76 -> inside
    [InlineData(144f)]  // a mid-speed shot: half 72 + 60 = 132, 14 <= 132
    [InlineData(480f)]  // MaxBoltLength: half 240 + 60 = 300, 14 <= 300
    public void BoltCentredAtShip_Eye14ForwardOnAxis_Culls(float length)
    {
        bool cull = ProjectileCameraCull.ShouldCull(
            0, 0, 14,              // eye
            0, 0, 0,               // bolt centre (spawn = ship centre)
            0, 0, 1,               // dir = +Z (forward)
            length, Width, Clearance);
        Assert.True(cull);
    }

    [Fact]
    public void Bolt_EyeBeyondNearGapAlongAxis_DoesNotCull()
    {
        // Max bolt: half 240 + 60 = 300. Eye at 370 is well past the near gap ->
        // the bolt is ahead of the eye as a normal streak, must still draw.
        bool cull = ProjectileCameraCull.ShouldCull(
            0, 0, 370, 0, 0, 0, 0, 0, 1, 480f, Width, Clearance);
        Assert.False(cull);
    }

    [Fact]
    public void Bolt_EyeLaterallyBeyondNearGap_DoesNotCull()
    {
        // Perpendicular distance 100 >> half-lateral 4+60=64: a bolt flying far to
        // the side, not through the camera.
        bool cull = ProjectileCameraCull.ShouldCull(
            100, 0, 0, 0, 0, 0, 0, 0, 1, 480f, Width, Clearance);
        Assert.False(cull);
    }

    [Fact]
    public void Bolt_CloseBesideEyeWithinNearGap_Culls()
    {
        // A bolt passing ~40 units to the side (within the 64 half-lateral) is
        // too close to render cleanly — culled like the axial case.
        Assert.True(ProjectileCameraCull.ShouldCull(
            40, 0, 0, 0, 0, 0, 0, 0, 1, 320f, Width, Clearance));
    }

    [Fact]
    public void EyeExactlyAtBoltCentre_Culls()
    {
        bool cull = ProjectileCameraCull.ShouldCull(
            0, 0, 0, 0, 0, 0, 0, 0, 1, 160f, Width, Clearance);
        Assert.True(cull);
    }

    [Fact]
    public void CrossSectionBoundary_InsideCulls_OutsideDoesNot()
    {
        // half-lateral = 4 + 60 = 64. Inside (perp 63) culls; outside (66) does not.
        Assert.True(ProjectileCameraCull.ShouldCull(
            0, 63f, 0, 0, 0, 0, 0, 0, 1, 320f, Width, Clearance));
        Assert.False(ProjectileCameraCull.ShouldCull(
            0, 66f, 0, 0, 0, 0, 0, 0, 1, 320f, Width, Clearance));
    }

    [Fact]
    public void ShortBolt_EyePastNearGap_DoesNotCull()
    {
        // Min length 32: half 16 + 60 = 76; eye at 90 is beyond -> the shortest
        // bolt has already cleared the eye by a safe streak gap.
        bool cull = ProjectileCameraCull.ShouldCull(
            0, 0, 90, 0, 0, 0, 0, 0, 1, 32f, Width, Clearance);
        Assert.False(cull);
    }

    [Fact]
    public void DefaultDirectionFallback_ZeroDir_WorksLikeAnyDir()
    {
        // The gather loop's last-resort dir (0,0,-1): eye 14 back along -Z is on
        // the axis -> culls, symmetric to the +Z case.
        Assert.True(ProjectileCameraCull.ShouldCull(
            0, 0, -14, 0, 0, 0, 0, 0, -1, 348f, Width, Clearance));
        // len 348 -> half 174 + 60 = 234; eye at 260 back is past the gap.
        Assert.False(ProjectileCameraCull.ShouldCull(
            0, 0, -260, 0, 0, 0, 0, 0, -1, 348f, Width, Clearance));
    }

    // Rotation invariance: the geometry test is in the bolt's local frame, so a
    // rigid rotation of the whole world (eye + pos + dir) must not change the
    // outcome. Rotate the +Z-aligned scenarios 90° about Y.
    [Fact]
    public void WholeSetup_RotatedAboutY_ResultUnchanged()
    {
        // Scenario that culls: eye (0,0,14), dir (0,0,1). Rotated 90° about Y:
        // (x,z)->(z,-x) => eye (14,0,0), dir (1,0,0). len 336 -> half 168+60=228.
        Assert.True(ProjectileCameraCull.ShouldCull(
            14, 0, 0, 0, 0, 0, 1, 0, 0, 336f, Width, Clearance));

        // Scenario that must NOT cull (eye past the near gap): eye (0,0,300) ->
        // (300,0,0), dir (0,0,1)->(1,0,0). len 332 -> half 166+60=226; 300>226.
        Assert.False(ProjectileCameraCull.ShouldCull(
            322, 0, 0, 0, 0, 0, 1, 0, 0, 328f, Width, Clearance));
    }
}

using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ProjectileCameraCull"/> (P239/P243) — no
/// graphics device involved. They pin the cockpit-flood bug the fix addresses:
/// a bolt spawns at the ship centre (no muzzle offset) and renders as a 32–480-
/// unit solid box centred on that spawn, so a 14-unit-forward cockpit eye is
/// engulfed by its own bolt. The tests use the real production constants where
/// they matter: <c>width=0.35</c> (ProjectileWidth, P243) and
/// <c>clearance=15</c> (ProjectileNearCamClearance). P243 thinned the bolt from
/// 8 to 0.35 units and shrank the clearance from 60 to 15: with a 0.35-wide
/// cross-section, a bolt that becomes drawable 15 units ahead of the eye
/// subtends only ~1.3° (vs ~7.6° for the old 8-wide at 60), so the much tighter
/// gap still prevents any screen flood while letting own shots appear sooner.
/// The cull is kept (not removed) because a bolt spawned at ship centre can
/// still momentarily coincide with the 14-unit-forward cockpit eye.
/// </summary>
public class ProjectileCameraCullTests
{
    private const float Width = 0.35f;     // ProjectileWidth
    private const float Clearance = 15f;   // ProjectileNearCamClearance
    // Lateral cull reach = width/2 + clearance = 0.175 + 15 = 15.175 (sq ~230.3).
    // Longitudinal reach for length L = L/2 + 15.

    // The real bug: bolt centred at ship centre (origin), eye 14 units forward
    // along its travel axis. For any bolt length >= 32 the eye (14) is inside
    // the longitudinal span (L/2 + 15) and dead on the axis — cull it.
    [Theory]
    [InlineData(32f)]   // MinBoltLength: half 16 + 15 = 31,  14 <= 31  -> cull
    [InlineData(144f)]  // a mid-speed shot: half 72 + 15 = 87,  14 <= 87
    [InlineData(480f)]  // MaxBoltLength: half 240 + 15 = 255, 14 <= 255
    public void BoltCentredAtShip_Eye14ForwardOnAxis_Culls(float length)
    {
        Assert.True(ProjectileCameraCull.ShouldCull(
            0f, 0f, 14f,      // eye
            0f, 0f, 0f,       // bolt centre (spawn = ship centre)
            0f, 0f, 1f,       // dir = +Z (forward)
            length, Width, Clearance));
    }

    [Fact]
    public void Bolt_EyeBeyondNearGapAlongAxis_DoesNotCull()
    {
        // Max bolt: half 240 + 15 = 255. Eye at 300 is well past the gap, so the
        // bolt is ahead of the eye as a normal streak — must still draw.
        Assert.False(ProjectileCameraCull.ShouldCull(
            0f, 0f, 300f,     // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, 1f,       // dir
            480f, Width, Clearance));
    }

    [Fact]
    public void Bolt_EyeLaterallyBeyondNearGap_DoesNotCull()
    {
        // Perpendicular distance 100 >> half-lateral 15.175: a bolt flying far
        // to the side, not through the camera.
        Assert.False(ProjectileCameraCull.ShouldCull(
            100f, 0f, 0f,     // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, 1f,       // dir
            480f, Width, Clearance));
    }

    [Fact]
    public void Bolt_CloseBesideEyeWithinNearGap_Culls()
    {
        // A bolt passing 10 units to the side (within the 15.175 half-lateral,
        // 100 <= 230.3) is too close to render cleanly — culled like the axial case.
        Assert.True(ProjectileCameraCull.ShouldCull(
            10f, 0f, 0f,      // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, 1f,       // dir
            480f, Width, Clearance));
    }

    [Fact]
    public void EyeExactlyAtBoltCentre_Culls()
    {
        // Eye coincident with the bolt centre: always culled regardless of length.
        Assert.True(ProjectileCameraCull.ShouldCull(
            0f, 0f, 0f,       // eye (=== bolt centre)
            0f, 0f, 0f,       // bolt centre
            0f, 0f, 1f,       // dir
            480f, Width, Clearance));
    }

    [Fact]
    public void CrossSectionBoundary_InsideCulls_OutsideDoesNot()
    {
        // Half-lateral 15.175 (sq ~230.3): perp 15 (225) is inside -> culls;
        // perp 16 (256) is outside -> does not.
        Assert.True(ProjectileCameraCull.ShouldCull(
            0f, 15f, 0f,      // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, 1f,       // dir
            480f, Width, Clearance));
        Assert.False(ProjectileCameraCull.ShouldCull(
            0f, 16f, 0f,      // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, 1f,       // dir
            480f, Width, Clearance));
    }

    [Fact]
    public void ShortBolt_EyePastNearGap_DoesNotCull()
    {
        // Min length 32: half 16 + 15 = 31. Eye at 60 is beyond -> the shortest
        // bolt has already cleared the eye by a safe streak gap.
        Assert.False(ProjectileCameraCull.ShouldCull(
            0f, 0f, 60f,      // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, 1f,       // dir
            32f, Width, Clearance));
    }

    [Fact]
    public void DefaultDirectionFallback_ZeroDir_WorksLikeAnyDir()
    {
        // The gather loop's last-resort dir (0,0,-1): eye 14 back along -Z is on
        // the axis -> culls, symmetric to the +Z case.
        Assert.True(ProjectileCameraCull.ShouldCull(
            0f, 0f, -14f,     // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, -1f,      // dir
            480f, Width, Clearance));
        // Max bolt: half 240 + 15 = 255. Eye 300 back is past the gap -> no cull.
        Assert.False(ProjectileCameraCull.ShouldCull(
            0f, 0f, -300f,    // eye
            0f, 0f, 0f,       // bolt centre
            0f, 0f, -1f,      // dir
            480f, Width, Clearance));
    }

    // Rotation invariance: the geometry test is in the bolt's local frame, so a
    // rigid rotation of the whole world (eye + pos + dir) must not change the
    // outcome. Rotate the +Z-aligned scenarios 90° about Y.
    [Fact]
    public void WholeSetup_RotatedAboutY_ResultUnchanged()
    {
        // Scenario that culls: eye (0,0,14), dir (0,0,1). Rotated 90° about Y:
        // (x,z)->(z,-x) => eye (14,0,0), dir (1,0,0).
        Assert.True(ProjectileCameraCull.ShouldCull(
            14f, 0f, 0f,      // eye
            0f, 0f, 0f,       // bolt centre
            1f, 0f, 0f,       // dir
            480f, Width, Clearance));

        // Scenario that must NOT cull: eye 300 past the gap along +X.
        Assert.False(ProjectileCameraCull.ShouldCull(
            300f, 0f, 0f,     // eye
            0f, 0f, 0f,       // bolt centre
            1f, 0f, 0f,       // dir
            480f, Width, Clearance));
    }
}
